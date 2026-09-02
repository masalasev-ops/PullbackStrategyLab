using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// What a resting order actually gets, at both ends of a trade.
///
/// <b>Pure, on the footing <see cref="TriggerTouch"/> and <see cref="RiskLimits"/> already set.</b>
/// Nothing here reads a store or a clock, so every price relationship is assertable rather than only
/// the ones a session happened to produce. PaperBroker is the component that writes a fill; this
/// decides what price it says.
///
/// <b>Pessimistic on purpose, and the direction is the point.</b> Being too pessimistic understates
/// edge, which is the safe direction for a lab whose question is whether edge exists at all. Every
/// rule below is chosen that way, and the one place the model is optimistic is named rather than
/// left to be found: the touch rule fills on a touch. The second such place closed at 4.8, when the
/// gap rule stopped naming the session's first regular minute and started reading the bar.
///
/// <b>Three rules and a fourth that cannot fire yet.</b>
///
/// <list type="number">
/// <item><b>An ordinary fill crosses the book and is charged the whole captured spread</b>, the wrong
/// way, at both ends and on both sides (see: Entry slippage is the whole captured spread, symmetric
/// between the directions) (see: Exit slippage is charged on the same terms as entry slippage).</item>
/// <item><b>A price a minute opened through fills at that open and is not slipped again</b>,
/// because the gap is the adverse move and a spread charged over it charges twice for one crossing.
/// Any minute, not the session's first alone: the argument is that a resting order cannot be hit at
/// a price that did not exist, and that says nothing about the time of day
/// (see: A minute that opens through a resting price fills at that open, whatever time of day it
/// is).</item>
/// <item><b>A minute holding both the give-up price and a profit-taking level gives up first.</b>
/// <see cref="GiveUpComesFirst"/> is that rule and it returns a constant, because there is no reading
/// of a minute bar that says which of two prices inside it traded first. It was written and asserted
/// here from 4.7 with nothing to order, and 4.8 gave it a subject: the short trim at 3R is the
/// profit-taking level, and a bar holding both it and an exit trigger takes the exit. The wider
/// ordering across two exit rules is <see cref="ExitReason.First"/>, which is a different question
/// and lives with the reasons rather than with the prices.</item>
/// </list>
///
/// <b>What the spread is a fraction of, which is 4.7's own question.</b> <c>spread_bps</c> is basis
/// points of the mid of a quote whose two sides the vendor stamped separately: on the capture of
/// 2026-09-01 AAPL's bid and ask were 32 seconds apart. So the figure charged need not be a spread
/// that existed at any instant, and on a name whose book moved between the stamps it can be wider or
/// narrower than anything a trader could have crossed. It is charged anyway and the straddle is
/// recorded on the fill, on exactly the terms the capture already took for the vendor's delay: a
/// threshold refusing a straddled quote would be a number authored from one measurement of one name
/// (see: A straddled quote is charged and the straddle is recorded, never widened or refused)
/// (see: A delayed quote records its own lag rather than being corrected for it).
/// </summary>
public static class FillModel
{
    /// <summary>The fill crossed the book and paid the captured spread.</summary>
    public const string Slipped = "slipped";

    /// <summary>The session opened through the price, so the open is the fill and nothing is added.</summary>
    public const string Gapped = "gapped";

    /// <summary>
    /// Whether a minute holding both the give-up price and a profit-taking level is read as giving
    /// up first.
    ///
    /// Always true, and it is a named rule rather than an inline constant because it is the one
    /// place the model resolves an ambiguity a minute bar cannot settle. A bar carries a high and a
    /// low and no order between them, so either reading is available and the pessimistic one is
    /// taken. Nothing calls this before 4.8: no profit-taking level exists, so no minute has two
    /// levels in it.
    /// </summary>
    public static bool GiveUpComesFirst => true;

    /// <summary>
    /// The money a spread of <paramref name="spreadBasisPoints"/> costs on one share at
    /// <paramref name="price"/>.
    ///
    /// <b>The one place this file crosses between the two worlds, and it is named for it.</b> Prices
    /// are decimal and statistics are double, and <c>spread_bps</c> is a statistic: it is stored REAL
    /// because it is basis points of a mid rather than money. The conversion is explicit and happens
    /// once, so nothing downstream multiplies a price by a double by accident.
    ///
    /// The whole spread and not half of it. The trigger is a traded price and a resting order
    /// entering on it crosses the book, so half a spread would price the fill at a midpoint the order
    /// did not get.
    /// </summary>
    public static decimal MoneyFromBasisPoints(decimal price, double spreadBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(price);

        if (spreadBasisPoints < 0d || double.IsNaN(spreadBasisPoints))
        {
            throw new ArgumentOutOfRangeException(
                nameof(spreadBasisPoints),
                $"A spread of {spreadBasisPoints} basis points is not a width. A crossed or locked book is "
                + "stored with no spread at all rather than with a negative one, so a figure below zero "
                + "here is an arithmetic fault upstream and charging it would pay the order to trade.");
        }

        return price * (decimal)spreadBasisPoints / 10_000m;
    }

