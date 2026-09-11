using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Computes a signal the library has gained across every setup already stored, in one run.
///
/// <b>This is why widening the library is cheap.</b> Most of what a signal reads is daily bars, and
/// `daily_bar` is append-only and holds the history from the first ingest, so a signal admitted
/// today can be computed for every night the lab ever recorded rather than only for the nights after
/// it. Without this a new signal is evidence about the future only, and a proposal resting on it
/// waits as long as the accumulation does.
///
/// <b>It computes nothing of its own, and that is the design rather than an economy.</b> Every value
/// comes out of <see cref="SignalVectorizer.Values"/>, which is the one implementation of every
/// formula in the library. A backfiller with its own arithmetic would produce a row that differs
/// from the night's row by an amount too small to see and too large to ignore, on exactly the
/// signals a version is later judged over.
///
/// <b>Point-in-time is bought by the as-of it passes, not by anything it does.</b> Each setup is
/// computed at its <i>own</i> session, so every read behind the vectorizer is bounded where that
/// night's read was bounded: bars restated since are invisible, an attribute resolved since is
/// invisible on the basis the store declares, and a level computed since is not read. A backfill run
/// with today's date as the as-of would produce values no night could have held, which is the one
/// way this stage could quietly void every replay in the system.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Disjoint from the vectorizer by date and by signal, and both halves are enforced.</b> The
/// vectorizer owns the sessions it has not yet run for, so this stage refuses any session at or
/// after its own as-of; and `setup_signal` is written once, so a name a setup already carries is
/// left exactly as the night wrote it. The pair is what SCHEMA declares of the two writers.
/// </summary>
public sealed class SignalBackfiller
{
    public const string Name = "backfill-signals";

    /// <summary>
    /// The signals this stage fills over stored history, and the reason each is here.
    ///
    /// <b>Both were added at 6.1 and neither widens the library's reach.</b> Each is a quantity a
    /// shipped gate already compares and the row already implied: `ema_gap_21_50_over_avg` is what
    /// `averages-squeezing` compares and `ceiling_distance_ranges` is what `reached-ceiling`
    /// compares. They are not new evidence about a name, which is why they arrive here rather than
    /// through the admission route a genuinely new axis takes, and why declaring them costs nothing
    /// against the correction threshold: nothing new is screened.
    /// see: A version whose moved gate cannot be judged from the frozen signals is refused at admission
    ///
    /// <b>A name here has to be one the vectorizer freezes.</b> A backfilled signal the nightly stage
    /// does not write would be a signal present on old setups and absent on new ones, which reads as
    /// a signal that stopped being computed. A test holds the two lists together in that direction.
    /// </summary>
    public static IReadOnlyList<string> Fills { get; } =
    [
        "ema_gap_21_50_over_avg",
        "ceiling_distance_ranges",

        // The sourced candidates frozen from 7.4. On the list so the operator's backfill reaches the
        // rows recorded before the vectorizer froze them, which is what a signal added to the frozen
        // set is supposed to cost; not run by any slot, because it is a one-time act per signal.
        "ema_150_distance",
        "ema_9_slope",
        "ema_21_slope",
        "ema_50_slope",
        "return_30_days",
        "base_span_ranges",
        "undercut_reclaim_ema_9",
        "from_session_extreme",
        "entry_ceiling",
        "weekly_ema_gap_9_21",
        "weekly_ema_21_distance",
        "weekly_squeeze_ratio",
        "ceiling_confluence_ranges",
    ];

