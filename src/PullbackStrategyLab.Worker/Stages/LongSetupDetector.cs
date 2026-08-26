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
/// The long pullback pattern: ten checks, every result recorded, pass or fail.
///
/// <b>Every check runs on every name that clears the recording floor.</b> Nothing short-circuits on
/// the first failure, because the research loop exists to find which checks carry the strategy and
/// that is unanswerable if the store only remembers the checks that ran.
/// see: Failed checks are recorded rather than discarded
///
/// <b>Calibration mode is the same detector, not a second one.</b> `--calibrate from to` walks a
/// range of sessions, reads membership as it stands today rather than the nightly snapshot, computes
/// each session's averages in memory, and writes to `calibration_setup`. Those rows carry
/// survivorship bias by construction and nothing downstream reads them; the evidence store stays
/// empty until the first forward night. A separate implementation would make the count a fact about
/// the calibration code rather than about the thresholds, which is the one thing the run is for.
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
/// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
/// </summary>
public sealed class LongSetupDetector
{
    public const string Name = "detect-long";

    /// <summary>The flag that sends a run to the calibration table and off the nightly snapshot.</summary>
    public const string CalibrateFlag = "--calibrate";

    /// <summary>The direction this detector owns, and the only one it may ever write.</summary>
    public const string Direction = "long";

    /// <summary>
    /// The recording floor: a name is recorded when it clears the cheap filters and had a thrust.
    ///
    /// Not every universe member gets a row. Two thousand rows a night of names that move one
    /// percent would bury the record the research loop reads, and the first four checks are cheap
    /// filters deciding whether a stock is worth recording at all. The floor is those four, so a
    /// recorded setup is one where the pattern test had something to say.
    /// </summary>
    public static IReadOnlyList<string> RecordingFloor { get; } = ["tradable", "moves-enough", "uptrend", "thrust"];

