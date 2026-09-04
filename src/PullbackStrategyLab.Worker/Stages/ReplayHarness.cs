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
/// paired test admits them). So this stage writes nothing at all: the register is written by
/// VariantAdmitter and the difference series by VariantScorer, and a screen that recorded a result
/// beside them would be a third statement about a version with nothing reconciling the three.
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
                    FrozenSignals(connection, night, zone);

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

    /// <summary>Every setup of one night, with the signals a replay can read, by setup.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> FrozenSignals(
        SqliteConnection connection, DateOnly night, string zone)
    {
        var rows = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);

        foreach (StoredSetupSignal signal in SetupSignalReader.Read(connection, night, zone))
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
