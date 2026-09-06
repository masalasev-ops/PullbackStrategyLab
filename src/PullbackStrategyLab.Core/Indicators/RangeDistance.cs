namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// A distance between two prices, stated in the name's own average daily range.
///
/// <b>The unit four gates are expressed in, and it was written out at every one of them until
/// 6.1.</b> `trigger-near`, `exit-tight` and `reached-ceiling` all compare a price gap divided by
/// the average daily range in price, and each detector, the vectorizer and the fixture's authored
/// row each spelled that division for itself. Every copy was correct and every copy was one edit
/// away from not being, on a quantity where a wrong answer is a plausible small number: the whole
/// argument the shared averages already rest on.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>The guard is the point of the type as much as the arithmetic is.</b> A range of nought or an
/// absent range makes the distance null rather than infinite or enormous, because a gate handed
/// nothing fails rather than passing, and a distance stated in a range that does not exist is not a
/// distance (see: A gate handed an absent or degenerate quantity fails rather than passing). A bar
/// the vendor sends with a close of nought reaches this, which is how the short side once threw
/// where the long side recorded a normal setup.
/// </summary>
public static class RangeDistance
{
    /// <summary>
    /// The average daily range expressed in price: the stored fraction times the session's raw
    /// close, or null where there is no fraction or it is nought.
    ///
    /// The raw close rather than the adjusted one, because that is the basis the fraction was
    /// computed against and the basis a trade is placed on.
    /// </summary>
    public static decimal? InPrice(decimal? averageDailyRange, decimal close) =>
        averageDailyRange is not decimal fraction || fraction == 0m ? null : fraction * close;

    /// <summary>The gap between two prices in ranges, or null where the range cannot carry it.</summary>
    public static decimal? Between(decimal from, decimal to, decimal? dailyRangeInPrice) =>
        dailyRangeInPrice is not decimal range || range == 0m ? null : Math.Abs(from - to) / range;
}
