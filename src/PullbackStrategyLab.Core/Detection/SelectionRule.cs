namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// How a selection rule is written down: the gate list as it stands, and a named threshold per
/// quantity a gate compares, each threshold saying which gate it belongs to, which family may move
/// it, and which frozen signal a replay reads it against.
///
/// <b>One representation, read by one implementation.</b> <see cref="LongPullbackRules.Evaluate(LongPullbackRules.LongEvidence, SelectionRule)"/>
/// and its short twin are the only code that turns evidence into verdicts, and both take the rule
/// they evaluate under. The nightly detector passes <see cref="Long"/> or <see cref="Short"/>; the
/// harness at 5.3 passes a version's rule over evidence rebuilt from the frozen signals; a test
/// passes whatever it is proving. A second reader is what the acceptance test at 5.3 exists to
/// refuse, and a representation that admitted two would make that test vacuous.
///
/// <b>The baseline's values are the rule constants and not a copy of them.</b> `pinned-constants`
/// holds the constants against the documents, and this record is built from those constants, so
/// the number a document states, the number the detector applies and the number a version starts
/// from are one number. A threshold that lived here as a literal would be a second statement of it.
///
/// <b>What is not here is a clause set.</b> A version changes one gate's threshold with every other
/// identical, and structural change is out of scope for this generation, so nothing in this record
/// can express a different gate list or a different shape of gate. A structural proposal would need
/// a representation of what a gate computes, not only what it compares against, and the acceptance
/// test would have nothing to reproduce the baseline with; that is named as out of scope rather than
/// left to be discovered.
/// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
/// see: A version changes one threshold over the existing gate list, and structural change is out of scope for this generation
/// </summary>
public sealed record SelectionRule(
    string Direction,
    IReadOnlyList<string> Gates,
    IReadOnlyList<RuleThreshold> Thresholds)
{
    // Threshold names, one per quantity a gate compares. Shared across the two sides where the
    // quantity is the same, and the value differs per side where the sides differ.
    public const string LiquidityFloor = "liquidity-floor";
    public const string PriceFloor = "price-floor";
    public const string MarketCapFloor = "market-cap-floor";
    public const string MinimumSessionsListed = "minimum-sessions-listed";
    public const string DailyRangeFloor = "daily-range-floor";
    public const string MaximumSqueezeRatio = "maximum-squeeze-ratio";
    public const string ThrustWindowSessions = "thrust-window-sessions";
    public const string MinimumPullbackBars = "minimum-pullback-bars";
    public const string MaximumPullbackBars = "maximum-pullback-bars";
    public const string MaximumRetrace = "maximum-retrace";
    public const string MaximumClosesBeyondFloor = "maximum-closes-beyond-floor";
    public const string MaximumRangeRatio = "maximum-range-ratio";
    public const string TriggerReachRanges = "trigger-reach-ranges";
    public const string CeilingReachRanges = "ceiling-reach-ranges";
    public const string GiveUpRanges = "give-up-ranges";
    public const string ClusterThreshold = "cluster-threshold";

    /// <summary>The baseline on the long side, built from the long rule constants.</summary>
    public static SelectionRule Long { get; } = new(
        SetupDirection.Long,
        SetupChecks.Long,
        [
            new(LiquidityFloor, "tradable", LongPullbackRules.LiquidityFloor, ThresholdFamily.Selection, ["dollar_volume_median_20"]),
            new(PriceFloor, "tradable", LongPullbackRules.PriceFloor, ThresholdFamily.Selection, ["close_adjusted"]),
            new(DailyRangeFloor, "moves-enough", LongPullbackRules.DailyRangeFloor, ThresholdFamily.Selection, ["adr_20"]),
            new(ThrustWindowSessions, "thrust", LongPullbackRules.ThrustWindowSessions, ThresholdFamily.Selection, ["days_since_thrust"], AssemblyBound: true),
            new(MinimumPullbackBars, "dip-shape", LongPullbackRules.MinimumPullbackBars, ThresholdFamily.Selection, ["pullback_bars"]),
            new(MaximumPullbackBars, "dip-shape", LongPullbackRules.MaximumPullbackBars, ThresholdFamily.Selection, ["pullback_bars"]),
            new(MaximumRetrace, "dip-shape", LongPullbackRules.MaximumRetrace, ThresholdFamily.Selection, ["retrace_depth"]),
            new(MaximumClosesBeyondFloor, "held-floor", 0m, ThresholdFamily.Selection, ["closes_beyond_floor"]),
            new(MaximumRangeRatio, "contraction", 1m, ThresholdFamily.Selection, ["range_today_over_avg"]),
            new(TriggerReachRanges, "trigger-near", LongPullbackRules.TriggerReachRanges, ThresholdFamily.Selection, ["trigger_distance_ranges"]),
            new(GiveUpRanges, "exit-tight", LongPullbackRules.GiveUpRanges, ThresholdFamily.Execution, ["stop_distance_ranges"]),
            new(ClusterThreshold, "cluster", LongPullbackRules.ClusterThreshold, ThresholdFamily.Recorded, ["cluster_count"]),
        ]);

    /// <summary>The baseline on the short side, built from the short rule constants.</summary>
    public static SelectionRule Short { get; } = new(
        SetupDirection.Short,
        SetupChecks.Short,
        [
            new(PriceFloor, "tradable-shortable", ShortPullbackRules.PriceFloor, ThresholdFamily.Selection, ["close_adjusted"]),
            new(MarketCapFloor, "tradable-shortable", ShortPullbackRules.MarketCapFloor, ThresholdFamily.Selection, ["market_cap"]),
            new(LiquidityFloor, "tradable-shortable", ShortPullbackRules.LiquidityFloor, ThresholdFamily.Selection, ["dollar_volume_median_20"]),
            new(MinimumSessionsListed, "tradable-shortable", ShortPullbackRules.MinimumSessionsListed, ThresholdFamily.Selection, ["listing_age_sessions"]),
            new(DailyRangeFloor, "moves-enough", ShortPullbackRules.DailyRangeFloor, ThresholdFamily.Selection, ["adr_20"]),
            new(MaximumSqueezeRatio, "averages-squeezing", 1m, ThresholdFamily.Selection, ["ema_gap_21_50", "ema_gap_21_50_avg_20"]),
            new(ThrustWindowSessions, "thrust", ShortPullbackRules.ThrustWindowSessions, ThresholdFamily.Selection, ["days_since_thrust"], AssemblyBound: true),
            new(MinimumPullbackBars, "bounce-shape", ShortPullbackRules.MinimumBounceBars, ThresholdFamily.Selection, ["pullback_bars"]),
            new(MaximumPullbackBars, "bounce-shape", ShortPullbackRules.MaximumBounceBars, ThresholdFamily.Selection, ["pullback_bars"]),
            new(MaximumRetrace, "bounce-shape", ShortPullbackRules.MaximumRecovery, ThresholdFamily.Selection, ["retrace_depth"]),
            // The two average clauses replay from the frozen distances and the daily range; the
            // anchored clause is a level over minute bars nothing froze, so a replay judges the
            // gate on the two clauses and says so, which is the same narrowing a reconstructed
            // session records.
            new(CeilingReachRanges, "reached-ceiling", ShortPullbackRules.CeilingReachRanges, ThresholdFamily.Selection, ["ema_21_distance", "ema_50_distance", "adr_20"]),
            new(MaximumClosesBeyondFloor, "no-reclaim", 0m, ThresholdFamily.Selection, ["closes_beyond_floor"]),
            new(GiveUpRanges, "exit-tight", ShortPullbackRules.GiveUpRanges, ThresholdFamily.Execution, ["stop_distance_ranges"]),
            new(ClusterThreshold, "cluster", ShortPullbackRules.ClusterThreshold, ThresholdFamily.Recorded, ["cluster_count"]),
        ]);

    /// <summary>The baseline for a direction.</summary>
    public static SelectionRule For(string direction) =>
        direction == SetupDirection.Long ? Long
        : direction == SetupDirection.Short ? Short
        : throw new ArgumentOutOfRangeException(nameof(direction), $"'{direction}' is neither long nor short.");

    /// <summary>The value of one named threshold, which every gate reads through rather than from a constant.</summary>
    public decimal Value(string name) =>
        Find(name)?.Value
        ?? throw new InvalidOperationException($"The {Direction} rule has no threshold named '{name}'.");

    /// <summary>The threshold by name, or null where the rule has none of that name.</summary>
    public RuleThreshold? Find(string name) =>
        Thresholds.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// This rule with one threshold at another value and everything else identical, which is the
    /// only shape of difference a version may have.
    /// </summary>
    public SelectionRule With(string name, decimal value)
    {
        if (Find(name) is null)
        {
            throw new InvalidOperationException($"The {Direction} rule has no threshold named '{name}' to move.");
        }

        return this with
        {
            Thresholds = [.. Thresholds.Select(t => string.Equals(t.Name, name, StringComparison.Ordinal) ? t with { Value = value } : t)],
        };
    }
}

