using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// One gate's verdict under a version's rule, taken over the frozen signals the night recorded.
///
/// <b>It judges nothing itself.</b> Every verdict here comes out of
/// <see cref="LongPullbackRules.Evaluate(LongPullbackRules.LongEvidence, SelectionRule)"/> or its
/// short twin, which is the one implementation that turns evidence into verdicts. What this class
/// does is fill an evidence record from stored signal values and read one gate's result back out.
/// A second implementation of a gate's arithmetic is exactly what the acceptance test at 5.3 exists
/// to refuse, and a scorer that re-compared a threshold itself would be one.
/// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
///
/// <b>A signal fills a field or it fills nothing, and that is what decides which gates are
/// replayable.</b> <see cref="RuleThreshold.FrozenSignals"/> names the frozen signals a threshold's
/// quantity comes from. Where it names one, the value goes straight into the evidence field the
/// gate reads, and the rebuild is a lookup. Where it names two or three, the quantity is arithmetic
/// over them, and doing that arithmetic here would be a second copy of a step the detector already
/// takes: the short side's `averages-squeezing` is a ratio of two frozen gaps, and its
/// `reached-ceiling` is the nearer of two distances over a range. Those two gates are not
/// replayable, so a version moving one of their thresholds cannot be scored and is refused at
/// admission rather than admitted and left open for ever
/// (see: No execution variant is admitted in this generation, and the condition that would reopen it is named).
///
/// <b>What would make them replayable is naming the derived quantity as a signal of its own</b>, so
/// the night freezes the number the gate compared rather than the numbers it computed that from.
/// That is a signal-library change and it is not this checkpoint's.
/// </summary>
public static class SelectionReplay
{
    /// <summary>
    /// The frozen signal names this can read, one per evidence field.
    ///
    /// Shared across the two sides where the quantity is the same. `ladder_grade` is not here: no
    /// threshold names it, because `uptrend` and `downtrend` compare a grade rather than a number,
    /// so no version can move them and no replay needs to judge them.
    /// </summary>
    public static IReadOnlySet<string> DirectSignals { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "close_adjusted",
        "dollar_volume_median_20",
        "market_cap",
        "listing_age_sessions",
        "adr_20",
        "days_since_thrust",
        "pullback_bars",
        "retrace_depth",
        "closes_beyond_floor",
        "range_today_over_avg",
        "trigger_distance_ranges",
        "stop_distance_ranges",
        "cluster_count",
    };

    /// <summary>
    /// Whether a threshold's quantity is one frozen signal, which is what makes its gate judgeable
    /// over the record.
    /// </summary>
    public static bool IsDirect(RuleThreshold threshold)
    {
        ArgumentNullException.ThrowIfNull(threshold);

        return threshold.FrozenSignals.Count == 1 && DirectSignals.Contains(threshold.FrozenSignals[0]);
    }