    /// <summary>Sessions of history read per name: the warm-up, plus the window the gap average needs.</summary>
    public const int HistorySessions = 170;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public LongSetupDetector(
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

    /// <summary>
    /// A range of past sessions, into the calibration store.
    ///
    /// Membership is today's, because the nightly snapshot only starts when the lab does. That is the
    /// survivorship bias these rows carry and it is why nothing downstream reads them.
    /// </summary>
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
            LongPullbackRules.LongEvidence? evidence = Evidence(connection, ticker, asOf);
            if (evidence is null)
            {
                continue;
            }

            examined++;
            IReadOnlyList<CheckResult> results = LongPullbackRules.Evaluate(evidence);

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

    /// <summary>Whether the cheap filters all passed, which is what decides a name is worth recording.</summary>
    public static bool ClearsRecordingFloor(IReadOnlyList<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return RecordingFloor.All(name => results.Any(r => r.Name == name && r.Passed));
    }

    /// <summary>
    /// What the night knew about one name, or null where it has no bar for the session.
    ///
    /// Public because the fixture replay authors a setup the captured data cannot produce, and it
    /// has to author it from the same evidence the detector would have used. A second assembly there
    /// would make the authored case a test of the test rather than of the rules.
    /// </summary>
    public static LongPullbackRules.LongEvidence? Evidence(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        IReadOnlyList<StoredDailyBar> bars = DailyBarReader.Read(connection, ticker, asOf, HistorySessions);

        if (bars.Count == 0 || bars[^1].BarDate != asOf)
        {
            return null;
        }

        StoredDailyBar last = bars[^1];
        StoredIndicators? figures = IndicatorDailyReader.Read(connection, ticker, asOf, asOf);

        // The thrust: the most recent hit on an upward mover scan inside the window.
        DateOnly windowStart = bars.Count >= LongPullbackRules.ThrustWindowSessions
            ? bars[^LongPullbackRules.ThrustWindowSessions].BarDate
            : bars[0].BarDate;

        StoredScanHit? thrust = ScanHitReader.ForTicker(connection, ticker, asOf, windowStart)
            .Where(h => h.Scan is "gainer" or "gapper" or "leader")
            .OrderByDescending(h => h.AsOf)
            .ThenBy(h => h.Rank)
            .FirstOrDefault();

        int? sessionsSince = thrust is null
            ? null
            : bars.Count(b => b.BarDate > thrust.AsOf && b.BarDate <= asOf);

        PullbackGeometry.Pullback? pullback = null;
        int? closesBeyond = null;

        if (thrust is not null)
        {
            PullbackGeometry.Bar[] shaped = [.. bars.Select(Shape)];
            int thrustIndex = IndexOf(bars, thrust.AsOf);

            if (thrustIndex >= 0)
            {
                pullback = PullbackGeometry.Of(shaped, thrustIndex, isLong: true);

                if (pullback is not null && figures is not null)
                {
                    closesBeyond = PullbackGeometry.ClosesBeyondFloor(shaped, pullback, figures.EmaMedium, isLong: true);
                }
            }
        }

        decimal? dailyRange = figures is null || figures.AverageDailyRange == 0m
            ? null
            : figures.AverageDailyRange * last.Close;

        return new LongPullbackRules.LongEvidence
        {
            Close = last.AdjustedClose,
            MedianDollarVolume = figures?.DollarVolumeMedian,
            AverageDailyRange = figures?.AverageDailyRange,
            LadderGrade = figures?.LadderGrade,
            SessionsSinceThrust = sessionsSince,
            Pullback = pullback,
            ClosesBeyondFloor = closesBeyond,
            RangeTodayOverAverage = figures is null || figures.RangeAverage == 0m
                ? null
                : (last.High - last.Low) * Factor(last) / figures.RangeAverage,
            // Absent where the thrust has not pulled back yet, rather than computed on a pullback of
            // no bars. With the extreme on the last session the trigger and the stop are the same
            // price, so the give-up distance is zero and `exit-tight` passes: the tightest possible
            // stop, on a trade that does not exist. A vacuous pass is worse than a fail, because the
            // research loop reads these results to find which checks carry the strategy and a check
            // that passes on nothing looks like a check that is easy to clear.
            TriggerDistanceRanges = NoPullbackYet(pullback) || dailyRange is not decimal range || range == 0m
                ? null
                : Math.Abs(pullback!.Trigger - last.Close) / range,
            StopDistanceRanges = NoPullbackYet(pullback) || dailyRange is not decimal stopRange || stopRange == 0m
                ? null
                : Math.Abs(pullback!.Trigger - pullback.Stop) / stopRange,
            ClusterCount = thrust?.ClusterCount,
        };
    }

    /// <summary>
    /// Whether the thrust has yet to give anything back, which is a real state and not a shape.
    ///
    /// The extreme is the last session, so there is no drift to measure and no level to enter
    /// against. The name is still recorded, because a name that had a thrust and has not pulled back
    /// is exactly what the record should show, and `dip-shape` says so by failing on nought bars.
    /// </summary>
    private static bool NoPullbackYet(PullbackGeometry.Pullback? pullback) =>
        pullback is null || pullback.PullbackBars == 0;

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
        LongPullbackRules.LongEvidence evidence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Two statements rather than one with the table interpolated, and the duplication is the
        // point. `writer-ownership` reads the shipped source for write statements and attributes
        // each to the type that issues it, so a table name that only exists at runtime is a write
        // the check cannot see: it reported this detector as declaring two inserts and issuing
        // none. A write nothing can attribute is a write nobody owns.
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
        command.Parameters.AddWithValue("@trigger_price", StoreText.PriceToStorageText(evidence.Pullback?.Trigger ?? 0m));
        command.Parameters.AddWithValue("@stop_price", StoreText.PriceToStorageText(evidence.Pullback?.Stop ?? 0m));
        command.Parameters.AddWithValue("@stop_distance_ranges", StoreText.RatioToStorageText(evidence.StopDistanceRanges ?? 0m));

        return command.ExecuteNonQuery();
    }

    /// <summary>The identity of one setup: one name, one direction, one night.</summary>
    public static string SetupId(string ticker, DateOnly asOf) =>
        $"{StoreText.DateToStorageText(asOf)}-{ticker}-{Direction}";

    private static readonly JsonSerializerOptions CheckResultsJson = new(JsonSerializerDefaults.Web);
}

/// <summary>What one forward night's detection did.</summary>
public sealed record DetectResult(
    DateOnly AsOf,
    int Members,
    int Examined,
    int Recorded,
    int PassedAll,
    int BelowFloor,
    int RowsWritten,
    RunOutcome Outcome);

/// <summary>What one calibration run counted, over a range of past sessions.</summary>
public sealed record CalibrationResult(
    DateOnly From,
    DateOnly To,
    int Sessions,
    int Members,
    int Recorded,
    int PassedAll,
    int RowsWritten,
    RunOutcome Outcome);

/// <summary>The counters one pass over the members produced.</summary>
internal sealed record Tally(int Examined, int Recorded, int PassedAll, int BelowFloor);
