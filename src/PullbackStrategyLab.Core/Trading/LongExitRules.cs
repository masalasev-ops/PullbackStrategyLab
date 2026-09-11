namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The long side's rule set: trim 15% of the planned position at 3R and again at 5R, and trail the
/// 9-day average on the daily close, filling at the next open.
///
/// <b>A separate file from the short side's, and that is the deliverable rather than a preference.</b>
/// The two rule sets are not mirror images. This one ends a position on a daily-series condition
/// evaluated once a session and acted on the next morning; <see cref="ShortExitRules"/> ends one on a
/// count of sessions held, because he states that he does not trail his shorts the way he trails his
/// longs. One routine with a sign flag would have to be the union of both and would test a strategy
/// nobody trades, which is the single easiest way to get a convincing answer to the wrong question.
/// see: Long and short are never pooled into one figure
///
/// <b>The comparison is on the adjusted basis, on both sides of it.</b> <c>ema_9</c> is computed on
/// adjusted close and the daily close read against it is the adjusted one, so a split inside the
/// position's life moves both together. Comparing an unadjusted close against an adjusted average
/// would arm the trail on the morning after every split, on every long the lab held.
///
/// <b>Active from entry with no arming threshold, so the trail takes no parameter beyond the two
/// prices.</b> The fixed give-up point already governs the early part of the trade, so a threshold
/// would be a rule nobody has described, and it would be a fourth arbitrary number.
/// see: The long trail is evaluated on the daily close and fills at the next open
///
/// <b>The trims are his, from 7.10.</b> "Every time I think this stock is extended I sell 15%", and
/// the two levels he names are three and five R. What "extended" means beyond those two he says he
/// judges by eye and measures with no indicator, so the rule stops at the two levels he gives rather
/// than carrying a third number nobody stated.
/// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
/// </summary>
public static class LongExitRules
{
    /// <summary>
    /// The exit forms this side runs, named as SOURCES.md's exits table names them, so
    /// <c>clause-provenance</c> can hold the document and the code to one list.
    /// </summary>
    public static IReadOnlyList<string> Forms { get; } = [ExitReason.Trail, ExitReason.Trim];

    /// <summary>The fraction of the planned share count each trim takes, his own figure.</summary>
    public const decimal TrimFraction = 0.15m;

    /// <summary>The R multiples at which the trims fire, in order, his own two figures.</summary>
    public static IReadOnlyList<decimal> TrimAt { get; } = [3m, 5m];

    /// <summary>
    /// Whether the session that just closed arms the trail, so the position exits at the next
    /// session's open.
    ///
    /// <b>Below, not below-or-equal.</b> A close sitting exactly on the average has not closed below
    /// it, and the strategy's own words are "the first close below the 9 EMA". This is the one
    /// comparison in the exit rules that is strict, where <see cref="TriggerTouch"/> is not, and the
    /// two differ because they are different questions: a touch asks whether a price was available
    /// and an equal price was, while a close asks whether a level was lost and an equal close did not
    /// lose it.
    /// </summary>
    public static bool TrailArmedBy(decimal adjustedClose, decimal nineDayAverage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustedClose);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nineDayAverage);

        return adjustedClose < nineDayAverage;
    }

    /// <summary>
    /// The price at which the next trim fires, having taken <paramref name="trimsTaken"/> already, or
    /// null where both have been taken.
    ///
    /// <b>From the realised risk, not the planned one.</b> R is taken over the distance from the fill
    /// to the give-up point, because that is the money the position can lose; the plan's intended
    /// distance is a figure the slippage moved.
    /// </summary>
    public static decimal? TrimLevel(decimal entryPrice, decimal giveUpPrice, int trimsTaken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(trimsTaken);

        if (giveUpPrice >= entryPrice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giveUpPrice),
                $"A long's give-up point of {giveUpPrice} is not below its entry of {entryPrice}. The "
                + "risk per share is the distance between them and a figure at or below zero would put "
                + "the trim level at or below the entry, where the position is not in profit at all.");
        }

        return trimsTaken >= TrimAt.Count
            ? null
            : entryPrice + (TrimAt[trimsTaken] * (entryPrice - giveUpPrice));
    }

    /// <summary>How many shares a trim takes: see <see cref="TrimArithmetic.SharesOf"/>.</summary>
    public static int TrimShares(int plannedShares, int heldShares) =>
        TrimArithmetic.SharesOf(plannedShares, heldShares, TrimFraction);
}
