namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The six scan magnitudes, as arithmetic. In Core for the reason the averages are: two components
/// need the same numbers and only one of them writes them down.
///
/// ScanEngine computes a magnitude for every universe member and keeps the top fifty by it.
/// SignalVectorizer freezes the magnitude that put a name on its scan, and reads it back from the
/// row rather than recomputing it. Anything else that needs one calls this.
///
/// <b>Everything here reads the adjusted basis, and that is the whole reason this class exists
/// rather than three expressions at the call site.</b> Read raw, a two-for-one split is a fifty
/// percent decline: it would top the decliner scan every time one happened, feed straight into the
/// thrust check as a real event, and produce a plausible ranked list rather than an error. The
/// averages closed the same trap at 1.12 and nothing had closed it for the scans.
/// see: Every scan magnitude is computed on the adjusted basis
///
/// The open has no stored adjusted counterpart, so it goes onto the adjusted basis through its own
/// bar's <c>adj_close / close</c> factor, which is what IndicatorEngine does for the high and the
/// low. Applying the previous bar's factor instead would be wrong on exactly the session a
/// distribution goes ex, which is the session the gap scan exists to notice.
/// </summary>
public static class ScanMagnitudes
{
    /// <summary>Yesterday's close to today's, on the adjusted basis.</summary>
    public static decimal DailyChange(decimal previousAdjustedClose, decimal adjustedClose) =>
        Ratio(previousAdjustedClose, adjustedClose);

    /// <summary>
    /// Yesterday's close to today's open, which is the part of the move that happened while the
    /// market was shut.
    /// </summary>
    public static decimal Gap(decimal previousAdjustedClose, decimal open, decimal close, decimal adjustedClose) =>
        Ratio(previousAdjustedClose, OnTheAdjustedBasis(open, close, adjustedClose));

    /// <summary>The change over the month-mover window, close to close on the adjusted basis.</summary>
    public static decimal MonthChange(decimal adjustedCloseThen, decimal adjustedCloseNow) =>
        Ratio(adjustedCloseThen, adjustedCloseNow);

    /// <summary>
    /// A raw intraday price on the adjusted basis, through its own bar's factor.
    ///
    /// A bar whose close is zero cannot have a factor, so it keeps the raw price rather than
    /// dividing by nothing. That is a vendor row this lab should never see, and it is handled here
    /// rather than left to throw somewhere further along where the cause would not be obvious.
    /// </summary>
    public static decimal OnTheAdjustedBasis(decimal price, decimal close, decimal adjustedClose) =>
        close == 0m ? price : price * (adjustedClose / close);

    private static decimal Ratio(decimal from, decimal to) => from == 0m ? 0m : (to - from) / from;
}
