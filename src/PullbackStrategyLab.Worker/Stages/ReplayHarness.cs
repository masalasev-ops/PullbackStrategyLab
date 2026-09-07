using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Re-filters the stored setups with a candidate selection rule, in seconds, and kills a proposal
/// before it costs anything.
///
/// <b>It never admits one.</b> A replay says a proposal is not worth running forward; only the
/// forward paired test says one is worth keeping (see: Replay screens proposals and the forward
/// paired test admits them). The register is written by VariantAdmitter and the difference series by
/// VariantScorer, and a screen that recorded a result beside them would be a third statement about a
/// version with nothing reconciling the three.
///
/// <b>It wrote nothing at all until 6.6, and what changed is that a screen now has a subject.</b>
/// From 5.3 to 6.5 there was no proposal for a result to belong to, so a result row would have been
/// a reading of a candidate nobody had proposed. <see cref="ScreenProposal"/> is the one path that
/// writes, and it writes exactly one table: <c>replay_result</c>, keyed on the proposal it screened.
/// <see cref="Reproduce"/> and <see cref="Screen"/> still write nothing, because the acceptance run
/// is evidence about the harness rather than about any proposal.
///
/// <b>Replay is not backtesting.</b> Nothing here reconstructs a past. Every row it reads was
/// written forward on the night, with the signals frozen on that night, and its outcome was filled
/// in by time passing. Applying a different threshold is a re-read of rows that already carry their
/// answers.
///
/// <b>What it can be run over is the evidence store and nothing else, and that is a property of the
/// schema rather than a choice made here.</b> A replay reads frozen signals, `setup_signal` carries
/// a foreign key into `setup`, and the calibration run computes its averages in memory and discards
/// them. So not one of the reconstructed sessions holds a signal to replay against, and no purchase
/// of history changes that. See <see cref="ReconstructedHistoryHasNoSignals"/>.
///
/// <b>The screenable set therefore grows at one night a night</b>, exactly as the execution
/// family's does, and this is the only screen this lab has.
/// </summary>
public sealed class ReplayHarness
{
    public const string Name = "replay";

    /// <summary>
    /// Why the reconstructed history cannot be screened, stated once and asserted against the
    /// store rather than described.
    ///
    /// <b>It supersedes the narrower worry the obligation raised at 3.3 carried.</b> That row said a
    /// short rule replayed over calibration rows would be screened against a funnel missing the
    /// market-capitalisation clause of `tradable-shortable`, and it scoped a shares-outstanding
    /// purchase to close it. The purchase would not close it. A replay needs the frozen quantity a
    /// gate compared, not the clause list it ran under, and no calibration row has one on either
    /// side.
    /// </summary>
    public const string ReconstructedHistoryHasNoSignals =
        "the reconstructed sessions carry no frozen signals, setup_signal keying into setup by "
        + "foreign key and the calibration run computing its averages in memory, so no rule can be "
        + "replayed over them on either side and no purchase of history changes that";

    /// <summary>What a screen says when the candidate is not a version the register would take.</summary>
    public const string NotAdmissible =
        "the candidate is not a rule this lab would register as a version, so screening it would "
        + "report on something that could never run";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ReplayHarness(
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

    /// <summary>
    /// <c>replay {direction} [threshold value] [as-of]</c>. With no threshold it reproduces the
    /// baseline, which is the acceptance run.
    /// </summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            Console.Error.WriteLine($"{Name}: a direction is required, being 'long' or 'short'.");
            return 2;
        }

        string direction = args[0];

        if (direction != SetupDirection.Long && direction != SetupDirection.Short)
        {
            Console.Error.WriteLine($"{Name}: '{direction}' is neither long nor short.");
            return 2;
        }

        SelectionRule baseline = SelectionRule.For(direction);

        DateOnly asOf = _clock.SessionDate(_clock.UtcNow, _options.SessionZone);
        SelectionRule? candidate = null;

        if (args.Length >= 3)
        {
            if (baseline.Find(args[1]) is null)
            {
                Console.Error.WriteLine($"{Name}: the {direction} rule has no threshold named '{args[1]}'.");
                return 2;
            }

            if (!decimal.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                Console.Error.WriteLine($"{Name}: '{args[2]}' is not a number.");
                return 2;
            }

            candidate = baseline.With(args[1], value);
        }

