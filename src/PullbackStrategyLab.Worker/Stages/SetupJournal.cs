using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Seals the night: the setup rows are complete, frozen, and not yet touched by anything downstream.
///
/// <b>It writes nothing, and that is the whole design.</b> Every other stage in this worker owns a
/// table. This one owns a property, so SCHEMA lists it as the writer of nothing and
/// `writer-ownership` never sees it. A component that enforces immutability by writing would be the
/// second writer of the thing it protects.
///
/// <b>What it can actually assert, and what it cannot.</b> It cannot compare a row against what the
/// detector wrote, because nothing keeps a second copy and keeping one would be a store whose only
/// purpose is to disagree with the first. What it can do is check the invariants that hold at 18:25
/// and would be false if anything had already written where it should not have:
///
/// <list type="bullet">
///   <item>every row of the night carries a parseable, complete check result set</item>
///   <item>every row has its frozen signal evidence, because the vectorizer ran at 18:25 before it</item>
///   <item>no row carries a rank or a cap verdict yet, because the capper runs at 18:28 after it</item>
///   <item>no row carries an agreement yet, because a person reads the gallery tomorrow</item>
/// </list>
///
/// The last two are ordering assertions wearing an immutability coat, and they are the useful half.
/// A rank present at 18:25 means the capper ran early or something else wrote the column; an
/// agreement present means the read surface wrote before anybody could have looked. Both are the
/// shape of defect that otherwise shows up months later as a night that reads oddly.
///
/// <b>The immutability itself is asserted in the suite, four ways</b>, on the pattern 2.2 used for
/// the frozen signal row: a rerun writes nothing, a restated bar does not revise a written value,
/// the store's own key refuses a second write, and no `UPDATE` against a detector-owned column
/// exists in the shipped source. This stage is what notices at runtime; those are what notice in CI.
/// see: The plan is written before the session and is immutable after publication
/// </summary>
public sealed class SetupJournal
{
    public const string Name = "journal";

    private static readonly JsonSerializerOptions CheckJson = new(JsonSerializerDefaults.Web);

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SetupJournal(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        JournalResult result = Seal(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Setups} setup(s) sealed");
        Console.WriteLine($"{Name}: {result.WithSignals} carrying frozen signal evidence");

        foreach (string breach in result.Breaches)
        {
            Console.Error.WriteLine($"{Name}: {breach}");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// One night, checked. A breach is reported and recorded partial rather than thrown, because the
    /// stages after this one still have work to do and a night that loses its cap because its
    /// journal threw is a worse night than one that records what was wrong.
    /// </summary>
    public JournalResult Seal(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name);

        // The instant this seal is answering for. Bounded, like every other read in the system:
        // the question is whether the evidence was frozen *before the journal ran*, and a signal
        // row written afterwards is not evidence this night's seal could have seen. On a live run
        // nothing later exists yet; on a replay it can, which is exactly when an unbounded read
        // would quietly answer yes.
        // see: A reader's signature does not establish point-in-time; the query does
        DateTimeOffset sealedAt = _clock.UtcNow;

        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, asOf);
        var breaches = new List<string>();
        int withSignals = 0;

        foreach (StoredSetup setup in setups)
        {
            IReadOnlyList<CheckResult> results;

            try
            {
                results = JsonSerializer.Deserialize<CheckResult[]>(setup.CheckResults, CheckJson) ?? [];
            }
            catch (JsonException e)
            {
                breaches.Add($"{setup.SetupId}: check results do not parse, {e.Message}");
                continue;
            }

            IReadOnlyList<string> expected = string.Equals(setup.Direction, "long", StringComparison.Ordinal)
                ? SetupChecks.Long
                : SetupChecks.Short;

            string[] missing = [.. expected.Where(name => !results.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))];

            if (missing.Length > 0)
            {
                breaches.Add($"{setup.SetupId}: no result recorded for {string.Join(", ", missing)}");
            }

            if (setup.Rank is not null || setup.CappedOut is not null)
            {
                breaches.Add(
                    $"{setup.SetupId}: carries a rank or a cap verdict at {Name}, and the capper runs after this stage. "
                    + "Either the night ran out of order or something else wrote a column it does not own.");
            }

            if (setup.Agreement is not null)
            {
                breaches.Add(
                    $"{setup.SetupId}: carries an agreement before anybody could have read the gallery. "
                    + "The read surface writes that column and only a person supplies its value.");
            }

            if (SignalCount(connection, setup.SetupId, sealedAt) > 0)
            {
                withSignals++;
            }
            else
            {
                breaches.Add(
                    $"{setup.SetupId}: no frozen signal evidence, and the vectorizer runs before this stage. "
                    + "A setup whose evidence was never frozen cannot be scored later against what it knew.");
            }
        }

        RunSummary summary = run.Complete(breaches.Count == 0 ? RunOutcome.Clean : RunOutcome.Partial);

        return new JournalResult(
            asOf, setups.Count, withSignals, breaches, summary.RowsWritten, summary.CallsUsed,
            breaches.Count == 0 ? RunOutcome.Clean : RunOutcome.Partial);
    }

    private static int SignalCount(SqliteConnection connection, string setupId, DateTimeOffset sealedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM setup_signal WHERE setup_id = @setup_id AND computed_at <= @sealed_at";
        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@sealed_at", StoreText.TimestampToStorageText(sealedAt));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>What one night's sealing found.</summary>
public sealed record JournalResult(
    DateOnly AsOf,
    int Setups,
    int WithSignals,
    IReadOnlyList<string> Breaches,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
