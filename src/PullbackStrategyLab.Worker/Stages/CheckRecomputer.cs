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
/// A row whose input exists but was stamped too late is <i>refused</i> and named, with both instants
/// printed, rather than repaired with today's answer. That is not a corner case: it is what happened
/// to the fifteen this stage was written for, whose sectors were resolved on 2026-08-28, and the
/// stage declines them.
///
/// The second is the mark. A corrected row records `corrected_at` and `corrected_because` together,
/// so a later reader can exclude corrected rows without knowing this happened.
/// see: A setup row is corrected only where the correction uses no information the night did not have
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

        int at = Array.IndexOf(args, CheckFlag);
        string? check = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;

        if (check is null)
        {
            Console.Error.WriteLine(
                $"{Name}: give the check, {CheckFlag} <name>, and the date. Without {ApplyFlag} it reports and writes nothing.");
            return 2;
        }

        string? date = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != check);
        DateOnly asOf = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

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

        RecheckResult result = Recompute(asOf, check, args.Contains(ApplyFlag));

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, check '{check}', {result.Candidates} row(s) with no value");
        Console.WriteLine($"{Name}: {result.Corrected} corrected, {result.Refused} refused because the input was stamped after the night");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {(result.Applied ? "written" : "reported only, rerun with " + ApplyFlag)}");

        // Non-zero where the repair was asked for and could not be made. A hand-run tool that was
        // asked to fix something, fixed nothing, and exited 0 is the shape this whole checkpoint is
        // about: the caller reads the exit code and the log line is what tells them why.
        return result.Refused > 0 ? 1 : 0;
    }

    public RecheckResult Recompute(DateOnly asOf, string check, bool apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(check);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "setup");

        // The bound, in the end-of-day form every reader in the lab uses. An input stamped after it
        // is something the night did not have, whatever it is and however slowly it moves.
        // see: A reader's signature does not establish point-in-time; the query does
        string bound = StoreText.DateToStorageText(asOf) + "T23:59:59.999Z";

        DateTimeOffset endOfSession = DateTimeOffset.Parse(bound, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        DateTimeOffset latestAdmissible = endOfSession.AddHours(MeasurementParameters.LatenessBoundHours);

        IReadOnlyDictionary<string, ClusterInput> inputs = ClusterInputs(connection, asOf, latestAdmissible);
        IReadOnlyList<Candidate> candidates = Candidates(connection, asOf, check);

        int corrected = 0;
        int refused = 0;

        foreach (Candidate candidate in candidates)
        {
            if (!inputs.TryGetValue(candidate.Ticker, out ClusterInput input) || input.Industry is null)
            {
                // Either nothing has resolved it at all, or it was resolved past the bound. The two
                // read differently to a person and the second says how far past.
                refused++;
                run.CountSkipped();
                Console.WriteLine(
                    input.ResolvedAt is null
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
                + $"at {input.Count}, from an industry resolved at {input.ResolvedAt}, {lateness} minute(s) late");
        }

        RunOutcome outcome = refused > 0 ? RunOutcome.Partial : RunOutcome.Clean;
        run.Complete(outcome);

        return new RecheckResult(asOf, check, candidates.Count, corrected, refused, apply, outcome);
    }

    /// <summary>
    /// The cluster count per ticker as it stood on the night, and when the industry behind it was
    /// resolved.
    ///
    /// This is <c>ThemeClusterer</c>'s own arithmetic and its own bound, deliberately reproduced
    /// rather than shared, because the two answer different questions: the clusterer writes a count
    /// per scan hit and this reads a count per ticker for a check that already ran. What must not
    /// differ is the bound, which is why it is passed in rather than rebuilt here.
    ///
    /// Grouped by scan as well as by industry, as the clusterer groups it. Two names in the same
    /// industry on opposite scans are the industry splitting rather than shifting. Where a ticker
    /// appears on more than one scan the largest of its counts is taken, which is what the detector
    /// read from <c>scan_hit.cluster_count</c> for that name.
    /// </summary>
    private static IReadOnlyDictionary<string, ClusterInput> ClusterInputs(
        SqliteConnection connection,
        DateOnly asOf,
        DateTimeOffset latestAdmissible)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.ticker, h.scan,
                   CASE WHEN s.sector_resolved_at IS NOT NULL AND s.sector_resolved_at <= @bound
                        THEN s.industry END,
                   s.sector_resolved_at
              FROM scan_hit h
              JOIN security s ON s.ticker = h.ticker
             WHERE h.as_of = @as_of
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@bound", StoreText.TimestampToStorageText(latestAdmissible));

        var hits = new List<(string Ticker, string Scan, string? Industry, string? ResolvedAt)>();
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                hits.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        Dictionary<(string Scan, string Industry), int> counts = hits
            .Where(h => h.Industry is not null)
            .GroupBy(h => (h.Scan, Industry: h.Industry!))
            .ToDictionary(g => g.Key, g => g.Count());

        var byTicker = new Dictionary<string, ClusterInput>(StringComparer.Ordinal);

        foreach ((string ticker, string scan, string? industry, string? resolvedAt) in hits)
        {
            int count = industry is null ? 0 : counts[(scan, industry)];

            if (!byTicker.TryGetValue(ticker, out ClusterInput held) || count > held.Count)
            {
                byTicker[ticker] = new ClusterInput(industry, count, resolvedAt);
            }
        }

        return byTicker;
    }

    /// <summary>The rows whose verdict for this check has no value, which is what an absent input leaves.</summary>
    private static IReadOnlyList<Candidate> Candidates(SqliteConnection connection, DateOnly asOf, string check)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT setup_id, ticker, direction, check_results, corrected_at
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
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
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
                   correction_lateness_minutes = @lateness
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
        command.Parameters.AddWithValue("@setup_id", candidate.SetupId);
        command.ExecuteNonQuery();
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
        string? CorrectedAt)
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
