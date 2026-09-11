namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The short side's rule set from 7.10: trim 15% of the planned position at 3R and again at 5R, and
/// close at the next open once the position has been held three sessions.
///
/// <b>Not the long side's rule with a sign flipped, and his own words are why.</b> "I'm not trailing
/// the shorts like the longs, like the close above the 9 EMA" (<c>P2</c> 1:47:28), and "generally I
/// hold my shorts for only maybe two to three days" (<c>P1</c> 25:18). So the short side has no trail
/// and ends on a count of sessions, where the long side ends on a daily-series condition. The
/// prediction written before the trace mirrored the long trail here, and the trace contradicts it,
/// which is why SOURCES.md records the prediction beside what the source actually says.
/// see: Long and short are never pooled into one figure
///
/// <b>The trims are the long side's figures mirrored and marked as the author's.</b> He takes profits
/// on a short into the first two or three days (<c>P1</c> 3:36:56) and names no fraction and no level
/// for it, so the short side carries the long side's 15% at 3R and 5R, generation 0's own fraction and
/// first level among them.
/// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
///
/// <b>The trim into support is not here and its absence is a decision rather than an oversight.</b>
/// Support is defined nowhere in this corpus as a level a trim could rest at (see: Trimming into
/// support is dropped from the baseline rather than defined here).
/// </summary>
public static class ShortExitRules
{
    /// <summary>
    /// The exit forms this side runs, named as SOURCES.md's exits table names them, so
    /// <c>clause-provenance</c> can hold the document and the code to one list.
    /// </summary>
    public static IReadOnlyList<string> Forms { get; } = [ExitReason.Trim, ExitReason.HoldLimit];

    /// <summary>The fraction of the planned share count each trim takes: the long side's, mirrored.</summary>
    public const decimal TrimFraction = LongExitRules.TrimFraction;

    /// <summary>The R multiples at which the trims fire: the long side's, mirrored.</summary>
    public static IReadOnlyList<decimal> TrimAt => LongExitRules.TrimAt;

    /// <summary>
    /// How many sessions a short is held before it is closed at the next open, the upper of his two.
    ///
    /// Counted from the store rather than from a calendar, the session the position opened in being
    /// the first, so a market holiday is absent from the count by not being there.
    /// </summary>
    public const int HoldSessions = 3;

    /// <summary>
    /// Whether a short held for <paramref name="sessionsHeld"/> sessions, this one included, is closed
    /// at the next open.
    ///
    /// At or beyond rather than exactly at, so a night the stage did not run is caught on the next one
    /// rather than holding the position for ever.
    /// </summary>
    public static bool HoldLimitReached(int sessionsHeld)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sessionsHeld);

        return sessionsHeld >= HoldSessions;
    }

    /// <summary>
    /// The price at which the next trim fires, having taken <paramref name="trimsTaken"/> already, or
    /// null where both have been taken.
    ///
    /// <b>From the realised risk, not the planned one</b>, on the long side's reasoning: R is the
    /// distance from the fill to the give-up point, because that is what the position can lose.
    /// </summary>
    public static decimal? TrimLevel(decimal entryPrice, decimal giveUpPrice, int trimsTaken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(trimsTaken);

        if (giveUpPrice <= entryPrice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giveUpPrice),
                $"A short's give-up point of {giveUpPrice} is not above its entry of {entryPrice}. The "
                + "risk per share is the distance between them and a figure at or below zero would put "
                + "the trim level at or above the entry, where the position is not in profit at all.");
        }

        return trimsTaken >= TrimAt.Count
            ? null
            : entryPrice - (TrimAt[trimsTaken] * (giveUpPrice - entryPrice));
    }

    /// <summary>How many shares a trim takes: see <see cref="TrimArithmetic.SharesOf"/>.</summary>
    public static int TrimShares(int plannedShares, int heldShares) =>
        TrimArithmetic.SharesOf(plannedShares, heldShares, TrimFraction);

    /// <summary>
    /// What multiplies a printed price to put it on the adjusted basis the averages are computed on.
    ///
    /// One session's own <c>adj_close / close</c>, taken from the daily bar the average was last
    /// computed against. It is exactly right while no action falls between that session and the
    /// minute being converted, and the store raises a rebuild demand on every action it observes, so
    /// the window in which it is wrong is the window in which the averages are stale anyway.
    /// </summary>
    public static decimal AdjustmentFactor(decimal close, decimal adjustedClose)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(close);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustedClose);

        return adjustedClose / close;
    }
}
