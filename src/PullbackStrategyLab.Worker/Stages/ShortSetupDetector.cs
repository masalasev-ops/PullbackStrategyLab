using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The short bounce pattern: ten checks, every result recorded, pass or fail.
///
/// The mirror of the long detector and deliberately its own type, because three of the ten checks
/// are not sign flips and the corpus says so: <c>tradable-shortable</c> carries four floors where
/// the long side has two, <c>averages-squeezing</c> has no long-side counterpart at all, and
/// <c>reached-ceiling</c> asks whether a bounce arrived at a level rather than whether a dip held
/// one. What genuinely is a sign flip is read out of the shared geometry with <c>isLong: false</c>.
/// see: Two directions are tested, with separate detectors, separate management and separate scoring
///
/// <b>This detector may never write a long row.</b> The two share one table, separated by direction
/// and by nothing else, and the store's own check constrains the column while a test asserts the
/// disjointness in both directions.
///
/// <b>The borrow assumption rides on every row it writes.</b> Whether a name could actually be
/// borrowed that morning is not in the price feed, so <c>tradable-shortable</c> stands in for the
/// information and every short result inherits that substitution. It is why short and long figures
/// are never pooled.
/// see: The short borrow problem is mitigated by a filter, not solved
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class ShortSetupDetector
{
    public const string Name = "detect-short";

    /// <summary>The flag that sends a run to the calibration table and off the nightly snapshot.</summary>
    public const string CalibrateFlag = "--calibrate";

    /// <summary>The direction this detector owns, and the only one it may ever write.</summary>
    public const string Direction = "short";

    /// <summary>
    /// The recording floor: the premise, not the first four rows of the list.
    ///
    /// The long side's floor is its first four checks and that is a coincidence of ordering rather
    /// than the rule. What the floor holds is the premise a recorded setup rests on: the name can be
    /// traded, it moves enough to be worth trading, it is in the trend the pattern needs, and
    /// something happened. On this list those four are positions one, two, three and five, because
    /// <c>averages-squeezing</c> sits fourth and belongs to the pattern test the way
    /// <c>contraction</c> does on the long side.
    /// </summary>
    public static IReadOnlyList<string> RecordingFloor { get; } =
        ["tradable-shortable", "moves-enough", "downtrend", "thrust"];

    /// <summary>The downward mover scans. A short thrust is a fall, not a rise.</summary>
    public static IReadOnlyList<string> ThrustScans { get; } = ["decliner", "gapdown", "laggard"];

    /// <summary>Sessions of history read per name: the warm-up, plus the window the gap average needs.</summary>
    public const int HistorySessions = LongSetupDetector.HistorySessions;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ShortSetupDetector(
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

        if (args.Contains(CalibrateFlag))
        {
            string[] dates = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
            if (dates.Length < 2)
            {
                Console.Error.WriteLine($"{Name}: {CalibrateFlag} needs a from date and a to date");
                return 2;
            }

            CalibrationResult calibration = Calibrate(Date(dates[0]), Date(dates[1]));

            Console.WriteLine($"{Name}: calibration {calibration.From:yyyy-MM-dd} to {calibration.To:yyyy-MM-dd}, {calibration.Sessions} session(s)");
            Console.WriteLine($"{Name}: {calibration.Recorded} recorded, {calibration.PassedAll} passing every gating check");
            Console.WriteLine($"{Name}: {calibration.Outcome.ToStorageText()}, {calibration.RowsWritten} rows into calibration_setup");

            return calibration.Outcome == RunOutcome.Failed ? 1 : 0;
        }

        DateOnly asOf = args.Length > 0
            ? Date(args[0])
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        DetectResult result = Detect(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Members} member(s), {result.Examined} examined");
        Console.WriteLine($"{Name}: {result.Recorded} recorded, {result.PassedAll} passing every gating check, {result.BelowFloor} below the recording floor");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    private static DateOnly Date(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One forward night, into the evidence store.</summary>
    public DetectResult Detect(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, SetupReader.SetupTable);

        IReadOnlyList<string> members = UniverseSnapshotReader.Members(connection, asOf);
        Tally tally = Walk(connection, members, asOf, SetupReader.SetupTable);

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new DetectResult(
            asOf, members.Count, tally.Examined, tally.Recorded, tally.PassedAll, tally.BelowFloor,
            summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>A range of past sessions, into the calibration store, against today's membership.</summary>
    public CalibrationResult Calibrate(DateOnly from, DateOnly to)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, SetupReader.CalibrationTable);

        IReadOnlyList<string> members = UniverseSnapshotReader.CurrentMembers(connection);
        IReadOnlyList<DateOnly> sessions = SessionsBetween(connection, from, to);

        int recorded = 0;
        int passedAll = 0;

        foreach (DateOnly session in sessions)
        {
            Tally tally = Walk(connection, members, session, SetupReader.CalibrationTable);
            recorded += tally.Recorded;
            passedAll += tally.PassedAll;
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new CalibrationResult(
            from, to, sessions.Count, members.Count, recorded, passedAll, summary.RowsWritten, RunOutcome.Clean);
    }

    private Tally Walk(SqliteConnection connection, IReadOnlyList<string> members, DateOnly asOf, string table)
    {
        int examined = 0;
        int recorded = 0;
        int passedAll = 0;
        int belowFloor = 0;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string ticker in members)
        {
            ShortPullbackRules.ShortEvidence? evidence = Evidence(connection, ticker, asOf);
            if (evidence is null)
            {
                continue;
            }

            examined++;
            IReadOnlyList<CheckResult> results = ShortPullbackRules.Evaluate(evidence);

            if (!ClearsRecordingFloor(results))
            {
                belowFloor++;
                continue;
            }

            bool all = SetupChecks.PassedAll(results);
            if (all)
            {
                passedAll++;
            }

            recorded += Insert(connection, transaction, table, ticker, asOf, results, all, evidence);
        }

        transaction.Commit();
        return new Tally(examined, recorded, passedAll, belowFloor);
    }

    /// <summary>Whether the premise checks all passed, which is what decides a name is worth recording.</summary>
    public static bool ClearsRecordingFloor(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return RecordingFloor.All(name => results.Any(r => r.Name == name && r.Passed));
    }

    /// <summary>
    /// What the night knew about one name, or null where it has no bar for the session.
    ///
    /// Public for the same reason the long side's is: the replay authors cases the captured data
    /// cannot produce and has to author them from the evidence the detector would have used.
    /// </summary>
    public static ShortPullbackRules.ShortEvidence? Evidence(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        IReadOnlyList<StoredDailyBar> bars = DailyBarReader.Read(connection, ticker, asOf, HistorySessions);

        if (bars.Count == 0 || bars[^1].BarDate != asOf)
        {
            return null;
        }

        StoredDailyBar last = bars[^1];
        StoredIndicators? figures = IndicatorDailyReader.Read(connection, ticker, asOf, asOf);

        // The thrust: the most recent hit on a downward mover scan inside the window.
        DateOnly windowStart = bars.Count >= ShortPullbackRules.ThrustWindowSessions
            ? bars[^ShortPullbackRules.ThrustWindowSessions].BarDate
            : bars[0].BarDate;

        StoredScanHit? thrust = ScanHitReader.ForTicker(connection, ticker, asOf, windowStart)
            .Where(h => ThrustScans.Contains(h.Scan))
            .OrderByDescending(h => h.AsOf)
            .ThenBy(h => h.Rank)
            .FirstOrDefault();

        int? sessionsSince = thrust is null
            ? null
            : bars.Count(b => b.BarDate > thrust.AsOf && b.BarDate <= asOf);

        PullbackGeometry.Pullback? bounce = null;
        int? closesBeyond = null;

        if (thrust is not null)
        {
            PullbackGeometry.Bar[] shaped = [.. bars.Select(Shape)];
            int thrustIndex = IndexOf(bars, thrust.AsOf);

            if (thrustIndex >= 0)
            {
                bounce = PullbackGeometry.Of(shaped, thrustIndex, isLong: false);

                if (bounce is not null && figures is not null)
                {
                    // The 50-day, where the long side reads the 21-day. The one place the two check
                    // lists are not mirrors, and it sits here rather than in the geometry.
                    closesBeyond = PullbackGeometry.ClosesBeyondFloor(shaped, bounce, figures.EmaLong, isLong: false);
                }
            }
        }

        decimal? dailyRange = figures is null || figures.AverageDailyRange == 0m
            ? null
            : figures.AverageDailyRange * last.Close;

        return new ShortPullbackRules.ShortEvidence
        {
            Close = last.AdjustedClose,
            MedianDollarVolume = figures?.DollarVolumeMedian,
            MarketCap = SecurityReader.MarketCap(connection, ticker, asOf),
            // Counted over the whole stored history rather than over the 170-session read window
            // above, and through the same reader SignalVectorizer freezes it with. The check that
            // decides and the signal that records the decision have to be one number.
            SessionsListed = DailyBarReader.SessionsStored(connection, ticker, asOf),
            AverageDailyRange = figures?.AverageDailyRange,
            LadderGrade = figures?.LadderGrade,
            GapOverAverageGap = SqueezeRatio(bars),
            SessionsSinceThrust = sessionsSince,
            Bounce = bounce,
            ClosesBeyondFloor = closesBeyond,
            DistanceToNearestAverageRanges = figures is null || dailyRange is not decimal ceilingRange
                ? null
                : Math.Min(
                    Math.Abs(last.AdjustedClose - figures.EmaMedium),
                    Math.Abs(last.AdjustedClose - figures.EmaLong)) / ceilingRange,
            // Absent where the thrust has not bounced yet, rather than computed on a bounce of no
            // bars. With the extreme on the last session the trigger and the stop are the same price
            // and the give-up distance is zero, which clears every threshold written as a maximum.
            // see: A gate handed an absent or degenerate quantity fails rather than passing
            StopDistanceRanges = NoBounceYet(bounce) || dailyRange is not decimal stopRange || stopRange == 0m
                ? null
                : Math.Abs(bounce!.Trigger - bounce.Stop) / stopRange,
            ClusterCount = thrust?.ClusterCount,
        };
    }

    /// <summary>
    /// Today's 21-to-50 gap over its own average across the squeeze window, or null.
    ///
    /// Through the shared series in Core, from the bars this evidence was already reading, for the
    /// two reasons that series states: the engine writes one row a session so a mean over stored
    /// rows would step over a night it did not run, and SignalVectorizer freezes the same series as
    /// evidence, so a second implementation here would eventually describe a decision nobody made.
    /// </summary>
    public static decimal? SqueezeRatio(IReadOnlyList<StoredDailyBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        decimal[] closes = [.. bars.Select(b => b.AdjustedClose)];

        return AverageGap.SqueezeRatio(AverageGap.Series(
            closes, IndicatorEngine.EmaMediumPeriod, IndicatorEngine.EmaLongPeriod, IndicatorEngine.WarmupSessions));
    }

    /// <summary>Whether the thrust has yet to bounce, which is a real state and not a shape.</summary>
    private static bool NoBounceYet(PullbackGeometry.Pullback? bounce) =>
        bounce is null || bounce.PullbackBars == 0;

    private static decimal Factor(StoredDailyBar bar) => bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;

    private static PullbackGeometry.Bar Shape(StoredDailyBar bar)
    {
        decimal factor = Factor(bar);
        return new PullbackGeometry.Bar(
            bar.Open * factor, bar.High * factor, bar.Low * factor, bar.AdjustedClose, bar.High, bar.Low);
    }

    private static int IndexOf(IReadOnlyList<StoredDailyBar> bars, DateOnly date)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].BarDate == date)
            {
                return i;
            }
        }

        return -1;
    }

    private static IReadOnlyList<DateOnly> SessionsBetween(SqliteConnection connection, DateOnly from, DateOnly to)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE bar_date >= @from AND bar_date <= @to
             ORDER BY bar_date
            """;
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(to));

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return sessions;
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string ticker,
        DateOnly asOf,
        IReadOnlyList<CheckResult> results,
        bool passedAll,
        ShortPullbackRules.ShortEvidence evidence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Two statements rather than one with the table interpolated, for the reason the long
        // detector records: `writer-ownership` reads the shipped source for write statements and
        // attributes each to the type that issues it, so a table name that only exists at runtime is
        // a write the check cannot see. A write nothing can attribute is a write nobody owns.
        command.CommandText = string.Equals(table, SetupReader.CalibrationTable, StringComparison.Ordinal)
            ? """
              INSERT INTO calibration_setup
                  (setup_id, as_of, ticker, direction, check_results, passed_all,
                   trigger_price, stop_price, stop_distance_ranges)
              VALUES (@setup_id, @as_of, @ticker, @direction, @check_results, @passed_all,
                      @trigger_price, @stop_price, @stop_distance_ranges)
              ON CONFLICT (setup_id) DO NOTHING
              """
            : """
              INSERT INTO setup
                  (setup_id, as_of, ticker, direction, check_results, passed_all,
                   trigger_price, stop_price, stop_distance_ranges)
              VALUES (@setup_id, @as_of, @ticker, @direction, @check_results, @passed_all,
                      @trigger_price, @stop_price, @stop_distance_ranges)
              ON CONFLICT (setup_id) DO NOTHING
              """;

        command.Parameters.AddWithValue("@setup_id", SetupId(ticker, asOf));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", Direction);
        command.Parameters.AddWithValue("@check_results", JsonSerializer.Serialize(results, CheckResultsJson));
        command.Parameters.AddWithValue("@passed_all", passedAll ? 1 : 0);
        command.Parameters.AddWithValue("@trigger_price", StoreText.PriceToStorageText(evidence.Bounce?.Trigger ?? 0m));
        command.Parameters.AddWithValue("@stop_price", StoreText.PriceToStorageText(evidence.Bounce?.Stop ?? 0m));
        command.Parameters.AddWithValue("@stop_distance_ranges", StoreText.RatioToStorageText(evidence.StopDistanceRanges ?? 0m));

        return command.ExecuteNonQuery();
    }

    /// <summary>The identity of one setup: one name, one direction, one night.</summary>
    public static string SetupId(string ticker, DateOnly asOf) =>
        $"{StoreText.DateToStorageText(asOf)}-{ticker}-{Direction}";

    private static readonly JsonSerializerOptions CheckResultsJson = new(JsonSerializerDefaults.Web);
}
