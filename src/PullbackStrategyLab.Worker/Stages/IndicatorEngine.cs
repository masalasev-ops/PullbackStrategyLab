using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The averages, computed locally from stored bars and never requested from the vendor. Asking
/// the provider for the same numbers would cost about 45,000 calls a day for arithmetic that is
/// one recursive loop over data already held.
/// see: Averages are computed locally, never through the vendor's technical endpoint
///
/// It refuses rather than approximates, in two cases, and both leave no row rather than a
/// number. A ticker whose window is shorter than the warm-up has not converged: a 50-day
/// exponential average seeded fifty sessions ago is still carrying its seed. A ticker with a
/// corporate action outstanding is worse, because its stored adjusted closes are on two
/// different scales and the average across the boundary is arithmetic on two different units.
/// Both produce a number that looks entirely reasonable and is wrong, which is the one thing
/// this design will not write down.
/// see: An unprocessed corporate action of any kind blocks calculation, not only a split
///
/// A demand is satisfied by a recorded refetch of that ticker made after the action was
/// observed. Not by inferring one from what the refetch changed, which fails in both directions
/// and does so quietly (see: A rebuild is satisfied by a recorded refetch, not by inferring one from what changed).
/// </summary>
public sealed class IndicatorEngine
{
    public const string Name = "indicators";

    public const int EmaShortPeriod = 9;
    public const int EmaMediumPeriod = 21;
    public const int EmaLongPeriod = 50;

    /// <summary>Wilder's period for the true range average, which is what ATR has always meant.</summary>
    public const int AtrPeriod = 14;

    /// <summary>The window the daily range, the range average and the median dollar volume are taken over.</summary>
    public const int RangeWindow = 20;

