using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Vendor;

/// <summary>
/// The market data vendor, as the stages see it. Every method takes the day's budget, so a
/// stage cannot make a request the ceiling does not know about.
///
/// The endpoint mix is what the call budget is built on, and it is not free to change: going
/// deep is free on the per-ticker endpoint and ruinous on the bulk one, which is the whole
/// reason the backfill screens on cheap bulk data before fetching history for survivors.
/// see: The vendor is EODHD, and the endpoint mix is what the call budget is built on
/// </summary>
public interface IMarketDataVendor
{
    /// <summary>
    /// Every instrument listed on an exchange, with the type field the universe filter reads.
    /// One request for the whole exchange.
    /// </summary>
    Task<VendorResult<IReadOnlyList<VendorSymbol>>> GetExchangeSymbolListAsync(
        string exchange,
        ICallBudget budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole market's closing prices for one date, in one request. Priced per market day,
    /// so twenty sessions costs twenty times one day and six hundred would be ruinous.
    ///
    /// A date the exchange did not trade returns an empty list rather than an error, and it
    /// still costs what a trading day costs.
    /// </summary>
    Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetBulkEndOfDayAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every split effective on one date, whole market, one request. Daily and non-negotiable:
    /// a split nobody recorded corrupts every average that stock has, all at once and silently.
    /// </summary>
    Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkSplitsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every dividend effective on one date, whole market, one request. Run weekly rather than
    /// nightly, because nothing downstream turns on it yet.
    /// </summary>
    Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkDividendsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One ticker's daily history over a window, in one request. Priced per ticker regardless of
    /// depth, which is the other half of the endpoint split: ten years costs what one year costs
    /// here, and one extra session costs a hundred on the bulk endpoint.
    ///
    /// Every bar comes back adjusted as the vendor adjusts it today, which is exactly what makes
    /// this the way a corporate action is honoured: the whole series arrives on one basis.
    /// </summary>
    Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetDailyHistoryAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        ICallBudget budget,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What came back, or why nothing did. A request the budget could not cover is not an error:
/// it is the stage being told to stop and complete as partial, which is the designed behaviour
/// at the ceiling rather than a failure to handle.
/// </summary>
public sealed record VendorResult<T>(T? Value, bool BudgetExhausted)
{
    public static VendorResult<T> Delivered(T value) => new(value, false);

    public static VendorResult<T> OutOfBudget() => new(default, true);

    public T Require() => BudgetExhausted || Value is null
        ? throw new InvalidOperationException("The request was not delivered, so there is nothing to read.")
        : Value;
}

/// <summary>One row of the exchange symbol list.</summary>
public sealed record VendorSymbol(string Ticker, string Name, string Exchange, string Type);

/// <summary>
/// One corporate action as the vendor publishes it. The ratio is decimal from the moment it
/// arrives: a split of 3 for 2 is 1.5 exactly in decimal and is not in binary floating point,
/// and a factor a hair under rescales an entire price history a hair under.
///
/// For a dividend the ratio is the cash per share rather than a factor, which is the one place
/// the column name argues against its contents. SCHEMA says so at the column.
/// </summary>
public sealed record VendorCorporateAction(
    string Ticker,
    DateOnly EffectiveDate,
    CorporateActionType Type,
    decimal Ratio)
{
    /// <summary>
    /// True when this action moves every adjusted close before it, which is what forces a
    /// rebuild. A split does it by a factor and a dividend by a smaller one, and magnitude does
    /// not enter it: "less wrong" is not a category this design has.
    /// see: An unprocessed corporate action of any kind blocks calculation, not only a split
    ///
    /// The two exclusions are actions that move nothing. A one-for-one split is a vendor
    /// bookkeeping artefact rather than an event, and a dividend of zero is a row with no
    /// payment in it.
    /// </summary>
    public bool MovesAdjustedClose => Type switch
    {
        CorporateActionType.Split => Ratio != 1m,
        CorporateActionType.Dividend => Ratio != 0m,
        _ => false,
    };
}

/// <summary>
/// One daily bar as the vendor publishes it. Prices are decimal from the moment they arrive:
/// there is no point in the system where a price is a double.
/// </summary>
public sealed record VendorDailyBar(
    string Ticker,
    DateOnly BarDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjustedClose,
    long Volume)
{
    /// <summary>
    /// What the liquidity floor is measured in. Raw close against raw volume, because that is
    /// what actually changed hands on the day.
    /// </summary>
    public decimal DollarVolume => Close * Volume;
}
