namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// The authored numbers phase 3 measures with, in one place so a document stating one can be pinned
/// against it.
///
/// <b>Why they are here rather than in the components that read them.</b> Three of the four are
/// stated in `DECISIONS.md`, and a number stated in a document and typed again in a component is the
/// defect this corpus greps for. `ControlSampler` arrives at 3.3, `CeilingCalculator` at 3.4 and
/// `ScoreboardBuilder` at 3.5; the decisions that govern them were authored at 3.0 on purpose, so
/// the constants exist before their consumers do.
///
/// <b>They are the instrument, not the implementation.</b> The control count decides what a
/// comparison is made of, the block length decides when an interval is allowed to say anything, and
/// both were settled before the code that reads them was written, because a session authoring them
/// while writing their consumer is reviewing its own choices.
/// </summary>
public static class MeasurementParameters
{
    /// <summary>
    /// How many control names are drawn per set per flagged setup.
    ///
    /// Five rather than one, so a thin night is visibly thin rather than silently narrow: with one
    /// control the comparison inherits that one name's idiosyncratic move, and with fifty the draw
    /// reaches so far down the distance ordering that the match stops meaning anything. Five is an
    /// authored choice and is the kind of thing a proposal may later move.
    /// see: Matched control populations are drawn nightly, loose and tight
    /// </summary>
    public const int ControlsPerSet = 5;

    /// <summary>
    /// The block length of the bootstrap, in sessions, which is the scoring horizon.
    ///
    /// A ten-session forward return means adjacent nights share most of their window, so consecutive
    /// observations are serially correlated by construction. A block at least as long as the horizon
    /// is what carries that correlation into the resampling instead of assuming it away.
    /// see: The interval is a block bootstrap over paired differences, and the effective sample is measured
    /// </summary>
    public const int BootstrapBlockSessions = 10;

    /// <summary>
    /// How many bootstrap resamples an interval is taken over.
    ///
    /// Enough that the percentile bounds are stable to the two decimals the scoreboard shows, which
    /// is the only property that matters here: the figure is reported to a person, not consumed by
    /// another calculation.
    /// see: The interval is a block bootstrap over paired differences, and the effective sample is measured
    /// </summary>
    public const int BootstrapDraws = 10_000;

    /// <summary>
    /// The forward horizon the ceiling and the scoreboard are computed at, in sessions.
    ///
    /// Stated in ARCHITECTURE's authored parameters as the scoring horizon that interacts directly
    /// with the ceiling arithmetic, and repeated here rather than in three components.
    /// see: The win-rate ceiling is computed from the outcome distribution, never assumed
    /// </summary>
    public const int ScoringHorizonSessions = 10;

    /// <summary>
    /// The difference in ten-day forward return the evidence should be able to detect.
    ///
    /// <b>Two points because it is the size of the effect being hunted, not a target chosen for
    /// roundness.</b> The strategy's claimed expectancy is about 0.55R on a 3% stop, which is about
    /// 1.7 points of forward return. Detecting less than two points would be detecting something too
    /// small to trade after costs, so the threshold is what is worth having rather than what is
    /// claimed.
    ///
    /// It is a judgement rather than a measurement, and it is the lever the minimum is most sensitive
    /// to: the sample goes as the inverse square of it, so at the ratified power three points would
    /// need 117 observations where two needs 262.
    /// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
    /// </summary>
    public const double DetectableDifference = 0.02d;

    /// <summary>
    /// How many effective observations band 1 needs before it is allowed to answer.
    ///
    /// <b>Derived from two measurements and two ratified judgements.</b> It falls out of the
    /// dispersion measured over the fixture's own bars, the two-point difference above, the 95% the
    /// interval already uses and 90% power. Every digit traces to one of those four, which is what
    /// the figure it replaces could not say.
    ///
    /// <b>Counted in effective observations, and that is the half that was missing.</b> A minimum
    /// satisfied by rows is satisfiable by rows carrying far less than their own number of
    /// observations' worth of information, and nothing on the surface would say so.
    /// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
    /// </summary>
    public const int MinimumEffectiveObservations = 262;

    /// <summary>
    /// How many names a session needs before its cross-section is used to measure dispersion.
    ///
    /// A thin session's mean is mostly one of its own names, so removing it removes part of the
    /// dispersion being measured and the estimate comes back too small. Too small shrinks the minimum
    /// and fires the decision early, which is the direction that costs something.
    /// </summary>
    public const int DispersionMinimumNames = 20;
}
