using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two rule sets and the order they resolve in, over every price relationship rather than the
/// ones a session happened to produce.
///
/// <b>Pure, on the footing <see cref="FillModelTests"/> already sets.</b> Nothing here opens a store,
/// so the arithmetic and the boundaries are asserted directly and <see cref="PositionManagerTests"/>
/// is about walking a session rather than about which side of a comparison an inequality falls.
/// </summary>
public sealed class ExitRulesTests
{
    // ---- the long trail ------------------------------------------------------------------------

    /// <summary>A daily close below the 9-day average arms the trail; one above it does not.</summary>
    [Fact]
    public void The_trail_arms_on_a_close_below_the_nine_day_average()
    {
        Assert.True(LongExitRules.TrailArmedBy(adjustedClose: 99m, nineDayAverage: 100m));
        Assert.False(LongExitRules.TrailArmedBy(adjustedClose: 101m, nineDayAverage: 100m));
    }

    /// <summary>
    /// A close sitting exactly on the average has not closed below it.
    ///
    /// The strict comparison here and the non-strict one in <see cref="TriggerTouch"/> are different
    /// questions rather than an inconsistency: a touch asks whether a price was available and an
    /// equal price was, while a close asks whether a level was lost and an equal close did not lose
    /// it.
    /// </summary>
    [Fact]
    public void A_close_exactly_on_the_nine_day_average_does_not_arm_the_trail()
    {
        Assert.False(LongExitRules.TrailArmedBy(adjustedClose: 100m, nineDayAverage: 100m));
        Assert.True(TriggerTouch.Reached(SetupDirection.Long, triggerPrice: 100m, high: 100m, low: 99m));
    }

