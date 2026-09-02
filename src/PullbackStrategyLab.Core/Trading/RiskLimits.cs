using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// What the caps do to one order, given what is already open.
///
/// <b>Pure, and separate from the component that writes the row.</b> The arithmetic is assertable
/// over every arrangement of the book rather than over the ones a session happened to produce, on the
/// footing <see cref="NightlyCap"/> and <see cref="PositionSizing"/> already set. RiskGate is still
/// the only thing that may write an order; this decides what the row says.
/// see: RiskGate is the sole writer of orders, for both directions and every version
///
/// <b>It reduces or blocks and it never recomputes a size.</b> The plan's share count arrives here
/// and leaves here either unchanged, smaller, or refused. Nothing in this file divides a risk budget
/// by a give-up distance: that happened at 18:30 and doing it again would make `plan_audit` compare
/// two runs of one formula rather than an intention against an outcome.
/// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
///
/// <b>A reduction keeps the plan's give-up price</b>, which is why nothing here returns one. R for a
/// trade is the distance the plan named, whatever size the caps allowed, and a trade that risked less
/// than planned is a trade that risked less than planned.
/// </summary>
public static class RiskLimits
{
    /// <summary>Four positions are open, so a fifth cannot be.</summary>
    public const string OpenPositions = "open-positions";

    /// <summary>Two shorts are open, so a third cannot be.</summary>
    public const string OpenShorts = "open-shorts";

    /// <summary>The position would be more of the account than one position may be.</summary>
    public const string PositionSize = "position-size";

    /// <summary>The account's total risk at stake would exceed what may be at risk at once.</summary>
    public const string TotalRisk = "total-risk";

    /// <summary>Every cap by name, in the order they are applied.</summary>
    public static IReadOnlyList<string> All { get; } = [OpenPositions, OpenShorts, PositionSize, TotalRisk];

    /// <summary>
    /// Apply every cap to one order, in the order that lets a count cap refuse before a proportional
    /// one does arithmetic on a slot that does not exist.
    ///
    /// <paramref name="triggerPrice"/> is what the position is valued at for
    /// <see cref="RiskCaps.MaxPositionFraction"/>. <b>It is the plan's trigger and not the fill</b>,
    /// because the fill is PaperBroker's and happens after this: an entry costs the whole captured
    /// spread the wrong way, so a position valued here at the cap can be worth a spread more once
    /// filled. That is a stated approximation rather than an oversight, and the alternative is a cap
    /// applied by the component that may not open a position.
    /// see: Entry slippage is the whole captured spread, symmetric between the directions
    /// </summary>
    public static RiskVerdict Apply(
        string direction, int plannedShares, decimal triggerPrice, decimal giveUpDistance, OpenBook book)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(plannedShares);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(triggerPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpDistance);

        if (direction is not (SetupDirection.Long or SetupDirection.Short))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}'. The short cap "
                + "is tighter than the whole, so an unknown direction would be granted the wrong one of two "
                + "limits rather than refused.");
        }

        // 1. The count caps, which can only block. There is no fraction of a slot, and doing the
        //    proportional arithmetic first would compute a size for an order that has nowhere to go.
        if (book.Positions >= RiskCaps.MaxOpenPositions)
        {
            return RiskVerdict.Blocked(OpenPositions,
                $"{book.Positions} position(s) are already open and at most {RiskCaps.MaxOpenPositions} may be");
        }

        if (direction == SetupDirection.Short && book.Shorts >= RiskCaps.MaxOpenShortPositions)
        {
            return RiskVerdict.Blocked(OpenShorts,
                $"{book.Shorts} short position(s) are already open and at most "
                + $"{RiskCaps.MaxOpenShortPositions} of the {RiskCaps.MaxOpenPositions} may be short");
        }

        // 2. The proportional caps, which reduce. Each is asked for the largest count it allows and
        //    the smallest answer wins, so a size that satisfies one and not the other cannot survive
        //    by being applied in a convenient order.
        int shares = plannedShares;
        string? boundBy = null;

        int byPositionSize = (int)Math.Floor(RiskCaps.MaxPositionValue / triggerPrice);
        if (byPositionSize < shares)
        {
            shares = byPositionSize;
            boundBy = PositionSize;
        }

        decimal roomLeft = RiskCaps.MaxTotalRisk - book.RiskAtStake;
        int byTotalRisk = roomLeft <= 0m ? 0 : (int)Math.Floor(roomLeft / giveUpDistance);
        if (byTotalRisk < shares)
        {
            shares = byTotalRisk;
            boundBy = TotalRisk;
        }

        // 3. The floor a reduction can fall through, which is the same floor PlanBuilder refuses on.
        //    The cap that took it there is named, because "blocked" with no cap is a refusal nobody
        //    can act on and this is the one path where a proportional cap ends in a block.
        if (shares < 1)
        {
            return RiskVerdict.Blocked(boundBy ?? TotalRisk,
                boundBy == PositionSize
                    ? $"one share at {triggerPrice} is more than the {RiskCaps.MaxPositionFraction:P0} "
                      + "of the account one position may be"
                    : $"{roomLeft} of risk is left and one share would put {giveUpDistance} at stake");
        }

        return shares == plannedShares
            ? RiskVerdict.Placed(shares, shares * giveUpDistance, boundBy: null, reduced: false)
            : RiskVerdict.Placed(shares, shares * giveUpDistance, boundBy, reduced: true);
    }
}

/// <summary>
/// What is open at the moment an order is decided.
///
/// <b>A value rather than a read, because the caps bind within a session as it is walked.</b> The
/// resolver hands triggers to RiskGate in time order and each placed order changes what the next one
/// faces, which is what the contention rule is: the earliest trigger fills and the later ones meet a
/// fuller book.
/// see: Plans are resting orders and fills go in time order when the caps bind
/// </summary>
public sealed record OpenBook(int Positions, int Shorts, decimal RiskAtStake)
{
    /// <summary>An account holding nothing, which is where every session in this lab currently starts.</summary>
    public static OpenBook Empty { get; } = new(0, 0, 0m);

    /// <summary>The book after an order of <paramref name="shares"/> in <paramref name="direction"/> is placed.</summary>
    public OpenBook With(string direction, decimal riskAtStake) => this with
    {
        Positions = Positions + 1,
        Shorts = Shorts + (direction == SetupDirection.Short ? 1 : 0),
        RiskAtStake = RiskAtStake + riskAtStake,
    };
}

/// <summary>
/// What the caps decided about one order.
///
/// <see cref="BoundBy"/> is the cap that changed the answer and is null where nothing bound, so a
/// placed order at the planned size is distinguishable from one that happens to equal a cap.
/// </summary>
public sealed record RiskVerdict(
    bool IsPlaced,
    int Shares,
    decimal RiskAtStake,
    bool Reduced,
    string? BoundBy,
    string? Because)
{
    public static RiskVerdict Placed(int shares, decimal riskAtStake, string? boundBy, bool reduced) =>
        new(true, shares, riskAtStake, reduced, boundBy, null);

    public static RiskVerdict Blocked(string boundBy, string because) =>
        new(false, 0, 0m, false, boundBy, because);
}
