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
    /// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
    /// </summary>
    public const int BootstrapBlockSessions = 10;

    /// <summary>
    /// How many bootstrap resamples an interval is taken over.
    ///
    /// Enough that the percentile bounds are stable to the two decimals the scoreboard shows, which
    /// is the only property that matters here: the figure is reported to a person, not consumed by
    /// another calculation.
    /// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
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
    /// to: the sample goes as the inverse square of it, so a figure derived at two points is derived
    /// at that lever's setting and moves with it.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    public const double DetectableDifference = 0.02d;

    /// <summary>
    /// How many effective observations band 1 needs before it is allowed to answer.
    ///
    /// <b>Derived at 5.0(b) against the interval band 1 actually computes, over the flagged
    /// population's own dispersion.</b> The paired dispersion measured over the calibration store's
    /// flagged rows is 0.188681 on the long side, nearly twice the universe figure the 262 rested
    /// on, which alone puts the normal-theory minimum at 936; the studentised moving-block bootstrap
    /// at a handful of blocks needs 1.925 times that to detect two points at 90% power, found by
    /// simulating the estimator over series shaped like the store's nights. Every digit traces to a
    /// named input or to that procedure, and `tools/derive-minimum-sample.py` reproduces it.
    ///
    /// <b>Counted in effective observations, and that is the half that was missing.</b> A minimum
    /// satisfied by rows is satisfiable by rows carrying far less than their own number of
    /// observations' worth of information, and nothing on the surface would say so.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    public const int MinimumEffectiveObservations = 1802;

    /// <summary>
    /// How many sessions band 1 needs before it is allowed to answer, which is the other half of
    /// checkpoint 3.6's trigger.
    ///
    /// <b>Derived rather than authored, because it is not a second judgement.</b> It is twice the
    /// block length, which is the floor <see cref="PairedInterval.Of"/> already enforces: a moving
    /// block bootstrap with nothing to resample cannot produce an interval at all. Writing twenty
    /// here as a literal would put the same number in two places and let them drift, and the one
    /// that governs is the bootstrap's.
    ///
    /// <b>It is a separate condition from the effective count and not a weaker form of it.</b> The
    /// two are settled by different things: sessions are what the bootstrap needs before an interval
    /// exists, observations are what the decision needs before the interval means anything, and
    /// neither substitutes for the other. A fortnight of very wide nights reaches the minimum sample
    /// before it reaches twenty sessions, and a year of thin ones does the reverse. A panel
    /// reporting one of them and calling it the trigger is reporting half a condition.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    public const int MinimumSessions = BootstrapBlockSessions * 2;

    /// <summary>
    /// How many names a session needs before its cross-section is used to measure dispersion.
    ///
    /// A thin session's mean is mostly one of its own names, so removing it removes part of the
    /// dispersion being measured and the estimate comes back too small. Too small shrinks the minimum
    /// and fires the decision early, which is the direction that costs something.
    /// </summary>
    public const int DispersionMinimumNames = 20;

    /// <summary>
    /// How late an answer the session itself asked for may arrive and still be attributed to it.
    ///
    /// Authored, and it lives here so the recomputer reads it rather than carrying a literal. The
    /// figure is the operator's: 24 hours is ample for a slot that runs at 18:12 and leaves room for
    /// a rerun the following morning, and it is short enough that nobody could call a week-old
    /// lookup part of the night. Moving it is one edit in ARCHITECTURE's parameters table and one
    /// here, and pinned-constants fails if the two disagree.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    public const int LatenessBoundHours = 24;

    /// <summary>
    /// The execution family's minimum sample, in paired trades.
    ///
    /// <b>A row count, and the record says so rather than dressing it as an effective figure.</b>
    /// The corpus's unit is effective observations because overlapping labels and a shared market
    /// factor make a row worth less than its own number, and the setup-level discount was measured
    /// at 3.40 rather than assumed at 1. No trade has ever fired, so nothing about a design effect
    /// over trades can be measured today, and stating one would invent the quantity the setup-level
    /// measurement refused to assume. What a version's pre-registration carries until then is this
    /// count, with `minimum_sample_unit` on the row saying it is a count.
    ///
    /// It detects about a 0.35R difference, in rows.
    /// see: The execution minimum is 200 paired trades and its conversion waits on a trade existing
    /// </summary>
    public const int ExecutionMinimumPairedTrades = 200;
}
