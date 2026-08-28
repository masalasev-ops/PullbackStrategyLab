using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The panels the scoreboard shows, computed nightly and stored as they stood.
///
/// <b>Three bands, none denominated in money.</b> Band 0 asks whether the record is healthy. Band 1
/// asks whether the pattern exists at all, which is the project's central question and the one
/// phase 3 answers. Band 2 asks whether the lab can sort what it finds.
///
/// <b>Every panel carries its own count, and a number without one is not shown.</b> The failure this
/// whole system exists to avoid is reading a pattern in forty observations, and a scoreboard that
/// prints a figure with no denominator is the most efficient way to commit it.
///
/// <b>Band 2's loss-cause panel is not built here and says so.</b> It needs closed trades from
/// LossClassifier, which arrives at 4.10. The panel declares the checkpoint that fills it rather
/// than being quietly absent, on the pattern the navigation already uses.
/// </summary>
public sealed class ScoreboardBuilder
{
    public const string Name = "scoreboard";

    /// <summary>How many rank deciles band 2 reports. Ten, because it is a decile curve.</summary>
    public const int Deciles = 10;

    /// <summary>
    /// The two populations this page computes over, named so a panel can say which it used.
    ///
    /// <b>Flagged is every setup the detectors recorded</b>, which is what ARCHITECTURE means by the
    /// word: its worked night is twenty-two flagged, of which fourteen pass every check, and all
    /// twenty-two are followed up. The evidence store's whole purpose is that a stock nobody bought
    /// is worth as much as one that filled.
    ///
    /// <b>Candidates are the subset that passed every gating check and carry a rank</b>, which a
    /// decile curve needs because a decile is a position in an ordering.
    ///
    /// They differ by three orders of magnitude at the calibrated thresholds, so a panel that cannot
    /// say which it used is a panel a reader will compare against the wrong one.
    /// see: The subject is the flagged setup population, not the trade log
    /// </summary>
    public const string Flagged = "every flagged setup";

    /// <summary>The ranked subset, which is what a decile curve can be computed over.</summary>
    public const string Candidates = "capped candidates only";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ScoreboardBuilder(
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

        ScoreboardResult result = Build(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Panels} panel(s) written");
        Console.WriteLine($"{Name}: {result.WithInterval} carrying an interval, {result.Withheld} withheld for want of a sample");
        Console.WriteLine($"{Name}: {result.Attempted} attempted, {result.Skipped} skipped");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        if (result.Outcome == RunOutcome.Failed && result.Skipped == result.Attempted && result.Attempted > 0)
        {
            Console.Error.WriteLine(
                $"{Name}: all {result.Skipped} panel(s) were skipped, so {asOf:yyyy-MM-dd} already carries panels and "
                + "nothing was rebuilt. The insert is ON CONFLICT DO NOTHING and there is no update path, so an "
                + "in-place rebuild of a past date writes nothing and would otherwise report a clean run. To rebuild "
                + "it, restore the snapshot taken before that night and re-run, or delete that date's panels first.");
        }

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One day's panels.</summary>
    public ScoreboardResult Build(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "scoreboard");

        DateTimeOffset computedAt = _clock.UtcNow;
        var panels = new List<Panel>();

        panels.AddRange(Health(connection, asOf, _options.SessionZone));

        foreach (string direction in new[] { "long", "short" })
        {
            panels.AddRange(AgainstControls(connection, direction, asOf, computedAt));
            panels.AddRange(RankDeciles(connection, direction, asOf, computedAt));
            panels.AddRange(CeilingGap(connection, direction, asOf, _options.SessionZone));
        }

        int skipped = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (Panel panel in panels)
            {
                if (!Insert(connection, transaction, asOf, panel, computedAt))
                {
                    skipped++;
                    run.CountSkipped();
                }
            }

            transaction.Commit();
        }

        // A build that wrote nothing at all is a no-op wearing a clean run. It happens when the date
        // already carries panels, because the insert is ON CONFLICT DO NOTHING and there is no
        // update path: the supported way to rebuild a past date is to restore the snapshot taken
        // before that night and re-run, or to delete that date's panels first. Failing here rather
        // than refusing up front keeps a first build for a date working and a genuine rebuild
        // loud, which is the pair that matters.
        //
        // Some panels skipped and some written is a different thing and is not a failure: it means
        // the date gained a panel the earlier build did not produce. It is still reported.
        RunOutcome outcome = panels.Count > 0 && skipped == panels.Count
            ? RunOutcome.Failed
            : RunOutcome.Clean;

