using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// The ten long checks, as arithmetic over one name's evidence.
///
/// In Core and pure, so the same rules run three ways without three implementations: the nightly
/// detector, the calibration run over stored history, and a test feeding figures by hand. A
/// calibration count produced by a second implementation would be a fact about the calibration code
/// rather than about the thresholds, which is the one thing that run is for.
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
///
/// <b>Every check returns a result, pass or fail, and none of them short-circuits.</b> The research
/// loop exists to find which checks carry the strategy, and that is unanswerable if the store only
/// remembers the setups that passed, or only the checks that ran before the first failure.
/// see: Failed checks are recorded rather than discarded
///
/// The first four are cheap filters deciding whether a name is worth recording at all. The last six
/// are the pattern test. `cluster` is recorded and never gates.
/// </summary>
public static class LongPullbackRules
{
    /// <summary>Median daily turnover a name must clear to be worth simulating.</summary>
    public const decimal LiquidityFloor = 20_000_000m;

    /// <summary>Below this, spreads widen enough to swallow the stop.</summary>
    public const decimal PriceFloor = 5m;

    /// <summary>A stock moving less than this cannot produce a large winner in a few weeks.</summary>
    public const decimal DailyRangeFloor = 0.05m;

    /// <summary>How recent the mover-scan hit has to be, in sessions.</summary>
    public const int ThrustWindowSessions = 10;

    /// <summary>The dip's shortest acceptable length, in sessions.</summary>
    public const int MinimumPullbackBars = 2;

    /// <summary>Its longest. Beyond this the holders are leaving rather than pausing.</summary>
    public const int MaximumPullbackBars = 7;

    /// <summary>The most of the thrust the dip may give back.</summary>
    public const decimal MaximumRetrace = 0.40m;

    /// <summary>How far the trigger may sit from the current price, in daily ranges.</summary>
    public const decimal TriggerReachRanges = 1.5m;

    /// <summary>The give-up distance cap, in daily ranges. The strategy's own rule.</summary>
    public const decimal GiveUpRanges = 0.5m;

    /// <summary>How many same-industry names flagged the same night make a cluster.</summary>
    public const int ClusterThreshold = 2;

    /// <summary>
    /// Every check, in the document's order, each with the number it turned on.
    ///
    /// <paramref name="evidence"/> carries what the night knew. A field it could not fill is null,
    /// and a check whose input is null fails with the reason rather than passing by default: a
    /// missing figure is not a cleared threshold.
    /// </summary>
    public static IReadOnlyList<CheckResult> Evaluate(LongEvidence evidence) => Evaluate(evidence, SelectionRule.Long);

    /// <summary>
    /// Every check under one selection rule, which is the single implementation the detector, the
    /// harness at 5.3 and a test all read. The overload above passes the baseline; a version passes
    /// its own rule; nothing else turns long evidence into verdicts.
    /// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
    /// </summary>
    public static IReadOnlyList<CheckResult> Evaluate(LongEvidence evidence, SelectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(rule);

        return
        [
            Tradable(evidence, rule),
            MovesEnough(evidence, rule),
            Uptrend(evidence),
            Thrust(evidence, rule),
            DipShape(evidence, rule),
            HeldFloor(evidence, rule),
            Contraction(evidence, rule),
            TriggerNear(evidence, rule),
            ExitTight(evidence, rule),
            Cluster(evidence, rule),
        ];
    }

    private static CheckResult Tradable(LongEvidence e, SelectionRule rule) =>
        e.MedianDollarVolume is not decimal volume || e.Close is not decimal close
            ? CheckResult.Unknown("tradable", "no indicator row or no bar for the session")
            : new CheckResult("tradable", volume >= rule.Value(SelectionRule.LiquidityFloor) && close > rule.Value(SelectionRule.PriceFloor), volume)
            {
                // Two clauses, not four. The long gate tests turnover and price; the short one adds
                // capitalisation and listing age. The reading beside this check said "of four
                // clauses" for both until 4.1, which is a count restated in a display helper rather
                // than derived from the gate, and it was wrong for this one.
                Clauses =
                [
                    new ClauseResult("liquidity", volume >= rule.Value(SelectionRule.LiquidityFloor), volume),
                    new ClauseResult("price", close > rule.Value(SelectionRule.PriceFloor), close),
                ],
            };

    private static CheckResult MovesEnough(LongEvidence e, SelectionRule rule) =>
        e.AverageDailyRange is not decimal adr
            ? CheckResult.Unknown("moves-enough", "no indicator row for the session")
            : new CheckResult("moves-enough", adr >= rule.Value(SelectionRule.DailyRangeFloor), adr);

    private static CheckResult Uptrend(LongEvidence e) =>
        e.LadderGrade is null
            ? CheckResult.Unknown("uptrend", "the ladder grade has not been written for this session")
            : new CheckResult("uptrend", e.LadderGrade == "rising", null, e.LadderGrade);

