namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The share count of one trim, which is arithmetic both rule sets share and not a rule either of
/// them owns.
///
/// It takes no direction, on purpose: the rule sets stay two code paths and each names its own
/// fraction, so what is shared is the rounding and nothing about when a trim fires or where.
/// </summary>
public static class TrimArithmetic
{
    /// <summary>
    /// How many shares a trim takes, given what the plan was sized at, what is actually held and the
    /// fraction the side's rule names.
    ///
    /// <b>A fraction of the planned count and not of what remains.</b> A fraction of the remainder is
    /// a decaying ladder that never fully exits and makes R accounting depend on how many times the
    /// rule has already fired; a fraction of the original is a fixed share count.
    ///
    /// <b>Floored, and nought where it would take everything that is held.</b> Floored because a share
    /// count is whole and the rounding has to go somewhere. A trim reduces a position and never ends
    /// one, so where RiskGate reduced the order far enough that the planned fraction is all of what
    /// was bought, there is no trim and the exit rules end the position. Until 7.10 this capped the
    /// trim at what was held instead, which on such a row closed every share while leaving the row
    /// open with nothing in it.
    /// </summary>
    public static int SharesOf(int plannedShares, int heldShares, decimal fraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plannedShares);
        ArgumentOutOfRangeException.ThrowIfNegative(heldShares);

        int wanted = (int)Math.Floor(plannedShares * fraction);

        return wanted < heldShares ? wanted : 0;
    }
}
