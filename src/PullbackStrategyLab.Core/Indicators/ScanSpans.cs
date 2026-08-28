namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// How many sessions of move each scan flags, which is the span its thrust is measured over.
///
/// <b>Why this is a fact about the scan rather than about the geometry.</b> The six scans do not
/// all flag the same kind of move. `gainer` and `decliner` rank on the change from the previous
/// close; `gapper` and `gapdown` rank on the overnight gap. All four are one session. `leader` and
/// `laggard` rank on the change over twenty sessions, so the move they flag began nineteen sessions
/// before the session they flag it on.
///
/// Until 3.0(c) the geometry took every thrust as one session: the origin was the close before the
/// flagged session and the extreme was searched forward from it. For the four day scans that is
/// right. For the two month scans it puts one session of a twenty-session run in the denominator of
/// the retrace, and it finds the extreme at the flag whenever the real high sits before it. Both
/// errors push the same way and both produce a plausible small number.
///
/// In Core because the detectors, the vectorizer and the geometry all need the same answer, and
/// because a second copy of the mapping is how the day scans and the month scans start disagreeing
/// about which is which. `ScanEngine.MonthWindow` is this constant rather than a second twenty.
/// see: The scans select a fixed count by rank, not a threshold on the move
/// </summary>
public static class ScanSpans
{
    /// <summary>The four scans that flag one session's move.</summary>
    public const int DaySessions = 1;

    /// <summary>The month-mover window, in sessions. One trading month.</summary>
    public const int MonthSessions = 20;

    /// <summary>
    /// The span a scan's thrust covers, in sessions.
    ///
    /// Throws on a name it does not know rather than defaulting to one session. A scan added later
    /// and not listed here would silently be measured as a one-day move, which is the exact defect
    /// this class exists to correct, arrived at from the other direction.
    /// </summary>
    public static int SessionsFor(string scan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scan);

        return scan switch
        {
            "gainer" or "gapper" or "decliner" or "gapdown" => DaySessions,
            "leader" or "laggard" => MonthSessions,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scan),
                scan,
                "No span is declared for this scan. A scan whose span is unknown would be measured "
                + "as a one-session move, which is the defect ScanSpans exists to correct."),
        };
    }
}
