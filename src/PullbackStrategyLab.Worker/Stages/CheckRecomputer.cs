using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Recomputes one check's verdict for one night, from inputs bounded to that night.
///
/// <b>It exists because a stage dying mid-walk leaves this behind, and one did.</b> On 2026-08-27
/// the sector walk threw on its 149th name, `clusters` ran three minutes later over a store it had
/// half filled, and fifteen of that night's forty-four setups recorded a cluster verdict of failed
/// with no value. Nothing was wrong with the detector, the clusterer or the gate: the input was
/// absent and every one of them behaved correctly given that. A script would have repaired those
/// fifteen and left the next occurrence to be rediscovered, which is why this is a stage.
///
/// <b>The permission it runs under is narrow, and both of its conditions are enforced here.</b>
///
/// The first is the bound. Every input is read as at the setup's own date, using the same
/// end-of-day form every reader in the lab uses, so a value the lab learned afterwards is invisible.
/// A row whose input exists but arrived more than the lateness bound after that end of day is
/// <i>refused</i> and named, with both instants printed, rather than repaired with today's answer.
/// The fifteen this stage was written for had their sectors resolved at 00:19 Eastern the next
/// morning, 20 minutes past the session's own end of day and inside the bound, so they were admitted
/// and marked; a rerun a further day on would have been declined.
///
/// The second is the mark. A corrected row records `corrected_at` and `corrected_because` together,
/// so a later reader can exclude corrected rows without knowing this happened.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
///
/// <b>And it will not touch a gating check, whatever it is asked.</b> A trigger, a stop, a size or a
/// verdict the strategy turns on is the plan, and rewriting one is the thing immutability protects
/// against. The stage refuses any check outside <see cref="SetupChecks.RecordedNotRequired"/> before
/// it reads a single row, which today admits `cluster` and nothing else.
///
/// <b>What else could need it, so the next occurrence is a rerun rather than a rediscovery.</b>
/// Every gate whose input comes from a stage that runs earlier the same evening is in the same
/// position, and three are: `cluster` reads what `sectors` and `clusters` wrote at 18:12 and 18:15;
/// `thrust` reads `scan_hit` from `scans` at 18:10; and every geometry gate reads `indicator_daily`
/// from `indicators` at 18:05. The difference is that the other three are gating, so a failure of
/// their input changes `passed_all` and the night's candidate list, and repairing them would move a
/// plan rather than a recorded measurement. They are not repairable by this stage and they should
/// not be; what they need is for their input stage not to die, which is the other half of this
/// checkpoint.
/// </summary>
public sealed class CheckRecomputer
{
    public const string Name = "recheck";

    /// <summary>Which check to recompute. Required, and refused unless it is recorded and never gating.</summary>
    public const string CheckFlag = "--check";

    /// <summary>Write the corrections. Without it the stage reports what it would do and writes nothing.</summary>
    public const string ApplyFlag = "--apply";

    /// <summary>
    /// How many rows the caller expects to find, so the run fails on any other number.
    ///
    /// A repair derives its own set from a query, and the count somebody carried in from an earlier
    /// investigation is what that set is checked against rather than what defines it. The pipeline
    /// keeps running between the two, so a set that has moved is a fact worth stopping on: either
    /// something else corrected rows, or the query no longer means what it meant.
    /// </summary>
    public const string ExpectFlag = "--expect";

    /// <summary>
    /// Put corrected rows back the way the night wrote them, from what the correction recorded.
    ///
    /// <b>It exists because the corpus already claimed it did.</b> `corrected_from` was added so a
    /// corrected population could be restored, and a test asserted the restore by issuing the
    /// <c>UPDATE</c> itself; nothing an operator could run offered it. A property asserted by a test
    /// and absent from every surface is the shape this lab keeps meeting, so the writer of these
    /// columns owns the reverse operation as well as the forward one.
    ///
    /// It is also the only correct way to redo a correction. A repair cannot be applied twice, by
    /// design, so a correction computed against something that has since been fixed is undone and
    /// made again rather than overwritten.
    /// </summary>
    public const string RestoreFlag = "--restore";