    /// <summary>
    /// Whether every threshold a gate carries is direct, so the gate can be judged from the frozen
    /// signals alone.
    ///
    /// A gate with no threshold at all answers false: `uptrend` and `downtrend` compare a grade and
    /// nothing here can rebuild one, and a version cannot move them in any case.
    /// </summary>
    public static bool IsReplayable(SelectionRule rule, string gate)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);

        List<RuleThreshold> thresholds =
            [.. rule.Thresholds.Where(t => string.Equals(t.Gate, gate, StringComparison.Ordinal))];

        return thresholds.Count > 0 && thresholds.TrueForAll(IsDirect);
    }

    /// <summary>
    /// The thresholds a version of this rule may move: the selection-family ones whose gate can be
    /// judged over the record.
    ///
    /// <b>Stated as a list rather than a count, because the two sides are different lists.</b> The
    /// long side has ten and the short side ten of its twelve, and the two are never added
    /// (see: Long and short are never pooled into one figure).
    /// </summary>
    public static IReadOnlyList<RuleThreshold> Movable(SelectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return
        [
            .. rule.Thresholds
                .Where(t => t.Family == ThresholdFamily.Selection && IsReplayable(rule, t.Gate))
                .OrderBy(t => rule.Gates.ToList().IndexOf(t.Gate))
                .ThenBy(t => t.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>What admission says about a version whose gate the record cannot judge.</summary>
    public const string NotReplayable =
        "the gate the moved threshold belongs to cannot be judged from the frozen signals, because its "
        + "quantity is arithmetic over several of them rather than one, so no night could be scored";

    /// <summary>
    /// Whether a candidate rule may be admitted: the mechanical one-threshold assertion, and then
    /// whether the gate that moved is one the record can judge.
    ///
    /// <b>The second half is here rather than in <see cref="RuleAdmission"/> because it is a fact
    /// about the stored signals rather than about the rule.</b> The one-threshold rule holds
    /// whatever the store contains; this refusal would lift the day the derived quantity is frozen
    /// as a signal of its own, and it is refused now on the same grounds an execution version is:
    /// a version nothing can score is a row that stays open for ever, and no timeout closes it
    /// (see: Targets and minimum samples are written at creation and are immutable).
    /// </summary>
    public static AdmissionVerdict AssertAdmissible(SelectionRule candidate, SelectionRule baseline)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(baseline);

        AdmissionVerdict verdict = RuleAdmission.Assert(candidate, baseline);

        if (!verdict.IsAdmitted || verdict.Gate is not string gate)
        {
            return verdict;
        }

        return IsReplayable(baseline, gate)
            ? verdict
            : AdmissionVerdict.Refused($"{NotReplayable}: {gate}");
    }

    /// <summary>
    /// One gate's verdict under <paramref name="rule"/>, over the signals one setup froze, or null
    /// where a signal the gate needs is not in the row.
    ///
    /// Null rather than a failing verdict, because the two are different facts: a gate that failed
    /// is evidence about the name, and a gate that could not be judged is evidence about the
    /// record. The caller counts the second apart and never folds it into the first.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public static CheckResult? Judge(
        SelectionRule rule, string gate, IReadOnlyDictionary<string, decimal> signals)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(gate);
        ArgumentNullException.ThrowIfNull(signals);

        if (!IsReplayable(rule, gate))
        {
            return null;
        }

        foreach (RuleThreshold threshold in rule.Thresholds.Where(t => t.Gate == gate))
        {
            if (!signals.ContainsKey(threshold.FrozenSignals[0]))
            {
                return null;
            }
        }

        IReadOnlyList<CheckResult> results = rule.Direction == SetupDirection.Long
            ? LongPullbackRules.Evaluate(LongEvidenceFrom(signals), rule)
            : ShortPullbackRules.Evaluate(ShortEvidenceFrom(signals), rule);

        return results.FirstOrDefault(r => string.Equals(r.Name, gate, StringComparison.Ordinal));
    }

    /// <summary>
    /// The long side's evidence, as much of it as the frozen row carries.
    ///
    /// Fields with no direct signal stay null, and every gate reading one of those answers unknown.
    /// That is harmless here because <see cref="Judge"/> hands back one replayable gate's result and
    /// no other, and it is stated rather than left implicit because the record this builds is the
    /// same shape the detector fills from bars.
    /// </summary>
    private static LongPullbackRules.LongEvidence LongEvidenceFrom(
        IReadOnlyDictionary<string, decimal> signals) =>
        new()
        {
            Close = Read(signals, "close_adjusted"),
            MedianDollarVolume = Read(signals, "dollar_volume_median_20"),
            AverageDailyRange = Read(signals, "adr_20"),
            SessionsSinceThrust = Whole(signals, "days_since_thrust"),
            Pullback = PullbackFrom(signals),
            ClosesBeyondFloor = Whole(signals, "closes_beyond_floor"),
            RangeTodayOverAverage = Read(signals, "range_today_over_avg"),
            TriggerDistanceRanges = Read(signals, "trigger_distance_ranges"),
            StopDistanceRanges = Read(signals, "stop_distance_ranges"),
            ClusterCount = Whole(signals, "cluster_count"),
        };

    /// <summary>The short side's, on the same terms.</summary>
    private static ShortPullbackRules.ShortEvidence ShortEvidenceFrom(
        IReadOnlyDictionary<string, decimal> signals) =>
        new()
        {
            Close = Read(signals, "close_adjusted"),
            MedianDollarVolume = Read(signals, "dollar_volume_median_20"),
            MarketCap = Read(signals, "market_cap"),

            // False, because a replay of a forward night is not a calibration run. The exemption
            // exists so a reconstructed 2024 session can be counted at all, and a night the lab was
            // running for has a capitalisation or the gate fails for the reason it should.
            MarketCapExempt = false,

            SessionsListed = Whole(signals, "listing_age_sessions"),
            AverageDailyRange = Read(signals, "adr_20"),
            SessionsSinceThrust = Whole(signals, "days_since_thrust"),
            Bounce = PullbackFrom(signals),
            ClosesBeyondFloor = Whole(signals, "closes_beyond_floor"),
            StopDistanceRanges = Read(signals, "stop_distance_ranges"),
            ClusterCount = Whole(signals, "cluster_count"),

            // False for the same reason: this replays nights the lab ran, so an absent anchored
            // level is the recoverable absence rather than the permanent one. Neither reaches a
            // verdict here, `reached-ceiling` being one of the two gates this cannot judge.
            Reconstructed = false,
        };

    /// <summary>
    /// The dip or the bounce, carrying the two numbers its gate reads and nothing else.
    ///
    /// <b>The other seven fields are placeholders and that is asserted rather than assumed.</b>
    /// `dip-shape` and `bounce-shape` read the bar count and the retrace and no other member of the
    /// record; the prices on it are what the plan is built from, and a plan is never built here. A
    /// test judges the same signals through two rebuilds whose placeholders differ and requires one
    /// verdict, so the day a shape gate starts reading a price this stops being silent.
    /// </summary>
    private static PullbackGeometry.Pullback? PullbackFrom(IReadOnlyDictionary<string, decimal> signals) =>
        Whole(signals, "pullback_bars") is not int bars
            ? null
            : new PullbackGeometry.Pullback(
                ThrustIndex: 0,
                ExtremeIndex: 0,
                ThrustOrigin: 0m,
                ThrustExtreme: 0m,
                PullbackExtreme: 0m,
                PullbackBars: bars,
                RetraceDepth: Read(signals, "retrace_depth"),
                Trigger: 0m,
                Stop: 0m);

    private static decimal? Read(IReadOnlyDictionary<string, decimal> signals, string name) =>
        signals.TryGetValue(name, out decimal value) ? value : null;

    /// <summary>
    /// A signal whose evidence field is a whole number, rounded rather than truncated.
    ///
    /// The store holds every signal as text and the counting ones are written whole, so this is a
    /// conversion and not a decision. Rounding away from zero rather than truncating, so a value
    /// stored as 6.9999999 by a formatting change reads as seven and not as six.
    /// </summary>
    private static int? Whole(IReadOnlyDictionary<string, decimal> signals, string name) =>
        Read(signals, name) is decimal value
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;
}
