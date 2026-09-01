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
    public const string Direction = SetupDirection.Short;

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
    /// A range of past sessions, into the calibration store, against today's membership.
    ///
    /// The session is carried in memory, on the terms the long side's entry states, and the ranking
    /// runs a thrust window ahead of the detection for the same reason. What differs here is the
    /// market-cap clause of `tradable-shortable`, which is exempted by name: a reconstructed session
    /// has no capitalisation to read, and every short candidate would fail the first gate.
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
            connection, from, ShortPullbackRules.ThrustWindowSessions, observedBefore);

        var source = new CalibrationFigures(connection, _clock.UtcNow, observedBefore, _options.IndexSymbols);

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

        // The night's incomplete inputs, read once for the whole walk. Every setup of a session
        // carries the same mark, because the question it answers is about the night rather than
        // about the name. Only for the evidence table: a calibration run reconstructs against
        // current membership and its rows go where nothing downstream reads them.
        string? degradedBecause = string.Equals(table, SetupReader.SetupTable, StringComparison.Ordinal)
            ? RunLogger.DegradedBecause(connection, asOf, _options.SessionZone)
            : null;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string ticker in members)
        {
            ShortPullbackRules.ShortEvidence? evidence;

            try
            {
                evidence = Evidence(ticker, asOf, window(ticker), source);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // An error row rather than a skip, for the reason DetectorErrorReader states: a
                // name the detector could not read is simply absent downstream, and the night looks
                // lighter rather than wrong.
                errored += RecordError(connection, transaction, asOf, ticker, e, _clock.UtcNow);
                continue;
            }

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

            recorded += Insert(connection, transaction, table, ticker, asOf, results, all, evidence, degradedBecause);
        }

        transaction.Commit();
        return new Tally(examined, recorded, passedAll, belowFloor, errored);
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
    public static ShortPullbackRules.ShortEvidence? Evidence(SqliteConnection connection, string ticker, DateOnly asOf) =>
        Evidence(ticker, asOf, DailyBarReader.Read(connection, ticker, asOf, HistorySessions), new StoredFigures(connection));

    /// <summary>
    /// The same evidence, with the bar window and the session's figures handed in. The long side's
    /// overload states why the split is here rather than in each caller.
    /// </summary>
    public static ShortPullbackRules.ShortEvidence? Evidence(
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

        // The thrust: the most recent hit on a downward mover scan inside the window.
        DateOnly windowStart = bars.Count >= ShortPullbackRules.ThrustWindowSessions
            ? bars[^ShortPullbackRules.ThrustWindowSessions].BarDate
            : bars[0].BarDate;

        StoredScanHit? thrust = source.Hits(ticker, asOf, windowStart)
            .Where(h => ThrustScans.Contains(h.Scan))
            .OrderByDescending(h => h.AsOf)
            .ThenBy(h => h.Rank)
            .FirstOrDefault();

        int? sessionsSince = thrust is null
            ? null
            : bars.Count(b => b.BarDate > thrust.AsOf && b.BarDate <= asOf);

        PullbackGeometry.Pullback? bounce = null;
        int? closesBeyond = null;
        DateOnly? anchorSession = null;

        if (thrust is not null)
        {
            PullbackGeometry.Bar[] shaped = [.. bars.Select(Shape)];
            int thrustIndex = IndexOf(bars, thrust.AsOf);

            if (thrustIndex >= 0)
            {
                int span = ScanSpans.SessionsFor(thrust.Scan);
                bounce = PullbackGeometry.Of(shaped, thrustIndex, span, isLong: false);

                if (bounce is not null && figures is not null)
                {
                    // The 50-day, where the long side reads the 21-day. The one place the two check
                    // lists are not mirrors, and it sits here rather than in the geometry.
                    closesBeyond = PullbackGeometry.ClosesBeyondFloor(
                        shaped, bounce, IndicatorEngine.FloorSeries(shaped, isLong: false), isLong: false);
                }

                anchorSession = AnchorSessionOf(bars, thrust.Scan, thrust.AsOf);
            }
        }

        decimal? anchored = anchorSession is DateOnly anchor
            ? source.AnchoredAveragePrice(ticker, asOf, anchor)
            : null;

        decimal? dailyRange = figures is null || figures.AverageDailyRange == 0m
            ? null
            : figures.AverageDailyRange * last.Close;

        return new ShortPullbackRules.ShortEvidence
        {
            Close = last.AdjustedClose,
            MedianDollarVolume = figures?.DollarVolumeMedian,
            MarketCap = source.MarketCap(ticker, asOf),
            MarketCapExempt = source.MarketCapExempt,
            // Counted over the whole stored history rather than over the 170-session read window
            // above, and through the same reader SignalVectorizer freezes it with. The check that
            // decides and the signal that records the decision have to be one number.
            SessionsListed = source.SessionsListed(ticker, asOf),
            AverageDailyRange = figures?.AverageDailyRange,
            LadderGrade = figures?.LadderGrade,
            GapOverAverageGap = SqueezeRatio(bars),
            SessionsSinceThrust = sessionsSince,
            Bounce = bounce,
            ClosesBeyondFloor = closesBeyond,
            // The zero guard matches its sibling below and both long-side equivalents. dailyRange
            // is null only when figures are absent or the average daily range is nought; it is 0m,
            // not null, when the session's close is 0m, which Factor() already treats as a bar the
            // vendor can send. Without the guard that bar threw DivideByZeroException on the short
            // side and recorded a normal setup on the long, which is a mirror break rather than a
            // stated asymmetry.
            DistanceToNearestAverageRanges =
                figures is null || dailyRange is not decimal ceilingRange || ceilingRange == 0m
                ? null
                : Math.Min(
                    Math.Abs(last.AdjustedClose - figures.EmaMedium),
                    Math.Abs(last.AdjustedClose - figures.EmaLong)) / ceilingRange,
            // The third disjunct, in the same units as the two above it and guarded the same way.
            // Null where there is no anchor, no level for it, or no range to express the distance
            // in, and each of the three leaves the clause not run rather than run at nought.
            DistanceToAnchoredRanges =
                anchored is not decimal level || dailyRange is not decimal anchorRange || anchorRange == 0m
                ? null
                : Math.Abs(last.AdjustedClose - level) / anchorRange,
            // Which of the two absences this row has, where it has one. A reconstructed session can
            // never be anchored and a forward one becomes anchorable as the store accumulates, so
            // the verdict records the two under different clause sets.
            Reconstructed = source.Reconstructed,
            // Absent where the thrust has not bounced yet, rather than computed on a bounce of no
            // bars. With the extreme on the last session the trigger and the stop are the same price
            // and the give-up distance is zero, which clears every threshold written as a maximum.
            // see: A gate handed an absent or degenerate quantity fails rather than passing
            StopDistanceRanges = NoBounceYet(bounce) || dailyRange is not decimal stopRange || stopRange == 0m
                ? null
                : Math.Abs(bounce!.Trigger - bounce.Stop) / stopRange,
            ClusterCount = thrust?.ClusterCount,
            ThrustScan = thrust?.Scan,
            ThrustSession = thrust?.AsOf,
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

    /// <summary>
    /// Which session the ceiling clause's level is anchored at: the swing high the thrust fell from.
    ///
    /// <b>One implementation, read by two components at different hours.</b> The detector needs it
    /// at 18:20 to ask for a level, and VwapEngine needs it at 21:00 to compute one, and the two
    /// have to name the same session or the level answers a question nobody asked. It takes the
    /// scan and the session from the setup row rather than re-resolving the thrust, because those
    /// two columns exist for exactly this: `gainer` and `gapper` flag one session where `leader` and
    /// `laggard` flag twenty, so a swing found without knowing which scan flagged the move is a
    /// swing searched over the wrong span.
    ///
    /// A session and not a price. The level itself is a volume-weighted average over that session's
    /// minutes forward, which is the one thing this class cannot compute.
    /// see: The anchored average price is anchored at the swing the thrust ran from
    /// </summary>
    public static DateOnly? AnchorSessionOf(
        IReadOnlyList<StoredDailyBar> bars, string thrustScan, DateOnly thrustSession)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentException.ThrowIfNullOrWhiteSpace(thrustScan);

        int thrustIndex = IndexOf(bars, thrustSession);

        if (thrustIndex < 0)
        {
            return null;
        }

        PullbackGeometry.Bar[] shaped = [.. bars.Select(Shape)];
        int span = ScanSpans.SessionsFor(thrustScan);
        PullbackGeometry.Pullback? bounce = PullbackGeometry.Of(shaped, thrustIndex, span, isLong: false);

        return bounce is not null
            && PullbackGeometry.SwingIndexOf(shaped, bounce, span, isLong: false) is int swing
            ? bars[swing].BarDate
            : null;
    }

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
        ShortPullbackRules.ShortEvidence evidence,
        string? degradedBecause)
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
                   thrust_scan, thrust_session, degraded_because)
              VALUES (@setup_id, @as_of, @ticker, @direction, @check_results, @passed_all,
                      @trigger_price, @stop_price, @stop_distance_ranges,
                      @thrust_scan, @thrust_session, @degraded_because)
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
        // to compute these on a degenerate bounce, so the flattening happened here and
        // nowhere else, and SignalVectorizer then froze the 0 into a table written once.
        // see: A gate handed an absent or degenerate quantity fails rather than passing
        command.Parameters.AddWithValue("@trigger_price", Text(evidence.Bounce?.Trigger, StoreText.PriceToStorageText));
        command.Parameters.AddWithValue("@stop_price", Text(evidence.Bounce?.Stop, StoreText.PriceToStorageText));
        command.Parameters.AddWithValue("@stop_distance_ranges", Text(evidence.StopDistanceRanges, StoreText.RatioToStorageText));

        // Null rather than an empty string where the thrust could not be resolved. A name with
        // no hit is a real state, and a column that says "" for it cannot be told apart from a
        // scan whose name went missing.
        // The night's incomplete inputs, which is the third clause of the vendor-ceiling rule and
        // had no column until 032. Null on an ordinary night; the stage names where a stage of this
        // session had already ended other than cleanly when this row was written.
        command.Parameters.AddWithValue("@degraded_because", (object?)degradedBecause ?? DBNull.Value);

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