    private static readonly JsonSerializerOptions CheckResultsJson = new(JsonSerializerDefaults.Web);

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public CheckRecomputer(
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

        Arguments parsed;

        try
        {
            parsed = Arguments.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine($"{Name}: {e.Message}");
            return 2;
        }

        string? check = parsed.Check;

        if (check is null)
        {
            Console.Error.WriteLine(
                $"{Name}: give the check, {CheckFlag} <name>, and the date. Without {ApplyFlag} it reports and writes nothing.");
            return 2;
        }

        DateOnly asOf = parsed.AsOf ?? _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        if (!SetupChecks.RecordedNotRequired.Contains(check))
        {
            // Refused before a row is read. A check the strategy turns on is the plan, and the
            // permission this stage runs under does not reach it at any date, with any input.
            Console.Error.WriteLine(
                $"{Name}: '{check}' is not a check this stage may recompute. It recomputes only checks the baseline "
                + $"records without requiring, which is {string.Join(", ", SetupChecks.RecordedNotRequired.Order(StringComparer.Ordinal))}. "
                + "A gating verdict is part of the plan and is never rewritten.");
            return 2;
        }

        int expect = parsed.Expect;
        bool restoring = parsed.Restoring;

        RecheckResult result = restoring
            ? Restore(asOf, check, parsed.Applying)
            : Recompute(asOf, check, parsed.Applying);

        Console.WriteLine(restoring
            ? $"{Name}: the set is every row of that date this stage corrected for '{check}' and recorded a prior state for"
            : $"{Name}: the set is every row of that date whose '{check}' verdict carries no value");
        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, check '{check}', {result.Candidates} row(s) in the set");
        Console.WriteLine(restoring
            ? $"{Name}: {result.Corrected} restored, {result.Refused} refused because no prior state was recorded"
            : $"{Name}: {result.Corrected} corrected, {result.Refused} refused because the input was stamped after the night");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {(result.Applied ? "written" : "reported only, rerun with " + ApplyFlag)}");

        if (expect >= 0 && expect != result.Candidates)
        {
            Console.Error.WriteLine(
                $"{Name}: the query found {result.Candidates} row(s) and the caller expected {expect}. The set is "
                + "what the query says and the expectation is what it is checked against, so a difference is a fact "
                + "about the store rather than a number to update.");
            return 2;
        }

        // Non-zero where the repair was asked for and could not be made. A hand-run tool that was
        // asked to fix something, fixed nothing, and exited 0 is the shape this whole checkpoint is
        // about: the caller reads the exit code and the log line is what tells them why.
        return result.Refused > 0 ? 1 : 0;
    }