/// <summary>
/// One named threshold: the gate it belongs to, its value, which family may move it, and the frozen
/// signals a replay reads its quantity from.
///
/// <paramref name="AssemblyBound"/> marks a threshold the detector also applies while assembling
/// the evidence rather than only while judging it. The thrust window is the one: hits outside it
/// are never read, so a frozen row carries no hit beyond the baseline's window and a version can
/// tighten the window over frozen signals but cannot widen it. Admission refuses the widening.
/// </summary>
public sealed record RuleThreshold(
    string Name,
    string Gate,
    decimal Value,
    ThresholdFamily Family,
    IReadOnlyList<string> FrozenSignals,
    bool AssemblyBound = false);

/// <summary>Which kind of version may move a threshold, on the two-family rule.</summary>
public enum ThresholdFamily
{
    /// <summary>Moves the choice of stock. A selection version may move one of these.</summary>
    Selection,

    /// <summary>Moves the size of the R unit. Belongs to the execution family, which admits no version this generation.</summary>
    Execution,

    /// <summary>Recorded on every row and gating nothing, so moving it selects nothing differently.</summary>
    Recorded,
}

/// <summary>
/// The admission assertion: whether a candidate rule differs from the baseline by exactly one
/// selection threshold, mechanically, with the reason it does not where it does not.
///
/// Asserted rather than described, because an admission rule that holds only while everyone
/// remembers it is not a rule. The gate at 5.1 that registers a version calls this and refuses on
/// anything but <see cref="AdmissionVerdict.Admitted"/>; the reasons are distinct so a version
/// refused for moving two thresholds is told apart from one refused for moving none.
/// see: A version is admitted when exactly one selection threshold differs from the baseline, and the assertion names the gate that moved
/// </summary>
public static class RuleAdmission
{
    public const string DifferentGateList = "the candidate's gate list is not the baseline's, which is a structural change and out of scope for this generation";
    public const string DifferentThresholdSet = "the candidate names a threshold the baseline does not have, or lacks one it has";
    public const string NothingMoved = "no threshold differs from the baseline, so the candidate is the baseline and not a version";
    public const string MoreThanOneMoved = "more than one threshold differs from the baseline";
    public const string NotSelectionFamily = "the threshold that moved is not a selection threshold";
    public const string WidensAssembly = "the threshold that moved is applied while the evidence is assembled, so it may tighten and not widen";