    /// <summary>
    /// What an entry gets, given the minute the trigger was reached in.
    ///
    /// <paramref name="openedThrough"/> is that minute's open where it is already past the trigger,
    /// and null otherwise. The caller decides that, because it is reading the bar it is standing on.
    ///
    /// <b>The gap rule is applied to an entry and the decision was written about an exit.</b> Its
    /// argument is symmetric and this is the direction that matters more: a long whose trigger sits
    /// at 100 in a minute that opened at 105 did not buy at 100, and filling it there would hand the
    /// lab five points it never had. Every other approximation in this model understates edge; that
    /// one would manufacture it, which is the only kind this lab cannot afford
    /// (see: A minute that opens through a resting price fills at that open, whatever time of day it
    /// is).
    /// </summary>
    public static Fill Entry(
        string direction, decimal triggerPrice, decimal? openedThrough, double spreadBasisPoints) =>
        At(direction, triggerPrice, openedThrough, spreadBasisPoints, isExit: false);

    /// <summary>
    /// What an exit gets, given the price the exit rule named and the session's opening gap if there
    /// was one.
    ///
    /// The same rules mirrored: a long exits by selling, so the adverse side is the low side, and an
    /// exit gapped through fills at the open unslipped. Trail exits and give-up exits are treated
    /// alike, because nothing in this corpus distinguishes the book one crosses on the way out by
    /// which rule sent the order (see: Exit slippage is charged on the same terms as entry slippage).
    /// </summary>
    public static Fill Exit(
        string direction, decimal exitPrice, decimal? openedThrough, double spreadBasisPoints) =>
        At(direction, exitPrice, openedThrough, spreadBasisPoints, isExit: true);

    /// <summary>
    /// Whether <paramref name="open"/> is already past <paramref name="restingPrice"/> in the
    /// direction that hurts, for an order of <paramref name="direction"/> at the end named by
    /// <paramref name="isExit"/>.
    ///
    /// Four cases and not two, because the adverse side flips twice: a long buys high and sells low,
    /// a short sells low and buys high. Written once here so the four are in one place rather than
    /// spread across the two callers, where three would be right and the fourth would be a sign.
    /// </summary>
    public static bool OpenedThrough(string direction, bool isExit, decimal restingPrice, decimal open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        return AdverseIsUpward(direction, isExit) ? open > restingPrice : open < restingPrice;
    }

    private static Fill At(
        string direction,
        decimal restingPrice,
        decimal? openedThrough,
        double spreadBasisPoints,
        bool isExit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(restingPrice);

        bool adverseIsUpward = AdverseIsUpward(direction, isExit);

        if (openedThrough is decimal open)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(open);

            // Refused rather than silently taken as an ordinary fill. An open on the favourable side
            // is not a gap, and reading it as one would price a fill better than the resting order
            // could have got, which is the one error this model is not allowed to make.
            if (adverseIsUpward ? open <= restingPrice : open >= restingPrice)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(openedThrough),
                    $"An open of {open} is not through a resting price of {restingPrice} for a "
                    + $"{direction} order, so this is not a gap. A gap fill is the open taken instead of "
                    + "the price the order named, and taking it on a favourable open would fill better "
                    + "than the order could have been hit at.");
            }

            return new Fill(open, Gapped, 0m, spreadBasisPoints);
        }

        decimal slippage = MoneyFromBasisPoints(restingPrice, spreadBasisPoints);
        decimal price = adverseIsUpward ? restingPrice + slippage : restingPrice - slippage;

        return new Fill(price, Slipped, slippage, spreadBasisPoints);
    }

    /// <summary>
    /// Which way hurts. A long entry and a short exit both buy, so both hurt upward; a long exit and
    /// a short entry both sell, so both hurt downward.
    /// </summary>
    private static bool AdverseIsUpward(string direction, bool isExit) => direction switch
    {
        SetupDirection.Long => !isExit,
        SetupDirection.Short => isExit,
        _ => throw new ArgumentOutOfRangeException(
            nameof(direction),
            $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}'. The two sides "
            + "charge slippage in opposite directions, so an unknown direction would be paid the spread "
            + "rather than charged it."),
    };
}

/// <summary>
/// One end of one trade, with what it cost and what that cost was computed from.
///
/// <see cref="Basis"/> is <see cref="FillModel.Slipped"/> or <see cref="FillModel.Gapped"/>, so a
/// night's fills group by how they were priced without parsing a price. <see cref="Slippage"/> is
/// nought on a gap, which is the rule rather than a missing figure, and
/// <see cref="SpreadBasisPoints"/> is carried on both so the charge that was not made is still
/// legible.
/// </summary>
public sealed record Fill(decimal Price, string Basis, decimal Slippage, double SpreadBasisPoints);