    public RecheckResult Recompute(DateOnly asOf, string check, bool apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.BeginUpdatingInPlace(connection, Name, "setup");

        // The bound, in the end-of-day form every reader in the lab uses. An input stamped after it
        // is something the night did not have, whatever it is and however slowly it moves.
        // see: A reader's signature does not establish point-in-time; the query does
        string bound = StoreText.EndOfSession(asOf, _options.SessionZone);

        DateTimeOffset endOfSession = DateTimeOffset.Parse(bound, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        DateTimeOffset latestAdmissible = endOfSession.AddHours(MeasurementParameters.LatenessBoundHours);

        ClusterCounts inputs = ClusterInputs(connection, asOf, latestAdmissible, bound);
        IReadOnlyList<Candidate> candidates = Candidates(connection, asOf, check);

        int corrected = 0;
        int refused = 0;

        foreach (Candidate candidate in candidates)
        {
            ClusterInput input = inputs.For(candidate);

            if (input.Industry is null)
            {
                // Three states, and they read differently to a person. Nothing has resolved the
                // industry at all; it was resolved past the bound, and the message says how far
                // past; or the row names no thrust, in which case its cluster verdict carried no
                // value for a reason no sector lookup can repair and the row is left alone.
                refused++;
                run.CountSkipped();
                Console.WriteLine(
                    candidate.ThrustScan is null || candidate.ThrustSession is null
                        ? $"{Name}: refused {candidate.Ticker}, its row names no thrust, so no hit decided its cluster verdict"
                        : input.ResolvedAt is null
                            ? $"{Name}: refused {candidate.Ticker}, still nothing resolved for it"
                            : $"{Name}: refused {candidate.Ticker}, resolved at {input.ResolvedAt}, which is more than "
                              + $"{MeasurementParameters.LatenessBoundHours} hour(s) after {bound}");
                continue;
            }

            if (candidate.AlreadyCorrected)
            {
                // A row corrected once is not corrected again. The second correction would have no
                // prior state to record and nothing would say which of the two the row now carries.
                refused++;
                run.CountSkipped();
                Console.WriteLine($"{Name}: refused {candidate.Ticker}, already corrected at {candidate.CorrectedAt}");
                continue;
            }

            CheckResult verdict = ClusterVerdict(candidate.Direction, input.Count);
            int lateness = Lateness(input.ResolvedAt, endOfSession);

            if (apply)
            {
                Write(connection, candidate, check, verdict, asOf, lateness);
            }

            corrected++;
            Console.WriteLine(
                $"{Name}: {candidate.Ticker} {candidate.Direction}, {check} is {(verdict.Passed ? "pass" : "fail")} "
                + $"at {input.Count}, over {candidate.ThrustScan} on {candidate.ThrustSession}, from an industry "
                + $"resolved at {input.ResolvedAt}, {lateness} minute(s) late");
        }

        RunOutcome outcome = refused > 0 ? RunOutcome.Partial : RunOutcome.Clean;
        run.Complete(outcome);

        return new RecheckResult(asOf, check, candidates.Count, corrected, refused, apply, outcome);
    }

    /// <summary>
    /// Puts every row this stage corrected for one date and check back the way the night wrote it.
    ///
    /// The prior text is the whole check-results JSON, so the restore is a column assignment rather
    /// than a merge, and the five correction columns are cleared in the same statement: a row that
    /// kept its mark after being restored would report a correction that no longer exists.
    ///
    /// A row marked corrected with no prior state recorded is refused rather than guessed at. That
    /// pair cannot be produced by this stage, which writes both in one statement, so encountering it
    /// means something else wrote the mark and the restore has nothing to put back.
    ///
    /// <b>"And check" was in this sentence before it was in the query.</b> The argument was
    /// validated against the recomputable list and then discarded: the read bounded on the date
    /// alone, so a restore put back every corrected row of that date whatever check each was
    /// corrected for. It is harmless while `cluster` is the only admitted check, because there is
    /// only one thing a row can have been corrected for, and it is silently destructive on the day
    /// a second is admitted, undoing one check's corrections in the course of restoring another's
    /// with nothing in the output naming them. Selecting on `corrected_check` rather than on a
    /// phrase inside `corrected_because` is the other half: a figure recovered from prose moves
    /// when somebody rewords the sentence.
    ///
    /// <b>A row corrected before 033 carries no `corrected_check` and is not restored by any
    /// scoped call.</b> That is the conservative direction and it is stated rather than left to be
    /// discovered: the fifteen rows of 2026-08-27 were all corrected for `cluster`, so the answer a
    /// backfill would give is knowable, and producing it would mean this stage reading the sentence
    /// the column exists to stop anybody reading. They are counted and named instead.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    public RecheckResult Restore(DateOnly asOf, string check, bool apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.BeginUpdatingInPlace(connection, Name, "setup");

        var rows = new List<(string SetupId, string Ticker, string? Prior)>();
        int unscoped = 0;

        using (SqliteCommand read = connection.CreateCommand())
        {
            // Scoped to the check, which is what the argument was validated for. A row corrected
            // for another check on the same date is another check's row and this call is not
            // about it.
            read.CommandText = """
                SELECT setup_id, ticker, corrected_from
                  FROM setup
                 WHERE as_of = @as_of AND corrected_at IS NOT NULL AND corrected_check = @check
                 ORDER BY ticker, direction
                """;
            read.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            read.Parameters.AddWithValue("@check", check);

            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        using (SqliteCommand read = connection.CreateCommand())
        {
            // Corrected before 033 and therefore outside every scoped call. Counted and reported
            // rather than swept in, because a restore that quietly widened itself to rows it cannot
            // identify is the fault this scoping is about, arrived at from the other side.
            read.CommandText = """
                SELECT COUNT(*) FROM setup
                 WHERE as_of = @as_of AND corrected_at IS NOT NULL AND corrected_check IS NULL
                """;
            read.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            unscoped = Convert.ToInt32(read.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (unscoped > 0)
        {
            Console.WriteLine(
                $"{Name}: {unscoped} corrected row(s) of that date record no check and are outside this restore. "
                + "They were corrected before migration 033 added the column, so which check each carries is in "
                + "`corrected_because` and nowhere a query can select on.");
        }

        int restored = 0;
        int refused = 0;

        foreach ((string setupId, string ticker, string? prior) in rows)
        {
            if (prior is null)
            {
                refused++;
                run.CountSkipped();
                Console.WriteLine($"{Name}: refused {ticker}, marked corrected with no prior state recorded");
                continue;
            }

            if (apply)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE setup
                       SET check_results = corrected_from,
                           corrected_at = NULL,
                           corrected_because = NULL,
                           correction_lateness_minutes = NULL,
                           corrected_from = NULL,
                           corrected_check = NULL
                     WHERE setup_id = @setup_id
                    """;
                command.Parameters.AddWithValue("@setup_id", setupId);
                command.ExecuteNonQuery();
            }

            restored++;
            Console.WriteLine($"{Name}: {ticker} restored to the state the night wrote");
        }

        RunOutcome outcome = refused > 0 ? RunOutcome.Partial : RunOutcome.Clean;
        run.Complete(outcome);

        return new RecheckResult(asOf, check, rows.Count, restored, refused, apply, outcome);
    }

    /// <summary>
    /// The cluster count behind the hit each setup's own detector selected, and when the industry
    /// behind it was resolved.
    ///
    /// This is <c>ThemeClusterer</c>'s own arithmetic and its own bound, deliberately reproduced
    /// rather than shared, because the two answer different questions: the clusterer writes a count
    /// per scan hit and this reads the count one already-recorded check was decided on. What must
    /// not differ is the bound, which is why it is passed in rather than rebuilt here.
    ///
    /// <b>Keyed on the hit the detector chose, which is what this got wrong.</b> It took the largest
    /// count over every scan the ticker hit on the setup's own session, and its comment said that
    /// was "what the detector read from <c>scan_hit.cluster_count</c> for that name". The detector
    /// reads one hit: the most recent inside the thrust window, restricted to the upward or downward
    /// mover scans, tie-broken by rank, and it records which one on the setup row as
    /// <c>thrust_scan</c> and <c>thrust_session</c>. A maximum over all scans is a different
    /// quantity and it is never smaller, so the recompute could only ever raise a verdict's value
    /// and never lower it, which is the direction a repair must not have. On 2026-08-27 it wrote 13
    /// where the detector's rule gives 6 for PATH, and 4 where it gives 3 for PURR: two of the
    /// fifteen rows the repair corrected.
    ///
    /// Grouped by session and by scan as well as by industry, as the clusterer groups it. Two names
    /// in the same industry on opposite scans are the industry splitting rather than shifting, and
    /// the thrust can sit on an earlier session than the setup, so the session is part of the key
    /// rather than assumed to be the as-of.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    private static ClusterCounts ClusterInputs(
        SqliteConnection connection,
        DateOnly asOf,
        DateTimeOffset latestAdmissible,
        string endOfSession)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.ticker, h.scan, h.as_of,
                   CASE WHEN s.sector_resolved_at IS NOT NULL AND s.sector_resolved_at <= @bound
                        THEN s.industry END,
                   s.sector_resolved_at
              FROM scan_hit h
              JOIN security s ON s.ticker = h.ticker
             WHERE h.as_of <= @as_of
               AND (h.observed_at <= @observed_before OR h.observed_at IS NULL)
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", endOfSession);
        command.Parameters.AddWithValue("@bound", StoreText.TimestampToStorageText(latestAdmissible));

        var hits = new List<(string Ticker, string Scan, string Session, string? Industry, string? ResolvedAt)>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                hits.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        Dictionary<(string Session, string Scan, string Industry), int> counts = hits
            .Where(h => h.Industry is not null)
            .GroupBy(h => (h.Session, h.Scan, Industry: h.Industry!))
            .ToDictionary(g => g.Key, g => g.Count());

        var byTicker = new Dictionary<string, (string? Industry, string? ResolvedAt)>(StringComparer.Ordinal);

        foreach ((string ticker, _, _, string? industry, string? resolvedAt) in hits)
        {
            byTicker[ticker] = (industry, resolvedAt);
        }

        return new ClusterCounts(counts, byTicker);
    }

    /// <summary>The rows whose verdict for this check has no value, which is what an absent input leaves.</summary>
    private static IReadOnlyList<Candidate> Candidates(SqliteConnection connection, DateOnly asOf, string check)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT setup_id, ticker, direction, check_results, corrected_at,
                   thrust_scan, thrust_session
              FROM setup
             WHERE as_of = @as_of
             ORDER BY ticker, direction
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var candidates = new List<Candidate>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            string json = reader.GetString(3);
            List<CheckResult> results = JsonSerializer.Deserialize<List<CheckResult>>(json, CheckResultsJson) ?? [];

            CheckResult? existing = results.FirstOrDefault(r => string.Equals(r.Name, check, StringComparison.Ordinal));

            // Only a verdict with no value at all. A check that ran and produced a number is a
            // measurement the night made, and this stage has no permission to revisit one.
            if (existing is { Value: null })
            {
                candidates.Add(new Candidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    results,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The cluster verdict, from the same rule the detectors apply.
    ///
    /// Read from the rules rather than restated, so a threshold moved in one place moves here too.
    /// The two sides carry the same figure today and are asked separately anyway, because the rule
    /// that they are never pooled applies to how a number is arrived at as well as to how it is
    /// reported.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    private static CheckResult ClusterVerdict(string direction, int count)
    {
        int threshold = direction == SetupDirection.Short
            ? ShortPullbackRules.ClusterThreshold
            : LongPullbackRules.ClusterThreshold;

        return new CheckResult("cluster", count >= threshold, count);
    }

    /// <summary>
    /// How far past the session's own end of day the input arrived, in minutes, never negative.
    ///
    /// Zero means the input was inside the session's own day, which is what a night rerun in time
    /// produces and is the ordinary case. Minutes rather than hours, because a column in the same
    /// unit as its own threshold cannot show how close to it a row sat.
    /// </summary>
    private static int Lateness(string? resolvedAt, DateTimeOffset endOfSession)
    {
        if (resolvedAt is null)
        {
            return 0;
        }

        DateTimeOffset at = DateTimeOffset.Parse(
            resolvedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        return (int)Math.Max(0, Math.Round((at - endOfSession).TotalMinutes));
    }

    private void Write(
        SqliteConnection connection,
        Candidate candidate,
        string check,
        CheckResult verdict,
        DateOnly asOf,
        int latenessMinutes)
    {
        List<CheckResult> results = [.. candidate.Results
            .Select(r => string.Equals(r.Name, check, StringComparison.Ordinal) ? verdict : r)];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE setup
               SET check_results = @check_results,
                   corrected_at = @corrected_at,
                   corrected_because = @corrected_because,
                   correction_lateness_minutes = @lateness,
                   corrected_from = @corrected_from,
                   corrected_check = @corrected_check
             WHERE setup_id = @setup_id
            """;

        command.Parameters.AddWithValue("@check_results", JsonSerializer.Serialize(results, CheckResultsJson));
        command.Parameters.AddWithValue("@corrected_at", StoreText.TimestampToStorageText(_clock.UtcNow));
        command.Parameters.AddWithValue(
            "@corrected_because",
            $"'{check}' recomputed for {asOf:yyyy-MM-dd} from inputs the session asked for, {latenessMinutes} "
            + $"minute(s) late against a bound of {MeasurementParameters.LatenessBoundHours} hour(s), after the "
            + "stage supplying them did not finish on the night");
        command.Parameters.AddWithValue("@lateness", latenessMinutes);

        // The prior text, in the same statement as the mark, so a corrected row can never carry a
        // mark without the state it was corrected from.
        command.Parameters.AddWithValue(
            "@corrected_from", JsonSerializer.Serialize(candidate.Results, CheckResultsJson));

        // And what the mark is about, in the same statement for the same reason. The name was
        // reaching the row inside `corrected_because` and nowhere else, so the one value a restore
        // has to select on was the one that was prose.
        command.Parameters.AddWithValue("@corrected_check", check);
        command.Parameters.AddWithValue("@setup_id", candidate.SetupId);
        command.ExecuteNonQuery();
    }

    // Nested types sit at the end of this class, beside ClusterInput and Candidate below.
    // That is this file's own convention and it is also load-bearing: `writer-ownership`
    // attributes a write to the nearest type declaration above it rather than to the type whose
    // braces enclose it, so a nested type declared above a statement reattributes that statement
    // to the nested type. Declaring Arguments at the top of the class moved both UPDATE setup
    // statements onto `Arguments` and turned the check red in both directions.
    /// <summary>The date to recompute for, named rather than positional.</summary>
    public const string AsOfFlag = "--as-of";

    /// <summary>
    /// This stage's command line, parsed once with the arity of every flag declared.
    ///
    /// <b>The date used to be whatever argument was neither a flag nor the check's own name.</b>
    /// `--check`'s value was excluded by naming it and no other flag's value was, so
    /// <c>recheck --check cluster --expect 15 2026-08-27</c> took <c>15</c> as the date and died on
    /// the format. It failed loudly and wrote nothing, so this was never a correctness fault; what
    /// it was is a command with exactly one ordering that works and nothing anywhere saying which.
    ///
    /// <b>The arity is declared rather than the exclusion, because the exclusion is what did not
    /// scale.</b> Naming `--check` fixed one flag and left the next one to reintroduce the fault,
    /// which is how `--expect` arrived. Here every flag is in one of two sets and anything else is
    /// refused by name, so a flag added later cannot be added without saying whether it takes a
    /// value: the alternative, assuming an unknown flag takes one, would let a new boolean swallow
    /// the date and fall back to today, which is the same fault reading as success.
    ///
    /// <c>--as-of</c> is the form to write and the bare date still works, so the RUNBOOK's
    /// documented ordering keeps parsing. Both are accepted and they must agree.
    /// </summary>
    public sealed record Arguments(string? Check, DateOnly? AsOf, int Expect, bool Applying, bool Restoring)
    {
        /// <summary>The flags carrying a value, which is the next argument along.</summary>
        public static IReadOnlySet<string> TakeAValue { get; } =
            new HashSet<string>(StringComparer.Ordinal) { CheckFlag, ExpectFlag, AsOfFlag };

        /// <summary>The flags that are their own value.</summary>
        public static IReadOnlySet<string> StandAlone { get; } =
            new HashSet<string>(StringComparer.Ordinal) { ApplyFlag, RestoreFlag };

        /// <summary>
        /// The command line, or an <see cref="ArgumentException"/> naming what it could not read.
        ///
        /// Every failure here is a refusal rather than a default. A command line this stage cannot
        /// read is one where somebody meant something specific, and guessing at it is how a repair
        /// runs against the wrong night.
        /// </summary>
        public static Arguments Parse(IReadOnlyList<string> args)
        {
            ArgumentNullException.ThrowIfNull(args);

            string? check = null;
            string? named = null;
            string? positional = null;
            int expect = -1;
            bool applying = false;
            bool restoring = false;

            for (int i = 0; i < args.Count; i++)
            {
                string argument = args[i];

                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    if (positional is not null)
                    {
                        throw new ArgumentException(
                            $"'{positional}' and '{argument}' are both given as the date. One date, or use {AsOfFlag}.");
                    }

                    positional = argument;
                    continue;
                }

                if (StandAlone.Contains(argument))
                {
                    applying |= string.Equals(argument, ApplyFlag, StringComparison.Ordinal);
                    restoring |= string.Equals(argument, RestoreFlag, StringComparison.Ordinal);
                    continue;
                }

                if (!TakeAValue.Contains(argument))
                {
                    throw new ArgumentException(
                        $"'{argument}' is not an option this stage knows. It takes "
                        + $"{string.Join(", ", TakeAValue.Order(StringComparer.Ordinal))} with a value and "
                        + $"{string.Join(", ", StandAlone.Order(StringComparer.Ordinal))} on their own. An option is "
                        + "refused rather than ignored, because an option nobody reads is an instruction nobody carried out.");
                }

                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"{argument} needs a value after it.");
                }

                // The value, consumed here so it can never be read as the date. This is the whole
                // repair: the loop knows what is a value because the flag said so.
                string value = args[++i];

                if (string.Equals(argument, CheckFlag, StringComparison.Ordinal))
                {
                    check = value;
                }
                else if (string.Equals(argument, AsOfFlag, StringComparison.Ordinal))
                {
                    named = value;
                }
                else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out expect))
                {
                    throw new ArgumentException($"{ExpectFlag} takes a whole number and was given '{value}'.");
                }
            }

            if (named is not null && positional is not null
                && !string.Equals(named, positional, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"the date is given twice and the two disagree: '{positional}' and {AsOfFlag} '{named}'.");
            }

            string? date = named ?? positional;

            return new Arguments(check, date is null ? null : ReadDate(date), expect, applying, restoring);
        }

