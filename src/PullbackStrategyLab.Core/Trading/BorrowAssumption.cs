namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The two things a short position in this lab assumes and does not know.
///
/// <b>Recorded on the position rather than applied to it.</b> The rate is charged per calendar day
/// held and the money is TradeJournal's at 4.9, which is the component that closes a trade and
/// states its result. What 4.7 owes is that the assumption is on the row: ARCHITECTURE has said
/// since the failure table was written that both are recorded as unmodelled assumptions on every
/// short trade from this checkpoint, and a claim that something is recorded on every row is a claim
/// about a surface, which is the sixth failure shape this corpus catalogues.
///
/// <b>The cost is not what decides the short side and the availability is.</b> At 1.0% a year a
/// four-day hold costs about 0.011% of position value, which against a 3% stop is roughly 0.4% of
/// one R. The rate is set several times higher than a general-collateral borrow actually costs and
/// it still rounds to nothing. What is not modelled is whether the shares could have been borrowed
/// at all, which the price feed does not carry: <c>tradable-shortable</c> stands in for it with a
/// market-capitalisation floor, and a short that was impossible to place is recorded by this lab as
/// though it filled cleanly.
///
/// <b>Both are stamped on the row rather than looked up later.</b> A rate held only as a constant
/// would restate every historical short at whatever the constant says today, which is the same fault
/// <c>trade_plan</c> stores <c>equity</c> and <c>risk_fraction</c> to avoid.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class BorrowAssumption
{
    /// <summary>
    /// The flat annualised borrow rate deducted per calendar day a short is held.
    ///
    /// Authored, and deliberately high. It is stated in ARCHITECTURE twice, in the short-checks
    /// prose and in the authored-parameters table, and <c>pinned-constants</c> reads both against
    /// this one number.
    /// </summary>
    public const decimal AnnualisedRate = 0.010m;

    /// <summary>
    /// What the check standing in for borrow availability was: a market-capitalisation floor, and
    /// not an observation of whether anybody would lend the shares.
    ///
    /// Stored as the reason string on every short position, so a person reading one row learns the
    /// assumption without being told to go and read the failure table.
    /// </summary>
    public const string AvailabilityIsNotModelled =
        "borrow availability is not in the price feed: the market-capitalisation floor of "
        + "tradable-shortable stands in for it, so a short nobody would have lent is recorded here as "
        + "though it filled";
}
