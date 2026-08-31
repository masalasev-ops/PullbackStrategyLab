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
/// covers a stated number of recent sessions; the draw is quadratic in that range, because a tight
/// control may be drawn from any earlier session sharing the mood. Stating the range once at the top
/// would let a figure be copied out of the report without it, and the range is what says how far
/// back the survivorship exposure reaches. It is not a lookback: no bound is added to
/// `ControlSampler`, whose own source says that fix is a decision nobody has taken.
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

        var reach = new HashSet<DateOnly>(range);
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

        foreach (DateOnly session in warmup.Concat(range))
        {
            var windows = new Dictionary<string, IReadOnlyList<StoredDailyBar>>(StringComparer.Ordinal);

            foreach (string ticker in members)
            {
                windows[ticker] = DailyBarReader.Read(
                    connection, ticker, session, LongSetupDetector.HistorySessions, observedBefore);
            }

            source.Rank(session, windows);

            if (!reach.Contains(session))
            {
                continue;
            }

            ControlResult result = _controls.Draw(
                connection, source, session, SubjectTables.Calibration, reach);

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
            panels, before, after, wall.Elapsed, Failed: false);
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

        return new Panel(direction, set, ceiling, nights.Count, estimate);
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
                continue;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"    mean {e.Mean:+0.0000;-0.0000;0.0000} [{e.Low:+0.0000;-0.0000;0.0000}, "
                + $"{e.High:+0.0000;-0.0000;0.0000}], {e.Rows} row(s), {e.Nights} night(s), "
                + $"{e.EffectiveObservations} effective of {MeasurementParameters.MinimumEffectiveObservations} needed, "
                + $"over {result.SessionsCovered} session(s) {result.From:yyyy-MM-dd}..{result.To:yyyy-MM-dd}");
            Console.WriteLine($"    {bias}, RECONSTRUCTED, not evidence, does not move 3.6");
        }

        Console.WriteLine();
        Console.WriteLine($"{Name}: evidence store before {result.Before}");
        Console.WriteLine($"{Name}: evidence store after  {result.After}");
        Console.WriteLine($"{Name}: evidence store untouched: {(result.Before == result.After ? "yes" : "NO")}");
    }

    /// <summary>One panel of the reconstructed read.</summary>
    public sealed record Panel(
        string Direction, string Set, ReachedCeiling Ceiling, int Nights, PairedInterval.Estimate? Estimate);

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
        bool Failed)
    {
        public static ReadResult Empty(int sessions, TimeSpan wall) =>
            new(sessions, default, default, 0, 0, 0, 0, [],
                new EvidenceCounts(0, 0, 0), new EvidenceCounts(0, 0, 0), wall, Failed: true);
    }
}
