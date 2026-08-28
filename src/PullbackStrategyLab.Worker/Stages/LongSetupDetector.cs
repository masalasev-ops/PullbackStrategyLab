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
    public const string Direction = SetupDirection.Long;

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
        Console.WriteLine($"{Name}: {result.Errored} name(s) could not be decided and have an error row");
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
        var source = new StoredFigures(connection);

        Tally tally = Walk(
            connection, members, asOf, SetupReader.SetupTable, source,
            ticker => DailyBarReader.Read(connection, ticker, asOf, HistorySessions));

        // A night that could not read a name did not do everything it set out to do, and saying so
        // is what stops the loss reading as a quiet night.
        RunOutcome outcome = tally.Errored == 0 ? RunOutcome.Clean : RunOutcome.Partial;
        RunSummary summary = run.Complete(outcome);

        return new DetectResult(
            asOf, members.Count, tally.Examined, tally.Recorded, tally.PassedAll, tally.BelowFloor,
            tally.Errored, summary.RowsWritten, outcome);
    }

    /// <summary>
    /// A range of past sessions, into the calibration store.
    ///
    /// Membership is today's, because the nightly snapshot only starts when the lab does. That is the
    /// survivorship bias these rows carry and it is why nothing downstream reads them.
    ///
    /// <b>The session is carried in memory rather than read from the store.</b> A night the lab was
    /// not running has no snapshot, so it has no indicator row, no ladder grade and no scan hit, and
    /// it may not be given them: writing those would be the reconstruction the evidence rule forbids.
    /// So each session is assembled from the bar windows this walk is reading anyway, through the
    /// nightly stages' own arithmetic (see: A calibration run reconstructs against current membership
    /// and computes its indicators in memory).
    ///
    /// <b>The ranking runs ahead of the detection by the thrust window.</b> Every check that reads a
    /// thrust looks back twenty sessions, so the first session detected needs twenty sessions of
    /// ranks behind it. Starting both at the same date would report a range whose opening sessions
    /// found no thrust and recorded nothing, and nothing about the count would say so.
    /// </summary>
    public CalibrationResult Calibrate(DateOnly from, DateOnly to)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, SetupReader.CalibrationTable);

        // The instant the whole run reads as of: now, rather than each session's own date.
        //
        // A backfill takes a name's whole history in one evening, so every bar of 2024 in this store
        // was observed in 2026, and a session bounded on its own instant sees none of it. Bounding
        // on the end of the range is not enough either, because the range ends on the last session
        // and the observation that recorded it came after the close. Both were tried on the way
        // here and both reported a run of nought sessions over a store of one and a half million
        // bars, which is the shape of failure this whole checkpoint is about: a number that is
        // wrong and looks like an answer.
        //
        // This is the bound a rebuild uses, named there for the same reason, and it is one more
        // thing these rows carry beside survivorship bias: the series is read as it stands now,
        // corrections included, rather than as it stood on the night.
        DateTimeOffset observedBefore = _clock.UtcNow;

        IReadOnlyList<string> listed = UniverseSnapshotReader.CurrentMembers(connection);
        IReadOnlyList<string> members = UniverseSnapshotReader.CurrentMembersWithHistory(
            connection, to, IndicatorEngine.WarmupSessions, observedBefore);
        IReadOnlyList<DateOnly> sessions = SessionsBetween(connection, from, to, observedBefore);
        IReadOnlyList<DateOnly> warmup = CalibrationFigures.SessionsBefore(
            connection, from, LongPullbackRules.ThrustWindowSessions, observedBefore);

        var source = new CalibrationFigures(connection, _clock.UtcNow, observedBefore);

        int recorded = 0;
        int passedAll = 0;
        int errored = 0;
        var nights = new List<NightCount>();

        foreach (DateOnly session in warmup.Concat(sessions))
        {
            var windows = new Dictionary<string, IReadOnlyList<StoredDailyBar>>(StringComparer.Ordinal);
            foreach (string ticker in members)
            {
                windows[ticker] = DailyBarReader.Read(
                    connection, ticker, session, HistorySessions, observedBefore);
            }

            source.Rank(session, windows);

            if (session < from)
            {
                continue;
            }

            Tally tally = Walk(connection, members, session, SetupReader.CalibrationTable, source,
                ticker => windows[ticker]);

            recorded += tally.Recorded;
            passedAll += tally.PassedAll;
            errored += tally.Errored;
            nights.Add(new NightCount(session, tally.Examined, tally.Recorded, tally.PassedAll));
        }

        RunOutcome outcome = errored == 0 ? RunOutcome.Clean : RunOutcome.Partial;
        RunSummary summary = run.Complete(outcome);

        return new CalibrationResult(
            from, to, sessions.Count, warmup.Count, listed.Count, members.Count, recorded, passedAll,
            errored, summary.RowsWritten, outcome, nights);
    }

    private Tally Walk(
        SqliteConnection connection,
        IReadOnlyList<string> members,
        DateOnly asOf,
        string table,
        ISessionFigures source,
        Func<string, IReadOnlyList<StoredDailyBar>> window)
    {
        int examined = 0;
        int recorded = 0;
        int passedAll = 0;
        int belowFloor = 0;
        int errored = 0;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string ticker in members)
        {
            LongPullbackRules.LongEvidence? evidence;

            try
            {
                evidence = Evidence(ticker, asOf, window(ticker), source);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // An error row rather than a skip. Every count downstream is over the setups that
                // were recorded, so a name the detector could not read is simply absent: the night
                // looks lighter, the counts stay plausible, and nothing says a name was lost.
                errored += RecordError(connection, transaction, asOf, ticker, e, _clock.UtcNow);
                continue;
            }

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
        return new Tally(examined, recorded, passedAll, belowFloor, errored);
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
    public static LongPullbackRules.LongEvidence? Evidence(SqliteConnection connection, string ticker, DateOnly asOf) =>
        Evidence(ticker, asOf, DailyBarReader.Read(connection, ticker, asOf, HistorySessions), new StoredFigures(connection));

    /// <summary>
    /// The same evidence, with the bar window and the session's figures handed in.
    ///
    /// What calibration mode uses, and what the nightly path above resolves to. The two differ in
    /// where the figures come from and nowhere else: a forward night reads them from the store,
    /// a reconstructed session computes them from this very window through the stages' own
    /// arithmetic. Splitting it here rather than in each caller is what keeps the rules seeing one
    /// evidence shape.
    /// </summary>
    public static LongPullbackRules.LongEvidence? Evidence(
        string ticker,
        DateOnly asOf,
        IReadOnlyList<StoredDailyBar> bars,
        ISessionFigures source)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(source);

        if (bars.Count == 0 || bars[^1].BarDate != asOf)
        {
            return null;
        }

        StoredDailyBar last = bars[^1];
        StoredIndicators? figures = source.Indicators(ticker, asOf, bars);

        // The thrust: the most recent hit on an upward mover scan inside the window.
        DateOnly windowStart = bars.Count >= LongPullbackRules.ThrustWindowSessions
            ? bars[^LongPullbackRules.ThrustWindowSessions].BarDate
            : bars[0].BarDate;

        StoredScanHit? thrust = source.Hits(ticker, asOf, windowStart)
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
                pullback = PullbackGeometry.Of(
                    shaped, thrustIndex, ScanSpans.SessionsFor(thrust.Scan), isLong: true);

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
            ThrustScan = thrust?.Scan,
            ThrustSession = thrust?.AsOf,
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

    private static IReadOnlyList<DateOnly> SessionsBetween(
        SqliteConnection connection,
        DateOnly from,
        DateOnly to,
        DateTimeOffset observedBefore)
    {
        using SqliteCommand command = connection.CreateCommand();
        // Bounded on the run's own instant, which the caller states once for every read it makes.
        // A session dated inside the range that the store learned about after the run began is not
        // one this run walks, and a session the backfill acquired last night is: the observation
        // that matters is when the lab came to know the bar, not when the market printed it.
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE bar_date >= @from AND bar_date <= @to
               AND observed_at <= @observed_before
             ORDER BY bar_date
            """;
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(to));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

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
                   trigger_price, stop_price, stop_distance_ranges,
                   thrust_scan, thrust_session)
              VALUES (@setup_id, @as_of, @ticker, @direction, @check_results, @passed_all,
                      @trigger_price, @stop_price, @stop_distance_ranges,
                      @thrust_scan, @thrust_session)
              ON CONFLICT (setup_id) DO NOTHING
              """
            : """
              INSERT INTO setup
                  (setup_id, as_of, ticker, direction, check_results, passed_all,
                   trigger_price, stop_price, stop_distance_ranges,
                   thrust_scan, thrust_session)
              VALUES (@setup_id, @as_of, @ticker, @direction, @check_results, @passed_all,
                      @trigger_price, @stop_price, @stop_distance_ranges,
                      @thrust_scan, @thrust_session)
              ON CONFLICT (setup_id) DO NOTHING
              """;

        command.Parameters.AddWithValue("@setup_id", SetupId(ticker, asOf));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", Direction);
        command.Parameters.AddWithValue("@check_results", JsonSerializer.Serialize(results, CheckResultsJson));
        command.Parameters.AddWithValue("@passed_all", passedAll ? 1 : 0);
        // Null rather than nought where the geometry is absent, which is what the column could
        // not say until 031. A give-up distance of 0 is not a small give-up: it is a trade with no
        // stop, and it clears every threshold written as a maximum. The detector already refuses
        // to compute these on a degenerate pullback, so the flattening happened here and
        // nowhere else, and SignalVectorizer then froze the 0 into a table written once.
        // see: A gate handed an absent or degenerate quantity fails rather than passing
        command.Parameters.AddWithValue("@trigger_price", Text(evidence.Pullback?.Trigger, StoreText.PriceToStorageText));
        command.Parameters.AddWithValue("@stop_price", Text(evidence.Pullback?.Stop, StoreText.PriceToStorageText));
        command.Parameters.AddWithValue("@stop_distance_ranges", Text(evidence.StopDistanceRanges, StoreText.RatioToStorageText));

        // Null rather than an empty string where the thrust could not be resolved. A name with
        // no hit is a real state, and a column that says "" for it cannot be told apart from a
        // scan whose name went missing.
        command.Parameters.AddWithValue("@thrust_scan", (object?)evidence.ThrustScan ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@thrust_session",
            evidence.ThrustSession is DateOnly session
                ? StoreText.DateToStorageText(session)
                : (object)DBNull.Value);

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// The row this detector writes for a stock it could not decide.
    ///
    /// Written out here rather than shared with the other detector, and the duplication is the same
    /// price the setup insert pays: `writer-ownership` reads the shipped source for write statements
    /// and attributes each to the type that issues it, so a shared helper would make both detectors
    /// declare an insert neither one issues. SCHEMA declares the two as writers of this table,
    /// disjoint by direction, exactly as it does for `setup`.
    ///
    /// A rerun of the same night collides on the key and writes nothing, so the first record of a
    /// failure is the one kept, and a later rerun that succeeds leaves the row behind as history.
    /// That is the honest state: the night did lose the name once.
    /// </summary>
    private static int RecordError(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly asOf,
        string ticker,
        Exception error,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO detector_error (as_of, ticker, direction, message, observed_at)
            VALUES (@as_of, @ticker, @direction, @message, @observed_at)
            ON CONFLICT (as_of, ticker, direction) DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", Direction);
        command.Parameters.AddWithValue("@message", DetectorErrorReader.Describe(error));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        return command.ExecuteNonQuery();
    }

    /// <summary>The identity of one setup: one name, one direction, one night.</summary>
    public static string SetupId(string ticker, DateOnly asOf) =>
        $"{StoreText.DateToStorageText(asOf)}-{ticker}-{Direction}";

    private static readonly JsonSerializerOptions CheckResultsJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A quantity as the column stores it, or DBNull where the detector recorded none.
    ///
    /// Named and shared rather than three inline conditionals, because the whole class of
    /// error here is one of the three being flattened while the others are not.
    /// </summary>
    private static object Text(decimal? value, Func<decimal, string> format) =>
        value is decimal present ? format(present) : DBNull.Value;

}

/// <summary>What one forward night's detection did.</summary>
public sealed record DetectResult(
    DateOnly AsOf,
    int Members,
    int Examined,
    int Recorded,
    int PassedAll,
    int BelowFloor,
    int Errored,
    int RowsWritten,
    RunOutcome Outcome);

/// <summary>
/// What one calibration run counted, over a range of past sessions.
///
/// <c>Nights</c> is the point of the run and the totals are the summary. A threshold is set against
/// how many candidates a night produces, and a total over five hundred sessions says nothing about
/// that: the same total is one night of six hundred and five hundred of nothing, or two a night
/// every night, and only one of those is a lab worth running.
/// </summary>
public sealed record CalibrationResult(
    DateOnly From,
    DateOnly To,
    int Sessions,
    int WarmupSessions,
    int Listed,
    int Members,
    int Recorded,
    int PassedAll,
    int Errored,
    int RowsWritten,
    RunOutcome Outcome,
    IReadOnlyList<NightCount> Nights);

/// <summary>What one reconstructed night produced, which is what the distribution is over.</summary>
public sealed record NightCount(DateOnly AsOf, int Examined, int Recorded, int PassedAll);

/// <summary>
/// The counters one pass over the members produced.
///
/// <c>Errored</c> is the count of names the detector could not decide, each with a row of its own in
/// <c>detector_error</c>. It is separate from <c>BelowFloor</c> because the two mean opposite things:
/// a name below the floor was read and found uninteresting, and an errored name was not read at all.
/// </summary>
internal sealed record Tally(int Examined, int Recorded, int PassedAll, int BelowFloor, int Errored);