        RunSummary summary = run.Complete(outcome);

        return new ScoreboardResult(
            asOf,
            panels.Count,
            panels.Count(p => p.Low is not null),
            panels.Count(p => string.Equals(p.Figure, "withheld", StringComparison.Ordinal)),
            summary.RowsWritten,
            summary.CallsUsed,
            outcome,
            panels.Count,
            skipped);
    }

    /// <summary>
    /// Band 0. Account-wide, so no direction: nights recorded, degraded runs, setups on file, and
    /// how much of the population rests on an answer that arrived late.
    ///
    /// <b>It reads red when degraded nights exceed 5% of the record</b>, because excluded nights are
    /// not missing at random: a night the lab lost is more likely to be a night something unusual
    /// happened, and a series with those quietly absent flatters every figure below it.
    ///
    /// <b>The corrections panel is the reader the correction mark needed.</b> The superseded rule
    /// recorded a mark "so a later reader can exclude corrected rows" and shipped with a guard that
    /// made corrected rows impossible, so the mark had neither a producer nor a consumer: a claim
    /// about a surface, asserted against a store. This is the surface. A reader who wants to know how
    /// much of a figure rests on a late answer can see the count and the worst lateness here rather
    /// than deriving it, and a corpus in which corrections became common would say so on the page
    /// rather than in a column nobody queries.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    private static IReadOnlyList<Panel> Health(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        int nights = Count(connection, "SELECT COUNT(DISTINCT as_of) FROM setup WHERE as_of <= @as_of", asOf, sessionZone);
        int degraded = Count(
            connection,
            "SELECT COUNT(DISTINCT started_at) FROM run_log WHERE outcome <> 'clean' AND started_at <= @end_of_day",
            asOf, sessionZone);
        int setups = Count(connection, "SELECT COUNT(*) FROM setup WHERE as_of <= @as_of", asOf, sessionZone);

        int corrected = Count(
            connection,
            "SELECT COUNT(*) FROM setup WHERE as_of <= @as_of AND corrected_at IS NOT NULL",
            asOf, sessionZone);

        // The worst lateness rather than the mean, because the question a bound invites is how close
        // anything came to it, and a mean over mostly-zero rows answers a different one.
        int worstLateness = Count(
            connection,
            "SELECT COALESCE(MAX(correction_lateness_minutes), 0) FROM setup WHERE as_of <= @as_of",
            asOf, sessionZone);

        return
        [
            new Panel("band0.nightsRecorded", null, nights.ToString(CultureInfo.InvariantCulture), null, null, nights, null, Flagged),
            new Panel("band0.degradedRuns", null, degraded.ToString(CultureInfo.InvariantCulture), null, null, nights, null, "runs recorded"),
            new Panel("band0.setupsOnFile", null, setups.ToString(CultureInfo.InvariantCulture), null, null, setups, null, Flagged),
            new Panel("band0.correctedRows", null, corrected.ToString(CultureInfo.InvariantCulture), null, null, setups, null, Flagged),
            new Panel("band0.worstLatenessMinutes", null, worstLateness.ToString(CultureInfo.InvariantCulture), null, null, corrected, null, "corrected rows"),
        ];
    }

    /// <summary>
    /// Band 1. The flagged population against each control set, as a paired difference with an
    /// interval.
    ///
    /// <b>Paired, and the pairing is what makes it honest.</b> A setup's difference is its own return
    /// less the mean of its own matched controls, so the market factor the two share cancels rather
    /// than being adjusted for. The nightly means are then resampled in blocks, because a ten-day
    /// label overlaps its neighbours and an interval that ignored that would be too narrow exactly
    /// where confidence matters most.
    /// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
    /// </summary>
    private static IReadOnlyList<Panel> AgainstControls(
        SqliteConnection connection, string direction, DateOnly asOf, DateTimeOffset computedAt)
    {
        var panels = new List<Panel>();

        foreach (string set in new[] { "loose", "tight" })
        {
            IReadOnlyList<PairedInterval.Night> series = Series(connection, direction, set, asOf, computedAt);

            PairedInterval.Estimate? estimate = PairedInterval.Of(
                series, MeasurementParameters.BootstrapBlockSessions, MeasurementParameters.BootstrapDraws);

            if (estimate is null)
            {
                // Withheld rather than printed wide. A panel showing an interval built from three
                // nights invites a reading, and the count beside it is not enough to stop that.
                //
                // <b>The counts are reported anyway, and from the first night.</b> The figure is
                // withheld because it would be read; the counts are the thing a reader is supposed
                // to watch, because 3.6 fires on the effective one. They are meaningless for the
                // first fortnight, which a number climbing from nothing says better than a date on a
                // calendar does.
                panels.Add(new Panel(
                    $"band1.vs{Capitalise(set)}", direction, "withheld", null, null,
                    series.Sum(n => n.Pairs),
                    PairedInterval.EffectiveObservations(series),
                    Flagged,
                    MeasurementParameters.MinimumEffectiveObservations,
                    WithheldBecause(
                        Shortage.Measure(connection, direction, set, asOf, computedAt),
                        series.Count)));
                continue;
            }

            panels.Add(new Panel(
                $"band1.vs{Capitalise(set)}",
                direction,
                PairedInterval.Figure(estimate.Mean),
                PairedInterval.Figure(estimate.Low),
                PairedInterval.Figure(estimate.High),
                estimate.Rows,
                estimate.EffectiveObservations,
                Flagged,
                MeasurementParameters.MinimumEffectiveObservations));
        }

        return panels;
    }

    /// <summary>
    /// Why a band 1 panel is showing no figure, in words, on the panel.
    ///
    /// <b>It named the wrong cause for the whole of phase 3, and that is worse than naming none.</b>
    /// It branched on the length of the difference series alone, so an empty series always printed
    /// "no session has a closed horizon yet". The series was empty because nothing ever wrote a
    /// control outcome, so with thirty nights of closed horizons in the store the panel still said
    /// the horizons had not closed. <b>A diagnostic that points away from the defect sends a reader
    /// to wait for something that has already happened.</b> The shortage is now measured rather than
    /// inferred, and the panel names which of the four it is.
    ///
    /// <b>The four are settled by different things and they arrive in order.</b> Nothing flagged, so
    /// there is no subject. Flagged but no setup outcome closed, which is the ten sessions everybody
    /// expects to wait. Setup outcomes closed but no control outcome, which is a defect rather than a
    /// wait and now says so in those words. And pairs on too few sessions, which is the bootstrap's
    /// own floor and the only one the old text ever got right.
    ///
    /// <b>The minimum sample is not one of the four.</b> The bootstrap needs twice its block length
    /// of sessions whatever the rows carry; the minimum is a separate statement shown beside the
    /// counts. They can contradict each other, which is why both are on the panel: a fortnight of
    /// very wide nights reaches the minimum before it reaches twenty sessions.
    ///
    /// <b>The population is not one of the reasons and cannot be.</b> Band 1 reads `setup`; a
    /// historical detector run writes to `calibration_setup`, which nothing downstream reads. That is
    /// settled by construction rather than by waiting, which is exactly why a reader of a withheld
    /// panel should not be left wondering whether it is the cause.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    private static string WithheldBecause(Shortage shortage, int sessions)
    {
        int needed = MeasurementParameters.BootstrapBlockSessions * 2;
        int horizon = MeasurementParameters.ScoringHorizonSessions;

        if (shortage.Setups == 0)
        {
            return "no setup has been flagged on this side yet, so there is nothing to compare";
        }

        if (shortage.ClosedSetupOutcomes == 0)
        {
            return $"{Count(shortage.Setups)} setup(s) flagged and none has closed its {horizon}-session horizon yet, so there is no series to take an interval over";
        }

        if (shortage.ClosedControlOutcomes == 0)
        {
            return $"{Count(shortage.ClosedSetupOutcomes)} setup outcome(s) have closed and no control outcome has, so no pair exists. That is a shortage of control outcomes rather than of time, and waiting does not fix it";
        }

        if (sessions == 0)
        {
            return $"{Count(shortage.ClosedSetupOutcomes)} setup and {Count(shortage.ClosedControlOutcomes)} control outcome(s) have closed but none pair up on the same session, so there is no series to take an interval over";
        }

        if (sessions < needed)
        {
            return $"only {Count(sessions)} session(s) carry a pair and a block bootstrap needs {needed}, which is a shortage of sessions rather than of evidence";
        }

        return $"{Count(sessions)} session(s) carry a pair and the blocks they form do not differ, so the interval would have no width. An interval of no width clears zero always and is withheld instead";
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Capitalise(string set) =>
        string.Concat(char.ToUpperInvariant(set[0]), set[1..]);

    /// <summary>
    /// Writes one panel, and says whether it wrote.
    ///
    /// <b>The return value is the whole point of this method having one.</b> The insert is
    /// <c>ON CONFLICT DO NOTHING</c>, so a build for a date that already carries panels writes none
    /// of them and, until 3.9(e), reported a clean run either way. A rebuild path that reports
    /// success having written nothing is the failure shape this lab keeps producing, and it is worse
    /// than a crash because the operator's next act is to go and read the panels they think they
    /// just rebuilt.
    /// </summary>
    private static bool Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly asOf,
        Panel panel,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO scoreboard
                (as_of, panel, direction, figure, low, high, n_rows, n_effective, population,
                 n_minimum, withheld_because, computed_at)
            VALUES (@as_of, @panel, @direction, @figure, @low, @high, @n_rows, @n_effective,
                    @population, @n_minimum, @withheld_because, @computed_at)
            -- No conflict target. The primary key does not constrain an account-wide panel,
            -- because SQLite treats nulls as distinct and `direction` is null on every band 0
            -- row; migration 030 adds the partial unique index that does. Naming the primary
            -- key here would raise on a violation of that index rather than skipping it.
            ON CONFLICT DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@panel", panel.Name);
        command.Parameters.AddWithValue("@direction", (object?)panel.Direction ?? DBNull.Value);
        command.Parameters.AddWithValue("@figure", panel.Figure);
        command.Parameters.AddWithValue("@low", (object?)panel.Low ?? DBNull.Value);
        command.Parameters.AddWithValue("@high", (object?)panel.High ?? DBNull.Value);
        command.Parameters.AddWithValue("@n_rows", panel.Rows);
        command.Parameters.AddWithValue("@n_effective", (object?)panel.Effective ?? DBNull.Value);
        command.Parameters.AddWithValue("@population", panel.Population);
        command.Parameters.AddWithValue("@n_minimum", (object?)panel.Minimum ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@withheld_because", (object?)panel.WithheldBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// What the store actually holds behind a withheld panel, so the reason can name the shortage
    /// rather than assume it.
    ///
    /// Three counts on the same bound as the panel itself. Measured per direction and per control
    /// set, because one side or one set can be short of controls while the other is not, and a
    /// single number covering both would send a reader to look at the wrong half.
    /// </summary>
    private sealed record Shortage(int Setups, int ClosedSetupOutcomes, int ClosedControlOutcomes)
    {
        public static Shortage Measure(
            SqliteConnection connection,
            string direction,
            string set,
            DateOnly asOf,
            DateTimeOffset computedAt)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM setup s
                    WHERE s.direction = @direction AND s.as_of <= @as_of),
                  (SELECT COUNT(*) FROM setup s
                     JOIN forward_return f
                       ON f.subject_id = s.setup_id AND f.subject_kind = 'setup'
                      AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                    WHERE s.direction = @direction AND s.as_of <= @as_of),
                  (SELECT COUNT(*) FROM setup s
                     JOIN control_setup c ON c.setup_id = s.setup_id AND c.control_set = @set
                                          AND c.drawn_at <= @computed_at
                     JOIN forward_return f
                       ON f.subject_id = c.control_id AND f.subject_kind = 'control'
                      AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                    WHERE s.direction = @direction AND s.as_of <= @as_of)
                """;
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@set", set);
            command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

            using SqliteDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? new Shortage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
                : new Shortage(0, 0, 0);
        }
    }

    /// <summary>
    /// Band 2's first panel. Mean forward return by rank decile.
    ///
    /// A downward slope from the first decile to the tenth means the ordering carries information. A
    /// flat line means the rank is decorative and the nightly cap is truncating at random, which is a
    /// different failure from the pattern not working and would otherwise look the same.
    /// </summary>
    private static IReadOnlyList<Panel> RankDeciles(
        SqliteConnection connection, string direction, DateOnly asOf, DateTimeOffset computedAt)
    {
        var byDecile = new SortedDictionary<int, List<decimal>>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.rank, f.return_signed
              FROM setup s
              JOIN forward_return f
                ON f.subject_id = s.setup_id AND f.subject_kind = 'setup'
               AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
             WHERE s.direction = @direction AND s.as_of <= @as_of AND s.rank IS NOT NULL
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                int rank = reader.GetInt32(0);
                int decile = Math.Clamp(((rank - 1) * Deciles / Math.Max(1, NightlyCapTotal)) + 1, 1, Deciles);

                if (!byDecile.TryGetValue(decile, out List<decimal>? returns))
                {
                    returns = [];
                    byDecile[decile] = returns;
                }

                returns.Add(StoreText.StorageTextToPrice(reader.GetString(1)));
            }
        }

        return
        [
            .. byDecile.Select(d => new Panel(
                $"band2.decile{d.Key.ToString(CultureInfo.InvariantCulture)}",
                direction,
                PairedInterval.Figure(d.Value.Average()),
                null,
                null,
                d.Value.Count,
                null,
                Candidates)),
        ];
    }

    private static int NightlyCapTotal => Core.Detection.NightlyCap.Total;

    /// <summary>
    /// Band 2's second panel. The gap between what was achieved and what was available.
    ///
    /// Read straight off `ceiling_bound` rather than recomputed, because two implementations of a
    /// bound would eventually disagree and the scoreboard would be the last place anyone looked.
    /// </summary>
    private static IReadOnlyList<Panel> CeilingGap(
        SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bound, achieved, subjects FROM ceiling_bound
             WHERE direction = @direction AND as_of <= @as_of
               AND computed_at <= @computed_before
             ORDER BY as_of DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        // The bound is recomputed weekly, so a week can carry more than one row over its life and
        // the panel must read the one that existed on the night it is building. Bounding the as-of
        // alone picks the right week and can still read a bound computed afterwards.
        command.Parameters.AddWithValue("@computed_before", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            // No bound yet. Withheld rather than a gap of nought, which would read as "selection has
            // no room" when it means "nobody has measured anything".
            return [new Panel("band2.ceilingGap", direction, "withheld", null, null, 0, null, Flagged)];
        }

        decimal bound = StoreText.StorageTextToPrice(reader.GetString(0));
        decimal achieved = StoreText.StorageTextToPrice(reader.GetString(1));

        return
        [
            new Panel("band2.ceilingGap", direction, PairedInterval.Figure(bound - achieved),
                null, null, reader.GetInt32(2), null, Flagged),
        ];
    }

    /// <summary>
    /// The nightly mean paired difference, per session, for one direction and one control set.
    ///
    /// Each setup's difference is its own return less the mean of its controls' returns at the same
    /// horizon. A setup with no controls filled contributes nothing rather than contributing its own
    /// return against nought, which would be the comparison silently becoming an absolute figure.
    /// </summary>
    private static IReadOnlyList<PairedInterval.Night> Series(
        SqliteConnection connection, string direction, string set, DateOnly asOf, DateTimeOffset computedAt)
    {
        var nights = new List<PairedInterval.Night>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.as_of,
                   AVG(sf.return_signed_num - cf.control_mean) AS difference,
                   COUNT(*) AS pairs,
                   AVG((sf.return_signed_num - cf.control_mean)
                     * (sf.return_signed_num - cf.control_mean)) AS mean_square
              FROM setup s
              JOIN (SELECT subject_id, CAST(return_signed AS REAL) AS return_signed_num
                      FROM forward_return
                     WHERE subject_kind = 'setup' AND horizon_days = @horizon
                       AND filled_at <= @computed_at) sf
                ON sf.subject_id = s.setup_id
              JOIN (SELECT c.setup_id, AVG(CAST(f.return_signed AS REAL)) AS control_mean
                      FROM control_setup c
                      JOIN forward_return f
                        ON f.subject_id = c.control_id AND f.subject_kind = 'control'
                       AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                     WHERE c.control_set = @set AND c.drawn_at <= @computed_at
                     GROUP BY c.setup_id) cf
                ON cf.setup_id = s.setup_id
             WHERE s.direction = @direction AND s.as_of <= @as_of
             GROUP BY s.as_of
             ORDER BY s.as_of
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@set", set);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            double difference = reader.GetDouble(1);
            int pairs = reader.GetInt32(2);

            // How far this night's own pairs sat apart, which is what lets the night count as more
            // than one observation. The sample form, so a night of one pair disperses by nought
            // rather than by a number computed from itself.
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

        return nights;
    }

    private static int Count(SqliteConnection connection, string sql, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(asOf, sessionZone));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One panel. <c>Population</c> is which rows the figure was computed over, and it is not
    /// optional: two panels on this page use different populations and a figure that cannot say
    /// which is a figure a reader will compare with the wrong one.
    ///
    /// <c>Minimum</c> is what the effective count has to reach before the panel's question may be
    /// answered, and it is set on band 1 alone because band 1 is the panel a checkpoint fires on.
    /// </summary>
    private sealed record Panel(
        string Name, string? Direction, string Figure, string? Low, string? High, int Rows,
        int? Effective, string Population, int? Minimum = null, string? WithheldBecause = null);
}

/// <summary>What one day's build produced.</summary>
public sealed record ScoreboardResult(
    DateOnly AsOf,
    int Panels,
    int WithInterval,
    int Withheld,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    int Attempted,
    int Skipped);
