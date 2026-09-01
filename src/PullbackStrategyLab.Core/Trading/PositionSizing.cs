namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// How many shares a plan is written for, and why the answer belongs to the plan rather than to the
/// component that later places the order.
///
/// Pure and in Core for the reason <see cref="Detection.NightlyCap"/> is: what it decides is
/// arithmetic over three numbers, and the stage around it is a read and an insert. That makes the
/// rounding assertable over every distance rather than over the ones a fixture happened to produce.
///
/// <b>PlanBuilder sizes and the plan's size is authoritative.</b> RiskGate may reduce it or block it
/// at trigger and never recomputes it.
/// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
///
/// <b>The give-up distance is a price, not a ratio.</b> `setup.stop_distance_ranges` is the distance
/// expressed in daily ranges, which is what `exit-tight` and the cap rank on; dividing a risk budget
/// by it would give a share count in the wrong unit and the result would look like a number. The
/// distance here is the money one share loses if the give-up point is reached.
/// </summary>
public static class PositionSizing
{
    /// <summary>
    /// The notional account every version sizes against, fixed and never compounding.
    ///
    /// Fixed because two versions that compounded would size differently after their first
    /// disagreement and stop being paired, which is the whole instrument.
    /// see: Equity is a fixed $100,000 notional that never compounds
    /// </summary>
    public const decimal NotionalEquity = 100_000m;

    /// <summary>
    /// The fraction of equity one trade is allowed to lose at its give-up point.
    ///
    /// The midpoint of the 0.5 to 1% range the strategy is described with.
    /// </summary>
    public const decimal RiskPerTrade = 0.0075m;

    /// <summary>The money a single trade may lose, being <see cref="NotionalEquity"/> at <see cref="RiskPerTrade"/>.</summary>
    public const decimal RiskBudget = NotionalEquity * RiskPerTrade;

    /// <summary>
    /// The give-up distance in money for one share, or null where the geometry cannot express one.
    ///
    /// <b>Null and nought are different answers and this returns null for both shapes that are not
    /// a distance.</b> An absent price is the shape migration 031 made expressible; an equal pair is
    /// the shape that survived it, where the thrust has not pulled back yet so the entry level and
    /// the give-up point are the same price and two of the four columns still state a number. Both
    /// are a setup with no trade geometry, and neither is a distance of nought.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public static decimal? GiveUpDistanceOf(decimal? triggerPrice, decimal? giveUpPrice)
    {
        if (triggerPrice is not decimal trigger || giveUpPrice is not decimal giveUp)
        {
            return null;
        }

        decimal distance = Math.Abs(trigger - giveUp);

        return distance == 0m ? null : distance;
    }

    /// <summary>
    /// The share count for a give-up distance, rounded down, or nought where the budget cannot buy
    /// a single share.
    ///
    /// <b>Down rather than to nearest, and the direction is the whole point.</b> Rounding up would
    /// put more than the risk budget at stake on a trade whose whole purpose is to risk exactly
    /// that, and it would do it on the widest stops, which are the trades least able to carry it.
    /// The lost fraction is visible rather than assumed away: the plan records the budget it was
    /// sized from beside the risk the rounded count actually puts at stake, which is the shape
    /// `position` already sets with `risk_intended` beside `risk_realised`.
    ///
    /// <b>Nought is a refusal, not a size.</b> A distance wider than the whole risk budget cannot be
    /// traded at this equity, and the caller writes no plan rather than a plan for no shares.
    /// </summary>
    public static int SharesFor(decimal giveUpDistance, decimal riskBudget = RiskBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpDistance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(riskBudget);

        decimal shares = Math.Floor(riskBudget / giveUpDistance);

        return shares > int.MaxValue ? int.MaxValue : (int)shares;
    }

    /// <summary>
    /// What a rounded share count actually puts at stake, which is at or below the budget it was
    /// sized from and never above it.
    /// </summary>
    public static decimal RiskAtStake(int shares, decimal giveUpDistance) => shares * giveUpDistance;
}
