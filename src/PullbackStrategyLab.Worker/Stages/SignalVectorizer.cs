using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Freezes the point-in-time signal row: every number the decision depended on, copied into a
/// row that is never updated.
///
/// The property this exists for is that the row cannot move. Months later a replay sees exactly
/// what was knowable on the night and nothing that arrived afterwards, which is what makes a
/// re-filtering of stored history a replay rather than a backtest. A signal whose value could be
/// revised would leave every comparison quietly meaningless, so the write is once and the store
/// enforces it: the primary key is (setup_id, signal_name), and a rerun writes only the signals a
/// setup does not already carry.
///
/// <b>The library is larger than what this can freeze today, and that is recorded rather than
/// hidden.</b> A signal can only be frozen once something stores what it reads, and phase 2 builds
/// those producers over several checkpoints: the scans arrive at 2.3, the ladder grade at 2.4, the
/// market mood at 2.5, and the pullback geometry, the sector and the cluster count with the
/// detectors at 2.6. Every active signal in SCHEMA is therefore either written here or named in
/// <see cref="AwaitingCheckpoint"/> against the checkpoint that supplies it, and a test asserts the
/// partition covers the library exactly. A signal in neither list is one nobody noticed had no
/// producer, which is the failure this arrangement exists to prevent.
/// </summary>
public sealed class SignalVectorizer
{
    public const string Name = "vectorize";

    /// <summary>
    /// Sessions of history read per setup. The longest average is 50 and the engine's own warm-up
    /// is 150, so a gap average computed over anything shorter would be seeded in a different place
    /// from the number the engine stored and would differ for a long time on the way to the same
    /// answer. Stated as the engine's warm-up plus the gap window rather than as a round number.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public const int HistorySessions = 170;

    /// <summary>The window the squeeze test compares against, and the one the contraction test uses.</summary>
    public const int GapWindow = 20;

