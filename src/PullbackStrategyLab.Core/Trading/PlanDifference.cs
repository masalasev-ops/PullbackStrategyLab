using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The difference between a price an instruction named and the price it got, signed so that worse is
/// always worse.
///
/// <b>Computed from the two prices rather than copied from the fill.</b> <c>fill.slippage</c> is what
/// the model charged, and an audit that read it would be comparing a number against itself. This
/// takes the resting price and the executed price off the row and derives the gap, so a model that
/// stops charging what it says it charges is visible here rather than agreeing with itself.
///
/// <b>Signed by direction and by end, on the four cases <see cref="FillModel"/> already names.</b> A
/// long entry and a short exit both buy, so paying more is worse; a long exit and a short entry both
/// sell, so getting less is worse. A positive difference is always the trade being worse off, which
/// is what lets one column be read across both sides without being pooled into one figure.
/// see: Long and short are never pooled into one figure
///
/// <b>Basis points beside the money, because a difference in dollars is not comparable across
/// names.</b> Six cents on a six-dollar stock and six cents on a four-hundred-dollar stock are two
/// different execution facts, and the journal's plan-against-actual column is stated in basis points
/// for exactly that reason.
/// </summary>
public static class PlanDifference
{
    /// <summary>
    /// What one end of a trade cost against the price its instruction named, in money per share.
    ///
    /// Positive is worse for the position. Negative is possible and is not an error: a gap can open
    /// past a price in the position's favour on a leg the model does not treat as a gap, and an
    /// audit that could not express that would be a check whose only reading is the one it expects.
    /// </summary>
    public static decimal PerShare(string direction, bool isExit, decimal restingPrice, decimal executedPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(restingPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(executedPrice);

        return AdverseIsUpward(direction, isExit)
            ? executedPrice - restingPrice
            : restingPrice - executedPrice;
    }

    /// <summary>
    /// The same difference as a fraction of the price that was named, in basis points.
    ///
    /// A statistic and not money, so it crosses to double here and is named for the crossing, on the
    /// footing <see cref="FillModel.MoneyFromBasisPoints"/> already sets going the other way.
    /// </summary>
    public static double BasisPoints(decimal perShare, decimal restingPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(restingPrice);

        return (double)(perShare / restingPrice * 10_000m);
    }

    /// <summary>
    /// Which way hurts, which is the same four cases the fill model charges slippage over. Written
    /// out again rather than shared, because the model's copy is private and a difference in sign
    /// between the two is precisely what this component exists to be able to show.
    /// </summary>
    private static bool AdverseIsUpward(string direction, bool isExit) => direction switch
    {
        SetupDirection.Long => !isExit,
        SetupDirection.Short => isExit,
        _ => throw new ArgumentOutOfRangeException(
            nameof(direction),
            $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}'. The two sides "
            + "differ in opposite directions, so an unknown one would report a worse fill as a better one."),
    };
}
