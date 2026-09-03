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
    public const int GapWindow = AverageGap.Window;

    /// <summary>
    /// Active signals this stage cannot freeze yet, each against the checkpoint that supplies what
    /// it reads. Asserted the way an out-of-scope claim is: the checkpoint has to exist in
    /// BUILD_PLAN and has to be one PROGRESS does not yet record, so an entry left here after its
    /// checkpoint lands is a checkpoint that shipped without coming back to it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AwaitingCheckpoint { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
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
        "ladder_grade",
        "thrust_scan",
        "thrust_rank",
        "thrust_session",
        "days_since_thrust",
        "thrust_magnitude",
        "thrust_size_in_ranges",
        "regime_index_score",
        "regime_breadth_score",
        "regime_label",
        "pullback_bars",
        "pullback_extreme",
        "retrace_depth",
        "closes_beyond_floor",
        "market_cap",
        "industry",
        "cluster_count",
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
        "regime_index_score",
        "regime_breadth_score",
        "pullback_bars",
        "closes_beyond_floor",
        "cluster_count",
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
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        // The trade geometry, read back from the row the detector wrote rather than recomputed.
        // These are raw prices, because that is what trades tomorrow.
        //
        // Added only where the detector recorded one. A setup whose geometry is degenerate has no
        // trigger, no stop and no give-up distance, and until 031 the column could not say so: the
        // detector wrote nought, this stage froze the nought into setup_signal, and setup_signal is
        // written once and never updated. The fixture's own 2026-08-24-INTC-short is the case, with
        // `exit-tight` recorded as failed with value null on the same row whose frozen signal said
        // stop_distance_ranges = 0. The rule fourteen lines below has always said what to do here:
        // absent rather than zero, because a missing signal is meaningful and a zero is a number a
        // rule could be built on.
        // see: A gate handed an absent or degenerate quantity fails rather than passing
        if (setup.TriggerPrice is decimal trigger)
        {
            values["trigger_price"] = StoreText.PriceToStorageText(trigger);
        }

        if (setup.StopPrice is decimal stop)
        {
            values["stop_price"] = StoreText.PriceToStorageText(stop);
        }

        if (setup.StopDistanceRanges is decimal giveUp)
        {
            values["stop_distance_ranges"] = StoreText.RatioToStorageText(giveUp);
        }

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
            if (indicators.LadderGrade is not null)
            {
                values["ladder_grade"] = indicators.LadderGrade;
            }

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
                    // Derived from the trigger, so it is absent wherever the trigger is. Computing
                    // it against a trigger the detector never set produced |0 - close| / range,
                    // which for a $150 name with a 4% daily range is about 25 ranges and reads as a
                    // very distant trigger rather than as no trigger at all.
                    if (setup.TriggerPrice is decimal triggerPrice)
                    {
                        values["trigger_distance_ranges"] =
                            StoreText.RatioToStorageText(Math.Abs(triggerPrice - last.Close) / range);
                    }
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
        Regime(connection, asOf, values);
        Shape(connection, setup, asOf, bars, indicators, values);
        TheName(connection, setup.Ticker, asOf, values);

        return values;
    }

    /// <summary>
    /// The pullback's shape, through the same Core geometry the detector decided on.
    ///
    /// One implementation, two callers, on the terms the averages already established. A second
    /// implementation here would eventually disagree with the detector, and the disagreement would
    /// be invisible: every one of these is a plausible small number whichever way it was computed,
    /// so the frozen evidence would quietly stop describing the decision it was frozen for.
    /// </summary>
    private static void Shape(
        SqliteConnection connection,
        StoredSetup setup,
        DateOnly asOf,
        IReadOnlyList<StoredDailyBar> bars,
        StoredIndicators? indicators,
        Dictionary<string, string> values)
    {
        if (!values.TryGetValue("thrust_session", out string? session))
        {
            return;
        }

        DateOnly thrustSession = StoreText.StorageTextToDate(session);
        int thrustIndex = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].BarDate == thrustSession)
            {
                thrustIndex = i;
                break;
            }
        }

        if (thrustIndex < 0)
        {
            return;
        }

        // The span the scan flags, from the scan this very method already froze. Read from `values`
        // rather than resolved again, because the frozen evidence has to describe the decision the
        // detector made and a second lookup could answer differently.
        if (!values.TryGetValue("thrust_scan", out string? thrustScan))
        {
            return;
        }

        bool isLong = string.Equals(setup.Direction, "long", StringComparison.Ordinal);
        PullbackGeometry.Bar[] shaped = [.. bars.Select(OnBothBases)];
        PullbackGeometry.Pullback? pullback =
            PullbackGeometry.Of(shaped, thrustIndex, ScanSpans.SessionsFor(thrustScan), isLong);

        if (pullback is null)
        {
            return;
        }

        values["pullback_bars"] = pullback.PullbackBars.ToString(CultureInfo.InvariantCulture);
        values["pullback_extreme"] = StoreText.PriceToStorageText(pullback.PullbackExtreme);

        if (pullback.RetraceDepth is decimal depth)
        {
            values["retrace_depth"] = StoreText.RatioToStorageText(depth);
        }

        // The floor is the 21-day average long and the 50-day short, which is the one place the two
        // check lists are not mirrors: held-floor reads the medium average and no-reclaim reads the
        // long one. Built from the bars through the same helper the detectors use, so the frozen
        // signal is the number the gate was decided on rather than a second computation of it.
        //
        // It no longer needs the stored figures. Those carry the average as at the setup date, which
        // is one point of a series, and comparing a dip against one point is the defect 3.11 fixed.
        values["closes_beyond_floor"] =
            PullbackGeometry.ClosesBeyondFloor(
                shaped, pullback, IndicatorEngine.FloorSeries(shaped, isLong), isLong)
                .ToString(CultureInfo.InvariantCulture);
    }

    private static PullbackGeometry.Bar OnBothBases(StoredDailyBar bar)
    {
        decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;
        return new PullbackGeometry.Bar(
            bar.Open * factor, bar.High * factor, bar.Low * factor, bar.AdjustedClose, bar.High, bar.Low);
    }

    /// <summary>
    /// What the lab knows about the security itself: its industry, its market cap, and how many
    /// same-industry names moved with it tonight.
    ///
    /// All three absent until SectorResolver has been asked about the name, and absent is the true
    /// answer rather than a placeholder. A cluster count of zero would say the name moved alone,
    /// which is a different statement from not knowing what industry it is in.
    /// </summary>
    private static void TheName(
        SqliteConnection connection,
        string ticker,
        DateOnly asOf,
        Dictionary<string, string> values)
    {
        // Through the reader, which bounds both on when the lookup was made. Read unbounded, these
        // two would freeze an industry and a capitalisation resolved after the night they are
        // evidence about, which is the point-in-time rule broken in the one row written to survive
        // it: everything else the lab can recompute, and a frozen signal is what nobody recomputes.
        if (SecurityReader.Industry(connection, ticker, asOf) is string industry)
        {
            values["industry"] = industry;
        }

        if (SecurityReader.MarketCap(connection, ticker, asOf) is decimal cap)
        {
            values["market_cap"] = StoreText.PriceToStorageText(cap);
        }

        if (values.TryGetValue("thrust_scan", out string? scan) && values.TryGetValue("thrust_session", out string? on))
        {
            using SqliteCommand cluster = connection.CreateCommand();
            cluster.CommandText =
                """
                SELECT cluster_count FROM scan_hit
                 WHERE ticker = @ticker AND as_of = @as_of AND scan = @scan
                   AND (observed_at <= @observed_before OR (observed_at IS NULL AND as_of = @as_of))
                """;
            cluster.Parameters.AddWithValue("@ticker", ticker);
            cluster.Parameters.AddWithValue("@as_of", on);
            cluster.Parameters.AddWithValue(
                "@observed_before", StoreText.EndOfSession(StoreText.StorageTextToDate(on), SessionBoundaries.UsEquities));
            cluster.Parameters.AddWithValue("@scan", scan);

            if (cluster.ExecuteScalar() is long count)
            {
                values["cluster_count"] = count.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// The market mood on the night, frozen on the setup and filtering nothing.
    ///
    /// Both raw scores as well as the label, so a later proposal can use the continuous form rather
    /// than the three buckets without recomputing it from bars that may since have been restated.
    /// </summary>
    private static void Regime(SqliteConnection connection, DateOnly asOf, Dictionary<string, string> values)
    {
        StoredRegime? regime = RegimeReader.Read(connection, asOf);

        if (regime is null)
        {
            return;
        }

        values["regime_index_score"] = regime.IndexScore.ToString(CultureInfo.InvariantCulture);
        values["regime_breadth_score"] = regime.BreadthScore.ToString(CultureInfo.InvariantCulture);
        values["regime_label"] = regime.Label;
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
    /// see: The thrust is the most recent qualifying hit inside the window, then rank, and the extreme clause of the order-price decision does not ship
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

    /// <summary>
    /// The mean gap between the two longer averages across the window.
    ///
    /// The arithmetic lives in Core because `averages-squeezing` decides on the same series and the
    /// check that decides and the signal that records the decision have to be one number.
    /// </summary>
    private static string? GapAverage(IReadOnlyList<StoredDailyBar> bars)
    {
        decimal[] closes = [.. bars.Select(b => b.AdjustedClose)];

        IReadOnlyList<decimal> gaps = AverageGap.Series(
            closes, IndicatorEngine.EmaMediumPeriod, IndicatorEngine.EmaLongPeriod, IndicatorEngine.WarmupSessions);

        return AverageGap.Average(gaps) is decimal average ? StoreText.RatioToStorageText(average) : null;
    }

    /// <summary>
    /// The listing age this setup's decision rested on, in sessions.
    ///
    /// Delegated rather than computed, because the short side's `tradable-shortable` check decides
    /// on this number and a signal that froze a different one would describe a decision nobody made.
    /// It did: this counted sessions since `security.first_seen`, which is when the universe build
    /// first saw the ticker, so it read 1 for every name on the fixture's only night while the check
    /// had cleared a floor of ninety.
    /// </summary>
    private static string? ListingAge(SqliteConnection connection, string ticker, DateOnly asOf) =>
        DailyBarReader.SessionsStored(connection, ticker, asOf)
            .ToString(CultureInfo.InvariantCulture);

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
