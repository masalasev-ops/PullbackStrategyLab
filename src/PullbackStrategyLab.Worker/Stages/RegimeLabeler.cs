using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// One market-mood label a night, from two scores summed.
///
/// <b>It filters nothing.</b> The label is recorded against every setup and gates no decision in the
/// baseline, which is what keeps it available as a clean experiment: baking it in now would be an
/// untested assumption, and adding it later as a version is a measurement. A test asserts no other
/// component reads it as a condition, because a filter is exactly what this would silently become.
/// see: The market-mood label is recorded on every setup and filters nothing in the baseline
///
/// The three-state form buffers itself: risk-on needs both scores at +1 and risk-off needs both at
/// -1, so the label cannot go from one to the other without passing through mixed. Breadth costs
/// nothing because it falls out of the ladder grades that already ran.
///
/// No vendor call. Both inputs are already stored.
/// </summary>
public sealed class RegimeLabeler
{
    public const string Name = "regime";

    /// <summary>
    /// The scoring lives in <see cref="MarketMood"/>, and this stage is the nightly reader of it.
    ///
    /// What is here is where the two counts come from, being a query against the latest observation
    /// of each name's session. What is there is everything downstream of those counts, because a
    /// calibration walk has the same arithmetic to do over counts it holds in memory.
    /// </summary>
    /// <summary>
    /// Sessions of tracker history read. The engine's warm-up, so the 21-day average here is seeded
    /// where every other average in the lab is seeded rather than wherever the window happened to
    /// start. Two averages differing only in their seed converge to the same place and differ for a
    /// long time on the way, and both look like a moving average.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public const int HistorySessions = IndicatorEngine.WarmupSessions;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public RegimeLabeler(
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

        RegimeResult result = Label(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.IndexesAbove} of {result.IndexesMeasured} tracker(s) above their {MarketMood.IndexAveragePeriod}-day average");
        Console.WriteLine($"{Name}: {result.LongLadderCount} rising, {result.ShortLadderCount} falling");
        Console.WriteLine($"{Name}: index {result.IndexScore:+0;-0;0}, breadth {result.BreadthScore:+0;-0;0}, label {result.Label}");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public RegimeResult Label(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "regime_daily");

        // The trackers as this path reads them, handed to the one scoring implementation. The
        // measurability rule lives there rather than here, because a reconstructed session reads
        // the same index bars and has to apply the same rule to them.
        var trackers = new List<MarketMood.Tracker>();

        foreach (string symbol in _options.IndexSymbols)
        {
            IReadOnlyList<StoredDailyBar> bars = IndexBarReader.Read(connection, symbol, asOf, HistorySessions, _options.SessionZone);

            trackers.Add(new MarketMood.Tracker(
                [.. bars.Select(b => b.AdjustedClose)],
                bars.Count == 0 ? default : bars[^1].BarDate));
        }

        (int longLadder, int shortLadder) = LadderCounts(connection, asOf);

        MoodScore scored = MarketMood.Of(trackers, asOf, HistorySessions, longLadder, shortLadder);

        int written = Insert(
            connection, asOf, scored.IndexScore, scored.BreadthScore, scored.Label,
            scored.LongLadderCount, scored.ShortLadderCount, scored.IndexesAbove);
        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new RegimeResult(
            asOf, scored.IndexesMeasured, scored.IndexesAbove, scored.LongLadderCount,
            scored.ShortLadderCount, scored.IndexScore, scored.BreadthScore, scored.Label,
            written, summary.RowsWritten, RunOutcome.Clean);
    }

    private static (int Long, int Short) LadderCounts(SqliteConnection connection, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();

        // The latest observation of each name's session, which is the one carrying the grade.
        // Counting every observation would count a name once for the engine's row and again for
        // the grade row, and the second is the only one with a grade on it.
        command.CommandText = """
            SELECT d.ladder_grade, COUNT(*)
              FROM indicator_daily d
             WHERE d.as_of = @as_of
               AND d.computed_at = (SELECT MAX(l.computed_at) FROM indicator_daily l
                                     WHERE l.ticker = d.ticker AND l.as_of = d.as_of)
               AND d.ladder_grade IS NOT NULL
             GROUP BY d.ladder_grade
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        int rising = 0;
        int falling = 0;
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (reader.GetString(0) == TierClassifier.Rising)
            {
                rising = reader.GetInt32(1);
            }
            else if (reader.GetString(0) == TierClassifier.Falling)
            {
                falling = reader.GetInt32(1);
            }
        }

        return (rising, falling);
    }

    private static int Insert(
        SqliteConnection connection,
        DateOnly asOf,
        int indexScore,
        int breadthScore,
        string label,
        int longLadder,
        int shortLadder,
        int above)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO regime_daily
                (as_of, index_score, breadth_score, label, long_ladder_count, short_ladder_count, indexes_above)
            VALUES (@as_of, @index_score, @breadth_score, @label, @long_count, @short_count, @above)
            ON CONFLICT (as_of) DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@index_score", indexScore);
        command.Parameters.AddWithValue("@breadth_score", breadthScore);
        command.Parameters.AddWithValue("@label", label);
        command.Parameters.AddWithValue("@long_count", longLadder);
        command.Parameters.AddWithValue("@short_count", shortLadder);
        command.Parameters.AddWithValue("@above", above);

        return command.ExecuteNonQuery();
    }
}

/// <summary>What one labelling run decided, with both scores and both raw counts beside the label.</summary>
public sealed record RegimeResult(
    DateOnly AsOf,
    int IndexesMeasured,
    int IndexesAbove,
    int LongLadderCount,
    int ShortLadderCount,
    int IndexScore,
    int BreadthScore,
    string Label,
    int Written,
    int RowsWritten,
    RunOutcome Outcome);
