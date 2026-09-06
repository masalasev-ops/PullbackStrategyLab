namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// How far a bounce sits from the ceiling it is being measured against, expressed in daily ranges.
///
/// <b>One implementation with three callers, added at 6.1 for the reason every other shared quantity
/// in this namespace has one.</b> ShortSetupDetector assembles the two distances, ShortPullbackRules
/// folds them to decide `reached-ceiling`, and SignalVectorizer freezes the fold as the signal that
/// records what the gate compared. Those three have to be one number: every figure here is a
/// plausible small ratio whichever way it was computed, so a second implementation would disagree
/// with the first in a way nobody could see, and the frozen evidence would quietly stop describing
/// the decision it was frozen for.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>The gate is a disjunction and its quantity is therefore one distance.</b> The document asks
/// whether the price is within half a daily range of the 21-day average, of the 50-day, <b>or</b> of
/// the declining average price anchored to the last swing high. The nearest of the levels that were
/// computable is what a disjunction over distances comes to, so the number the gate compares is
/// single and the three levels are how it was arrived at. Until 6.1 nothing froze that number and the
/// row carried the inputs instead, which is why a version moving this gate's threshold could not be
/// scored (see: A version whose moved gate cannot be judged from the frozen signals is refused at admission).
///
/// <b>An absent anchor neither widens nor narrows the quantity.</b> The anchored clause is a level
/// over minute bars, so it is present only where the store holds minutes back to the swing. Where it
/// is absent the fold is over the two averages, which is strictly less than the document describes,
/// and the verdict's own clause note is what says which set ran. That note is a fact about the night
/// rather than part of this quantity, which is why it stays on the check result and not here.
/// </summary>
public static class CeilingDistance
{
    /// <summary>
    /// The nearer of the two average levels, in daily ranges, or null where the gate had no
    /// comparison to make.
    ///
    /// Null rather than a large number where the range is absent or nought: a distance stated in a
    /// range that does not exist is not a distance, and a gate handed nothing fails rather than
    /// passing (see: A gate handed an absent or degenerate quantity fails rather than passing).
    /// </summary>
    /// <param name="close">The session's close on the adjusted basis, which is the basis both averages are on.</param>
    /// <param name="emaMedium">The 21-day average as at the session.</param>
    /// <param name="emaLong">The 50-day average as at the session.</param>
    /// <param name="dailyRangeInPrice">The average daily range expressed in price, being the fraction times the close.</param>
    public static decimal? ToAveragesInRanges(
        decimal close,
        decimal emaMedium,
        decimal emaLong,
        decimal? dailyRangeInPrice) =>
        Nearest(
            RangeDistance.Between(close, emaMedium, dailyRangeInPrice),
            RangeDistance.Between(close, emaLong, dailyRangeInPrice));

    /// <summary>
    /// The anchored level's distance, in the same units and guarded the same way, or null where
    /// there is no level or no range to express it in.
    /// </summary>
    public static decimal? ToAnchoredInRanges(
        decimal close,
        decimal? anchoredLevel,
        decimal? dailyRangeInPrice) =>
        anchoredLevel is not decimal level
            ? null
            : RangeDistance.Between(close, level, dailyRangeInPrice);

    /// <summary>
    /// The disjunction, which is the nearest of the clauses that were computable, or null where none
    /// of them was.
    ///
    /// This is the quantity `reached-ceiling` compares and the quantity `ceiling_distance_ranges`
    /// freezes, and it is one method so that those two can never be two numbers.
    /// </summary>
    public static decimal? Nearest(decimal? toAverages, decimal? toAnchored) =>
        (toAverages, toAnchored) switch
        {
            (decimal averages, decimal anchored) => Math.Min(averages, anchored),
            (decimal averages, null) => averages,

            // A row with an anchored level and no average distance is not a state the detector can
            // reach, both being guarded on the same range. Answered rather than assumed away, because
            // the backfill computes the two from stored rows and a later reader should not have to
            // work out which combinations this method was written for.
            (null, decimal anchored) => anchored,
            _ => null,
        };
}
