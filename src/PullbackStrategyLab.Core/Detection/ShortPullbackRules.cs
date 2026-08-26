using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// The ten short checks, as arithmetic over one name's evidence.
///
/// The mirror, with three that are not sign flips. <c>tradable-shortable</c> is stricter on three
/// of its four floors and adds a fourth the long side does not have. <c>averages-squeezing</c> is a
/// rule of its own with no long-side counterpart. <c>reached-ceiling</c> asks whether the bounce
/// reached a level rather than whether the dip held one. Two omissions the corpus states only by
/// leaving them out: there is no <c>contraction</c> and no <c>trigger-near</c> on this side. And one
/// asymmetry inside a shared shape: <c>no-reclaim</c> reads the 50-day average where the long side's
/// <c>held-floor</c> reads the 21-day.
/// see: Two directions are tested, with separate detectors, separate management and separate scoring
///
/// Everything that <b>is</b> a sign flip lives in <see cref="PullbackGeometry"/> and is read with
/// <c>isLong: false</c>, because two implementations of the same geometry drift and the drift is
/// invisible: every quantity is a plausible small number whichever way it was computed.
///
/// <b>Every check returns a result, pass or fail, and none of them short-circuits.</b>
/// see: Failed checks are recorded rather than discarded
///
/// <b>A check handed nothing fails and says what was absent.</b>
/// see: A gate handed an absent or degenerate quantity fails rather than passing
/// </summary>
public static class ShortPullbackRules
{
    /// <summary>Same floor as the long side. The parameter table calls it the price floor, both sides.</summary>
    public const decimal PriceFloor = LongPullbackRules.PriceFloor;

    /// <summary>Stricter than the long side's, because dollar volume predicts borrow better than size.</summary>
    public const decimal LiquidityFloor = 50_000_000m;

    /// <summary>The borrow-availability proxy. The cap comes free from the sector lookup already made.</summary>
    public const decimal MarketCapFloor = 2_000_000_000m;

    /// <summary>Borrow on a recent listing is unreliable at any size, so they are excluded outright.</summary>
    public const int MinimumSessionsListed = 90;

    /// <summary>Identical to the long side, and shared rather than restated so the two cannot drift.</summary>
    public const decimal DailyRangeFloor = LongPullbackRules.DailyRangeFloor;

    /// <summary>How recent the downward mover-scan hit has to be, in sessions.</summary>
    public const int ThrustWindowSessions = LongPullbackRules.ThrustWindowSessions;

    /// <summary>The bounce's shortest acceptable length, in sessions.</summary>
    public const int MinimumBounceBars = LongPullbackRules.MinimumPullbackBars;

    /// <summary>Its longest.</summary>
    public const int MaximumBounceBars = LongPullbackRules.MaximumPullbackBars;

    /// <summary>The most of the drop the bounce may recover.</summary>
    public const decimal MaximumRecovery = LongPullbackRules.MaximumRetrace;

    /// <summary>The window the 21-to-50 gap is compared against its own average over.</summary>
    public const int SqueezeWindowSessions = 20;

    /// <summary>How close to a level counts as having reached it, in daily ranges.</summary>
    public const decimal CeilingReachRanges = 0.5m;

    /// <summary>The give-up distance cap, in daily ranges. The strategy's own rule, both sides.</summary>
    public const decimal GiveUpRanges = LongPullbackRules.GiveUpRanges;

    /// <summary>How many same-industry names flagged the same night make a cluster.</summary>
    public const int ClusterThreshold = LongPullbackRules.ClusterThreshold;

    /// <summary>Every check, in the document's order, each with the number it turned on.</summary>
    public static IReadOnlyList<CheckResult> Evaluate(ShortEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return
        [
            TradableShortable(evidence),
            MovesEnough(evidence),
            Downtrend(evidence),
            AveragesSqueezing(evidence),
            Thrust(evidence),
            BounceShape(evidence),
            ReachedCeiling(evidence),
            NoReclaim(evidence),
            ExitTight(evidence),
            Cluster(evidence),
        ];
    }