    /// <summary>
    /// Active signals this stage cannot freeze yet, each against the checkpoint that supplies what
    /// it reads. Asserted the way an out-of-scope claim is: the checkpoint has to exist in
    /// BUILD_PLAN and has to be one PROGRESS does not yet record, so an entry left here after its
    /// checkpoint lands is a checkpoint that shipped without coming back to it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AwaitingCheckpoint { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ladder_grade"] = "2.4",
            ["pullback_bars"] = "2.6",
            ["pullback_extreme"] = "2.6",
            ["retrace_depth"] = "2.6",
            ["closes_beyond_floor"] = "2.6",
            ["market_cap"] = "2.6",
            ["industry"] = "2.6",
            ["cluster_count"] = "2.6",
            ["regime_index_score"] = "2.5",
            ["regime_breadth_score"] = "2.5",
            ["regime_label"] = "2.5",
        };

    /// <summary>The signals this stage freezes, in the order the library lists them.</summary>
    public static IReadOnlyList<string> Frozen { get; } =
    [
        "close_adjusted",
        "ema_9_distance",
        "ema_21_distance",
        "ema_50_distance",
        "ema_gap_21_50",
        "ema_gap_21_50_avg_20",
        "adr_20",
        "atr_14",
        "range_avg_20",
        "range_today_over_avg",
        "trigger_price",
        "stop_price",
        "stop_distance_ranges",
        "trigger_distance_ranges",
        "dollar_volume_median_20",
        "listing_age_sessions",
        "thrust_scan",
        "thrust_rank",
        "thrust_session",
        "days_since_thrust",
        "thrust_magnitude",
        "thrust_size_in_ranges",
    ];

    /// <summary>
    /// The signals whose value is a count rather than a measurement.
    ///
    /// Named rather than inferred from the value. Inferring would read a price of 355.00 as a count
    /// because it happens to be whole, and would start rounding it differently the day it is not.
    /// A count is a fact about the signal, so it is declared beside the signal.
    /// </summary>
    public static IReadOnlySet<string> Counts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "thrust_rank",
        "days_since_thrust",
        "listing_age_sessions",
    };

    /// <summary>
    /// How far back the thrust check looks, in sessions. The check is "appeared on a mover scan
    /// within the last ten days", and the signals freeze which hit that was.
    /// </summary>
    public const int ThrustWindowSessions = 10;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SignalVectorizer(
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

        VectorizeResult result = Vectorize(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Setups} setup(s)");
        Console.WriteLine($"{Name}: {result.Written} signal(s) frozen, {result.AlreadyFrozen} already frozen, {result.Absent} absent for want of history");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public VectorizeResult Vectorize(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "setup_signal");

        DateTimeOffset computedAt = run.StartedAt;
        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, asOf);

        int written = 0;
        int alreadyFrozen = 0;
        int absent = 0;

        foreach (StoredSetup setup in setups)
        {
            IReadOnlySet<string> existing = SetupSignalReader.NamesFor(connection, setup.SetupId);
            IReadOnlyDictionary<string, string> values = Values(connection, setup, asOf);

            foreach (string name in Frozen)
            {
                if (existing.Contains(name))
                {
                    // Already frozen. Not rewritten and not compared: the row is the record of what
                    // was knowable, and a second look at it tonight is not new information.
                    alreadyFrozen++;
                    continue;
                }

                if (!values.TryGetValue(name, out string? value))
                {
                    // The history behind this setup is too short to compute it. Absent rather than
                    // zero, because a missing signal is meaningful and a zero is a number a rule
                    // could be built on.
                    absent++;
                    continue;
                }

                Insert(connection, setup.SetupId, name, value, computedAt);
                written++;
            }
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new VectorizeResult(
            asOf, setups.Count, written, alreadyFrozen, absent, summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>
    /// Every signal this stage can compute for one setup. A name absent from the result is one the
    /// stored history is too short for, which the caller records as absent rather than as zero.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Values(
        SqliteConnection connection,
        StoredSetup setup,
        DateOnly asOf)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The trade geometry, read back from the row the detector wrote rather than recomputed.
            // These are raw prices, because that is what trades tomorrow.
            ["trigger_price"] = StoreText.PriceToStorageText(setup.TriggerPrice),
            ["stop_price"] = StoreText.PriceToStorageText(setup.StopPrice),
            ["stop_distance_ranges"] = StoreText.RatioToStorageText(setup.StopDistanceRanges),
        };

        IReadOnlyList<StoredDailyBar> bars = DailyBarReader.Read(connection, setup.Ticker, asOf, HistorySessions);
        if (bars.Count == 0)
        {
            return values;
        }

        StoredDailyBar last = bars[^1];
        values["close_adjusted"] = StoreText.PriceToStorageText(last.AdjustedClose);

        StoredIndicators? indicators = IndicatorDailyReader.Read(connection, setup.Ticker, asOf, asOf);
        if (indicators is not null)
        {
            values["adr_20"] = StoreText.RatioToStorageText(indicators.AverageDailyRange);
            values["atr_14"] = StoreText.PriceToStorageText(indicators.AverageTrueRange);
            values["range_avg_20"] = StoreText.PriceToStorageText(indicators.RangeAverage);
            values["dollar_volume_median_20"] = StoreText.PriceToStorageText(indicators.DollarVolumeMedian);

            Distance(values, "ema_9_distance", last.AdjustedClose, indicators.EmaShort);
            Distance(values, "ema_21_distance", last.AdjustedClose, indicators.EmaMedium);
            Distance(values, "ema_50_distance", last.AdjustedClose, indicators.EmaLong);

            if (indicators.EmaLong != 0m)
            {
                values["ema_gap_21_50"] =
                    StoreText.RatioToStorageText((indicators.EmaMedium - indicators.EmaLong) / indicators.EmaLong);
            }

            if (indicators.RangeAverage != 0m)
            {
                decimal factor = last.Close == 0m ? 1m : last.AdjustedClose / last.Close;
                decimal today = (last.High - last.Low) * factor;
                values["range_today_over_avg"] = StoreText.RatioToStorageText(today / indicators.RangeAverage);
            }

            if (indicators.AverageDailyRange != 0m && last.Close != 0m)
            {
                decimal range = indicators.AverageDailyRange * last.Close;
                if (range != 0m)
                {
                    values["trigger_distance_ranges"] =
                        StoreText.RatioToStorageText(Math.Abs(setup.TriggerPrice - last.Close) / range);
                }
            }
        }

        // The gap between the two longer averages against its own recent average. Computed from the
        // bars through the shared arithmetic in Core rather than from twenty stored rows, because
        // the engine writes one row a session and a night it did not run leaves a hole a mean would
        // silently step over.
        string? gapAverage = GapAverage(bars);
        if (gapAverage is not null)
        {
            values["ema_gap_21_50_avg_20"] = gapAverage;
        }

        string? age = ListingAge(connection, setup.Ticker, asOf);
        if (age is not null)
        {
            values["listing_age_sessions"] = age;
        }

        Thrust(connection, setup, asOf, bars, indicators, values);

        return values;
    }

    /// <summary>
    /// The thrust: which mover scan put this name in play, when, and how big the move was.
    ///
    /// The most recent qualifying hit inside the window, and "qualifying" means on the side the
    /// setup is taken from. A long setup whose only recent hit was on the decliner scan has not had
    /// a thrust in the sense the check means, and freezing that hit would describe the opposite
    /// event. The window is measured in sessions from the stored bars rather than in calendar days,
    /// because ten calendar days back is eight sessions after a long weekend and the check is
    /// stated in sessions.
    ///
    /// The magnitude is read back from the row rather than recomputed. ScanEngine already did this
    /// arithmetic and storing it is what makes the rank auditable; recomputing here would put the
    /// same formula in two places in the one situation where a disagreement is invisible.
    /// </summary>
    private static void Thrust(
        SqliteConnection connection,
        StoredSetup setup,
        DateOnly asOf,
        IReadOnlyList<StoredDailyBar> bars,
        StoredIndicators? indicators,
        Dictionary<string, string> values)
    {
        // The window's near edge, taken from the bars so it is sessions rather than days.
        DateOnly from = bars.Count >= ThrustWindowSessions
            ? bars[^ThrustWindowSessions].BarDate
            : bars[0].BarDate;

        string[] side = string.Equals(setup.Direction, "long", StringComparison.Ordinal)
            ? ["gainer", "gapper", "leader"]
            : ["decliner", "gapdown", "laggard"];

        StoredScanHit? thrust = ScanHitReader.ForTicker(connection, setup.Ticker, asOf, from)
            .Where(hit => side.Contains(hit.Scan, StringComparer.Ordinal))
            .OrderByDescending(hit => hit.AsOf)
            .ThenBy(hit => hit.Rank)
            .FirstOrDefault();

        if (thrust is null)
        {
            // No qualifying hit. Absent rather than a placeholder: the thrust check will fail for
            // this setup and the signals say the same thing by not being there.
            return;
        }

        values["thrust_scan"] = thrust.Scan;
        values["thrust_rank"] = thrust.Rank.ToString(CultureInfo.InvariantCulture);
        values["thrust_session"] = StoreText.DateToStorageText(thrust.AsOf);
        values["thrust_magnitude"] = StoreText.RatioToStorageText(thrust.Magnitude);

        int sessions = bars.Count(bar => bar.BarDate > thrust.AsOf && bar.BarDate <= asOf);
        values["days_since_thrust"] = sessions.ToString(CultureInfo.InvariantCulture);

        if (indicators is not null && indicators.AverageDailyRange != 0m)
        {
            // The lever the computed ceiling moves on: a nineteen percent jump means something
            // different for a stock that travels seven percent a day than for one that travels
            // three.
            values["thrust_size_in_ranges"] =
                StoreText.RatioToStorageText(thrust.Magnitude / indicators.AverageDailyRange);
        }
    }

    private static void Distance(Dictionary<string, string> values, string name, decimal close, decimal average)
    {
        if (average == 0m)
        {
            return;
        }

        values[name] = StoreText.RatioToStorageText((close - average) / average);
    }

    private static string? GapAverage(IReadOnlyList<StoredDailyBar> bars)
    {
        decimal[] closes = [.. bars.Select(b => b.AdjustedClose)];

        IReadOnlyList<decimal?> medium = Averages.ExponentialSeries(closes, 21, IndicatorEngine.WarmupSessions);
        IReadOnlyList<decimal?> longer = Averages.ExponentialSeries(closes, 50, IndicatorEngine.WarmupSessions);

        var gaps = new List<decimal>();
        for (int i = 0; i < closes.Length; i++)
        {
            if (medium[i] is not decimal m || longer[i] is not decimal l || l == 0m)
            {
                continue;
            }

            gaps.Add((m - l) / l);
        }

        if (gaps.Count < GapWindow)
        {
            return null;
        }

        decimal total = 0m;
        for (int i = gaps.Count - GapWindow; i < gaps.Count; i++)
        {
            total += gaps[i];
        }

        return StoreText.RatioToStorageText(total / GapWindow);
    }

    private static string? ListingAge(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT first_seen FROM security WHERE ticker = @ticker";
        command.Parameters.AddWithValue("@ticker", ticker);

        if (command.ExecuteScalar() is not string firstSeen)
        {
            return null;
        }

        // Trading sessions rather than calendar days, because the floor it feeds is stated in
        // sessions and the two differ by two fifths.
        using SqliteCommand sessions = connection.CreateCommand();
        sessions.CommandText = """
            SELECT COUNT(DISTINCT bar_date) FROM daily_bar
             WHERE ticker = @ticker AND bar_date >= @first_seen AND bar_date <= @as_of
            """;
        sessions.Parameters.AddWithValue("@ticker", ticker);
        sessions.Parameters.AddWithValue("@first_seen", firstSeen);
        sessions.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        long count = Convert.ToInt64(sessions.ExecuteScalar(), CultureInfo.InvariantCulture);
        return count.ToString(CultureInfo.InvariantCulture);
    }

    private static void Insert(
        SqliteConnection connection,
        string setupId,
        string name,
        string value,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES (@setup_id, @signal_name, @value, @computed_at)
            """;

        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@signal_name", name);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        command.ExecuteNonQuery();
    }
}

/// <summary>What one vectorize run did.</summary>
public sealed record VectorizeResult(
    DateOnly AsOf,
    int Setups,
    int Written,
    int AlreadyFrozen,
    int Absent,
    int RowsWritten,
    RunOutcome Outcome);