    private static CheckResult Thrust(LongEvidence e, SelectionRule rule) =>
        e.SessionsSinceThrust is not int sessions
            ? new CheckResult("thrust", false, null, "no upward mover scan hit in the window")
            : new CheckResult("thrust", sessions <= rule.Value(SelectionRule.ThrustWindowSessions), sessions);

    private static CheckResult DipShape(LongEvidence e, SelectionRule rule)
    {
        if (e.Pullback is not PullbackGeometry.Pullback pullback)
        {
            return CheckResult.Unknown("dip-shape", "no thrust to measure a dip against");
        }

        // Two conditions on one check, because the corpus states them as one: a dip of the right
        // length that gave back too much is the same failure as one of the wrong length. The value
        // recorded is the retrace, which is the half a threshold experiment would move.
        bool rightLength = pullback.PullbackBars >= rule.Value(SelectionRule.MinimumPullbackBars)
            && pullback.PullbackBars <= rule.Value(SelectionRule.MaximumPullbackBars);
        bool shallowEnough = pullback.RetraceDepth is decimal depth && depth <= rule.Value(SelectionRule.MaximumRetrace) && depth >= 0m;

        return new CheckResult(
            "dip-shape",
            rightLength && shallowEnough,
            pullback.RetraceDepth,
            $"{pullback.PullbackBars} bar(s)")
        {
            // The bar count lived in the note beside a value that is the retrace, so a failing
            // dip-shape could not say which of the two it failed on. Both are clauses now and both
            // carry their own number.
            Clauses =
            [
                new ClauseResult("length", rightLength, pullback.PullbackBars),
                new ClauseResult("depth", shallowEnough, pullback.RetraceDepth),
            ],
        };
    }

    private static CheckResult HeldFloor(LongEvidence e, SelectionRule rule) =>
        e.ClosesBeyondFloor is not int beyond
            ? CheckResult.Unknown("held-floor", "no 21-day average over the dip")
            : new CheckResult("held-floor", beyond <= rule.Value(SelectionRule.MaximumClosesBeyondFloor), beyond);

    private static CheckResult Contraction(LongEvidence e, SelectionRule rule) =>
        e.RangeTodayOverAverage is not decimal ratio
            ? CheckResult.Unknown("contraction", "no range average for the session")
            : new CheckResult("contraction", ratio < rule.Value(SelectionRule.MaximumRangeRatio), ratio);

    private static CheckResult TriggerNear(LongEvidence e, SelectionRule rule) =>
        e.TriggerDistanceRanges is not decimal distance
            ? CheckResult.Unknown("trigger-near", "no trigger or no daily range for the session")
            : new CheckResult("trigger-near", distance <= rule.Value(SelectionRule.TriggerReachRanges), distance);

    private static CheckResult ExitTight(LongEvidence e, SelectionRule rule) =>
        e.StopDistanceRanges is not decimal distance
            ? CheckResult.Unknown("exit-tight", CheckResult.NoStopOrRange)
            : new CheckResult("exit-tight", distance <= rule.Value(SelectionRule.GiveUpRanges), distance);

    private static CheckResult Cluster(LongEvidence e, SelectionRule rule) =>
        new("cluster", (e.ClusterCount ?? 0) >= rule.Value(SelectionRule.ClusterThreshold), e.ClusterCount);

    /// <summary>
    /// What the night knew about one name, on the long side.
    ///
    /// Every field nullable, because every one of them can genuinely be absent: a name short of the
    /// warm-up has no averages, a name with no scan hit has no thrust, a name whose sector has never
    /// been resolved has no cluster count. A check whose input is absent fails and says why, which
    /// is the difference between a threshold that was not cleared and one that was never tested.
    /// </summary>
    public sealed record LongEvidence
    {
        public decimal? Close { get; init; }

        public decimal? MedianDollarVolume { get; init; }

        public decimal? AverageDailyRange { get; init; }

        public string? LadderGrade { get; init; }

        public int? SessionsSinceThrust { get; init; }

        public PullbackGeometry.Pullback? Pullback { get; init; }

        public int? ClosesBeyondFloor { get; init; }

        public decimal? RangeTodayOverAverage { get; init; }

        public decimal? TriggerDistanceRanges { get; init; }

        public decimal? StopDistanceRanges { get; init; }

        public int? ClusterCount { get; init; }

        /// <summary>
        /// Which scan produced the thrust, and the session it flagged.
        ///
        /// Not read by any gate. It is here because the detector resolves it while assembling the
        /// evidence and nothing downstream can recover it afterwards: a setup row records that a
        /// thrust was found and not whether it was a one-session move or a twenty-session one, and
        /// the two are measured from different places.
        /// </summary>
        public string? ThrustScan { get; init; }

        public DateOnly? ThrustSession { get; init; }

    }
}