    /// <summary>
    /// Four floors rather than the long side's two, and a name missing any input fails.
    ///
    /// The market cap in particular is absent until SectorResolver has seen the name, and an absent
    /// cap is not a cleared cap: this is the check standing in for borrow availability, which the
    /// feed does not carry at all, so passing a name nobody has looked up would be the one place a
    /// missing figure turns into a tradable verdict.
    ///
    /// <b>Except in calibration, where the clause is exempted by name.</b> The lookup is bounded on
    /// when it was made, like every other point-in-time read, so a reconstructed 2024 session has no
    /// capitalisation at all: it was resolved in 2026 or it was never resolved. Left alone, every
    /// short candidate fails here and the short half of the distribution is empty, and a threshold
    /// calibrated against an empty distribution is worse than no threshold. Dropping the whole check
    /// was the other option and is worse still, because it changes what the short side is without
    /// saying so. One clause is exempted, the other three still measure, and every verdict says which
    /// on the same pattern `reached-ceiling` uses for its anchored clause.
    /// see: A calibration run reconstructs against current membership and computes its indicators in memory
    /// </summary>
    private static CheckResult TradableShortable(ShortEvidence e)
    {
        if (e.MedianDollarVolume is not decimal volume
            || e.Close is not decimal close
            || e.SessionsListed is not int listed)
        {
            return CheckResult.Unknown(
                "tradable-shortable",
                "no indicator row or no bar for the session");
        }

        if (!e.MarketCapExempt && e.MarketCap is not decimal)
        {
            return CheckResult.Unknown(
                "tradable-shortable",
                "no resolved market capitalisation for the session");
        }

        bool passes = volume >= LiquidityFloor
            && close > PriceFloor
            && (e.MarketCapExempt || e.MarketCap > MarketCapFloor)
            && listed >= MinimumSessionsListed;

        return new CheckResult(
            "tradable-shortable",
            passes,
            volume,
            e.MarketCapExempt ? ClausesRunWithoutTheCap : null);
    }

    /// <summary>
    /// What `tradable-shortable` actually tested when the cap was exempted, recorded on the verdict.
    ///
    /// Every calibration row carries it, so a later session reading a short count knows the gate that
    /// produced it was three clauses rather than four. Without it the exemption is a fact about a run
    /// nobody kept, and the count reads as the count the nightly detector would have produced.
    /// </summary>
    public const string ClausesRunWithoutTheCap =
        "liquidity, price and listing age only; the market-cap clause is exempt in calibration";

    private static CheckResult MovesEnough(ShortEvidence e) =>
        e.AverageDailyRange is not decimal adr
            ? CheckResult.Unknown("moves-enough", "no indicator row for the session")
            : new CheckResult("moves-enough", adr >= DailyRangeFloor, adr);

    private static CheckResult Downtrend(ShortEvidence e) =>
        e.LadderGrade is null
            ? CheckResult.Unknown("downtrend", "the ladder grade has not been written for this session")
            : new CheckResult("downtrend", e.LadderGrade == "falling", null, e.LadderGrade);

    /// <summary>
    /// The 21-to-50 gap against its own average over the last twenty sessions.
    ///
    /// The gap is absolute rather than signed. In a downtrend the 21-day sits below the 50-day, so a
    /// signed gap is negative and "narrower" would read as "further below", which is the opposite
    /// rule on the one side this check runs. The value recorded is the ratio, because that is the
    /// half a threshold experiment would move.
    /// </summary>
    private static CheckResult AveragesSqueezing(ShortEvidence e)
    {
        if (e.GapOverAverageGap is not decimal ratio)
        {
            return CheckResult.Unknown(
                "averages-squeezing",
                $"fewer than {SqueezeWindowSessions} sessions of averages, or an average gap of zero");
        }

        return new CheckResult("averages-squeezing", ratio < 1m, ratio);
    }

    private static CheckResult Thrust(ShortEvidence e) =>
        e.SessionsSinceThrust is not int sessions
            ? new CheckResult("thrust", false, null, "no downward mover scan hit in the window")
            : new CheckResult("thrust", sessions <= ThrustWindowSessions, sessions);

    private static CheckResult BounceShape(ShortEvidence e)
    {
        if (e.Bounce is not PullbackGeometry.Pullback bounce)
        {
            return CheckResult.Unknown("bounce-shape", "no thrust to measure a bounce against");
        }

        bool rightLength = bounce.PullbackBars >= MinimumBounceBars && bounce.PullbackBars <= MaximumBounceBars;
        bool shallowEnough = bounce.RetraceDepth is decimal depth && depth <= MaximumRecovery && depth >= 0m;

        return new CheckResult(
            "bounce-shape",
            rightLength && shallowEnough,
            bounce.RetraceDepth,
            $"{bounce.PullbackBars} bar(s)");
    }