        private static DateOnly ReadDate(string value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
                ? date
                : throw new ArgumentException($"'{value}' is not a date. Give it as yyyy-MM-dd.");
    }

    /// <summary>
    /// The per-(session, scan, industry) counts the clusterer would have written, and what each
    /// ticker's industry is and when it was resolved.
    ///
    /// Two lookups rather than one per-ticker answer, because the count a setup's verdict was
    /// decided on is a property of the hit its detector chose rather than of the ticker.
    /// </summary>
    private sealed record ClusterCounts(
        IReadOnlyDictionary<(string Session, string Scan, string Industry), int> Counts,
        IReadOnlyDictionary<string, (string? Industry, string? ResolvedAt)> ByTicker)
    {
        /// <summary>
        /// The count behind one setup's own thrust, or null where the row names no thrust, the
        /// industry is unresolved or resolved too late, or that scan and session produced no
        /// countable hit at all.
        /// </summary>
        public ClusterInput For(Candidate candidate)
        {
            (string? industry, string? resolvedAt) =
                ByTicker.TryGetValue(candidate.Ticker, out (string? Industry, string? ResolvedAt) held)
                    ? held
                    : (null, null);

            if (industry is null || candidate.ThrustScan is null || candidate.ThrustSession is null)
            {
                return new ClusterInput(null, 0, resolvedAt);
            }

            return Counts.TryGetValue((candidate.ThrustSession, candidate.ThrustScan, industry), out int count)
                ? new ClusterInput(industry, count, resolvedAt)
                : new ClusterInput(null, 0, resolvedAt);
        }
    }

    private readonly record struct ClusterInput(string? Industry, int Count, string? ResolvedAt);

    /// <summary>
    /// One row this stage could correct, with the mark it already carries.
    ///
    /// <c>CorrectedAt</c> is the reader the correction mark needed. A mark nothing reads is a claim
    /// about a consumer that does not exist, which is the shape the corpus names sixth, and the
    /// superseded rule shipped exactly that: it recorded a correction "so a later reader can exclude
    /// corrected rows" under a guard that made corrected rows impossible.
    /// </summary>
    private sealed record Candidate(
        string SetupId,
        string Ticker,
        string Direction,
        List<CheckResult> Results,
        string? CorrectedAt,
        string? ThrustScan,
        string? ThrustSession)
    {
        public bool AlreadyCorrected => CorrectedAt is not null;
    }
}

/// <summary>What one recompute found, corrected, and declined to correct.</summary>
public sealed record RecheckResult(
    DateOnly AsOf,
    string Check,
    int Candidates,
    int Corrected,
    int Refused,
    bool Applied,
    RunOutcome Outcome);
