using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
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

    /// <summary>Both scores at +1.</summary>
    public const string RiskOn = "risk_on";

    /// <summary>Anything in between, which is most nights.</summary>
    public const string Mixed = "mixed";

    /// <summary>Both scores at -1.</summary>
    public const string RiskOff = "risk_off";

    /// <summary>The average each tracker is measured against.</summary>
    public const int IndexAveragePeriod = 21;

    /// <summary>
    /// Sessions of tracker history read. The engine's warm-up, so the 21-day average here is seeded
    /// where every other average in the lab is seeded rather than wherever the window happened to
    /// start. Two averages differing only in their seed converge to the same place and differ for a
    /// long time on the way, and both look like a moving average.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public const int HistorySessions = IndicatorEngine.WarmupSessions;

    /// <summary>Above this ratio of long-ladder names to short-ladder names, breadth scores +1.</summary>
    public const decimal BreadthUpper = 1.5m;

    /// <summary>Below this ratio, breadth scores -1.</summary>
    public const decimal BreadthLower = 0.67m;

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

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.IndexesAbove} of {result.IndexesMeasured} tracker(s) above their {IndexAveragePeriod}-day average");
        Console.WriteLine($"{Name}: {result.LongLadderCount} rising, {result.ShortLadderCount} falling");
        Console.WriteLine($"{Name}: index {result.IndexScore:+0;-0;0}, breadth {result.BreadthScore:+0;-0;0}, label {result.Label}");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public RegimeResult Label(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "regime_daily");

        int above = 0;
        int measured = 0;

        foreach (string symbol in _options.IndexSymbols)
        {
            IReadOnlyList<StoredDailyBar> bars = IndexBarReader.Read(connection, symbol, asOf, HistorySessions);

            if (bars.Count < HistorySessions || bars[^1].BarDate != asOf)
            {
                // A tracker without a full window is not measured, rather than measured as below.
                // Counting it as below would move the score toward risk-off on exactly the nights
                // the data is thin, which is a bias rather than a missing value.
                continue;
            }

            measured++;
            decimal average = Averages.Exponential([.. bars.Select(b => b.AdjustedClose)], IndexAveragePeriod);

            if (bars[^1].AdjustedClose > average)
            {
                above++;
            }
        }

        (int longLadder, int shortLadder) = LadderCounts(connection, asOf);

        int indexScore = IndexScore(above, measured);
        int breadthScore = BreadthScore(longLadder, shortLadder);
        string label = LabelFor(indexScore, breadthScore);

        int written = Insert(connection, asOf, indexScore, breadthScore, label, longLadder, shortLadder, above);
        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new RegimeResult(
            asOf, measured, above, longLadder, shortLadder, indexScore, breadthScore, label,
            written, summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>
    /// +1 when every tracker closed above its own average, -1 when none did, 0 otherwise.
    ///
    /// Pure, and it takes how many were measured rather than assuming three. With no tracker
    /// measurable the answer is 0 and not -1: "none of nothing was above" is not the same statement
    /// as "none of three was above", and scoring it -1 would read a missing feed as a falling market.
    /// </summary>
    public static int IndexScore(int above, int measured)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(above);
        ArgumentOutOfRangeException.ThrowIfNegative(measured);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(above, measured);

        if (measured == 0)
        {
            return 0;
        }

        return above == measured ? 1 : above == 0 ? -1 : 0;
    }

    /// <summary>
    /// +1 above 1.5, -1 below 0.67, 0 between, on the ratio of rising names to falling ones.
    ///
    /// With no falling names the ratio is undefined and the answer is +1 rather than a division by
    /// zero: every name that laddered at all laddered upward, which is the strongest reading of the
    /// score there is. With neither the answer is 0, because nothing laddered either way.
    /// </summary>
    public static int BreadthScore(int longLadder, int shortLadder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(longLadder);
        ArgumentOutOfRangeException.ThrowIfNegative(shortLadder);

        if (shortLadder == 0)
        {
            return longLadder == 0 ? 0 : 1;
        }

        decimal ratio = (decimal)longLadder / shortLadder;
        return ratio > BreadthUpper ? 1 : ratio < BreadthLower ? -1 : 0;
    }

    /// <summary>The label from the sum, which is why the three states buffer themselves.</summary>
    public static string LabelFor(int indexScore, int breadthScore) =>
        (indexScore + breadthScore) switch
        {
            2 => RiskOn,
            -2 => RiskOff,
            _ => Mixed,
        };

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
