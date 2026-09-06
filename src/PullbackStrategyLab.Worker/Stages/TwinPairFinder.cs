using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Finds the setups that looked the same in everything the lab records and ended somewhere else.
///
/// <b>A twin pair is the most informative object in the store, and it is what the signal-request
/// channel exists for.</b> Two setups near-identical on every recorded signal whose ten-day
/// outcomes differ by more than the threshold are a statement that something the lab does not
/// measure decided the result. The model is not asked which one wins; it is asked what it would
/// want to see.
///
/// <b>The window cannot be filled and the run says so rather than pretending otherwise.</b> The
/// metric standardises each signal over the trailing 250 setups. The store holds a small fraction
/// of that and will for months, so every run records how many setups its window actually held
/// beside the number it wanted. A z-score over forty rows and a z-score over two hundred and fifty
/// are different quantities, and a column carrying only the first would make them look alike.
/// see: The twin-pair threshold is reviewed at the first full window rather than at a phase
///
/// <b>Neither threshold moves here, and that is a decision rather than a deferral.</b> A review
/// taken against a window shorter than the one the values were set for is a review in name, so the
/// review point is the first full window and this stage is what lets it be seen approaching.
///
/// <b>A rerun of a date is a new generation beside the old, never a rewrite.</b> The window grows
/// as outcomes fill, so a second run of one date can honestly produce a different answer. The stale
/// generation stays as it stood, because it is what a person saw
/// (see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood).
///
/// <b>One side at a time.</b> The outcome is signed by direction, so a long that rose twelve points
/// and a short that fell twelve points both read the same way; a pair drawn across the two sides
/// could differ by twenty points with both names having done the same thing.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class TwinPairFinder
{
    public const string Name = "twin-pairs";

    /// <summary>A window under two setups forms no pair at all, which is not the same as finding none.</summary>
    public const string WindowTooThin = "the window held fewer than two setups with a closed outcome, so no pair could be formed";

    /// <summary>
    /// No signal is carried as a number by every setup in the window, so there is no space to take a
    /// distance in. Different from finding no twins, and only this one is a gap in the evidence.
    /// </summary>
    public const string NoCommonSignals =
        "no signal is carried as a number by every setup in the window, so there were no axes to take a distance across";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public TwinPairFinder(
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

    /// <summary><c>twin-pairs [as-of]</c>.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        TwinPairResult result = Find(asOf);

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, window wants {TwinPairs.WindowSetups} setup(s), "
            + $"threshold distance under {TwinPairs.MaximumDistance.ToString("0.##", CultureInfo.InvariantCulture)} "
            + $"and outcomes over {TwinPairs.MinimumOutcomeGapPoints.ToString("0.#", CultureInfo.InvariantCulture)} points apart");

        // Per side and never added, on both lines, because the whole figure is a property of one
        // side's population.
        foreach (TwinPairSide side in result.Sides)
        {
            Console.WriteLine(
                $"{Name}: {side.Direction} window held {side.WindowSetups} of {TwinPairs.WindowSetups}, "
                + $"{side.SignalsCompared} signal(s) compared, {side.CandidatePairs} candidate pair(s), "
                + $"{side.PairsFound} twin(s)");

            if (side.EmptyBecause is string why)
            {
                Console.WriteLine($"{Name}: {side.Direction} found none, {why}");
            }
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One pass, once per side.</summary>
    public TwinPairResult Find(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "twin_pair");

        DateTimeOffset observedAt = run.StartedAt;
        var sides = new List<TwinPairSide>();

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string direction in (string[])[SetupDirection.Long, SetupDirection.Short])
            {
                sides.Add(FindOneSide(connection, transaction, direction, asOf, observedAt));
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new TwinPairResult(asOf, sides, summary.RowsWritten, RunOutcome.Clean);
    }

    private TwinPairSide FindOneSide(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string direction,
        DateOnly asOf,
        DateTimeOffset observedAt)
    {
        IReadOnlyList<ScoredSetup> scored = ScoredSetupReader.Read(connection, direction, asOf, _options.SessionZone);

        // The trailing window, taken from the end of a list the reader returns in session order with
        // the setup id as the tiebreak, so the same window comes back on both machines. Where the
        // store holds fewer than the window wants, the window is what it holds and the row says so.
        IReadOnlyList<ScoredSetup> window =
            scored.Count <= TwinPairs.WindowSetups ? scored : [.. scored.Skip(scored.Count - TwinPairs.WindowSetups)];

        IReadOnlyList<string> axes =
            ScoredSetupReader.CommonSignals(window, [.. SignalLibrary.Active.Select(s => s.Name)]);

        long candidates = TwinPairs.CandidatePairs(window.Count);

        if (window.Count < 2)
        {
            return Record(connection, transaction, direction, asOf, observedAt,
                window.Count, 0, candidates, [], WindowTooThin);
        }

        if (axes.Count == 0)
        {
            return Record(connection, transaction, direction, asOf, observedAt,
                window.Count, 0, candidates, [], NoCommonSignals);
        }

        IReadOnlyList<TwinPair> pairs = TwinPairs.Find(
            [.. window.Select(s => s.SetupId)],
            [.. window.Select(s => (IReadOnlyList<double>)[.. axes.Select(a => s.Values[a])])],
            [.. window.Select(s => s.Outcome)]);

        // A run that found nothing over a real window is a finding rather than a gap, and it says
        // which it was: the thresholds were asked and refused every pair.
        string? empty = pairs.Count > 0
            ? null
            : $"no pair among {candidates} candidate(s) over a window of {window.Count} was both closer than "
              + $"{TwinPairs.MaximumDistance.ToString("0.##", CultureInfo.InvariantCulture)} across {axes.Count} "
              + $"signal(s) and further apart than {TwinPairs.MinimumOutcomeGapPoints.ToString("0.#", CultureInfo.InvariantCulture)} points";

        return Record(connection, transaction, direction, asOf, observedAt,
            window.Count, axes.Count, candidates, pairs, empty);
    }

    private static TwinPairSide Record(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string direction,
        DateOnly asOf,
        DateTimeOffset observedAt,
        int windowSetups,
        int signalsCompared,
        long candidates,
        IReadOnlyList<TwinPair> pairs,
        string? emptyBecause)
    {
        foreach (TwinPair pair in pairs)
        {
            InsertPair(connection, transaction, pair, direction, asOf, windowSetups, observedAt);
        }

        InsertRun(connection, transaction, direction, asOf, windowSetups, signalsCompared,
            candidates, pairs.Count, emptyBecause, observedAt);

        return new TwinPairSide(direction, windowSetups, signalsCompared, candidates, pairs.Count, emptyBecause);
    }

    private static void InsertPair(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TwinPair pair,
        string direction,
        DateOnly asOf,
        int windowSetups,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO twin_pair (pair_id, as_of, direction, left_setup_id, right_setup_id,
                                   distance, gap_points, left_outcome, right_outcome,
                                   signals_compared, window_setups, observed_at)
            VALUES (@pair_id, @as_of, @direction, @left, @right, @distance, @gap, @left_outcome,
                    @right_outcome, @signals, @window, @observed_at)
            """;

        command.Parameters.AddWithValue("@pair_id", pair.PairId);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@left", pair.Left);
        command.Parameters.AddWithValue("@right", pair.Right);
        command.Parameters.AddWithValue("@distance", StoreText.StatisticToStorageText(pair.Distance));
        command.Parameters.AddWithValue("@gap", StoreText.StatisticToStorageText(pair.GapPoints));
        command.Parameters.AddWithValue("@left_outcome", StoreText.StatisticToStorageText(pair.LeftOutcome));
        command.Parameters.AddWithValue("@right_outcome", StoreText.StatisticToStorageText(pair.RightOutcome));
        command.Parameters.AddWithValue("@signals", pair.SignalsCompared);
        command.Parameters.AddWithValue("@window", windowSetups);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        command.ExecuteNonQuery();
    }

    private static void InsertRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string direction,
        DateOnly asOf,
        int windowSetups,
        int signalsCompared,
        long candidates,
        int pairsFound,
        string? emptyBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO twin_run (as_of, direction, window_setups, window_wanted, signals_compared,
                                  candidate_pairs, pairs_found, empty_because, outcome, observed_at)
            VALUES (@as_of, @direction, @window, @wanted, @signals, @candidates, @found,
                    @empty_because, @outcome, @observed_at)
            ON CONFLICT (as_of, direction, observed_at) DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@window", windowSetups);
        command.Parameters.AddWithValue("@wanted", TwinPairs.WindowSetups);
        command.Parameters.AddWithValue("@signals", signalsCompared);
        command.Parameters.AddWithValue("@candidates", candidates);
        command.Parameters.AddWithValue("@found", pairsFound);
        command.Parameters.AddWithValue("@empty_because", (object?)emptyBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@outcome", RunOutcome.Clean.ToStorageText());
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        command.ExecuteNonQuery();
    }
}

/// <summary>
/// What one side's search looked at and what it found.
///
/// <c>WindowSetups</c> against <c>CandidatePairs</c> is the pair of figures that makes
/// <c>PairsFound</c> readable: nought twins over four setups and nought over two hundred are
/// different statements, and only the second says anything about the thresholds.
/// </summary>
public sealed record TwinPairSide(
    string Direction,
    int WindowSetups,
    int SignalsCompared,
    long CandidatePairs,
    int PairsFound,
    string? EmptyBecause);

/// <summary>
/// What one run did, as a list of sides rather than a total.
///
/// There is no figure here over both directions, and that is structural rather than remembered: a
/// twin pair is a comparison inside one side's population and a count over both would be two
/// populations under one name (see: Long and short are never pooled into one figure).
/// </summary>
public sealed record TwinPairResult(
    DateOnly AsOf,
    IReadOnlyList<TwinPairSide> Sides,
    int RowsWritten,
    RunOutcome Outcome);