    /// <summary>A price at or below nothing is refused rather than compared.</summary>
    [Fact]
    public void The_trail_refuses_a_price_that_is_not_a_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LongExitRules.TrailArmedBy(0m, 100m));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongExitRules.TrailArmedBy(100m, -1m));
    }

    // ---- the short trim ------------------------------------------------------------------------

    /// <summary>
    /// The trim level is three units of realised risk below the price the entry actually got.
    ///
    /// From the realised distance and not the plan's, because R is taken over the money the position
    /// can lose and the slippage moved that.
    /// </summary>
    [Fact]
    public void The_trim_level_is_three_units_of_realised_risk_below_the_entry()
    {
        Assert.Equal(84.60m, ShortExitRules.TrimLevel(entryPrice: 99.90m, giveUpPrice: 105m));
        Assert.Equal(3m, ShortExitRules.TrimAt);
    }

    /// <summary>A give-up point that is not above a short's entry is refused rather than inverted.</summary>
    [Fact]
    public void A_give_up_point_at_or_below_a_shorts_entry_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortExitRules.TrimLevel(100m, 100m));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortExitRules.TrimLevel(100m, 95m));
    }

    /// <summary>The trim takes 15% of the planned share count, floored to whole shares.</summary>
    [Fact]
    public void The_trim_takes_fifteen_per_cent_of_the_planned_count_floored()
    {
        Assert.Equal(0.15m, ShortExitRules.TrimFraction);
        Assert.Equal(22, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 150));
        Assert.Equal(15, ShortExitRules.TrimShares(plannedShares: 100, heldShares: 100));
    }

    /// <summary>
    /// The trim is capped at what is held, because RiskGate may have reduced the order below the
    /// plan's size and a trim larger than the position would close more than was ever opened.
    /// </summary>
    [Fact]
    public void The_trim_never_exceeds_what_is_held()
    {
        Assert.Equal(10, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 10));
        Assert.Equal(0, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 0));
    }

    /// <summary>A position too small for one whole share of trim is not trimmed at all.</summary>
    [Fact]
    public void A_position_below_seven_shares_yields_no_trim()
    {
        Assert.Equal(0, ShortExitRules.TrimShares(plannedShares: 6, heldShares: 6));
        Assert.Equal(1, ShortExitRules.TrimShares(plannedShares: 7, heldShares: 7));
    }

    // ---- the short's hourly exit ---------------------------------------------------------------

    /// <summary>An hourly close above the 50-day average reclaims it; one at it does not.</summary>
    [Fact]
    public void A_close_above_the_fifty_day_average_reclaims_it_and_one_at_it_does_not()
    {
        Assert.True(ShortExitRules.Reclaimed(adjustedHourlyClose: 101m, fiftyDayAverage: 100m));
        Assert.False(ShortExitRules.Reclaimed(adjustedHourlyClose: 100m, fiftyDayAverage: 100m));
        Assert.False(ShortExitRules.Reclaimed(adjustedHourlyClose: 99m, fiftyDayAverage: 100m));
    }

    /// <summary>
    /// The adjustment factor puts a printed price on the basis the averages are computed on, and is
    /// one where no action has fallen since.
    ///
    /// A two-for-one split halves every earlier adjusted close, so a printed price of 100 on the
    /// session before it is 50 on the adjusted basis, and comparing it against an average computed
    /// there without converting would clear a 50-day average of 60 by a mile.
    /// </summary>
    [Fact]
    public void The_adjustment_factor_puts_a_printed_price_on_the_averages_basis()
    {
        Assert.Equal(1m, ShortExitRules.AdjustmentFactor(close: 100m, adjustedClose: 100m));
        Assert.Equal(0.5m, ShortExitRules.AdjustmentFactor(close: 100m, adjustedClose: 50m));
        Assert.Equal(50m, 100m * ShortExitRules.AdjustmentFactor(100m, 50m));
    }

    // ---- the order two rules resolve in --------------------------------------------------------

    /// <summary>
    /// An exit at a minute's open resolves before one reached inside that minute, whatever rule sent
    /// either.
    ///
    /// A fact about the bar rather than a choice, which is why it outranks the reason.
    /// </summary>
    [Fact]
    public void An_exit_at_the_open_resolves_before_one_inside_the_bar()
    {
        ExitCandidate? first = ExitReason.First(
        [
            new ExitCandidate(ExitReason.GaveUp, 95m, AtTheOpen: false),
            new ExitCandidate(ExitReason.Trail, 98m, AtTheOpen: true),
        ]);

        Assert.Equal(ExitReason.Trail, first!.Reason);
    }

    /// <summary>
    /// Two rules at the same open resolve as the give-up point, which is the rule 4.8 owed and the
    /// only thing running both to the end needs.
    ///
    /// A gap through the stop names how the loss occurred, and LossClassifier at 4.10 keys on that;
    /// recording such a minute as a trail exit would hide a gap loss inside a rule exit.
    /// </summary>
    [Fact]
    public void Giving_up_resolves_before_a_rule_set_at_the_same_open()
    {
        ExitCandidate? first = ExitReason.First(
        [
            new ExitCandidate(ExitReason.Trail, 88m, AtTheOpen: true),
            new ExitCandidate(ExitReason.GaveUp, 95m, AtTheOpen: true),
        ]);

        Assert.Equal(ExitReason.GaveUp, first!.Reason);
        Assert.Equal(95m, first.RestingPrice);

        Assert.Equal(0, ExitReason.Rank(ExitReason.GaveUp));
        Assert.Equal(1, ExitReason.Rank(ExitReason.Trail));
        Assert.Equal(1, ExitReason.Rank(ExitReason.Reclaim));
    }

    /// <summary>
    /// The two rule-set exits share a rank because they can never contest each other: one is the
    /// long side's and one is the short side's, and no position has both.
    /// </summary>
    [Fact]
    public void The_two_rule_set_exits_share_a_rank_because_no_position_has_both()
    {
        Assert.Equal(ExitReason.Rank(ExitReason.Trail), ExitReason.Rank(ExitReason.Reclaim));
        Assert.Equal(3, ExitReason.ThatCloseAPosition.Count);
        Assert.DoesNotContain(ExitReason.Trim, ExitReason.ThatCloseAPosition);
    }

    /// <summary>
    /// A reason with no rank is refused rather than sorted to one end, so a fourth rule added later
    /// fails here instead of deciding an exit by accident.
    /// </summary>
    [Fact]
    public void A_reason_with_no_rank_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExitReason.Rank(ExitReason.Trim));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExitReason.Rank("something-later"));
    }

    /// <summary>A minute no rule named ends nothing.</summary>
    [Fact]
    public void A_minute_no_rule_named_ends_nothing() => Assert.Null(ExitReason.First([]));
}
