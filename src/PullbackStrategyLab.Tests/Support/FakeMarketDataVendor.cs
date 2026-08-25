using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// A market a test can state outright. It records which dates were asked for, so a test can
/// assert what the stage did not request as well as what it did: not asking for a Saturday is
/// worth a hundred calls a time, and nothing else would notice if the stage started.
/// </summary>
public sealed class FakeMarketDataVendor : IMarketDataVendor
{
    private readonly List<VendorSymbol> _symbols = [];
    private readonly Dictionary<DateOnly, List<VendorDailyBar>> _bars = [];

    public List<DateOnly> DatesRequested { get; } = [];

    public int SymbolListRequests { get; private set; }

    public FakeMarketDataVendor Listing(string ticker, string type = "Common Stock", string name = "")
    {
        _symbols.Add(new VendorSymbol(ticker, name.Length == 0 ? ticker : name, "NASDAQ", type));
        return this;
    }

    /// <summary>States one bar. Volume is given in shares, and dollar volume follows from the close.</summary>
    public FakeMarketDataVendor Bar(DateOnly date, string ticker, decimal close, long volume)
    {
        if (!_bars.TryGetValue(date, out List<VendorDailyBar>? day))
        {
            day = [];
            _bars[date] = day;
        }

        day.Add(new VendorDailyBar(ticker, date, close, close, close, close, close, volume));
        return this;
    }

    /// <summary>
    /// The same bar on every trading day in a window, walking back from <paramref name="from"/>
    /// and skipping weekends. What most tests want: a name that simply trades.
    /// </summary>
    public FakeMarketDataVendor Trading(string ticker, DateOnly from, int sessions, decimal close, long volume)
    {
        DateOnly date = from;
        int written = 0;

        while (written < sessions)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                Bar(date, ticker, close, volume);
                written++;
            }

            date = date.AddDays(-1);
        }

        return this;
    }

    public Task<VendorResult<IReadOnlyList<VendorSymbol>>> GetExchangeSymbolListAsync(
        string exchange,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(EodhdClient.SymbolListCost))
        {
            return Task.FromResult(VendorResult<IReadOnlyList<VendorSymbol>>.OutOfBudget());
        }

        SymbolListRequests++;
        return Task.FromResult(VendorResult<IReadOnlyList<VendorSymbol>>.Delivered((IReadOnlyList<VendorSymbol>)_symbols));
    }

    public Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetBulkEndOfDayAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(EodhdClient.BulkEndOfDayCost))
        {
            return Task.FromResult(VendorResult<IReadOnlyList<VendorDailyBar>>.OutOfBudget());
        }

        DatesRequested.Add(date);

        IReadOnlyList<VendorDailyBar> day = _bars.TryGetValue(date, out List<VendorDailyBar>? bars)
            ? bars
            : [];

        return Task.FromResult(VendorResult<IReadOnlyList<VendorDailyBar>>.Delivered(day));
    }
}