    /// <summary>
    /// Why a run wrote nothing for a setup: every name it fills was already on the row.
    ///
    /// Recorded rather than counted as a skip, because a second run of this stage over the same
    /// history is expected to fill nothing and that is a clean outcome, not an empty one.
    /// </summary>
    public const string AlreadyCarried = "every signal this run fills was already frozen on the setup";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SignalBackfiller(
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

    /// <summary>
    /// <c>backfill-signals [as-of] [signal ...]</c>, filling every name in <see cref="Fills"/> where
    /// none is named.
    ///
    /// A name the vectorizer cannot freeze is refused rather than run as an empty pass, because a
    /// run that fills nothing and a run that was asked for a signal nobody computes look the same in
    /// a log and only one of them is a mistake.
    /// </summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        IReadOnlyList<string> names = args.Length > 1 ? args[1..] : Fills;

        foreach (string name in names)
        {
            if (!SignalVectorizer.Frozen.Contains(name, StringComparer.Ordinal))
            {
                Console.Error.WriteLine(
                    $"{Name}: '{name}' is not a signal SignalVectorizer freezes, so nothing computes it.");
                return 2;
            }
        }

        SignalBackfillResult result = Backfill(asOf, names);

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, {result.Sessions} session(s) before it, {result.Setups} setup(s)");
        Console.WriteLine($"{Name}: filling {string.Join(", ", names)}");
        Console.WriteLine(
            $"{Name}: {result.Written} value(s) written, {result.AlreadyFrozen} already frozen, "
            + $"{result.Absent} absent for want of history");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// One backfill pass over every session the store holds a setup on before <paramref name="asOf"/>.
    ///
    /// <b>Strictly before, which is the date half of the disjointness.</b> The vectorizer runs for
    /// the session it is given and owns every signal on it, so a backfill that reached today's rows
    /// would race the stage that owns them for exactly the names it was asked to fill. Nothing is
    /// lost by the exclusion: tonight's setups get these signals from the vectorizer itself.
    /// </summary>
    public SignalBackfillResult Backfill(DateOnly asOf, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "setup_signal");

        DateTimeOffset computedAt = run.StartedAt;
        IReadOnlyList<DateOnly> sessions = SessionsBefore(connection, asOf);

        int setups = 0;
        int written = 0;
        int alreadyFrozen = 0;
        int absent = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (DateOnly session in sessions)
            {
                foreach (StoredSetup setup in SetupReader.Read(connection, session))
                {
                    setups++;

                    IReadOnlySet<string> existing = SetupSignalReader.NamesFor(connection, setup.SetupId);

                    // Nothing to compute where the row already carries every name this run fills.
                    // Checked before the values are assembled, because assembling them is 170
                    // sessions of bars per setup and a second run over a filled history would
                    // otherwise pay that for every row to write nothing.
                    if (names.All(existing.Contains))
                    {
                        alreadyFrozen += names.Count;
                        continue;
                    }

                    // The setup's own session, never the run's. This is the whole point-in-time
                    // property of the stage and it is one argument.
                    IReadOnlyDictionary<string, string> values =
                        SignalVectorizer.Values(connection, setup, setup.AsOf, _options.SessionZone);

                    foreach (string name in names)
                    {
                        if (existing.Contains(name))
                        {
                            alreadyFrozen++;
                            continue;
                        }

                        if (!values.TryGetValue(name, out string? value))
                        {
                            // The history behind that night was too short to compute it, which is
                            // the same absence the vectorizer records and means the same thing:
                            // the signal is missing rather than nought.
                            absent++;
                            continue;
                        }

                        Insert(connection, transaction, setup.SetupId, name, value, computedAt);
                        written++;
                    }
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new SignalBackfillResult(
            asOf, sessions.Count, setups, written, alreadyFrozen, absent, summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>
    /// The sessions the evidence store holds a setup on, strictly before the as-of, both directions
    /// merged.
    ///
    /// <b>Through the reader rather than through a statement of its own.</b> `SetupReader.Sessions`
    /// is bounded and reads one direction, so this asks it twice and merges, which keeps the read
    /// count at two per run and leaves the point-in-time bound in the one place that already holds
    /// it. The two are merged and never added (see: Long and short are never pooled into one figure):
    /// what comes back is the set of sessions to walk, not a count of anything.
    /// </summary>
    private static IReadOnlyList<DateOnly> SessionsBefore(SqliteConnection connection, DateOnly asOf)
    {
        if (asOf == DateOnly.MinValue)
        {
            return [];
        }

        DateOnly through = asOf.AddDays(-1);
        var sessions = new SortedSet<DateOnly>();

        foreach (string direction in (string[])[SetupDirection.Long, SetupDirection.Short])
        {
            foreach (DateOnly session in
                SetupReader.Sessions(connection, direction, DateOnly.MinValue, through))
            {
                sessions.Add(session);
            }
        }

        return [.. sessions];
    }

    /// <summary>
    /// One value onto a setup already stored.
    ///
    /// The same statement the vectorizer issues, and the store's own key is what keeps the two
    /// writers disjoint by signal: a name the night already froze cannot be replaced from here even
    /// if the guard above were removed.
    /// </summary>
    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string setupId,
        string name,
        string value,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES (@setup_id, @signal_name, @value, @computed_at)
            ON CONFLICT (setup_id, signal_name) DO NOTHING
            """;

        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@signal_name", name);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        command.ExecuteNonQuery();
    }
}

/// <summary>
/// What one backfill pass did.
///
/// <c>Setups</c> is the population walked and <c>Written</c> is what was added to it; the two are
/// stated apart because a run over a history already filled walks every row and writes nothing, and
/// a single figure would make that indistinguishable from a run that found no history at all.
/// </summary>
public sealed record SignalBackfillResult(
    DateOnly AsOf,
    int Sessions,
    int Setups,
    int Written,
    int AlreadyFrozen,
    int Absent,
    int RowsWritten,
    RunOutcome Outcome);
