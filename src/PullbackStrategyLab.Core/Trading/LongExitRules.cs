namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The long side's rule set: trail the 9-day average on the daily close and fill at the next open.
///
/// <b>A separate file from the short side's, and that is the deliverable rather than a preference.</b>
/// The two rule sets are not mirror images. This one is a daily-series condition evaluated once a
/// session and acted on the next morning; <see cref="ShortExitRules"/> is an intraday level plus an
/// hourly-close condition acted on inside the same session. One routine with a sign flag would have
/// to be the union of both and would test a strategy nobody trades, which is the single easiest way
/// to get a convincing answer to the wrong question.
/// see: Long and short are never pooled into one figure
///
/// <b>The comparison is on the adjusted basis, on both sides of it.</b> <c>ema_9</c> is computed on
/// adjusted close and the daily close read against it is the adjusted one, so a split inside the
/// position's life moves both together. Comparing an unadjusted close against an adjusted average
/// would arm the trail on the morning after every split, on every long the lab held.
///
/// <b>Active from entry with no arming threshold, so this takes no parameter beyond the two prices.</b>
/// The fixed give-up point already governs the early part of the trade, so a threshold would be a
/// rule nobody has described, and it would be a fourth arbitrary number.
/// see: The long trail is evaluated on the daily close and fills at the next open
/// </summary>
public static class LongExitRules
{
    /// <summary>
    /// Whether the session that just closed arms the trail, so the position exits at the next
    /// session's open.
    ///
    /// <b>Below, not below-or-equal.</b> A close sitting exactly on the average has not closed below
    /// it, and the strategy's own words are "closes below". This is the one comparison in the exit
    /// rules that is strict, where <see cref="TriggerTouch"/> is not, and the two differ because they
    /// are different questions: a touch asks whether a price was available and an equal price was,
    /// while a close asks whether a level was lost and an equal close did not lose it.
    /// </summary>
    public static bool TrailArmedBy(decimal adjustedClose, decimal nineDayAverage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustedClose);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nineDayAverage);

        return adjustedClose < nineDayAverage;
    }
}
