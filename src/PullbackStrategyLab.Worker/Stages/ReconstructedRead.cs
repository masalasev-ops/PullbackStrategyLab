using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The paired comparison over reconstructed history: what the pattern would have looked like, in
/// tables nothing downstream reads.
///
/// <b>It answers 3.6's question and it is not 3.6.</b> 3.6 fires on forward evidence and on nothing
/// else. This exists because the forward-only decision was read as saying a historical run is good
/// for counting setups and nothing more, which is narrower than the run can answer, and the
/// narrowness had the project waiting eighteen trading nights for the number that decides whether to
/// continue.
/// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
///
/// <b>The range is the population and it is stated beside every figure.</b> A reconstructed read
/// covers a stated number of recent sessions, and the range is what says how far back the
/// survivorship exposure reaches. Stating it once at the top would let a figure be copied out of
/// the report without it. It was also quadratic in the range for one day, while a tight control
/// could be drawn from any earlier session sharing the mood; both sets draw within the night again,
/// so the walk is linear and a session's draw depends on no other session.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
///
/// <b>Survivorship runs opposite ways on the two sides and each figure says which.</b> The universe
/// is today's, so the long side is measured over disproportionately the winners and its figure is a
/// ceiling; the short side is missing the names that fell furthest, which are the ones a short
/// profits most from, so its figure is a floor. One caveat covering both would let a reader take the
/// long number as conservative and the short as generous, and both readings are backwards.
/// </summary>
public sealed class ReconstructedRead
{
    public const string Name = "reconstructed-read";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;
    private readonly ControlSampler _controls;
    private readonly ForwardReturnFiller _forward;

