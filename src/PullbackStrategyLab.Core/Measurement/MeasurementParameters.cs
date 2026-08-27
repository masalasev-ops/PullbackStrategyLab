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
}