        if (args.Length >= 4)
        {
            asOf = DateOnly.ParseExact(args[3], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        ReplayScreening screening = candidate is null
            ? Reproduce(direction, asOf)
            : Screen(candidate, asOf);

        Console.WriteLine(
            $"{Name}: {screening.Direction}, {screening.SessionsRead} session(s), "
            + $"{screening.RowsExamined} row(s), {screening.Elapsed.TotalSeconds:0.00}s");

        if (screening.Refused is string refused)
        {
            Console.Error.WriteLine($"{Name}: refused, {refused}");
            return 1;
        }

        Console.WriteLine(
            $"{Name}: {screening.GatesJudged} gate(s) rebuilt a row, {screening.GatesReadBack} read back");
        Console.WriteLine(
            $"{Name}: baseline selected {screening.BaselineSelected}, candidate {screening.CandidateSelected}, "
            + $"both {screening.BothSelected}, candidate only {screening.CandidateOnly}, "
            + $"baseline only {screening.BaselineOnly}");

        if (screening.Unjudgeable > 0)
        {
            Console.WriteLine($"{Name}: {screening.Unjudgeable} row(s) the record could not judge");
        }

        if (screening.UnmeasuredGateVerdicts > 0)
        {
            Console.WriteLine(
                $"{Name}: {screening.UnmeasuredGateVerdicts} gate verdict(s) read back, the night having "
                + $"measured no quantity for them, of which {screening.FrozenYetUnmeasured} froze one anyway");
        }

        foreach (ReplayDisagreement d in screening.Disagreements)
        {
            Console.Error.WriteLine(
                $"{Name}: {d.SetupId} {d.Gate}, the night recorded {Verdict(d.Recorded)} "
                + $"and the rebuild says {Verdict(d.Rebuilt)}");
        }

        if (screening.Disagreements.Count > 0)
        {
            Console.Error.WriteLine(
                $"{Name}: the harness and the detector disagree, so every result above is worthless");
            return 1;
        }

        Console.WriteLine($"{Name}: {ReconstructedHistoryHasNoSignals}");
        return 0;
    }

    private static string Verdict(bool passed) => passed ? "pass" : "fail";

    /// <summary>
    /// The baseline's own rule replayed over its own recorded selections, which is the acceptance
    /// run and is the same walk a screen makes.
    ///
    /// <b>It is not a mode.</b> Admission refuses a candidate that moves nothing, so the acceptance
    /// run enters by its own door; everything past that door is the code a screen runs, so a green
    /// acceptance run is evidence about the harness rather than about a rehearsal of it.
    /// </summary>
    public ReplayScreening Reproduce(string direction, DateOnly asOf)
    {
        SelectionRule baseline = SelectionRule.For(direction);
        return Walk(baseline, baseline, asOf, refused: null);
    }

    /// <summary>One candidate rule over every stored night up to the as-of.</summary>
    public ReplayScreening Screen(SelectionRule candidate, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        SelectionRule baseline = SelectionRule.For(candidate.Direction);
        AdmissionVerdict verdict = SelectionReplay.AssertAdmissible(candidate, baseline);

        return Walk(
            candidate,
            baseline,
            asOf,
            verdict.IsAdmitted ? null : $"{NotAdmissible}: {verdict.Reason}");
    }

    /// <summary>
    /// One filed proposal screened, with the result recorded against it.
    ///
    /// <b>This is the only path in this stage that writes, and it writes one table.</b> The rule is
    /// rebuilt from the proposal's own five fields rather than passed in, because the five fields
    /// are what a version register would hold and screening anything else would report on something
    /// that could never run.
    ///
    /// <b>A screen kills or lets through and never admits.</b> `survived` means replay found no
    /// reason to stop, which is a much weaker statement than that the proposal is any good: replay
    /// is free, and free tests are how you overfit.
    /// see: Replay screens proposals and the forward paired test admits them
    ///
    /// <b>The window is the caller's and is null today.</b> No holdout window has matured, so every
    /// screen this lab can take is over the accumulated store, and a sentinel would make the two
    /// indistinguishable in a count.
    /// see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight
    /// </summary>
    public ReplayResult ScreenProposal(StoredProposal proposal, DateOnly asOf, string? windowId = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (proposal.Outcome != "proposed" || proposal.Direction is null || proposal.ThresholdName is null)
        {
            throw new InvalidOperationException(
                $"proposal {proposal.ProposalId} is \"{proposal.Outcome}\" and only a rule change can be "
                + "screened. A signal request is a build task and an abstention is a result.");
        }

        SelectionRule candidate =
            SelectionRule.For(proposal.Direction).With(proposal.ThresholdName, proposal.To!.Value);

        ReplayScreening screening = Screen(candidate, asOf);

        DateTimeOffset observedAt = _clock.UtcNow;

        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Record(connection, transaction, proposal.ProposalId, windowId, screening, observedAt);
            transaction.Commit();
        }

        return new ReplayResult(proposal.ProposalId, windowId, observedAt, screening);
    }

