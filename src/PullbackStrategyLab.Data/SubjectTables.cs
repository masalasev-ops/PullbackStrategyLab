namespace PullbackStrategyLab.Data;

/// <summary>
/// Which three tables a measurement pass reads and writes: the subjects, their controls, and where
/// the outcomes go.
///
/// <b>One stage filling either population, rather than a second implementation of the arithmetic.</b>
/// `ForwardReturnFiller` is already correct for a reconstructed subject: it bounds bars on
/// `observed_at` at or before the fill instant and takes the latest observation, and its own comment
/// says it is the one stage reading bars after its subject's date by design. The only thing tying it
/// to the evidence store was the literal table name in two queries and one insert. A second
/// implementation is the defect this corpus has met four times, and here the arithmetic is the thing
/// under test, so the two populations differ in which rows they read and in nothing else.
/// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
///
/// <b>Named pairs rather than free strings, so a caller cannot mix the two.</b> A parameter taking
/// any table name would let a reconstructed fill write into `forward_return` by a typo, and the rows
/// are real returns of real stocks: nothing about their shape would say which population they came
/// from. The two instances below are the only two that exist.
/// </summary>
public sealed record SubjectTables(
    string Setup,
    string Control,
    string ForwardReturn,
    bool ExcursionsAvailable)
{
    /// <summary>The evidence store: setups flagged forward, and the outcomes 3.6 fires on.</summary>
    public static readonly SubjectTables Evidence =
        new("setup", "control_setup", "forward_return", ExcursionsAvailable: true);

    /// <summary>
    /// The reconstructed population, which nothing downstream reads.
    ///
    /// <b>Excursions are unavailable and that is a property of the population rather than a setting.</b>
    /// They are expressed in the subject's own ATR, `indicator_daily` holds no row for a session the
    /// lab was not running, and the calibration run computes its averages in memory and discards
    /// them. Approximating one from daily bars is the stand-in `reached-ceiling`'s anchored clause
    /// already refuses by name, so the columns are written null with the reason on the row.
    /// </summary>
    public static readonly SubjectTables Calibration =
        new("calibration_setup", "calibration_control_setup", "calibration_forward_return",
            ExcursionsAvailable: false);

    /// <summary>Whether this pass writes to the evidence store, which one caller asserts before it runs.</summary>
    public bool IsEvidence => ReferenceEquals(this, Evidence);
}
