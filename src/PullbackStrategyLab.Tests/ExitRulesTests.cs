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

    // ---- the trims, both sides, from 7.10 --------------------------------------------------------

    /// <summary>
    /// The short's trim levels are three and then five units of realised risk below the price the
    /// entry actually got, and there is no third.
    ///
    /// From the realised distance and not the plan's, because R is taken over the money the position
    /// can lose and the slippage moved that.
    /// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
    /// </summary>
    [Fact]
    public void The_short_trims_at_three_and_then_five_units_of_realised_risk_and_no_further()
    {
        Assert.Equal(84.60m, ShortExitRules.TrimLevel(entryPrice: 99.90m, giveUpPrice: 105m, trimsTaken: 0));
        Assert.Equal(74.40m, ShortExitRules.TrimLevel(entryPrice: 99.90m, giveUpPrice: 105m, trimsTaken: 1));
        Assert.Null(ShortExitRules.TrimLevel(entryPrice: 99.90m, giveUpPrice: 105m, trimsTaken: 2));
        Assert.Equal([3m, 5m], ShortExitRules.TrimAt);
    }

    /// <summary>The long's are the same multiples above its entry, which is the side they are his on.</summary>
    [Fact]
    public void The_long_trims_at_three_and_then_five_units_of_realised_risk_and_no_further()
    {
        Assert.Equal(115.40m, LongExitRules.TrimLevel(entryPrice: 100.10m, giveUpPrice: 95m, trimsTaken: 0));
        Assert.Equal(125.60m, LongExitRules.TrimLevel(entryPrice: 100.10m, giveUpPrice: 95m, trimsTaken: 1));
        Assert.Null(LongExitRules.TrimLevel(entryPrice: 100.10m, giveUpPrice: 95m, trimsTaken: 2));
        Assert.Equal([3m, 5m], LongExitRules.TrimAt);
    }

    /// <summary>A give-up point on the wrong side of either entry is refused rather than inverted.</summary>
    [Fact]
    public void A_give_up_point_on_the_wrong_side_of_the_entry_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortExitRules.TrimLevel(100m, 100m, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortExitRules.TrimLevel(100m, 95m, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongExitRules.TrimLevel(100m, 100m, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongExitRules.TrimLevel(100m, 105m, 0));
    }

    /// <summary>Each trim takes 15% of the planned share count, floored to whole shares, on both sides.</summary>
    [Fact]
    public void Each_trim_takes_fifteen_per_cent_of_the_planned_count_floored()
    {
        Assert.Equal(0.15m, LongExitRules.TrimFraction);
        Assert.Equal(LongExitRules.TrimFraction, ShortExitRules.TrimFraction);
        Assert.Equal(22, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 150));
        Assert.Equal(22, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 128));
        Assert.Equal(15, LongExitRules.TrimShares(plannedShares: 100, heldShares: 100));
    }

    /// <summary>
    /// A trim never takes everything that is held, because it reduces a position and the exit rules
    /// end one. Until 7.10 it was capped at what was held instead, which on a row RiskGate had cut far
    /// enough closed every share and left the row open with nothing in it.
    /// </summary>
    [Fact]
    public void A_trim_never_takes_everything_that_is_held()
    {
        Assert.Equal(0, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 22));
        Assert.Equal(0, LongExitRules.TrimShares(plannedShares: 150, heldShares: 10));
        Assert.Equal(0, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 0));
        Assert.Equal(22, ShortExitRules.TrimShares(plannedShares: 150, heldShares: 23));
    }

    /// <summary>A position too small for one whole share of trim is not trimmed at all.</summary>
    [Fact]
    public void A_position_below_seven_shares_yields_no_trim()
    {
        Assert.Equal(0, ShortExitRules.TrimShares(plannedShares: 6, heldShares: 6));
        Assert.Equal(1, ShortExitRules.TrimShares(plannedShares: 7, heldShares: 7));
    }

    // ---- the short's hold limit, from 7.10 ----------------------------------------------------

    /// <summary>
    /// A short held three sessions, the one it opened in counted, is closed at the next open; one held
    /// two is not. The upper of his "two to three days".
    /// </summary>
    [Fact]
    public void A_short_held_three_sessions_reaches_its_hold_limit_and_one_held_two_does_not()
    {
        Assert.Equal(3, ShortExitRules.HoldSessions);
        Assert.False(ShortExitRules.HoldLimitReached(2));
        Assert.True(ShortExitRules.HoldLimitReached(3));
        Assert.True(ShortExitRules.HoldLimitReached(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShortExitRules.HoldLimitReached(-1));
    }

    /// <summary>The two sides run the exit forms SOURCES.md traces, and the short side has no trail.</summary>
    [Fact]
    public void Each_side_runs_the_forms_the_trace_records_and_the_short_side_has_no_trail()
    {
        Assert.Equal([ExitReason.Trail, ExitReason.Trim], LongExitRules.Forms);
        Assert.Equal([ExitReason.Trim, ExitReason.HoldLimit], ShortExitRules.Forms);
        Assert.DoesNotContain(ExitReason.Trail, ShortExitRules.Forms);
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
        Assert.Equal(1, ExitReason.Rank(ExitReason.HoldLimit));
    }

    /// <summary>
    /// The rule-set exits share a rank because they can never contest each other: the trail is the
    /// long side's and the hold limit the short side's, and no position has both. The retired hourly
    /// reclaim keeps its rank so an arm made before 7.10 still resolves.
    /// </summary>
    [Fact]
    public void The_rule_set_exits_share_a_rank_because_no_position_has_both()
    {
        Assert.Equal(ExitReason.Rank(ExitReason.Trail), ExitReason.Rank(ExitReason.HoldLimit));
        Assert.Equal(ExitReason.Rank(ExitReason.Trail), ExitReason.Rank(ExitReason.Reclaim));
        Assert.Equal(4, ExitReason.ThatCloseAPosition.Count);
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