    public ReconstructedRead(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options,
        ControlSampler controls,
        ForwardReturnFiller forward)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
        _controls = controls;
        _forward = forward;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length < 1 || !int.TryParse(args[0], CultureInfo.InvariantCulture, out int sessions)
            || sessions < 1)
        {
            Console.Error.WriteLine($"usage: {Name} <sessions>");
            Console.Error.WriteLine(
                "  the number of most recent calibration sessions to read over, which is the "
                + "population every figure is stated against");
            return 2;
        }

        ReadResult result = Read(sessions);
        Report(result);
        return result.Failed ? 1 : 0;
    }

    /// <summary>
    /// One rung: draw, fill and measure over the most recent <paramref name="sessions"/> sessions
    /// the calibration store holds.
    /// </summary>
    public ReadResult Read(int sessions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sessions, 1);

        var wall = Stopwatch.StartNew();
        using SqliteConnection connection = _connections.OpenWrite();

        // The evidence store, counted before anything runs. Asserted rather than trusted: this pass
        // writes to three calibration tables and the whole permission it rests on is that it touches
        // no evidence row.
        EvidenceCounts before = EvidenceCounts.Read(connection);

        IReadOnlyList<DateOnly> range = MostRecentSessions(connection, sessions);

        if (range.Count == 0)
        {
            return ReadResult.Empty(sessions, wall.Elapsed);
        }

        var inRange = new HashSet<DateOnly>(range);
        DateTimeOffset observedBefore = _clock.UtcNow;

        // The warm-up the ranking needs behind the first session read, on the same terms the
        // calibration walk uses: every check that reads a thrust looks back twenty sessions.
        IReadOnlyList<DateOnly> warmup = CalibrationFigures.SessionsBefore(
            connection, range[0], LongPullbackRules.ThrustWindowSessions, observedBefore);

        IReadOnlyList<string> members = UniverseSnapshotReader.CurrentMembersWithHistory(
            connection, range[^1], IndicatorEngine.WarmupSessions, observedBefore);

        var source = new CalibrationFigures(
            connection, _clock.UtcNow, observedBefore, _options.IndexSymbols);

        int pool = 0;
        int drawn = 0;
        var diagnosis = new TightDrawDiagnosis();

        foreach (DateOnly session in warmup.Concat(range))
        {
            var windows = new Dictionary<string, IReadOnlyList<StoredDailyBar>>(StringComparer.Ordinal);

            foreach (string ticker in members)
            {
                windows[ticker] = DailyBarReader.Read(
                    connection, ticker, session, LongSetupDetector.HistorySessions, observedBefore);
            }

            source.Rank(session, windows, _options.SessionZone);

            if (!inRange.Contains(session))
            {
                // A warm-up session is ranked so the checks behind the first read session have their
                // thrust window, and it has no subjects of its own. Nothing is drawn from it either,
                // which used to need saying and no longer does: a draw cannot leave its own night.
                continue;
            }

            // Beside the draw and reading the same pool, not after it and reading the store. What
            // eliminated a candidate is a fact about the pool the draw was handed, and the store
            // holds only the rows that survived.
            diagnosis.Observe(
                connection, source, session, SubjectTables.Calibration, _options.SessionZone);

            ControlResult result = _controls.Draw(
                connection, source, session, SubjectTables.Calibration);

            drawn += result.Loose + result.Tight;
            pool = Math.Max(pool, result.Pool);
        }

        // The outcomes, over both populations, through the one filler. Its as-of is the last session
        // read, which is what bounds every horizon that has elapsed.
        FillResult filled = _forward.Fill(range[^1], SubjectTables.Calibration);

        var panels = new List<Panel>();

        foreach (string direction in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            foreach (string set in new[] { "loose", "tight" })
            {
                panels.Add(Measure(connection, direction, set, range, ReachedCeiling.AsItStands));
            }
        }

        // The short bracket, both ways. `reached-ceiling` is a three-clause disjunction running two
        // until VwapEngine lands at 4.4, and a disjunction missing a disjunct is strictly harder to
        // pass. So the deferred clause's effect is bounded rather than guessed: the panels above
        // restrict the short side to rows that passed the gate as it stands, which is the fewest
        // rows it will ever admit, and the panels below ignore the gate entirely, which is the most.
        // The truth with three clauses running is between them and neither is it.
        foreach (string set in new[] { "loose", "tight" })
        {
            panels.Add(Measure(connection, SetupDirection.Short, set, range, ReachedCeiling.Ignored));
        }

        EvidenceCounts after = EvidenceCounts.Read(connection);

        return new ReadResult(
            sessions, range[0], range[^1], range.Count, pool, drawn, filled.Written + filled.ControlsWritten,
            panels, before, after, wall.Elapsed, Failed: false,
            diagnosis, TightDrawn(connection, range), Reuse(connection, range));
    }

    /// <summary>
    /// The most recent sessions the calibration store holds, oldest first.
    ///
    /// Read from `calibration_setup` rather than from the bar table, because the population is the
    /// sessions a detector actually walked. A session with bars and no calibration row is a session
    /// this read has no subjects on, and counting it into the range would state a coverage the
    /// figures do not have.
    /// </summary>
    private static IReadOnlyList<DateOnly> MostRecentSessions(SqliteConnection connection, int sessions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT as_of FROM (
                SELECT DISTINCT as_of FROM calibration_setup ORDER BY as_of DESC LIMIT @sessions
            ) ORDER BY as_of
            """;
        command.Parameters.AddWithValue("@sessions", sessions);

        var dates = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            dates.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return dates;
    }

    /// <summary>
    /// How many tight controls each subject in the range actually got, nought included.
    ///
    /// A left join rather than a group over the control table, because a subject that drew nothing
    /// has no row there and is exactly the subject the distribution is about. Counting only the
    /// subjects that appear would report a yield over the subjects that had one.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private IReadOnlyDictionary<string, int> TightDrawn(
        SqliteConnection connection, IReadOnlyList<DateOnly> range)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.setup_id, COALESCE(c.drawn, 0)
              FROM calibration_setup s
              LEFT JOIN (SELECT setup_id, COUNT(*) AS drawn
                           FROM calibration_control_setup
                          WHERE control_set = 'tight' AND drawn_at <= @computed_at
                          GROUP BY setup_id) c
                ON c.setup_id = s.setup_id
             WHERE s.as_of >= @from AND s.as_of <= @to
            """;
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(range[0]));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(range[^1]));
        command.Parameters.AddWithValue(
            "@computed_at", StoreText.TimestampToStorageText(_clock.UtcNow));

        var drawn = new Dictionary<string, int>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            drawn[reader.GetString(0)] = reader.GetInt32(1);
        }

        return drawn;
    }

    /// <summary>
    /// How many distinct names the two sets actually used, per night and over the range.
    ///
    /// <b>The count that separates a thin set from a repetitive one.</b> Five controls per subject
    /// says nothing about how many different names those five came from across a night's subjects: a
    /// night of a hundred subjects can hold five hundred control rows over five distinct names or
    /// over four hundred. Where every subject is compared against nearly the same controls, every
    /// paired difference on that night carries the same control term, the night's pairs move
    /// together, and the design effect spends the row count. That is a fact about the pool a
    /// dimension left, and no total of rows can show it.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private IReadOnlyList<ReuseRow> Reuse(SqliteConnection connection, IReadOnlyList<DateOnly> range)
    {
        var perNight = new Dictionary<(string Direction, string Set), List<(int Names, int Subjects)>>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.direction, c.control_set,
                       COUNT(DISTINCT c.control_ticker), COUNT(DISTINCT c.setup_id)
                  FROM calibration_control_setup c
                  JOIN calibration_setup s ON s.setup_id = c.setup_id
                 WHERE s.as_of >= @from AND s.as_of <= @to AND c.drawn_at <= @computed_at
                 GROUP BY s.direction, c.control_set, s.as_of
                """;
            Bind(command, range);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                (string, string) key = (reader.GetString(0), reader.GetString(1));

                if (!perNight.TryGetValue(key, out List<(int, int)>? nights))
                {
                    nights = [];
                    perNight[key] = nights;
                }

                nights.Add((reader.GetInt32(2), reader.GetInt32(3)));
            }
        }

        var overRange = new Dictionary<
            (string Direction, string Set), (int Names, int Rows, double DaysApart, int SameSession)>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.direction, c.control_set, COUNT(DISTINCT c.control_ticker), COUNT(*),
                       AVG(CAST(json_extract(c.match_quality, '$.sessionsApart') AS INTEGER)),
                       SUM(CASE WHEN CAST(json_extract(c.match_quality, '$.sessionsApart') AS INTEGER) = 0
                                THEN 1 ELSE 0 END)
                  FROM calibration_control_setup c
                  JOIN calibration_setup s ON s.setup_id = c.setup_id
                 WHERE s.as_of >= @from AND s.as_of <= @to AND c.drawn_at <= @computed_at
                 GROUP BY s.direction, c.control_set
                """;
            Bind(command, range);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                overRange[(reader.GetString(0), reader.GetString(1))] = (
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0d : reader.GetDouble(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
            }
        }

        var rows = new List<ReuseRow>();

        foreach (((string direction, string set), List<(int Names, int Subjects)> nights) in perNight)
        {
            (int names, int total, double apart, int sameSession) =
                overRange.GetValueOrDefault((direction, set));

            rows.Add(new ReuseRow(
                direction, set, nights.Count,
                Middle([.. nights.Select(n => n.Names)]),
                Middle([.. nights.Select(n => n.Subjects)]),
                names, total, apart, sameSession));
        }

        return [.. rows.OrderBy(r => r.Direction, StringComparer.Ordinal)
            .ThenBy(r => r.Set, StringComparer.Ordinal)];
    }

    private void Bind(SqliteCommand command, IReadOnlyList<DateOnly> range)
    {
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(range[0]));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(range[^1]));
        command.Parameters.AddWithValue(
            "@computed_at", StoreText.TimestampToStorageText(_clock.UtcNow));
    }

    private static int Middle(int[] values)
    {
        Array.Sort(values);
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    /// <summary>
    /// How many different names one set drew on, and how far from the subject it had to reach.
    ///
    /// <paramref name="MeanDaysApart"/> and <paramref name="SameSessionRows"/> measured the price
    /// the across-session ruling accepted and now assert that it is not being paid. A control on the
    /// subject's own session shares that session's market move and the paired difference cancels it;
    /// a control from another session does not, so the market factor stays in every pair on the
    /// night and every pair carries the same one. Both figures should read nought days apart and
    /// every row on the session on both sets, and they are reported rather than assumed because a
    /// number that only ever confirms is the one nobody notices going wrong.
    /// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
    /// </summary>
    public sealed record ReuseRow(
        string Direction,
        string Set,
        int Nights,
        int MedianNamesPerNight,
        int MedianSubjectsPerNight,
        int NamesOverRange,
        int Rows,
        double MeanDaysApart,
        int SameSessionRows);

    /// <summary>Whether the short side's tightest gate is applied as it stands or set aside.</summary>
    public enum ReachedCeiling
    {
        AsItStands,
        Ignored,
    }

    /// <summary>
    /// One panel: the paired difference per night over the range, run through the same block
    /// bootstrap 3.6 reads.
    ///
    /// The same arithmetic as `ScoreboardBuilder.Series`, over the calibration tables. Not a second
    /// estimator: a reconstructed interval computed a different way would be a fact about which
    /// code produced it.
    /// </summary>
    private Panel Measure(
        SqliteConnection connection,
        string direction,
        string set,
        IReadOnlyList<DateOnly> range,
        ReachedCeiling ceiling)
    {
        var nights = new List<PairedInterval.Night>();

        // The gate filter is a constant chosen by comparing against a constant, so nothing from
        // outside reaches the statement.
        // `check_results` is a JSON array of {name, passed, value, note}, so the gate is read with
        // SQLite's own JSON functions rather than matched as text. A LIKE pattern was tried first
        // and it silently matched nothing at all: every short panel came back withheld at nought
        // nights, which reads exactly like a side with no evidence rather than like a filter that
        // never fired.
        string gate = ceiling == ReachedCeiling.AsItStands && direction == SetupDirection.Short
            ? """
                AND EXISTS (SELECT 1 FROM json_each(s.check_results) je
                             WHERE json_extract(je.value, '$.name') = 'reached-ceiling'
                               AND json_extract(je.value, '$.passed') = 1)
              """
            : string.Empty;

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.as_of,
                   AVG(sf.return_signed_num - cf.control_mean) AS difference,
                   COUNT(*) AS pairs,
                   AVG((sf.return_signed_num - cf.control_mean)
                     * (sf.return_signed_num - cf.control_mean)) AS mean_square
              FROM calibration_setup s
              JOIN (SELECT subject_id, CAST(return_signed AS REAL) AS return_signed_num
                      FROM calibration_forward_return
                     WHERE subject_kind = 'setup' AND horizon_days = @horizon
                       AND filled_at <= @computed_at) sf
                ON sf.subject_id = s.setup_id
              JOIN (SELECT c.setup_id, AVG(CAST(f.return_signed AS REAL)) AS control_mean
                      FROM calibration_control_setup c
                      JOIN calibration_forward_return f
                        ON f.subject_id = c.control_id AND f.subject_kind = 'control'
                       AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                     WHERE c.control_set = @set AND c.drawn_at <= @computed_at
                     GROUP BY c.setup_id) cf
                ON cf.setup_id = s.setup_id
             WHERE s.direction = @direction
               AND s.as_of >= @from AND s.as_of <= @to
               {gate}
             GROUP BY s.as_of
             ORDER BY s.as_of
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@set", set);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(range[0]));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(range[^1]));

        // Bounded on the run's own instant, like the scoreboard's equivalent. A reconstructed row is
        // not evidence and the read that produces it still obeys the rule: a replay can hold draws
        // and fills made after the instant being answered for, and an unbounded read is the shape
        // the rule refuses whether or not today's ordering happens to make it safe.
        // see: A reader's signature does not establish point-in-time; the query does
        command.Parameters.AddWithValue(
            "@computed_at", StoreText.TimestampToStorageText(_clock.UtcNow));

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                double difference = reader.GetDouble(1);
                int pairs = reader.GetInt32(2);

                double spread = pairs < 2
                    ? 0d
                    : Math.Sqrt(Math.Max(
                        0d,
                        (reader.GetDouble(3) - (difference * difference)) * pairs / (pairs - 1)));

                nights.Add(new PairedInterval.Night(
                    StoreText.StorageTextToDate(reader.GetString(0)),
                    (decimal)difference,
                    pairs,
                    (decimal)spread));
            }
        }

        PairedInterval.Estimate? estimate = PairedInterval.Of(
            nights, MeasurementParameters.BootstrapBlockSessions, MeasurementParameters.BootstrapDraws);

        // The discounts beside the figure they produced. A panel reading "262 needed, 65 held" is
        // read as thinness, and thinness is one of four things it can be.
        return new Panel(
            direction, set, ceiling, nights.Count, estimate, PairedInterval.Disperse(nights));
    }

    private void Report(ReadResult result)
    {
        string window = $"{result.Sessions} session(s) requested, {result.SessionsCovered} covered, "
            + $"{result.From:yyyy-MM-dd} to {result.To:yyyy-MM-dd}";

        Console.WriteLine($"{Name}: {window}");
        Console.WriteLine($"{Name}: wall clock {result.Wall.TotalSeconds:F1}s, pool {result.Pool} candidate(s) at its widest");
        Console.WriteLine($"{Name}: {result.Drawn} control(s) drawn, {result.Outcomes} outcome(s) filled");
        Console.WriteLine();

        // Every figure carries the range. Stated per line rather than once above, because a figure
        // copied out of this report without its population is a figure that fits four populations.
        foreach (Panel panel in result.Panels)
        {
            string bias = panel.Direction == SetupDirection.Long
                ? "survivorship: CEILING, the honest figure is lower"
                : "survivorship: FLOOR, the honest figure is higher";

            string gate = panel.Direction != SetupDirection.Short
                ? string.Empty
                : panel.Ceiling == ReachedCeiling.AsItStands
                    ? ", reached-ceiling as it stands at 2 of 3 clauses (fewest rows)"
                    : ", reached-ceiling set aside entirely (most rows)";

            Console.Write($"{Name}: RECONSTRUCTED {panel.Direction}/{panel.Set}{gate}");
            Console.Write($" over {result.SessionsCovered} session(s) {result.From:yyyy-MM-dd}..{result.To:yyyy-MM-dd}");

            if (panel.Estimate is not PairedInterval.Estimate e)
            {
                Console.WriteLine($": withheld, {panel.Nights} night(s) against a floor of "
                    + $"{MeasurementParameters.MinimumSessions}; {bias}");
                Console.WriteLine($"    {Discounts(panel)}");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"    mean {e.Mean:+0.0000;-0.0000;0.0000} [{e.Low:+0.0000;-0.0000;0.0000}, "
                + $"{e.High:+0.0000;-0.0000;0.0000}], {e.Rows} row(s), {e.Nights} night(s), "
                + $"{e.EffectiveObservations} effective of {MeasurementParameters.MinimumEffectiveObservations} needed, "
                + $"over {result.SessionsCovered} session(s) {result.From:yyyy-MM-dd}..{result.To:yyyy-MM-dd}");
            Console.WriteLine($"    {Discounts(panel)}");
            Console.WriteLine($"    {bias}, RECONSTRUCTED, not evidence, does not move 3.6");
        }

        ReportDiagnosis(result);

        Console.WriteLine();
        Console.WriteLine($"{Name}: evidence store before {result.Before}");
        Console.WriteLine($"{Name}: evidence store after  {result.After}");
        Console.WriteLine($"{Name}: evidence store untouched: {(result.Before == result.After ? "yes" : "NO")}");
    }

    /// <summary>
    /// Why the tight set came up short, per subject and per direction.
    ///
    /// <b>Measurement only.</b> Nothing here is read by any stage, no threshold moves, and the
    /// figures are stated over the same range as the panels above them because they are figures
    /// about the same population.
    /// </summary>
    private static void ReportDiagnosis(ReadResult result)
    {
        if (result.Diagnosis is not TightDrawDiagnosis diagnosis
            || result.TightDrawn is not IReadOnlyDictionary<string, int> actual)
        {
            return;
        }

        string range = $"over {result.SessionsCovered} session(s) "
            + $"{result.From:yyyy-MM-dd}..{result.To:yyyy-MM-dd}";

        Console.WriteLine();

        // <b>The mood distribution, because it is the dimension nothing had ever measured.</b> The
        // tight pool is the sessions sharing the subject's label, so a window one label dominates
        // gives the draw almost everything to reach across and a window that alternates gives it
        // little. It became computable for a reconstructed session only when the scoring moved to
        // Core, so no run before this one could have reported it.
        var moods = diagnosis.Moods
            .Where(m => m.Key >= result.From && m.Key <= result.To)
            .GroupBy(m => m.Value ?? "(unlabelled)")
            .OrderByDescending(g => g.Count())
            .ToList();

        Console.WriteLine($"{Name}: market mood {range}: "
            + string.Join(", ", moods.Select(g => $"{g.Key} {g.Count()}")));

        // The prediction against the rows written. A counting pass that re-states a filter can drift
        // from the filter while still answering, so the drift is measured rather than assumed away.
        var checkable = diagnosis.Entries.Where(e => actual.ContainsKey(e.SetupId)).ToList();
        int disagreed = checkable.Count(e => e.Predicted != actual[e.SetupId]);

        foreach (ReuseRow row in result.Reuse ?? [])
        {
            Console.WriteLine($"{Name}: CONTROL REUSE {row.Direction}/{row.Set} {range}: median "
                + $"{row.MedianNamesPerNight} distinct name(s) a night serving a median "
                + $"{row.MedianSubjectsPerNight} subject(s), {row.NamesOverRange} distinct name(s) "
                + $"over the whole range in {row.Rows} row(s); mean {row.MeanDaysApart:F1} calendar "
                + $"day(s) from the subject's own session, {row.SameSessionRows} row(s) on it "
                + $"({(row.Rows == 0 ? 0m : (decimal)row.SameSessionRows * 100 / row.Rows):F1}%)");
        }

        Console.WriteLine($"{Name}: diagnosis checked against the rows written on "
            + $"{checkable.Count} subject(s), {disagreed} disagreement(s)"
            + (disagreed == 0 ? "" : " — THE DIAGNOSIS BELOW IS NOT THE DRAW"));

        foreach (string direction in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            var entries = diagnosis.Entries
                .Where(e => e.Direction == direction && e.AsOf >= result.From && e.AsOf <= result.To)
                .ToList();

            if (entries.Count == 0)
            {
                Console.WriteLine($"{Name}: TIGHT YIELD {direction}: no subject {range}");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"{Name}: TIGHT YIELD {direction}, {entries.Count} subject(s) {range}");

            // The distribution, not the total. Read from the rows written rather than from the
            // prediction, so the figure is what the draw did.
            for (int drawn = MeasurementParameters.ControlsPerSet; drawn >= 0; drawn--)
            {
                int held = entries.Count(e => actual.GetValueOrDefault(e.SetupId, 0) == drawn);
                Console.WriteLine($"    drew {drawn}: {held} subject(s), "
                    + $"{(decimal)held * 100 / entries.Count:F1}% of {entries.Count}");
            }

            // The funnel over every subject that had figures, in a fixed order, not only over the
            // ones that came up short. Reporting it only for the short subjects would leave the
            // pool sizes unstated in the case where nothing came up short, which is the case where
            // the reader most needs to see how wide the pool was.
            var faced = entries.Where(e => !e.NoFigures).ToList();

            if (faced.Count > 0)
            {
                Console.WriteLine($"    funnel, median over {faced.Count} subject(s) with figures: "
                    + $"the night's pool {Median(faced, e => e.PoolOnTheNight)} name(s) "
                    + $"-> same mood {Median(faced, e => e.PoolAfterMood)} "
                    + $"-> same ladder {Median(faced, e => e.PoolAfterLadder)} "
                    + $"-> drawable {Median(faced, e => e.DistinctNames)}, against "
                    + $"{MeasurementParameters.ControlsPerSet} wanted");
                Console.WriteLine("    turnover and daily range eliminate nobody: they are distances "
                    + "that order the survivors. Turnover eliminates once and earlier, as the "
                    + "liquidity floor on pool membership.");

                // The mood clause dropping nobody is the decision's central claim, so it is stated
                // as a count and as the number of rows where it did not hold rather than as prose.
                int moodExcluded = faced.Count(e => e.WithoutMood != e.DistinctNames);

                Console.WriteLine($"    mood dropped, ladder kept: median {Median(faced, e => e.WithoutMood)} "
                    + $"name(s), differing from the drawable count on {moodExcluded} subject(s); "
                    + $"ladder dropped, mood kept: median {Median(faced, e => e.WithoutLadder)} name(s)");
            }

            var shortOfFive = entries
                .Where(e => actual.GetValueOrDefault(e.SetupId, 0) < MeasurementParameters.ControlsPerSet)
                .ToList();

            if (shortOfFive.Count == 0)
            {
                Console.WriteLine("    every subject drew five; no dimension eliminated anybody");
                continue;
            }

            int noFigures = shortOfFive.Count(e => e.NoFigures);
            int unlabelled = shortOfFive.Count(e => !e.NoFigures && e.Mood is null);

            // An unlabelled night draws its tight set like any other now, so it is counted beside
            // the others rather than excluded from them. Under the superseded ruling it emptied the
            // tight pool and was its own cause.
            var eliminated = shortOfFive.Where(e => !e.NoFigures).ToList();

            Console.WriteLine($"    short of five: {shortOfFive.Count}, of which {noFigures} had no "
                + $"figures on their own night and {eliminated.Count} faced a pool that eliminated "
                + $"them. {unlabelled} sat on an unlabelled session, which is no longer a cause of "
                + "its own and is reported so that stays visible");

            if (eliminated.Count == 0)
            {
                continue;
            }

            // The funnel, in a fixed order, over the subjects a pool eliminated. Medians rather
            // than means, because one subject on a huge pool would carry the average past every
            // other subject in the group.
            Console.WriteLine($"    funnel, median over those {eliminated.Count}: "
                + $"the night's pool {Median(eliminated, e => e.PoolOnTheNight)} name(s) "
                + $"-> same mood {Median(eliminated, e => e.PoolAfterMood)} "
                + $"-> same ladder {Median(eliminated, e => e.PoolAfterLadder)} "
                + $"-> drawable {Median(eliminated, e => e.DistinctNames)}");
            int withoutMood = eliminated.Count(e => e.WithoutMood >= MeasurementParameters.ControlsPerSet);
            int withoutLadder = eliminated.Count(e => e.WithoutLadder >= MeasurementParameters.ControlsPerSet);

            Console.WriteLine($"    with the mood dropped and the ladder kept: median "
                + $"{Median(eliminated, e => e.WithoutMood)} name(s), {withoutMood} of "
                + $"{eliminated.Count} would reach five");
            Console.WriteLine($"    with the ladder dropped and the mood kept: median "
                + $"{Median(eliminated, e => e.WithoutLadder)} name(s), {withoutLadder} of "
                + $"{eliminated.Count} would reach five");

            string verdict = withoutMood == withoutLadder
                ? "neither dimension alone accounts for it"
                : withoutMood > withoutLadder
                    ? "the market mood is the dimension doing the eliminating"
                    : "the ladder grade is the dimension doing the eliminating";

            Console.WriteLine($"    {verdict}, {range}");

            foreach (TightDrawDiagnosis.Entry sample in eliminated
                .OrderBy(e => e.DistinctNames)
                .ThenBy(e => e.SetupId, StringComparer.Ordinal)
                .Take(8))
            {
                Console.WriteLine($"      {sample.AsOf:yyyy-MM-dd} {sample.Ticker} "
                    + $"ladder {sample.LadderGrade ?? "(ungraded)"} mood {sample.Mood ?? "(unlabelled)"}: "
                    + $"{sample.PoolOnTheNight} -> {sample.PoolAfterMood} -> {sample.PoolAfterLadder} "
                    + $"-> {sample.DistinctNames} distinct, drew {actual.GetValueOrDefault(sample.SetupId, 0)}, "
                    + $"without mood {sample.WithoutMood}, without ladder {sample.WithoutLadder}");
            }
        }
    }

    /// <summary>
    /// What a panel spent its rows on, which is the half of "262 needed, 65 held" the figure omits.
    ///
    /// A panel short of the minimum is read as thin. It can instead be repeating itself across
    /// nights, or carrying pairs that all move together within a night, and those are different
    /// findings with different repairs. Reported for every panel including a withheld one, because
    /// a withheld panel is exactly the one a reader wants the reason for.
    /// </summary>
    private static string Discounts(Panel panel)
    {
        PairedInterval.Dispersion d = panel.Dispersion;
        string design = d.Design is decimal effect
            ? effect.ToString("F2", CultureInfo.InvariantCulture)
            : "not measurable";

        return $"discounts: {d.Rows} row(s) over {d.Nights} night(s), {d.IndependentRows:F1} were the "
            + $"nights independent of each other; across-night factor {d.Serial:F4}, within-night "
            + $"design effect {design}, leaving {d.Effective} effective";
    }

    private static int Median(
        IReadOnlyList<TightDrawDiagnosis.Entry> entries, Func<TightDrawDiagnosis.Entry, int> of)
    {
        int[] values = [.. entries.Select(of).Order()];
        return values.Length == 0 ? 0 : values[values.Length / 2];
    }

    /// <summary>One panel of the reconstructed read.</summary>
    public sealed record Panel(
        string Direction,
        string Set,
        ReachedCeiling Ceiling,
        int Nights,
        PairedInterval.Estimate? Estimate,
        PairedInterval.Dispersion Dispersion);

    /// <summary>
    /// The evidence store's row counts, which this pass must not move.
    ///
    /// Counted rather than argued. The permission the whole read rests on is that it writes only to
    /// calibration tables, and a count before and after is what makes that a fact about the run
    /// rather than a property of the code somebody read.
    /// </summary>
    public sealed record EvidenceCounts(long Setup, long Control, long Forward)
    {
        public static EvidenceCounts Read(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            return new EvidenceCounts(
                Rows(connection, SubjectTables.Evidence.Setup),
                Rows(connection, SubjectTables.Evidence.Control),
                Rows(connection, SubjectTables.Evidence.ForwardReturn));
        }

        private static long Rows(SqliteConnection connection, string table)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(command.ExecuteScalar() ?? 0L);
        }

        public override string ToString() =>
            $"setup {Setup}, control_setup {Control}, forward_return {Forward}";
    }

    /// <summary>What one rung produced, with the range it was produced over.</summary>
    public sealed record ReadResult(
        int Sessions,
        DateOnly From,
        DateOnly To,
        int SessionsCovered,
        int Pool,
        int Drawn,
        int Outcomes,
        IReadOnlyList<Panel> Panels,
        EvidenceCounts Before,
        EvidenceCounts After,
        TimeSpan Wall,
        bool Failed,
        TightDrawDiagnosis? Diagnosis = null,
        IReadOnlyDictionary<string, int>? TightDrawn = null,
        IReadOnlyList<ReuseRow>? Reuse = null)
    {
        public static ReadResult Empty(int sessions, TimeSpan wall) =>
            new(sessions, default, default, 0, 0, 0, 0, [],
                new EvidenceCounts(0, 0, 0), new EvidenceCounts(0, 0, 0), wall, Failed: true);
    }
}
