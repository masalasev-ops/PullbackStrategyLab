using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Six mover scans a night, three per direction. A stock must appear on one to be eligible,
/// because the premise of the whole strategy is that something happened.
///
/// Each scan takes the top <see cref="Breadth"/> universe members by its own magnitude, ranked from
/// one. Not everything clearing a threshold on the move: a rank cut can be calibrated against
/// nightly counts with no forward return in the store, which is what 2.11 does, and a percentage
/// floor cannot, because whether eight percent is strict is a claim about market volatility over
/// the sample rather than about the corpus. Rank is also the only thing that makes the six
/// comparable to each other.
/// see: The scans select a fixed count by rank, not a threshold on the move
///
/// Every magnitude is on the adjusted basis, through <see cref="ScanMagnitudes"/>, and that is not
/// a detail. Read raw, a two-for-one split is a fifty percent decline and tops the decliner scan
/// every time one happens.
/// see: Every scan magnitude is computed on the adjusted basis
///
/// No vendor call. The scans are a function of stored bars, so a rerun of the same night recomputes
/// the same answer and finds its rows already there.
/// </summary>
public sealed class ScanEngine
{
    public const string Name = "scans";

    /// <summary>How many names each scan keeps. Authored, and marked "phase 2 count check".</summary>
    public const int Breadth = 50;

    /// <summary>
    /// The month-mover window, in sessions. One trading month.
    ///
    /// The Core constant rather than a second twenty, because the geometry reads the same span
    /// to measure a month scan's thrust and two copies is how the two start disagreeing about
    /// which scans are month scans.
    /// </summary>
    public const int MonthWindow = ScanSpans.MonthSessions;

    /// <summary>
    /// Sessions of history read per name: the month window, the session before it to measure the
    /// first change from, and today. Stated from what the widest magnitude needs.
    /// </summary>
    public const int HistorySessions = MonthWindow + 2;

    /// <summary>The three long-side scans and the three short-side ones, in SCHEMA's order.</summary>
    public static IReadOnlyList<string> Scans { get; } =
        ["gainer", "gapper", "leader", "decliner", "gapdown", "laggard"];

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ScanEngine(
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

        ScanResult result = Scan(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Members} member(s), {result.Measured} measured");
        Console.WriteLine($"{Name}: {result.Hits} hit(s) across {Scans.Count} scans, {result.Inserted} written, {result.AlreadyStored} already stored");
        Console.WriteLine($"{Name}: {result.ShortOfHistory} short of the {HistorySessions}-session window");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public ScanResult Scan(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "scan_hit");

        IReadOnlyList<string> members = UniverseSnapshotReader.Members(connection, asOf);
        var measured = new List<Candidate>();
        int shortOfHistory = 0;

        foreach (string ticker in members)
        {
            IReadOnlyList<StoredDailyBar> bars =
                DailyBarReader.Read(connection, ticker, asOf, HistorySessions, _options.SessionZone);

            // The month magnitude needs the whole window and the daily one needs two bars. A name
            // short of either is measured on neither: a scan that quietly ranked a name on a
            // shorter window would put a recent listing at the top of the month movers every time,
            // because a stock with three sessions of history has moved a long way in all of them.
            if (bars.Count < HistorySessions || bars[^1].BarDate != asOf)
            {
                shortOfHistory++;
                continue;
            }

            StoredDailyBar today = bars[^1];
            StoredDailyBar yesterday = bars[^2];
            StoredDailyBar monthAgo = bars[^(MonthWindow + 1)];

            measured.Add(new Candidate(
                ticker,
                ScanMagnitudes.DailyChange(yesterday.AdjustedClose, today.AdjustedClose),
                ScanMagnitudes.Gap(yesterday.AdjustedClose, today.Open, today.Close, today.AdjustedClose),
                ScanMagnitudes.MonthChange(monthAgo.AdjustedClose, today.AdjustedClose)));
        }

        int inserted = 0;
        int alreadyStored = 0;
        int hits = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string scan in Scans)
            {
                foreach ((Candidate candidate, int rank) in Top(measured, scan))
                {
                    hits++;
                    if (Insert(connection, transaction, candidate.Ticker, asOf, scan, rank, Magnitude(candidate, scan), _clock.UtcNow))
                    {
                        inserted++;
                    }
                    else
                    {
                        alreadyStored++;
                    }
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new ScanResult(
            asOf, members.Count, measured.Count, shortOfHistory, hits, inserted, alreadyStored,
            summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>
    /// The top <see cref="Breadth"/> by one scan's magnitude, with ticker as the tiebreak.
    ///
    /// The tiebreak matters more than it looks. Two names with the same magnitude to the last
    /// decimal is unlikely on a real market day and certain on a fixture, and without a stated
    /// second key the boundary of the top fifty would depend on the order the store returned rows.
    /// That is a diff that fails on a platform rather than on a defect.
    /// </summary>
    public static IReadOnlyList<(Candidate Candidate, int Rank)> Top(IReadOnlyList<Candidate> candidates, string scan)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(scan);

        bool descending = scan is "gainer" or "gapper" or "leader";

        IOrderedEnumerable<Candidate> ordered = descending
            ? candidates.OrderByDescending(c => Magnitude(c, scan)).ThenBy(c => c.Ticker, StringComparer.Ordinal)
            : candidates.OrderBy(c => Magnitude(c, scan)).ThenBy(c => c.Ticker, StringComparer.Ordinal);

        return [.. ordered.Take(Breadth).Select((c, i) => (c, i + 1))];
    }

    /// <summary>Which of a candidate's three magnitudes a scan ranks on.</summary>
    public static decimal Magnitude(Candidate candidate, string scan)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return scan switch
        {
            "gainer" or "decliner" => candidate.DailyChange,
            "gapper" or "gapdown" => candidate.Gap,
            "leader" or "laggard" => candidate.MonthChange,
            _ => throw new ArgumentOutOfRangeException(nameof(scan), scan, "not one of the six scans"),
        };
    }

    private static bool Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateOnly asOf,
        string scan,
        int rank,
        decimal magnitude,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO scan_hit (ticker, as_of, scan, rank, magnitude, observed_at)
            VALUES (@ticker, @as_of, @scan, @rank, @magnitude, @observed_at)
            ON CONFLICT (ticker, as_of, scan) DO NOTHING
            """;

        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@scan", scan);
        command.Parameters.AddWithValue("@rank", rank);
        command.Parameters.AddWithValue("@magnitude", StoreText.RatioToStorageText(magnitude));

        // When the lab observed the hit, so a rerun of `scans` for a past date writes rows a
        // point-in-time read can tell from the originals. `ON CONFLICT DO NOTHING` means a
        // rerun of the same session leaves the first stamp standing, which is correct: the
        // row is the one the first run wrote.
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>One universe member with all three magnitudes, measured once and ranked six ways.</summary>
    public sealed record Candidate(string Ticker, decimal DailyChange, decimal Gap, decimal MonthChange);
}

/// <summary>What one scan run did.</summary>
public sealed record ScanResult(
    DateOnly AsOf,
    int Members,
    int Measured,
    int ShortOfHistory,
    int Hits,
    int Inserted,
    int AlreadyStored,
    int RowsWritten,
    RunOutcome Outcome);