    public static AdmissionVerdict Assert(SelectionRule candidate, SelectionRule baseline)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        if (!string.Equals(candidate.Direction, baseline.Direction, StringComparison.Ordinal)
            || !candidate.Gates.SequenceEqual(baseline.Gates, StringComparer.Ordinal))
        {
            return AdmissionVerdict.Refused(DifferentGateList);
        }

        if (!candidate.Thresholds.Select(t => t.Name).SequenceEqual(baseline.Thresholds.Select(t => t.Name), StringComparer.Ordinal))
        {
            return AdmissionVerdict.Refused(DifferentThresholdSet);
        }

        List<(RuleThreshold Baseline, RuleThreshold Candidate)> moved = [];

        for (int i = 0; i < baseline.Thresholds.Count; i++)
        {
            RuleThreshold b = baseline.Thresholds[i];
            RuleThreshold c = candidate.Thresholds[i];

            if (b.Value != c.Value)
            {
                moved.Add((b, c));
            }
        }

        if (moved.Count == 0)
        {
            return AdmissionVerdict.Refused(NothingMoved);
        }

        if (moved.Count > 1)
        {
            return AdmissionVerdict.Refused(
                $"{MoreThanOneMoved}: {string.Join(", ", moved.Select(m => $"{m.Baseline.Gate} {m.Baseline.Name}"))}");
        }

        (RuleThreshold from, RuleThreshold to) = moved[0];

        if (from.Family != ThresholdFamily.Selection)
        {
            return AdmissionVerdict.Refused($"{NotSelectionFamily}: {from.Gate} {from.Name} is {from.Family}");
        }

        if (from.AssemblyBound && to.Value > from.Value)
        {
            return AdmissionVerdict.Refused($"{WidensAssembly}: {from.Gate} {from.Name} {from.Value} to {to.Value}");
        }

        return AdmissionVerdict.Admitted(from.Gate, from.Name, from.Value, to.Value);
    }
}

/// <summary>What admission decided, naming the gate that moved where it admitted.</summary>
public sealed record AdmissionVerdict(bool IsAdmitted, string Reason, string? Gate, string? Threshold, decimal? From, decimal? To)
{
    public static AdmissionVerdict Admitted(string gate, string threshold, decimal from, decimal to) =>
        new(true, $"{gate} {threshold} moves from {from} to {to} and every other threshold is the baseline's", gate, threshold, from, to);

    public static AdmissionVerdict Refused(string reason) => new(false, reason, null, null, null, null);
}
