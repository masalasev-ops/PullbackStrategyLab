using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What holding a short costs, and how far a fill missed the price its instruction named.
///
/// <b>Pure, on the footing <see cref="FillModelTests"/> and <see cref="ExitRulesTests"/> set.</b>
/// Nothing here opens a store, so the arithmetic and its signs are asserted over every relationship
/// rather than the ones a session happened to produce.
/// </summary>
public sealed class BorrowCostTests
{
    // ---- the borrow ----------------------------------------------------------------------------

    /// <summary>
    /// The rate is annualised, so a day costs a 365th of it and four days cost four.
    ///
    /// At 1.0% a year a four-day hold of a position worth 15,000 costs about 1.64, which against a
    /// stop of 750 is roughly two tenths of one per cent of a unit of risk. The figure is small on
    /// purpose: the rate is set several times higher than a general-collateral borrow costs and it
    /// still rounds to nothing, which is why availability rather than cost is what the short side
    /// turns on.
    /// </summary>
    [Fact]
    public void The_rate_is_annualised_and_a_day_costs_a_three_hundred_and_sixty_fifth_of_it()
    {
        Assert.Equal(365, BorrowCost.DaysInTheYear);

        decimal oneDay = BorrowCost.Charged(15_000m, 0.010m, 1);
        decimal fourDays = BorrowCost.Charged(15_000m, 0.010m, 4);

        Assert.Equal(15_000m * 0.010m / 365m, oneDay, 10);

        // To ten places rather than exactly, and the gap is worth naming. The charge is computed
        // once from the day count rather than accumulated a day at a time, so four days and four
        // times one day agree to every digit anybody reads and differ in the last one a decimal can
        // represent. An exact assertion here would be asserting the order of two multiplications.
        Assert.Equal(4m * oneDay, fourDays, 10);
    }

    /// <summary>A position held no calendar days was never held overnight and pays nothing.</summary>
    [Fact]
    public void A_same_day_hold_costs_nothing() =>
        Assert.Equal(0m, BorrowCost.Charged(15_000m, 0.010m, 0));

    /// <summary>
    /// The rate is taken as an argument rather than read off the constant, so a trade is charged what
    /// its own position assumed.
    ///
    /// A rate held only as a constant would restate every historical short at whatever the constant
    /// says today, which is the same fault `trade_plan` stores `equity` and `risk_fraction` to avoid.
    /// </summary>
    [Fact]
    public void The_rate_charged_is_the_one_handed_in_and_not_the_constant()
    {
        decimal atTwice = BorrowCost.Charged(15_000m, BorrowAssumption.AnnualisedRate * 2m, 4);
        decimal atOnce = BorrowCost.Charged(15_000m, BorrowAssumption.AnnualisedRate, 4);

        Assert.Equal(2m * atOnce, atTwice, 10);
    }

    /// <summary>A value or a span that is not one is refused rather than charged.</summary>
    [Fact]
    public void An_absent_or_degenerate_quantity_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BorrowCost.Charged(0m, 0.010m, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => BorrowCost.Charged(15_000m, -0.010m, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => BorrowCost.Charged(15_000m, 0.010m, -1));
    }

    // ---- the difference ------------------------------------------------------------------------

    /// <summary>
    /// A worse fill is positive on all four cases, which is what lets one column be read across both
    /// sides.
    ///
    /// A long entry and a short exit both buy, so paying more is worse; a long exit and a short entry
    /// both sell, so getting less is worse.
    /// </summary>
    [Fact]
    public void A_worse_fill_is_positive_on_all_four_cases()
    {
        Assert.Equal(0.10m, PlanDifference.PerShare(SetupDirection.Long, isExit: false, 100m, 100.10m));
        Assert.Equal(0.10m, PlanDifference.PerShare(SetupDirection.Long, isExit: true, 100m, 99.90m));
        Assert.Equal(0.10m, PlanDifference.PerShare(SetupDirection.Short, isExit: false, 100m, 99.90m));
        Assert.Equal(0.10m, PlanDifference.PerShare(SetupDirection.Short, isExit: true, 100m, 100.10m));
    }

    /// <summary>
    /// A better fill is negative and is not an error, because a difference that could only be
    /// positive is a check whose only reading is the one it expects.
    /// </summary>
    [Fact]
    public void A_better_fill_is_negative_rather_than_refused() =>
        Assert.Equal(-0.10m, PlanDifference.PerShare(SetupDirection.Long, isExit: false, 100m, 99.90m));

    /// <summary>
    /// Basis points are of the price the instruction named, so the same money is a different figure
    /// on a six-dollar stock and a four-hundred-dollar one.
    ///
    /// That is the whole reason the journal's plan-against-actual column is stated in them.
    /// </summary>
    [Fact]
    public void Basis_points_are_of_the_price_that_was_named()
    {
        Assert.Equal(10d, PlanDifference.BasisPoints(0.10m, 100m), 9);
        Assert.Equal(1000d, PlanDifference.BasisPoints(0.60m, 6m), 9);
        Assert.Equal(1.5d, PlanDifference.BasisPoints(0.06m, 400m), 9);
    }

    /// <summary>An unknown direction is refused, because it would report a worse fill as a better one.</summary>
    [Fact]
    public void An_unknown_direction_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlanDifference.PerShare("sideways", isExit: false, 100m, 101m));
}