    /// <summary>
    /// One result row.
    ///
    /// <b>A refused screen reads nothing and says why, which the store holds as a biconditional.</b>
    /// So the counts written for a refusal are nought rather than whatever the walk happened to
    /// leave in them, and a refusal carrying a population would be refused by the store.
    /// </summary>
    private static void Record(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string proposalId,
        string? windowId,
        ReplayScreening screening,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO replay_result
                (proposal_id, window_id, observed_at, as_of, direction, sessions_read, rows_examined,
                 baseline_selected, candidate_selected, both_selected, candidate_only, baseline_only,
                 unjudgeable, unmeasured_verdicts, disagreements, verdict, refused_because, elapsed_ms)
            VALUES
                (@proposal_id, @window_id, @observed_at, @as_of, @direction, @sessions_read, @rows_examined,
                 @baseline_selected, @candidate_selected, @both_selected, @candidate_only, @baseline_only,
                 @unjudgeable, @unmeasured_verdicts, @disagreements, @verdict, @refused_because, @elapsed_ms)
            """;

        bool refused = screening.Refused is not null;

        command.Parameters.AddWithValue("@proposal_id", proposalId);
        command.Parameters.AddWithValue("@window_id", (object?)windowId ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(screening.AsOf));
        command.Parameters.AddWithValue("@direction", screening.Direction);
        command.Parameters.AddWithValue("@sessions_read", screening.SessionsRead);
        command.Parameters.AddWithValue("@rows_examined", screening.RowsExamined);
        command.Parameters.AddWithValue("@baseline_selected", screening.BaselineSelected);
        command.Parameters.AddWithValue("@candidate_selected", screening.CandidateSelected);
        command.Parameters.AddWithValue("@both_selected", screening.BothSelected);
        command.Parameters.AddWithValue("@candidate_only", screening.CandidateOnly);
        command.Parameters.AddWithValue("@baseline_only", screening.BaselineOnly);
        command.Parameters.AddWithValue("@unjudgeable", screening.Unjudgeable);
        command.Parameters.AddWithValue("@unmeasured_verdicts", screening.UnmeasuredGateVerdicts);
        command.Parameters.AddWithValue("@disagreements", screening.Disagreements.Count);
        command.Parameters.AddWithValue("@verdict", ReplayResult.VerdictOf(screening));
        command.Parameters.AddWithValue("@refused_because", (object?)screening.Refused ?? DBNull.Value);
        command.Parameters.AddWithValue("@elapsed_ms", (long)screening.Elapsed.TotalMilliseconds);

        command.ExecuteNonQuery();
    }

    private ReplayScreening Walk(
        SelectionRule rule, SelectionRule baseline, DateOnly asOf, string? refused)
    {
        var stopwatch = Stopwatch.StartNew();

        using SqliteConnection connection = _connections.OpenWrite();

        // The run entry declares no table, which is what makes "writes nothing" a property of the
        // record rather than a sentence in a comment.
        using RunScope run = _runLogger.Begin(connection, Name);

        string zone = _options.SessionZone;
        string direction = baseline.Direction;

        IReadOnlyList<string> judgeable = SelectionReplay.JudgeableGates(baseline);

        var baselineSet = new HashSet<string>(StringComparer.Ordinal);
        var candidateSet = new HashSet<string>(StringComparer.Ordinal);
        var disagreements = new List<ReplayDisagreement>();
        int rows = 0;
        int unjudgeable = 0;
        int sessions = 0;
        int unmeasured = 0;
        int frozenYetUnmeasured = 0;

        if (refused is null)
        {
            // One read of the setups and one of the signals per session, which is what "in seconds"
            // rests on: the cost is a function of how many nights the store holds and not of how
            // many rows they hold between them.
            foreach (DateOnly night in SetupReader.Sessions(connection, direction, DateOnly.MinValue, asOf))
            {
                sessions++;

                IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> signals =
                    FrozenSignals(connection, night);

                foreach (StoredSetup setup in SetupReader.Read(connection, night)
                             .Where(s => s.Direction == direction))
                {
                    rows++;

                    if (setup.PassedAll)
                    {
                        baselineSet.Add(setup.SetupId);
                    }

                    IReadOnlyList<CheckResult> recorded = Recorded(setup);
                    IReadOnlyDictionary<string, decimal> row =
                        signals.TryGetValue(setup.SetupId, out IReadOnlyDictionary<string, decimal>? found)
                            ? found
                            : EmptyRow;

                    ReplayRow replayed = SelectionReplay.Replay(rule, baseline, recorded, row);

                    unmeasured += replayed.Unmeasured.Count;
                    frozenYetUnmeasured += replayed.FrozenYetUnmeasured.Count;

                    foreach (string gate in replayed.Disagreed)
                    {
                        bool night_ = recorded.Single(r => r.Name == gate).Passed;
                        disagreements.Add(new ReplayDisagreement(setup.SetupId, gate, night_, !night_));
                    }

                    if (replayed.Selected is not bool selected)
                    {
                        unjudgeable++;
                        continue;
                    }

                    if (selected)
                    {
                        candidateSet.Add(setup.SetupId);
                    }
                }
            }
        }

        run.Complete(refused is null && disagreements.Count == 0 ? RunOutcome.Clean : RunOutcome.Partial);
        stopwatch.Stop();

        return new ReplayScreening(
            direction,
            asOf,
            sessions,
            rows,
            judgeable.Count,
            baseline.Gates.Count - judgeable.Count,
            baselineSet.Count,
            candidateSet.Count,
            baselineSet.Intersect(candidateSet, StringComparer.Ordinal).Count(),
            candidateSet.Except(baselineSet, StringComparer.Ordinal).Count(),
            baselineSet.Except(candidateSet, StringComparer.Ordinal).Count(),
            unjudgeable,
            unmeasured,
            frozenYetUnmeasured,
            disagreements,
            stopwatch.Elapsed,
            refused);
    }

    private static readonly IReadOnlyDictionary<string, decimal> EmptyRow =
        new Dictionary<string, decimal>(StringComparer.Ordinal);

    /// <summary>
    /// Every setup of one night, with the signals a replay can read, by setup.
    ///
    /// <b>Including the ones a backfill computed, from 6.1.</b> The question a replay asks is what a
    /// rule would have selected given the evidence, and a backfilled value is a function of that
    /// night's own inputs: the backfill passes each setup's own session as the as-of, so only the
    /// moment of computation is later. Bounding this on the stamp would leave the two derived
    /// quantities the library gained visible on the nights after the change and nowhere before it,
    /// which is the half of the 5.2 obligation that was about the rows already recorded.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> FrozenSignals(
        SqliteConnection connection, DateOnly night)
    {
        var rows = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);

        foreach (StoredSetupSignal signal in SetupSignalReader.ReadIncludingBackfilled(connection, night))
        {
            if (!SelectionReplay.DirectSignals.Contains(signal.SignalName))
            {
                continue;
            }

            if (!decimal.TryParse(
                    signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                continue;
            }

            if (!rows.TryGetValue(signal.SetupId, out Dictionary<string, decimal>? row))
            {
                row = new Dictionary<string, decimal>(StringComparer.Ordinal);
                rows[signal.SetupId] = row;
            }

            row[signal.SignalName] = value;
        }

        return rows.ToDictionary(
            p => p.Key,
            p => (IReadOnlyDictionary<string, decimal>)p.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<CheckResult> Recorded(StoredSetup setup)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CheckResult>>(setup.CheckResults, CheckResultsJson) ?? [];
        }
        catch (JsonException)
        {
            // A row whose verdicts cannot be read is one this cannot judge. It becomes unjudgeable
            // rather than throwing the screen away.
            return [];
        }
    }

    private static readonly JsonSerializerOptions CheckResultsJson =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
}

/// <summary>
/// One recorded screen: which proposal, which window, when, and what the walk found.
///
/// <b>Three verdicts and none of them admits.</b> `killed` is a screen that found a reason to stop,
/// `survived` a screen that found none, and `refused` a candidate the register would not take as a
/// version at all. A screen that could admit would make replay the thing that decides, and replay is
/// free (see: Replay screens proposals and the forward paired test admits them).
/// </summary>
public sealed record ReplayResult(
    string ProposalId, string? WindowId, DateTimeOffset ObservedAt, ReplayScreening Screening)
{
    /// <summary>A candidate the register would not take, so screening it reports on nothing.</summary>
    public const string Refused = "refused";

    /// <summary>The screen found a reason to stop.</summary>
    public const string Killed = "killed";

    /// <summary>The screen found none, which is much weaker than saying the proposal is any good.</summary>
    public const string Survived = "survived";

    /// <summary>
    /// The screen read rows and neither rule selected any of them, so it says nothing.
    ///
    /// <b>Ordinary rather than rare, which is why it is a verdict and not an edge case.</b> The
    /// funnel passes a median of nought candidates a night, so over the store as it stands the
    /// baseline selects nothing and a candidate selecting nothing too has not changed nothing: it
    /// has said nothing. Reporting that as `killed` would be a verdict reached over a population of
    /// none, which is the failure shape this corpus names as surviving every guard it has.
    /// </summary>
    public const string Inconclusive = "inconclusive";

    /// <summary>
    /// What a screening comes to, as a value rather than as a rule the caller applies.
    ///
    /// <b>Two things kill a proposal and they are different faults.</b> A candidate that selects
    /// exactly what the baseline selects has changed nothing, so running it forward would spend a
    /// paired test on a rule that cannot differ from the control. And a screen whose walk disagreed
    /// with the night on a judgeable gate is a screen whose own reading is in doubt, so its verdict
    /// is not evidence about the proposal at all.
    ///
    /// <b>What does not kill a proposal is selecting fewer setups.</b> A tighter rule selecting less
    /// is the ordinary shape of a selection change, and a screen that killed it would be deciding
    /// the question the forward paired test exists to answer.
    ///
    /// <b>And neither of those questions can be asked where nothing was selected at all</b>, so that
    /// case is answered first and answered as <see cref="Inconclusive"/>.
    /// </summary>
    public static string VerdictOf(ReplayScreening screening)
    {
        ArgumentNullException.ThrowIfNull(screening);

        if (screening.Refused is not null)
        {
            return Refused;
        }

        // Asked before the two that kill, because both of those rest on the selections meaning
        // something and neither does over a population where nothing was selected at all.
        if (screening.BaselineSelected == 0 && screening.CandidateSelected == 0)
        {
            return Inconclusive;
        }

        return screening.Disagreements.Count > 0 || screening.SelectionsReproduced ? Killed : Survived;
    }

    /// <summary>This screen's verdict.</summary>
    public string Verdict => VerdictOf(Screening);

    /// <summary>How the screen reads in a run line.</summary>
    public string Describe() =>
        Screening.Refused is string refused
            ? $"{ProposalId}: refused, {refused}"
            : $"{ProposalId}: {Verdict}, baseline {Screening.BaselineSelected} against candidate "
              + $"{Screening.CandidateSelected} over {Screening.RowsExamined} row(s)";
}

/// <summary>One gate on which the harness and the night disagree, which voids the screen.</summary>
public sealed record ReplayDisagreement(string SetupId, string Gate, bool Recorded, bool Rebuilt);

/// <summary>
/// What one screen came to, on one side.
///
/// <b>One side only, and the record says which.</b> A version is one side's, because a threshold
/// belongs to one side's gate list, and there is no figure here that could be added to the other
/// side's (see: Long and short are never pooled into one figure).
/// </summary>
public sealed record ReplayScreening(
    string Direction,
    DateOnly AsOf,
    int SessionsRead,
    int RowsExamined,
    int GatesJudged,
    int GatesReadBack,
    int BaselineSelected,
    int CandidateSelected,
    int BothSelected,
    int CandidateOnly,
    int BaselineOnly,
    int Unjudgeable,
    int UnmeasuredGateVerdicts,
    int FrozenYetUnmeasured,
    IReadOnlyList<ReplayDisagreement> Disagreements,
    TimeSpan Elapsed,
    string? Refused)
{
    /// <summary>
    /// Whether the replay selected the set the store says the baseline selected, which is the
    /// done condition's own claim.
    ///
    /// <b>Two clauses, because one is not enough.</b> Equal counts with different members is not
    /// reproduction, so the intersection has to be the whole of both.
    ///
    /// <b>It is not the whole of the acceptance claim and does not pretend to be.</b> Over a
    /// population the baseline selected nothing out of, this is true of any harness at all, and the
    /// per-gate agreement beside it is what carries the property there. Both are reported and
    /// neither is folded into the other.
    /// </summary>
    public bool SelectionsReproduced =>
        Refused is null
        && BaselineSelected == CandidateSelected
        && BothSelected == BaselineSelected;

    /// <summary>
    /// Whether the run stands behind every row it read: the selections reproduce, no judgeable gate
    /// disagreed with the night, and no row was left unjudged.
    ///
    /// <b>Stronger than <see cref="SelectionsReproduced"/> on purpose.</b> A harness that judged
    /// nothing would reproduce an empty selection perfectly, so the clause that a screen's worth
    /// actually rests on is that every row it read was one it could stand behind.
    /// </summary>
    public bool Reproduced =>
        SelectionsReproduced && Disagreements.Count == 0 && Unjudgeable == 0;
}