    /// <summary>
    /// Two of the document's three clauses. The third arrives at 4.4 and is not approximated.
    ///
    /// The document says the price is within half a daily range of the 21-day average, of the 50-day
    /// average, <b>or</b> of the declining average price anchored to the last swing high. That third
    /// level is a volume-weighted average over minute bars from the swing high forward, and
    /// VwapEngine is what computes it. Until then this check runs its two average clauses and the
    /// setup record says which ran, because a later session reading a passing `reached-ceiling` has
    /// no other way to know it was narrower than the document describes.
    ///
    /// Not approximated from daily bars, deliberately. A daily-bar stand-in produces a number that
    /// looks like the real thing inside the check that decides whether the bounce reached its
    /// ceiling, which is plausible, wrong and silent: the same shape as a gate passing on a quantity
    /// that was never there.
    /// </summary>
    private static CheckResult ReachedCeiling(ShortEvidence e)
    {
        if (e.DistanceToNearestAverageRanges is not decimal distance)
        {
            return CheckResult.Unknown(
                "reached-ceiling",
                "no 21-day or 50-day average, or no daily range, for the session");
        }

        return new CheckResult(
            "reached-ceiling",
            distance <= CeilingReachRanges,
            distance,
            ClausesRun);
    }

    /// <summary>What `reached-ceiling` actually tested, recorded beside every one of its verdicts.</summary>
    public const string ClausesRun = "21-day and 50-day only; the anchored clause arrives at 4.4";

    private static CheckResult NoReclaim(ShortEvidence e) =>
        e.ClosesBeyondFloor is not int beyond
            ? CheckResult.Unknown("no-reclaim", "no 50-day average for the session")
            : new CheckResult("no-reclaim", beyond == 0, beyond);

    private static CheckResult ExitTight(ShortEvidence e) =>
        e.StopDistanceRanges is not decimal distance
            ? CheckResult.Unknown("exit-tight", "no stop or no daily range for the session")
            : new CheckResult("exit-tight", distance <= GiveUpRanges, distance);

    private static CheckResult Cluster(ShortEvidence e) =>
        new("cluster", (e.ClusterCount ?? 0) >= ClusterThreshold, e.ClusterCount);

    /// <summary>
    /// What the night knew about one name, on the short side.
    ///
    /// Every field nullable, for the reason the long side's is: a field the night could not fill is
    /// absent, and a check handed an absent field fails with the reason rather than passing by
    /// default.
    /// </summary>
    public sealed record ShortEvidence
    {
        public decimal? Close { get; init; }

        public decimal? MedianDollarVolume { get; init; }

        public decimal? MarketCap { get; init; }

        /// <summary>
        /// Whether the market-cap clause is exempted by name for this run.
        ///
        /// False everywhere but a calibration run, and false by default so a caller that forgets it
        /// gets the strict gate rather than the lenient one. A default of true would exempt every
        /// forward night the day somebody added a second constructor.
        /// </summary>
        public bool MarketCapExempt { get; init; }

        /// <summary>Sessions of stored history, which is what the lab can see of a listing's age.</summary>
        public int? SessionsListed { get; init; }

        public decimal? AverageDailyRange { get; init; }

        public string? LadderGrade { get; init; }

        /// <summary>Today's absolute 21-to-50 gap over its own average across the squeeze window.</summary>
        public decimal? GapOverAverageGap { get; init; }

        public int? SessionsSinceThrust { get; init; }

        public PullbackGeometry.Pullback? Bounce { get; init; }

        /// <summary>Closes above the 50-day average during the bounce. The long side reads the 21-day.</summary>
        public int? ClosesBeyondFloor { get; init; }

        /// <summary>Distance to whichever of the two averages is nearer, in daily ranges.</summary>
        public decimal? DistanceToNearestAverageRanges { get; init; }

        public decimal? StopDistanceRanges { get; init; }

        public int? ClusterCount { get; init; }
    }
}
