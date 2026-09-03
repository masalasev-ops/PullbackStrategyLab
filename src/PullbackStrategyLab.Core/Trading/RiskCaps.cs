namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The six limits, held once and applied by one component.
///
/// <b>Stated in two tables of ARCHITECTURE and pinned nowhere until 4.6.</b> "The limits" states them
/// in plain terms and the authored-parameters table states them again with their family, and the code
/// held only the two that <see cref="PositionSizing"/> needs. A limit written in a document and
/// nowhere else is a limit nothing enforces, and the two tables could have disagreed with each other
/// with nothing reading both. `pinned-constants` now reads both against these.
///
/// <b>What each cap can do is a property of the cap, and the six are not six of a kind.</b> The row
/// at 4.6 says three count caps that can only block and two proportional caps that could do either,
/// which is five of six and one count cap more than the tables hold. Reconciled here, because a
/// miscount in a done condition becomes a component with a cap nobody wrote:
///
/// <list type="bullet">
/// <item><b>Two count caps</b>, <see cref="MaxOpenPositions"/> and <see cref="MaxOpenShortPositions"/>.
/// A count cap can only block. There is no fraction of a slot.</item>
/// <item><b>Two proportional caps</b>, <see cref="MaxPositionFraction"/> and
/// <see cref="MaxTotalRiskFraction"/>. Both reduce to fit, and block only where the fit is under one
/// share, which is the same floor PlanBuilder refuses on.</item>
/// <item><b>Risk per trade</b> is <see cref="PositionSizing.RiskPerTrade"/>, and it is not a cap this
/// component applies: it is the quantity the plan was sized from at 18:30. RiskGate asserts it rather
/// than enforcing it, because a plan risking more than the budget it names is a defect in the plan
/// and not an order to be trimmed.</item>
/// <item><b>The give-up distance cap</b> is <see cref="GiveUpDistanceRanges"/>, and it is a gate at
/// detection rather than a limit at trigger. `exit-tight` refuses a setup whose give-up point is
/// further than this, so a plan that reached a trigger cleared it hours earlier and re-applying it
/// here would be a second implementation of a gate, disagreeing with the first on a day the daily
/// range was restated. It is held here so the two places that state it are pinned against one
/// number, and it is deliberately not read by RiskGate.</item>
/// </list>
///
/// see: Equity is a fixed $100,000 notional that never compounds
/// </summary>
public static class RiskCaps
{
    /// <summary>
    /// How many positions may be open at once, either direction.
    ///
    /// A count cap, so it blocks. Triggers cluster, because they are driven by the same market
    /// conditions, and the fifth one of a morning is the one this refuses.
    /// </summary>
    public const int MaxOpenPositions = 4;

    /// <summary>
    /// How many of those may be short.
    ///
    /// Tighter than the whole because a short loss is unbounded in principle. It is a bound inside
    /// <see cref="MaxOpenPositions"/> rather than beside it: two shorts and three longs is five
    /// positions and is refused by the first cap, not by this one.
    /// </summary>
    public const int MaxOpenShortPositions = 2;

    /// <summary>
    /// The most of the account one position may be, at the price it is entered at.
    ///
    /// A proportional cap, so it reduces. It binds only when the give-up point is unusually close,
    /// which is exactly when the risk budget would otherwise buy an alarming number of shares.
    /// </summary>
    public const decimal MaxPositionFraction = 0.35m;

    /// <summary>
    /// The most of the account that may be at risk across every open position at once.
    ///
    /// A proportional cap, so it reduces: an order that does not fit the remaining budget is placed
    /// at the size that does. Four positions each risking the full per-trade budget is 3% exactly,
    /// so this binds only where a reduction elsewhere has not already made room.
    /// </summary>
    public const decimal MaxTotalRiskFraction = 0.03m;

    /// <summary>
    /// The furthest the give-up point may sit from the trigger, in that stock's own daily ranges.
    ///
    /// <b>Held here and applied at detection.</b> `exit-tight` is the gate that refuses a wider one,
    /// and it runs on the evening the setup is flagged. This constant exists so the two tables
    /// stating it are pinned against the same number as
    /// <see cref="Detection.LongPullbackRules.GiveUpRanges"/>, which is what the detectors read.
    /// </summary>
    public const decimal GiveUpDistanceRanges = 0.5m;

    /// <summary>The money one position may be worth, being the account at <see cref="MaxPositionFraction"/>.</summary>
    public static decimal MaxPositionValue => PositionSizing.NotionalEquity * MaxPositionFraction;

    /// <summary>The money that may be at risk at once, being the account at <see cref="MaxTotalRiskFraction"/>.</summary>
    public static decimal MaxTotalRisk => PositionSizing.NotionalEquity * MaxTotalRiskFraction;

    /// <summary>
    /// How many simulated accounts a version trades, which is one, both directions in it.
    ///
    /// <b>It is a cap's scope rather than a cap, and that is why it lives here.</b> Every limit
    /// above is counted within one account: the open-position count, the short count and the total
    /// risk are all questions about a book, and a book belongs to a version. Two versions holding
    /// the same name at once are two positions in two accounts, each capped by this same code and
    /// neither aware of the other, which is the only reading under which a difference series
    /// measures the rule rather than the contention between rules.
    ///
    /// <b>Both directions share the account, and that is the point of it.</b> The caps only mean
    /// anything if a short and a long compete for the same budget. Reporting stays separate by
    /// direction, which is a different question and is answered by never pooling the two figures.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    public const int AccountsPerVersion = 1;
}
