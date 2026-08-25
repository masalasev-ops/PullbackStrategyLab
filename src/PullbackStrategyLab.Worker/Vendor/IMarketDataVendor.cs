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
