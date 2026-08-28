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
    private readonly Dictionary<DateOnly, List<VendorCorporateAction>> _actions = [];

    public List<DateOnly> DatesRequested { get; } = [];

    /// <summary>
    /// Which action types were actually asked for, and on what date. A stage that started
    /// pulling dividends nightly would cost a bulk request a night and nothing else would
    /// notice, so the fake records the question as well as answering it.
    /// </summary>
    public List<(DateOnly Date, CorporateActionType Type)> ActionsRequested { get; } = [];

    /// <summary>Which tickers the per-ticker endpoint was asked for. One call each, so this is the bill.</summary>
    public List<string> HistoriesRequested { get; } = [];

    public int SymbolListRequests { get; private set; }

    public FakeMarketDataVendor Listing(string ticker, string type = "Common Stock", string name = "")
    {
        _symbols.Add(new VendorSymbol(ticker, name.Length == 0 ? ticker : name, "NASDAQ", type));
        return this;
    }

    /// <summary>States one bar. Volume is given in shares, and dollar volume follows from the close.</summary>
    public FakeMarketDataVendor Bar(DateOnly date, string ticker, decimal close, long volume) =>
        Bar(date, ticker, close, close, close, close, close, volume);

    /// <summary>
    /// States one bar in full, which a test needs when the raw close and the adjusted close have
    /// to differ: that gap is the whole of what a corporate action does to a stored series.
    /// A second call for a date and ticker already stated replaces it, so a test can restate a
    /// series on a new basis the way the vendor does.
    /// </summary>
    public FakeMarketDataVendor Bar(
        DateOnly date,
        string ticker,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal adjustedClose,
        long volume)
    {
        if (!_bars.TryGetValue(date, out List<VendorDailyBar>? day))
        {
            day = [];
            _bars[date] = day;
        }

        day.RemoveAll(b => string.Equals(b.Ticker, ticker, StringComparison.Ordinal));
        day.Add(new VendorDailyBar(ticker, date, open, high, low, close, adjustedClose, volume));
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

    /// <summary>
    /// Restates one ticker's whole adjusted series by a factor, which is what the vendor does the
    /// evening a corporate action lands: every adjusted close before it moves and the raw prices
    /// do not. Without this a test's refetch returns figures identical to the stored ones, which
    /// is a market where a four-for-one changed nothing.
    /// </summary>
    public FakeMarketDataVendor Adjust(string ticker, decimal factor)
    {
        foreach (List<VendorDailyBar> day in _bars.Values)
        {
            for (int i = 0; i < day.Count; i++)
            {
                if (string.Equals(day[i].Ticker, ticker, StringComparison.Ordinal))
                {
                    day[i] = day[i] with { AdjustedClose = day[i].AdjustedClose * factor };
                }
            }
        }

        return this;
    }

    /// <summary>States one split, as a factor: 4 for a four-for-one, 1.5 for a three-for-two.</summary>
    public FakeMarketDataVendor Split(DateOnly date, string ticker, decimal ratio) =>
        Action(date, ticker, CorporateActionType.Split, ratio);

    /// <summary>States one dividend, in cash per share.</summary>
    public FakeMarketDataVendor Dividend(DateOnly date, string ticker, decimal perShare) =>
        Action(date, ticker, CorporateActionType.Dividend, perShare);

    private FakeMarketDataVendor Action(DateOnly date, string ticker, CorporateActionType type, decimal ratio)
    {
        if (!_actions.TryGetValue(date, out List<VendorCorporateAction>? day))
        {
            day = [];
            _actions[date] = day;
        }

        day.Add(new VendorCorporateAction(ticker, date, type, ratio));
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

    /// <summary>
    /// Everything the fake holds for one ticker in the window, whatever date it was stated
    /// under. The real endpoint returns a name's whole series adjusted as the vendor adjusts it
    /// today, so a test that wants a rebuilt series states the new figures and asks again.
    /// </summary>
    /// <summary>What the fundamentals lookup returns per ticker, and which names were asked about.</summary>
    public Dictionary<string, VendorFundamentals> Fundamentals { get; } = new(StringComparer.Ordinal);

    public List<string> FundamentalsRequested { get; } = [];

    /// <summary>
    /// Names the lookup throws on, and what it throws.
    ///
    /// The call is counted first, exactly as the real client counts it before issuing the request,
    /// so a test can tell a name that cost a call from one that never got asked.
    /// </summary>
    public Dictionary<string, Exception> FundamentalsThrows { get; } = new(StringComparer.Ordinal);

    public Task<VendorResult<VendorFundamentals?>> GetFundamentalsAsync(
        string ticker,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(EodhdClient.FundamentalsCost))
        {
            return Task.FromResult(VendorResult<VendorFundamentals?>.OutOfBudget());
        }

        FundamentalsRequested.Add(ticker);

        if (FundamentalsThrows.TryGetValue(ticker, out Exception? thrown))
        {
            throw thrown;
        }

        // Absent rather than empty for a name the fixture says nothing about. A vendor that has no
        // fundamentals for a ticker returns nothing, and the stage has to tell that apart from a
        // name whose sector is genuinely blank.
        return Task.FromResult(VendorResult<VendorFundamentals?>.Delivered(
            Fundamentals.TryGetValue(ticker, out VendorFundamentals? found) ? found : null));
    }

    public Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetDailyHistoryAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(EodhdClient.DailyHistoryCost))
        {
            return Task.FromResult(VendorResult<IReadOnlyList<VendorDailyBar>>.OutOfBudget());
        }

        HistoriesRequested.Add(ticker);

        IReadOnlyList<VendorDailyBar> history = _bars.Values
            .SelectMany(day => day)
            .Where(b => string.Equals(b.Ticker, ticker, StringComparison.Ordinal) && b.BarDate >= from && b.BarDate <= to)
            .OrderBy(b => b.BarDate)
            .ToArray();

        return Task.FromResult(VendorResult<IReadOnlyList<VendorDailyBar>>.Delivered(history));
    }

    public Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkSplitsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default) =>
        Actions(date, CorporateActionType.Split, EodhdClient.BulkSplitCost, budget);

    public Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkDividendsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default) =>
        Actions(date, CorporateActionType.Dividend, EodhdClient.BulkDividendCost, budget);

    private Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> Actions(
        DateOnly date,
        CorporateActionType type,
        int cost,
        ICallBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(cost))
        {
            return Task.FromResult(VendorResult<IReadOnlyList<VendorCorporateAction>>.OutOfBudget());
        }

        ActionsRequested.Add((date, type));

        IReadOnlyList<VendorCorporateAction> day = _actions.TryGetValue(date, out List<VendorCorporateAction>? actions)
            ? actions.Where(a => a.Type == type).ToArray()
            : [];

        return Task.FromResult(VendorResult<IReadOnlyList<VendorCorporateAction>>.Delivered(day));
    }
}