    /// <summary>
    /// 150 sessions. RUNBOOK states it as the warm-up depth and gives the reason: a 50-day
    /// exponential average needs roughly three times its period to converge, so a value computed
    /// over fewer sessions is still carrying the seed it started from.
    ///
    /// A ticker short of this gets no row. The alternative is a number that is wrong by an amount
    /// nobody can see and that shrinks as the window grows, which is worse than a gap because a
    /// gap is visible.
    /// </summary>
    public const int WarmupSessions = 150;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public IndicatorEngine(
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

        IndicatorResult result = Compute(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Members} universe member(s)");
        Console.WriteLine($"{Name}: {result.Computed} computed, {result.AlreadyWritten} already written, {result.ShortOfWarmup} short of the {WarmupSessions}-session warm-up, {result.Blocked} blocked by an open demand");
        Console.WriteLine($"{Name}: {result.DemandsSatisfied} demand(s) satisfied");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public IndicatorResult Compute(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "indicator_daily", "indicator_rebuild");

        DateTimeOffset computedAt = run.StartedAt;

        IReadOnlyList<string> members = ReadUniverse(connection, asOf);
        ILookup<string, RebuildDemand> openDemands = IndicatorRebuildReader.Open(connection, asOf)
            .ToLookup(d => d.Ticker, StringComparer.Ordinal);

        // Bounded by the end of the as-of date like every other read here, so a replay of a night
        // sees the refetches the lab had made by then and not the ones it made afterwards.
        IReadOnlyDictionary<string, DateTimeOffset> refetchedAt =
            HistoryRefetchReader.LatestByTicker(connection, EndOf(asOf));

        int computed = 0;
        int alreadyWritten = 0;
        int shortOfWarmup = 0;
        int blocked = 0;
        int satisfied = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string ticker in members)
            {
                IReadOnlyList<StoredDailyBar> window = DailyBarReader.Read(connection, ticker, asOf, WarmupSessions);

                if (window.Count < WarmupSessions)
                {
                    shortOfWarmup++;
                    continue;
                }

                // When this name's series was last put on one basis, or nothing at all if it never
                // has been. A demand observed after that moment is one the window does not
                // account for.
                DateTimeOffset lastRefetch = refetchedAt.TryGetValue(ticker, out DateTimeOffset at)
                    ? at
                    : DateTimeOffset.MinValue;

                if (openDemands[ticker].Any(d => d.ObservedAt > lastRefetch))
                {
                    blocked++;
                    continue;
                }

                if (Insert(connection, transaction, ticker, asOf, Calculate(window)) == 0)
                {
                    alreadyWritten++;
                }
                else
                {
                    computed++;
                }

                // Every demand this window does account for is satisfied by it. Stamped rather
                // than cleared, so the record still says which actions this store has honoured
                // and when.
                satisfied += Stamp(connection, transaction, ticker, lastRefetch, computedAt);
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new IndicatorResult(
            asOf, members.Count, computed, alreadyWritten, shortOfWarmup, blocked, satisfied,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// The arithmetic, over one ticker's window, oldest bar first.
    ///
    /// Everything except the median dollar volume is computed on adjusted prices, because a
    /// split five years ago must not poison an average taken today. The store holds an adjusted
    /// close and raw open, high and low, so the high and the low are put on the adjusted basis
    /// through the bar's own factor, <c>adj_close / close</c>. Trigger and stop prices are raw
    /// and are not computed here; mixing the two produces a plan that says buy at 37.67 when the
    /// real price is 150.68, silently, because both look reasonable.
    ///
    /// The median dollar volume is the exception and is deliberately raw. It is what actually
    /// changed hands on the day, it is the figure UniverseBuilder screens on, and computing it
    /// two ways in two components would make the screen and the indicator disagree.
    /// </summary>
    public static IndicatorValues Calculate(IReadOnlyList<StoredDailyBar> window)
    {
        ArgumentNullException.ThrowIfNull(window);

        int n = window.Count;
        var adjustedClose = new decimal[n];
        var adjustedHigh = new decimal[n];
        var adjustedLow = new decimal[n];

        for (int i = 0; i < n; i++)
        {
            StoredDailyBar bar = window[i];
            decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;
            adjustedClose[i] = bar.AdjustedClose;
            adjustedHigh[i] = bar.High * factor;
            adjustedLow[i] = bar.Low * factor;
        }

        decimal[] tail = Enumerable.Range(n - RangeWindow, RangeWindow)
            .Select(i => adjustedHigh[i] - adjustedLow[i])
            .ToArray();

        // A fraction, not a percentage: 0.068 rather than 6.8. It is also the one figure here a
        // corporate action cannot corrupt, because the factor cancels top and bottom, and it is
        // still withheld from a blocked ticker rather than written beside six numbers that are
        // wrong.
        decimal adr = Enumerable.Range(n - RangeWindow, RangeWindow)
            .Select(i => (adjustedHigh[i] - adjustedLow[i]) / adjustedClose[i])
            .Sum() / RangeWindow;

        decimal[] dollarVolume = Enumerable.Range(n - RangeWindow, RangeWindow)
            .Select(i => window[i].Close * window[i].Volume)
            .ToArray();

        return new IndicatorValues(
            ExponentialAverage(adjustedClose, EmaShortPeriod),
            ExponentialAverage(adjustedClose, EmaMediumPeriod),
            ExponentialAverage(adjustedClose, EmaLongPeriod),
            AverageTrueRange(adjustedHigh, adjustedLow, adjustedClose, AtrPeriod),
            adr,
            Median(dollarVolume),
            tail.Sum() / RangeWindow);
    }

    /// <summary>
    /// The exponential moving average, seeded on the simple average of the first
    /// <paramref name="period"/> values and then recursive.
    ///
    /// The seed is a choice rather than a law and it is stated here because it is the single
    /// most common reason two correct implementations disagree. Seeding on the first value
    /// instead converges to the same place and differs for a long time on the way, which is
    /// exactly the sort of difference that is invisible in a chart and fatal in a comparison.
    /// </summary>
    public static decimal ExponentialAverage(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Count, period);

        decimal average = 0m;
        for (int i = 0; i < period; i++)
        {
            average += values[i];
        }

        average /= period;

        decimal multiplier = 2m / (period + 1);
        for (int i = period; i < values.Count; i++)
        {
            average += (values[i] - average) * multiplier;
        }

        return average;
    }

    /// <summary>
    /// Wilder's average true range. True range is the greatest of the day's own range, the gap
    /// up from yesterday's close and the gap down to it, so a stock that opens ten percent away
    /// and does not move all day has a large true range and a small daily range.
    ///
    /// Wilder's smoothing, not an exponential average with the same period: they are different
    /// numbers and only one of them is what ATR has meant since 1978. The seed is the simple
    /// average of the first <paramref name="period"/> true ranges.
    /// </summary>
    public static decimal AverageTrueRange(
        IReadOnlyList<decimal> high,
        IReadOnlyList<decimal> low,
        IReadOnlyList<decimal> close,
        int period)
    {
        ArgumentNullException.ThrowIfNull(high);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentOutOfRangeException.ThrowIfLessThan(close.Count, period + 1);

        // The first bar has no previous close, so it has no true range. The series starts at the
        // second bar, which is why this needs one more session than its period.
        var trueRange = new decimal[close.Count - 1];
        for (int i = 1; i < close.Count; i++)
        {
            decimal previous = close[i - 1];
            decimal range = high[i] - low[i];
            decimal upGap = Math.Abs(high[i] - previous);
            decimal downGap = Math.Abs(low[i] - previous);
            trueRange[i - 1] = Math.Max(range, Math.Max(upGap, downGap));
        }

        decimal atr = 0m;
        for (int i = 0; i < period; i++)
        {
            atr += trueRange[i];
        }

        atr /= period;

        for (int i = period; i < trueRange.Length; i++)
        {
            atr = ((atr * (period - 1)) + trueRange[i]) / period;
        }

        return atr;
    }

    /// <summary>
    /// The median rather than the mean, for the reason UniverseBuilder takes the median: one
    /// earnings day at twenty times normal volume carries a name over a floor it does not
    /// otherwise clear.
    /// </summary>
    public static decimal Median(decimal[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            return 0m;
        }

        decimal[] sorted = [.. values];
        Array.Sort(sorted);

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }

    /// <summary>The last instant of a session, in the form observed_at is stored in.</summary>
    private static DateTimeOffset EndOf(DateOnly session) =>
        new(session.Year, session.Month, session.Day, 23, 59, 59, 999, TimeSpan.Zero);

    private static IReadOnlyList<string> ReadUniverse(SqliteConnection connection, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();

        // The snapshot rather than current membership, because that is what makes a replay free
        // of survivorship bias: a name delisted since is simply absent from today's list.
        command.CommandText = """
            SELECT ticker FROM universe_snapshot WHERE as_of = @as_of ORDER BY ticker;
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateOnly asOf,
        IndicatorValues values)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. SCHEMA declares IndicatorEngine as the inserter of this table and
        // TierClassifier as its only updater, on ladder_grade alone, so an upsert here would give
        // this component an undeclared update on a table somebody else owns. A night already
        // written therefore stands, and the run reports how many it left alone rather than
        // overwriting them quietly.
        command.CommandText = """
            INSERT INTO indicator_daily
                (ticker, as_of, ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20)
            VALUES (@ticker, @as_of, @ema_9, @ema_21, @ema_50, @atr_14, @adr_20, @dollar_volume_median_20, @range_avg_20)
            ON CONFLICT (ticker, as_of) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ema_9", StoreText.PriceToStorageText(values.EmaShort));
        command.Parameters.AddWithValue("@ema_21", StoreText.PriceToStorageText(values.EmaMedium));
        command.Parameters.AddWithValue("@ema_50", StoreText.PriceToStorageText(values.EmaLong));
        command.Parameters.AddWithValue("@atr_14", StoreText.PriceToStorageText(values.AverageTrueRange));
        command.Parameters.AddWithValue("@adr_20", StoreText.RatioToStorageText(values.AverageDailyRange));
        command.Parameters.AddWithValue("@dollar_volume_median_20", StoreText.PriceToStorageText(values.DollarVolumeMedian));
        command.Parameters.AddWithValue("@range_avg_20", StoreText.PriceToStorageText(values.RangeAverage));
        return command.ExecuteNonQuery();
    }

    private static int Stamp(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateTimeOffset lastRefetch,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // rebuilt_at and nothing else, which is what SCHEMA declares this component may write on
        // this table. The row itself stays: the question worth answering months from now is which
        // actions this store has honoured and when, and a queue that empties cannot answer it.
        command.CommandText = """
            UPDATE indicator_rebuild
               SET rebuilt_at = @computed_at
             WHERE ticker = @ticker
               AND rebuilt_at IS NULL
               AND observed_at <= @last_refetch;
            """;
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@last_refetch", StoreText.TimestampToStorageText(lastRefetch));
        return command.ExecuteNonQuery();
    }
}

/// <summary>
/// One session's indicators for one ticker. Prices are decimal, and the daily range is a
/// fraction rather than a percentage.
/// </summary>
public sealed record IndicatorValues(
    decimal EmaShort,
    decimal EmaMedium,
    decimal EmaLong,
    decimal AverageTrueRange,
    decimal AverageDailyRange,
    decimal DollarVolumeMedian,
    decimal RangeAverage);

public sealed record IndicatorResult(
    DateOnly AsOf,
    int Members,
    int Computed,
    int AlreadyWritten,
    int ShortOfWarmup,
    int Blocked,
    int DemandsSatisfied,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
