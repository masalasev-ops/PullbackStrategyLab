using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Where the caps bind once more than one version is live: over each version's own book, and never
/// over the books added together.
///
/// Verified over authored books, because no version exists and none can until 5.1, and no position
/// has ever been opened. The property is the caps' arithmetic over the book it is handed, and the
/// decision says which book that is.
/// see: Each live version has its own account, and the six caps bind over that version's positions alone
/// </summary>
public sealed class VersionAccountTests
{
    private static RiskVerdict Place(OpenBook book) =>
        RiskLimits.Apply(SetupDirection.Long, plannedShares: 10, triggerPrice: 50m, giveUpDistance: 1m, book);

    [Fact]
    public void Two_versions_each_at_the_cap_are_each_refused_and_a_third_with_nothing_open_is_not()
    {
        var first = new OpenBook(RiskCaps.MaxOpenPositions, 0, 0m);
        var second = new OpenBook(RiskCaps.MaxOpenPositions, 0, 0m);

        Assert.Equal(RiskLimits.OpenPositions, Place(first).BoundBy);
        Assert.Equal(RiskLimits.OpenPositions, Place(second).BoundBy);
        Assert.True(Place(OpenBook.Empty).IsPlaced);
    }

    [Fact]
    public void A_version_holding_three_is_not_refused_because_another_version_holds_four()
    {
        // The book handed to the gate is the version's own. Pooling the two would refuse the
        // three-position version on the other's fill, and the paired comparison would then be
        // measuring which version fired first rather than which rule selects better.
        var own = new OpenBook(3, 0, 0m);
        var pooled = new OpenBook(3 + RiskCaps.MaxOpenPositions, 0, 0m);

        Assert.True(Place(own).IsPlaced);
        Assert.Equal(RiskLimits.OpenPositions, Place(pooled).BoundBy);
    }

    [Fact]
    public void The_risk_budget_is_a_version_s_own_as_well()
    {
        // Two versions each carrying the whole 3% at stake are each full; neither is fuller for the
        // other's risk, and an empty account has the whole budget.
        var full = new OpenBook(2, 0, RiskCaps.MaxTotalRisk);
        RiskVerdict refused = Place(full);
        Assert.False(refused.IsPlaced);
        Assert.Equal(RiskLimits.TotalRisk, refused.BoundBy);

        RiskVerdict placed = Place(OpenBook.Empty);
        Assert.True(placed.IsPlaced);
        Assert.Equal(10, placed.Shares);
    }
}
