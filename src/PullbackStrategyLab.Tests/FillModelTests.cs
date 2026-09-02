using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What a resting order gets, over every price relationship rather than over a session.
///
/// <b>Pure, so the arithmetic is asserted where it lives.</b> PaperBroker walks a session and writes
/// rows; every rule about what a fill costs is here, and a rule tested only through a stage would be
/// tested over whatever prices that stage's fixture happened to hold.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class FillModelTests
{
    private const double TenBasisPoints = 10d;

    // ---- the ordinary fill, which crosses the book -----------------------------------------

    /// <summary>
    /// The whole spread and not half of it, on both sides and at both ends.
    ///
    /// Four cases and not two, because the adverse side flips twice: a long buys high and sells low,
    /// a short sells low and buys high. Written out one by one rather than through a loop, so a sign
    /// error in one of the four cannot be hidden by the parameter that produced it.
    /// </summary>
    [Fact]
    public void An_ordinary_fill_pays_the_whole_spread_the_wrong_way_at_both_ends()
    {
        // Ten basis points of 100 is ten cents.
        Assert.Equal(0.10m, FillModel.MoneyFromBasisPoints(100m, TenBasisPoints));

        Fill longEntry = FillModel.Entry(SetupDirection.Long, 100m, null, TenBasisPoints);
        Fill longExit = FillModel.Exit(SetupDirection.Long, 95m, null, TenBasisPoints);
        Fill shortEntry = FillModel.Entry(SetupDirection.Short, 100m, null, TenBasisPoints);
        Fill shortExit = FillModel.Exit(SetupDirection.Short, 105m, null, TenBasisPoints);

        Assert.Equal(100.10m, longEntry.Price);
        Assert.Equal(94.905m, longExit.Price);
        Assert.Equal(99.90m, shortEntry.Price);
        Assert.Equal(105.105m, shortExit.Price);

        Assert.All(
            new[] { longEntry, longExit, shortEntry, shortExit },
            fill => Assert.Equal(FillModel.Slipped, fill.Basis));
    }

    /// <summary>
    /// A round trip costs two crossings, which is the whole of why exits are priced at all.
    ///
    /// Pricing one end and not the other flatters every R figure by half the round trip, in the
    /// direction that manufactures edge. Asserted as an inequality against the give-up distance so
    /// it fails if either end stops being charged.
    /// see: Exit slippage is charged on the same terms as entry slippage
    /// </summary>
    [Fact]
    public void A_round_trip_loses_more_than_the_distance_the_plan_named()
    {
        decimal trigger = 100m;
        decimal giveUp = 95m;

        Fill entry = FillModel.Entry(SetupDirection.Long, trigger, null, TenBasisPoints);
        Fill exit = FillModel.Exit(SetupDirection.Long, giveUp, null, TenBasisPoints);

        decimal planned = trigger - giveUp;
        decimal actual = entry.Price - exit.Price;

        Assert.True(actual > planned,
            $"A stop that lost {actual} against a planned {planned} was charged one crossing or none.");
        Assert.Equal(entry.Slippage + exit.Slippage, actual - planned);
    }

    /// <summary>A spread of nought is a free crossing and the model does not refuse it, because the reader does.</summary>
    [Fact]
    public void A_spread_of_nought_costs_nothing_and_is_still_a_slipped_fill()
    {
        Fill fill = FillModel.Entry(SetupDirection.Long, 100m, null, 0d);

        Assert.Equal(100m, fill.Price);
        Assert.Equal(0m, fill.Slippage);
        Assert.Equal(FillModel.Slipped, fill.Basis);
    }

    /// <summary>A negative width would pay the order to trade, so it is refused rather than charged.</summary>
    [Fact]
    public void A_negative_spread_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.MoneyFromBasisPoints(100m, -1d));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.Entry(SetupDirection.Long, 100m, null, double.NaN));
    }

    // ---- the gap, which is the adverse move itself -----------------------------------------

    /// <summary>
    /// A gap fills at the open and is not slipped on top, because the gap is the crossing.
    ///
    /// The loss is never clamped, so the fill is where the session actually opened. Taking the worse
    /// of the open and the give-up point would price an adverse move that did not occur.
    /// see: A minute that opens through a resting price fills at that open, whatever time of day it is
    /// </summary>
    [Fact]
    public void A_gap_fills_at_the_open_and_charges_nothing_on_top()
    {
        Fill exit = FillModel.Exit(SetupDirection.Long, 95m, openedThrough: 90m, TenBasisPoints);

        Assert.Equal(90m, exit.Price);
        Assert.Equal(0m, exit.Slippage);
        Assert.Equal(FillModel.Gapped, exit.Basis);

        // The quote is carried even though it was not charged, so the charge that was not made stays
        // legible on the row.
        Assert.Equal(TenBasisPoints, exit.SpreadBasisPoints);
    }

    /// <summary>
    /// A gap through the give-up point loses more than one R, and by the size of the gap.
    ///
    /// This is the done condition's first sentence as arithmetic: never clamped, and the price of
    /// the gap is stated rather than only its sign.
    /// </summary>
    [Fact]
    public void A_gap_through_the_give_up_point_loses_more_than_one_unit_of_risk()
    {
        Fill entry = FillModel.Entry(SetupDirection.Long, 100m, null, TenBasisPoints);
        Fill exit = FillModel.Exit(SetupDirection.Long, 95m, openedThrough: 88m, TenBasisPoints);

        decimal risk = entry.Price - 95m;
        decimal loss = exit.Price - entry.Price;

        Assert.Equal(-12.10m, loss);
        Assert.True(loss / risk < -1m,
            $"A gap that lost {loss} against a risk of {risk} was clamped back to one unit.");
    }

    /// <summary>
    /// The gap rule is applied to an entry as well as an exit, and that is the half the decision was
    /// not written about.
    ///
    /// Its argument is symmetric and this direction is the one that matters more. A long whose
    /// trigger sits at 100 in a session that opened at 105 did not buy at 100. Every other
    /// approximation in this model understates edge; filling that entry at the trigger would
    /// manufacture five points the lab never had.
    /// </summary>
    [Fact]
    public void An_entry_the_session_opened_through_fills_at_the_open()
    {
        Fill entry = FillModel.Entry(SetupDirection.Long, 100m, openedThrough: 105m, TenBasisPoints);

        Assert.Equal(105m, entry.Price);
        Assert.Equal(FillModel.Gapped, entry.Basis);

        Fill shortEntry = FillModel.Entry(SetupDirection.Short, 100m, openedThrough: 94m, TenBasisPoints);

        Assert.Equal(94m, shortEntry.Price);
        Assert.Equal(FillModel.Gapped, shortEntry.Basis);
    }

    /// <summary>
    /// An open on the favourable side is not a gap, and taking it as one would fill better than the
    /// resting order could have been hit at.
    ///
    /// The one error this model is not allowed to make, so it throws rather than choosing.
    /// </summary>
    [Fact]
    public void A_favourable_open_is_refused_rather_than_taken_as_a_gap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.Entry(SetupDirection.Long, 100m, openedThrough: 98m, TenBasisPoints));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.Exit(SetupDirection.Long, 95m, openedThrough: 99m, TenBasisPoints));
    }

    /// <summary>
    /// Which way hurts, in all four cases, read from the one predicate the stage asks.
    ///
    /// A long entry and a short exit both buy, so both are gapped by an open above; a long exit and
    /// a short entry both sell, so both are gapped by an open below. Three of these four could be
    /// right with the fourth a sign error, which is why the four are written out.
    /// </summary>
    [Fact]
    public void The_adverse_side_flips_twice_across_the_four_cases()
    {
        Assert.True(FillModel.OpenedThrough(SetupDirection.Long, isExit: false, 100m, 105m));
        Assert.False(FillModel.OpenedThrough(SetupDirection.Long, isExit: false, 100m, 95m));

        Assert.True(FillModel.OpenedThrough(SetupDirection.Long, isExit: true, 95m, 90m));
        Assert.False(FillModel.OpenedThrough(SetupDirection.Long, isExit: true, 95m, 99m));

        Assert.True(FillModel.OpenedThrough(SetupDirection.Short, isExit: false, 100m, 95m));
        Assert.False(FillModel.OpenedThrough(SetupDirection.Short, isExit: false, 100m, 105m));

        Assert.True(FillModel.OpenedThrough(SetupDirection.Short, isExit: true, 105m, 110m));
        Assert.False(FillModel.OpenedThrough(SetupDirection.Short, isExit: true, 105m, 101m));
    }

    /// <summary>An unknown direction is refused rather than being paid the spread it should be charged.</summary>
    [Fact]
    public void An_unknown_direction_is_refused_at_both_ends()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.Entry("sideways", 100m, null, TenBasisPoints));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.Exit("sideways", 100m, null, TenBasisPoints));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FillModel.OpenedThrough("sideways", isExit: false, 100m, 105m));
    }

    // ---- the third rule, which cannot fire until 4.8 ----------------------------------------

    /// <summary>
    /// A minute holding the give-up price and a profit-taking level gives up first.
    ///
    /// Written and asserted at 4.7 although nothing can call it: no profit-taking level exists until
    /// PositionManager arrives at 4.8, so no minute has two levels in it. The rule is here so 4.8
    /// adds a level rather than a rule, and because a bar carries no order between its own high and
    /// low, so the pessimistic reading is the one that is available.
    /// </summary>
    [Fact]
    public void A_minute_holding_both_levels_gives_up_first()
    {
        Assert.True(FillModel.GiveUpComesFirst);
    }

    // ---- the give-up touch, which is the entry rule inverted --------------------------------

    /// <summary>
    /// A long is stopped by a low reaching down and a short by a high reaching up, with no margin
    /// either way.
    ///
    /// Written through the entry predicate with the direction inverted, so the two can never
    /// disagree about a bar that touches a price exactly.
    /// </summary>
    [Fact]
    public void The_give_up_touch_is_the_trigger_touch_with_the_direction_inverted()
    {
        Assert.True(TriggerTouch.GaveUp(SetupDirection.Long, 95m, high: 101m, low: 95m));
        Assert.False(TriggerTouch.GaveUp(SetupDirection.Long, 95m, high: 101m, low: 95.01m));

        Assert.True(TriggerTouch.GaveUp(SetupDirection.Short, 105m, high: 105m, low: 99m));
        Assert.False(TriggerTouch.GaveUp(SetupDirection.Short, 105m, high: 104.99m, low: 99m));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerTouch.GaveUp("sideways", 95m, 101m, 95m));
    }

    // ---- which quote a fill is charged ------------------------------------------------------

    /// <summary>
    /// The widest usable sample of the session, whatever time the fill happened.
    ///
    /// Pessimism on purpose, and it removes the within-day question entirely: a fill at 09:31
    /// charged the 10:15 quote would be priced from a book the morning had not reached.
    /// see: A fill is charged the widest usable quote of its session, not the nearest one
    /// </summary>
    [Fact]
    public void A_fill_is_charged_the_widest_usable_quote_of_its_session()
    {
        QuotedSpread? charged = SpreadCharge.Widest([
            new QuotedSpread("after_open", 4.0d, 900, 32),
            new QuotedSpread("before_close", 11.5d, 880, 3),
        ]);

        Assert.Equal("before_close", charged!.Pass);
        Assert.Equal(11.5d, charged.BasisPoints);
        Assert.Equal(3, charged.StraddleSeconds);
    }

    /// <summary>
    /// A tie is broken by pass name, so two samples of equal width choose the same one on every
    /// machine, on the same grounds a tie in trigger time is broken by ticker.
    /// </summary>
    [Fact]
    public void A_tie_between_two_quotes_is_broken_by_the_pass_name()
    {
        QuotedSpread? charged = SpreadCharge.Widest([
            new QuotedSpread("before_close", 7d, null, null),
            new QuotedSpread("after_open", 7d, null, null),
        ]);

        Assert.Equal("after_open", charged!.Pass);
    }

    /// <summary>
    /// A name with no usable quote answers with nothing rather than with nought.
    ///
    /// A spread of nought is a free entry that clears every threshold written as a maximum, so the
    /// two are told apart here and the caller refuses the fill.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void A_name_the_session_quoted_no_book_for_answers_with_nothing()
    {
        Assert.Null(SpreadCharge.Widest([]));
    }
}
