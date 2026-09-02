using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The order prices, derived from the final pullback session's extremes and not from the screening
/// geometry, over every relationship rather than the ones a fixture happened to hold.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a plan; the
/// sessions below are written to sit either side of each clause of the derivation.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// see: The order prices are derived from the final pullback session's minutes, not from the screening geometry
/// </summary>
public sealed class OrderPricesTests
{
    /// <summary>A long enters through the session's high and gives up a tenth of a range under its low.</summary>
    [Fact]
    public void A_long_enters_at_the_sessions_high_and_gives_up_below_its_low()
    {
        OrderPrices.Pair pair = OrderPrices.For(SetupDirection.Long, sessionHigh: 104m, sessionLow: 101m, averageDailyRange: 5m);

        Assert.Equal(104m, pair.Trigger);
        Assert.Equal(100.5m, pair.GiveUp);
        Assert.Equal(3.5m, pair.Distance);
    }

    /// <summary>A short enters through the session's low and gives up a tenth of a range over its high.</summary>
    [Fact]
    public void A_short_enters_at_the_sessions_low_and_gives_up_above_its_high()
    {
        OrderPrices.Pair pair = OrderPrices.For(SetupDirection.Short, sessionHigh: 52m, sessionLow: 49m, averageDailyRange: 2.5m);

        Assert.Equal(49m, pair.Trigger);
        Assert.Equal(52.25m, pair.GiveUp);
        Assert.Equal(3.25m, pair.Distance);
    }

    /// <summary>
    /// The offset is the same fraction of the range on both sides, so neither side carries one the
    /// other does not, and it is the constant the authored-parameters table states.
    /// </summary>
    [Fact]
    public void The_offset_is_a_tenth_of_the_range_on_both_sides()
    {
        Assert.Equal(0.1m, OrderPrices.GiveUpOffsetInRanges);

        OrderPrices.Pair longSide = OrderPrices.For(SetupDirection.Long, 110m, 100m, 20m);
        OrderPrices.Pair shortSide = OrderPrices.For(SetupDirection.Short, 110m, 100m, 20m);

        Assert.Equal(2m, 100m - longSide.GiveUp);
        Assert.Equal(2m, shortSide.GiveUp - 110m);
    }

    /// <summary>
    /// A range that is not positive, a session whose low is above its high, and a direction that is
    /// neither side are refused rather than priced, on the terms every degenerate quantity is.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void A_degenerate_input_is_refused_rather_than_priced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPrices.For(SetupDirection.Long, 104m, 101m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPrices.For(SetupDirection.Long, 104m, 101m, -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPrices.For(SetupDirection.Long, 100m, 101m, 5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPrices.For("sideways", 104m, 101m, 5m));
    }
}
