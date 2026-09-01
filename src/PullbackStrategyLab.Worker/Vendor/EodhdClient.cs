using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Vendor;

/// <summary>
/// EODHD, over HTTP. Only the Worker holds one of these: the Api never calls the vendor and
/// gets no key.
///
/// The costs below are what the call budget in ARCHITECTURE.html is built on, and they are
/// what makes the ceiling meaningful. Counting requests instead of their cost would report a
/// twentieth of what a night of bulk requests actually spends.
/// see: The vendor is EODHD, and the endpoint mix is what the call budget is built on
/// </summary>
public sealed class EodhdClient : IMarketDataVendor
{
    /// <summary>The exchange symbol list, whole exchange, one request.</summary>
    public const int SymbolListCost = 5;

    /// <summary>One market day of the whole market's closing prices. Replaces about 6,000 individual requests.</summary>
    public const int BulkEndOfDayCost = 100;

    /// <summary>
    /// One market day of every split, whole market. Priced as a bulk request because that is
    /// what it is: the same endpoint as the closing prices, asked a different question.
    /// </summary>
    public const int BulkSplitCost = 100;

    /// <summary>
    /// One market day of every dividend, whole market. The same request as the other two and
    /// the same price.
    ///
    /// The data budget's row for dividends says roughly 20, which is the nightly average of a
    /// weekly request rather than the price of one, and it is the one row of that table whose
    /// figure is an average rather than a cost. Charging the budget 20 for a request that costs
    /// 100 would under-count by 80 every time it ran, which is precisely the accounting error
    /// the ceiling exists to catch, so the constant is the cost and the schedule is what makes
    /// it weekly.
    /// </summary>
    public const int BulkDividendCost = 100;

    /// <summary>
    /// One ticker's daily history, any depth, one request. The other half of the endpoint split:
    /// going deep is free here and ruinous on the bulk endpoint, which is why the backfill
    /// screens on bulk data first and only then fetches history for the survivors.
    /// </summary>
    public const int DailyHistoryCost = 1;

    /// <summary>
    /// One fundamentals lookup. Priced per request like the per-ticker history, and asked once per
    /// name for ever rather than nightly, which is what keeps the sector lookup at about fifty calls
    /// a night in the steady state instead of one per universe member.
    /// </summary>
    public const int FundamentalsCost = 1;

    /// <summary>
    /// One ticker's minute bars for one window. Five, not one: the vendor prices intraday above the
    /// per-ticker daily endpoint, and the data budget's minute-bar row is built on this figure.
    ///
    /// It is what makes the minute-bar row the second largest consumer in the lab, so under-counting
    /// it by four would let a night overrun the ceiling by four fifths of that row without the
    /// counter noticing, which is the accounting error the ceiling exists to catch.
    /// </summary>
    public const int IntradayCost = 5;

    /// <summary>
    /// One name's delayed quote, carrying both sides of the book. One, and the vendor prices it per
    /// <b>ticker</b> rather than per request even though the endpoint takes a batch, so a hundred
    /// names in one call is a hundred calls and the saving is in requests alone.
    ///
    /// That distinction is the whole reason this constant exists separately from the request count.
    /// A budget that charged a batch as one would let a single request spend sixty and report one,
    /// which is the accounting error the ceiling exists to catch, arriving from the direction the
    /// other endpoints cannot produce.
    /// </summary>
    public const int UsQuoteCost = 1;

    /// <summary>
    /// The endpoint that carries a bid and an ask.
    ///
    /// <b>It is not the real-time endpoint, and that was established rather than assumed.</b> A
    /// probe of <c>real-time/AAPL.US</c> on 2026-09-01 answered with open, high, low, close, volume,
    /// previous close and change, and no side of the book at all; the spread this lab is built to
    /// capture is not derivable from any of them. This path answers with <c>bidPrice</c>,
    /// <c>askPrice</c>, their sizes and a stamp for each side, which is what the store's columns are.
    /// </summary>
    public const string UsQuotePath = "us-quote-delayed";

    /// <summary>
    /// How many names go in one request. The vendor documents no maximum, so this is the lab's own
    /// bound rather than the vendor's: a request naming every capped name at once would put the
    /// whole pass on one response, and a single unreadable body would cost the session its spread
    /// where sixty small ones cost it one name.
    ///
    /// It changes no cost. The price is per ticker, so batching buys fewer round trips and nothing
    /// else, which is why the figure can be chosen for robustness rather than for the budget.
    /// </summary>
    public const int UsQuoteBatchSize = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly PullbackStrategyLabOptions _options;

    public EodhdClient(HttpClient http, IOptions<PullbackStrategyLabOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(_options.Vendor.BaseAddress, UriKind.Absolute);
        }
    }

    public async Task<VendorResult<IReadOnlyList<VendorSymbol>>> GetExchangeSymbolListAsync(
        string exchange,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(SymbolListCost))
        {
            return VendorResult<IReadOnlyList<VendorSymbol>>.OutOfBudget();
        }

        return await SymbolListAsync(exchange, query: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VendorResult<IReadOnlyList<VendorSymbol>>> GetDelistedSymbolListAsync(
        string exchange,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(SymbolListCost))
        {
            return VendorResult<IReadOnlyList<VendorSymbol>>.OutOfBudget();
        }

        // The same endpoint and the same price. `delisted=1` is the whole difference, and the
        // response has the same shape, which is why the capture switch keys on the endpoint
        // prefix and needs no arm of its own.
        return await SymbolListAsync(exchange, query: "delisted=1", cancellationToken).ConfigureAwait(false);
    }

    private async Task<VendorResult<IReadOnlyList<VendorSymbol>>> SymbolListAsync(
        string exchange,
        string? query,
        CancellationToken cancellationToken)
    {
        SymbolRow[] rows = await GetAsync<SymbolRow[]>(
            $"exchange-symbol-list/{Uri.EscapeDataString(exchange)}",
            query,
            cancellationToken).ConfigureAwait(false) ?? [];

        IReadOnlyList<VendorSymbol> symbols = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Code))
            .Select(r => new VendorSymbol(r.Code!, r.Name ?? r.Code!, r.Exchange ?? exchange, r.Type ?? string.Empty))
            .ToArray();

        return VendorResult<IReadOnlyList<VendorSymbol>>.Delivered(symbols);
    }

    public async Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetBulkEndOfDayAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(BulkEndOfDayCost))
        {
            return VendorResult<IReadOnlyList<VendorDailyBar>>.OutOfBudget();
        }

        BulkBarRow[] rows = await GetAsync<BulkBarRow[]>(
            $"eod-bulk-last-day/{Uri.EscapeDataString(exchange)}",
            query: $"date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false) ?? [];

        IReadOnlyList<VendorDailyBar> bars = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Code) && r.Date is not null)
            .Select(r => new VendorDailyBar(
                r.Code!,
                DateOnly.ParseExact(r.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Open,
                r.High,
                r.Low,
                r.Close,
                r.AdjustedClose ?? r.Close,
                (long)r.Volume))
            .ToArray();

        return VendorResult<IReadOnlyList<VendorDailyBar>>.Delivered(bars);
    }

    public Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkSplitsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default) =>
        GetBulkActionsAsync(exchange, date, CorporateActionType.Split, BulkSplitCost, budget, cancellationToken);

    public Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkDividendsAsync(
        string exchange,
        DateOnly date,
        ICallBudget budget,
        CancellationToken cancellationToken = default) =>
        GetBulkActionsAsync(exchange, date, CorporateActionType.Dividend, BulkDividendCost, budget, cancellationToken);

    public async Task<VendorResult<IReadOnlyList<VendorDailyBar>>> GetDailyHistoryAsync(
        string ticker,
        DateOnly from,
        DateOnly to,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(DailyHistoryCost))
        {
            return VendorResult<IReadOnlyList<VendorDailyBar>>.OutOfBudget();
        }

        HistoryBarRow[] rows = await GetAsync<HistoryBarRow[]>(
            $"eod/{Uri.EscapeDataString(ticker)}.{Uri.EscapeDataString(_options.Vendor.Exchange)}",
            query: $"period=d&from={Iso(from)}&to={Iso(to)}",
            cancellationToken).ConfigureAwait(false) ?? [];

        IReadOnlyList<VendorDailyBar> bars = rows
            .Where(r => r.Date is not null)
            .Select(r => new VendorDailyBar(
                ticker,
                DateOnly.ParseExact(r.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Open,
                r.High,
                r.Low,
                r.Close,
                r.AdjustedClose ?? r.Close,
                (long)r.Volume))
            .ToArray();

        return VendorResult<IReadOnlyList<VendorDailyBar>>.Delivered(bars);
    }

    public async Task<VendorResult<IReadOnlyList<VendorIntradayBar>>> GetIntradayAsync(
        string ticker,
        DateTimeOffset from,
        DateTimeOffset to,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(IntradayCost))
        {
            return VendorResult<IReadOnlyList<VendorIntradayBar>>.OutOfBudget();
        }

        // The window is sent as UNIX seconds, which is the vendor's own unit and carries its zone
        // with it. Formatting the boundary as a local date string would put the session's definition
        // in a query parameter and let the vendor decide where a day starts.
        IntradayBarRow[] rows = await GetAsync<IntradayBarRow[]>(
            $"intraday/{Uri.EscapeDataString(ticker)}.{Uri.EscapeDataString(_options.Vendor.Exchange)}",
            query: $"interval={MinuteInterval}&from={from.ToUnixTimeSeconds()}&to={to.ToUnixTimeSeconds()}",
            cancellationToken).ConfigureAwait(false) ?? [];

        var bars = new List<VendorIntradayBar>(rows.Length);

        foreach (IntradayBarRow row in rows)
        {
            if (row.Timestamp is not long stamp)
            {
                // A row the vendor published with no stamp is a bar with no place in a series.
                // Skipped rather than given the previous bar's minute, which would put volume that
                // traded at an unknown time onto a minute that has its own.
                continue;
            }

            // A minute with no trades comes back with nulls in the four price fields rather than
            // being absent, and it is not a bar: storing it as four zeros would make the day's low
            // nought on every quiet name. Volume of zero with real prices is a real bar and stays.
            if (row.Open is not decimal open || row.High is not decimal high
                || row.Low is not decimal low || row.Close is not decimal close)
            {
                continue;
            }

            bars.Add(new VendorIntradayBar(
                ticker,
                DateTimeOffset.FromUnixTimeSeconds(stamp),
                open,
                high,
                low,
                close,
                (long)(row.Volume ?? 0m)));
        }

        return VendorResult<IReadOnlyList<VendorIntradayBar>>.Delivered(bars);
    }

    /// <summary>
    /// Delayed quotes for a batch of names, both sides of the book with a stamp on each.
    ///
    /// <b>The budget is charged per name and the request is made once.</b> Both facts are the
    /// vendor's and neither is inferable from the other, so the count is taken before the call and
    /// the whole batch is refused together where the ceiling cannot cover it. Charging on arrival
    /// would spend calls the ceiling had already said no to.
    ///
    /// <b>A name the vendor omits from the answer comes back absent rather than as an empty quote.</b>
    /// The caller asked for a set and got a map, and the difference between the two is the fact the
    /// pass records: a name quoted with nulls and a name the vendor never mentioned are different
    /// answers, and only the first is evidence about the name.
    /// </summary>
    public async Task<VendorResult<IReadOnlyList<VendorQuote>>> GetQuotesAsync(
        IReadOnlyList<string> tickers,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tickers);
        ArgumentNullException.ThrowIfNull(budget);

        if (tickers.Count == 0)
        {
            return VendorResult<IReadOnlyList<VendorQuote>>.Delivered([]);
        }

        if (!budget.TryCountCalls(UsQuoteCost * tickers.Count))
        {
            return VendorResult<IReadOnlyList<VendorQuote>>.OutOfBudget();
        }

        string symbols = string.Join(
            ',',
            tickers.Select(t => $"{t}.{_options.Vendor.Exchange}"));

        QuoteEnvelope? envelope = await GetAsync<QuoteEnvelope>(
            UsQuotePath,
            query: $"s={Uri.EscapeDataString(symbols)}",
            cancellationToken).ConfigureAwait(false);

        var quotes = new List<VendorQuote>(tickers.Count);

        foreach (string ticker in tickers)
        {
            string key = $"{ticker}.{_options.Vendor.Exchange}";

            if (envelope?.Data is null || !envelope.Data.TryGetValue(key, out QuoteRow? row) || row is null)
            {
                // The vendor did not mention this name. Not a quote with nothing in it, and the
                // caller has to be able to tell the two apart, so nothing is added for it.
                continue;
            }

            quotes.Add(new VendorQuote(
                ticker,
                row.BidPrice,
                row.AskPrice,
                row.BidSize,
                row.AskSize,
                FromMilliseconds(row.BidTime),
                FromMilliseconds(row.AskTime),
                row.LastTradePrice,
                FromMilliseconds(row.LastTradeTime)));
        }

        return VendorResult<IReadOnlyList<VendorQuote>>.Delivered(quotes);
    }

    /// <summary>
    /// The quote stamps arrive as milliseconds since the epoch, where every other stamp this vendor
    /// sends is seconds. Converted here, once, rather than at each of the three call sites: a stamp
    /// read on the wrong unit lands in 1970 or fifty thousand years out, and both are obvious in a
    /// row and neither is obvious in a diff.
    /// </summary>
    private static DateTimeOffset? FromMilliseconds(long? epochMilliseconds) =>
        epochMilliseconds is long value && value > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : null;

    /// <summary>
    /// The interval the lab asks for, in the vendor's own vocabulary. One minute is the finest the
    /// vendor offers and the only one this lab stores: a coarser bar cannot be refined afterwards
    /// and a session captured at five minutes is a session captured wrong for ever.
    /// </summary>
    public const string MinuteInterval = "1m";

    public async Task<VendorResult<VendorFundamentals?>> GetFundamentalsAsync(
        string ticker,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(FundamentalsCost))
        {
            return VendorResult<VendorFundamentals?>.OutOfBudget();
        }

        // Only the three fields the lab uses. The whole fundamentals document is large and most of
        // it is accounting the strategy never reads, so the request names its filter: asking for
        // everything and keeping three fields would spend the same call and store a great deal more.
        FundamentalsRow? row = await GetAsync<FundamentalsRow>(
            $"fundamentals/{Uri.EscapeDataString(ticker)}.{Uri.EscapeDataString(_options.Vendor.Exchange)}",
            query: "filter=General::Sector,General::Industry,Highlights::MarketCapitalization",
            cancellationToken).ConfigureAwait(false);

        // An empty string is the vendor's way of saying it holds no sector, and it is not the same
        // as a field it did not send. Stored as it arrives it would be a resolved sector of "", which
        // reads as an industry group of its own and would cluster every such name together.
        string? sector = Blank(row?.Sector);
        string? industry = Blank(row?.Industry);
        decimal? cap = row?.MarketCapitalization;

        // All three absent is the vendor having nothing on the name, which is a real answer the
        // stage already handles: it stamps the name so the question is not asked again and leaves
        // the three columns null, which is the truth. Returning a row of nulls instead would count
        // it as resolved and say the opposite.
        return VendorResult<VendorFundamentals?>.Delivered(
            sector is null && industry is null && cap is null
                ? null
                : new VendorFundamentals(ticker, sector, industry, cap));
    }

    /// <summary>
    /// The filtered fundamentals response: three fields, flattened by the vendor's own filter.
    ///
    /// <b>The property names are the filter's, not the document's.</b> Asking for
    /// <c>General::Sector</c> returns a key literally called <c>General::Sector</c> rather than a
    /// nested object, so the names are pinned here rather than left to the serializer's convention.
    /// Found by capturing the real response: the convention-named version deserialized to a row of
    /// nulls without erroring, which would have left every name unresolved and looked like a vendor
    /// that had nothing on any of them.
    ///
    /// The cap comes back as a JSON number and is read as decimal rather than long, on the same
    /// reasoning volume was at 1.3: a vendor that publishes one value with a fractional part would
    /// otherwise make the whole response unreadable over one field.
    ///
    /// <b>And it does not always come back as a number.</b> A name the vendor holds no capitalisation
    /// for answers 200 with the string <c>"NA"</c> in that field and empty strings in the other two,
    /// which threw mid-deserialization and took the sector walk down with it on 2026-08-27. The same
    /// lesson as the property names, learned the same way and one field along: thirty captured
    /// responses were thirty working examples, and the shape that mattered was the one nothing had
    /// asked for. <c>fundamentals-MUZ.json</c> is that response, captured.
    /// </summary>
    private sealed record FundamentalsRow(
        [property: JsonPropertyName("General::Sector")] string? Sector,
        [property: JsonPropertyName("General::Industry")] string? Industry,
        [property: JsonPropertyName("Highlights::MarketCapitalization")]
        [property: JsonConverter(typeof(TolerantDecimalConverter))] decimal? MarketCapitalization);

    /// <summary>Whitespace read as absent, because the vendor writes an empty string for a field it does not hold.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Why a captured response could not be read, or null where it reads cleanly.
    ///
    /// <b>The condition a capture refuses on, stated as what it is.</b> A non-200 was the obvious
    /// guard and it is not the one that would have caught anything: the response that killed the
    /// sector walk on 2026-08-27 came back 200 with a capitalisation of the string "NA", and a guard
    /// on status would have stored it as a working example. Status is one way a response goes wrong.
    /// The condition is any payload the parse cannot read, whatever the status.
    ///
    /// It reads the body through the same options and the same shapes the stages read it through, so
    /// a change to either moves this with it. A path with no shape declared here is reported as
    /// unchecked rather than as clean, because "nothing objected" and "nothing looked" are the two
    /// answers this whole corpus exists to keep apart.
    /// </summary>
    public static string? WhyUnreadable(CapturedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Status != 200)
        {
            return $"the vendor answered {response.Status}";
        }

        try
        {
            if (response.Endpoint.StartsWith("fundamentals/", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<FundamentalsRow>(response.Body, Json);
            }
            else if (response.Endpoint.StartsWith("exchange-symbol-list/", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<SymbolRow[]>(response.Body, Json);
            }
            else if (response.Endpoint.StartsWith("eod-bulk-last-day/", StringComparison.Ordinal))
            {
                if (response.Query.Contains("type=", StringComparison.Ordinal))
                {
                    JsonSerializer.Deserialize<BulkActionRow[]>(response.Body, Json);
                }
                else
                {
                    JsonSerializer.Deserialize<BulkBarRow[]>(response.Body, Json);
                }
            }
            else if (response.Endpoint.StartsWith("eod/", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<HistoryBarRow[]>(response.Body, Json);
            }
            else if (response.Endpoint.StartsWith("intraday/", StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<IntradayBarRow[]>(response.Body, Json);
            }
            else if (response.Endpoint.StartsWith(UsQuotePath, StringComparison.Ordinal))
            {
                JsonSerializer.Deserialize<QuoteEnvelope>(response.Body, Json);
            }
            else
            {
                return null;
            }
        }
        catch (JsonException e)
        {
            return $"the body will not shape: {e.Message}";
        }

        return null;
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// The bulk endpoint asked for actions rather than prices. One shape for both types, because
    /// the vendor answers both from the same path with a different type parameter, and two
    /// copies of this would drift the moment one of them was corrected.
    /// </summary>
    private async Task<VendorResult<IReadOnlyList<VendorCorporateAction>>> GetBulkActionsAsync(
        string exchange,
        DateOnly date,
        CorporateActionType type,
        int cost,
        ICallBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(cost))
        {
            return VendorResult<IReadOnlyList<VendorCorporateAction>>.OutOfBudget();
        }

        BulkActionRow[] rows = await GetAsync<BulkActionRow[]>(
            $"eod-bulk-last-day/{Uri.EscapeDataString(exchange)}",
            query: $"type={type.ToStorageText()}s&date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false) ?? [];

        var actions = new List<VendorCorporateAction>();
        foreach (BulkActionRow row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Code) || row.Date is null)
            {
                continue;
            }

            decimal? ratio = type == CorporateActionType.Split ? ParseSplit(row.Split) : row.Dividend;
            if (ratio is null)
            {
                // A row the vendor published without the figure the row is about. Skipped rather
                // than stored as zero, which would read as a stock whose price went to nothing.
                continue;
            }

            actions.Add(new VendorCorporateAction(
                row.Code!,
                DateOnly.ParseExact(row.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                type,
                ratio.Value));
        }

        return VendorResult<IReadOnlyList<VendorCorporateAction>>.Delivered(actions);
    }

    /// <summary>
    /// The vendor writes a split as "4.000000/1.000000", new shares over old. Divided here and
    /// stored as the factor, because every use of it multiplies or divides a price and nothing
    /// downstream wants to parse a string to do arithmetic.
    ///
    /// Decimal throughout. A four-for-one is exact either way, but a three-for-two is 1.5 in
    /// decimal and is not in binary floating point, and a factor a hair under rescales a whole
    /// price history a hair under.
    /// </summary>
    public static decimal? ParseSplit(string? split)
    {
        if (string.IsNullOrWhiteSpace(split))
        {
            return null;
        }

        string[] parts = split.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal newShares)
            || !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out decimal oldShares)
            || oldShares == 0m)
        {
            return null;
        }

        return newShares / oldShares;
    }

    /// <summary>
    /// One response, exactly as the vendor sent it, with the endpoint and query it was asked
    /// for. For capturing a fixture input at the CAPTURED tier and for nothing else: no stage
    /// reads a response this way, because a stage that parsed raw text would be a second reading
    /// of the vendor's shape.
    ///
    /// The query comes back without the token. It is the one field that must never reach a file
    /// the repository holds.
    ///
    /// <b>It records the status rather than throwing on a non-200, and that is deliberate.</b> Every
    /// other read here refuses a failed response, which is right for a stage and wrong for the one
    /// path whose purpose is to store what the vendor actually sent. Captured that way the fixture
    /// held thirty well-formed <c>fundamentals</c> responses and no case where anything could go
    /// wrong, so the parse was exercised thirty times against nothing. The caller decides what a
    /// status means: <see cref="FixtureCapture"/> refuses one for an endpoint it is capturing as a
    /// working example, and takes it for one it is capturing as a failing shape.
    /// see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one
    /// </summary>
    public async Task<VendorResult<CapturedResponse>> GetRawAsync(
        string path,
        string? query,
        int cost,
        ICallBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(budget);

        if (!budget.TryCountCalls(cost))
        {
            return VendorResult<CapturedResponse>.OutOfBudget();
        }

        (int status, string body) = await GetStringAsync(path, query, cancellationToken).ConfigureAwait(false);
        return VendorResult<CapturedResponse>.Delivered(new CapturedResponse(path, query ?? string.Empty, body, status));
    }

    private async Task<T?> GetAsync<T>(string path, string? query, CancellationToken cancellationToken)
    {
        if (!_options.Vendor.HasApiKey)
        {
            throw new VendorException(
                $"No vendor token is configured. It is read from '{VendorOptions.VendorTokenKey}', which lives in "
                + "appsettings.Secrets.json beside appsettings.json in this project. The file is gitignored, so it "
                + "does not arrive with the repository and travels by deliberate copy.");
        }

        string url = $"{path}?api_token={Uri.EscapeDataString(_options.Vendor.ApiKey)}&fmt=json"
            + (query is null ? string.Empty : "&" + query);

        using HttpResponseMessage response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The token is in the URL, so the URL never appears in a message. A failure that
            // printed it would put the key in the run log and in every terminal scrollback.
            throw new VendorException(
                $"{path} returned {(int)response.StatusCode} {response.StatusCode}."
                + (response.StatusCode == HttpStatusCode.Unauthorized
                    ? " The configured token was rejected."
                    : string.Empty));
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(body, Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same request, returning the status and the body as text.
    ///
    /// A missing token still throws, because that is a configuration fault rather than something the
    /// vendor said. A non-200 does not: the status is returned beside the body so the one caller,
    /// the fixture capture, can store what came back. Every other read refuses a failed response.
    /// </summary>
    private async Task<(int Status, string Body)> GetStringAsync(string path, string? query, CancellationToken cancellationToken)
    {
        if (!_options.Vendor.HasApiKey)
        {
            throw new VendorException(
                $"No vendor token is configured. It is read from '{VendorOptions.VendorTokenKey}', which lives in "
                + "appsettings.Secrets.json beside appsettings.json in this project.");
        }

        string url = $"{path}?api_token={Uri.EscapeDataString(_options.Vendor.ApiKey)}&fmt=json"
            + (query is null ? string.Empty : "&" + query);

        using HttpResponseMessage response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // The token is in the URL, so neither the URL nor anything derived from it is returned.
        return ((int)response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The vendor's own field names. Kept as a private shape so a change at the vendor is one
    /// edit here rather than a change to anything the rest of the lab reads.
    /// </summary>
    private sealed record SymbolRow
    {
        public string? Code { get; init; }

        public string? Name { get; init; }

        public string? Exchange { get; init; }

        public string? Type { get; init; }
    }

    private sealed record BulkBarRow
    {
        public string? Code { get; init; }

        public string? Date { get; init; }

        public decimal Open { get; init; }

        public decimal High { get; init; }

        public decimal Low { get; init; }

        public decimal Close { get; init; }

        [JsonPropertyName("adjusted_close")]
        public decimal? AdjustedClose { get; init; }

        /// <summary>
        /// Read as a decimal and narrowed here. The vendor publishes volume as a JSON number
        /// with a fractional part on some rows, and a long would refuse the whole response
        /// rather than the one field.
        /// </summary>
        public decimal Volume { get; init; }
    }

    /// <summary>
    /// The per-ticker endpoint's row. Same fields as the bulk one minus the code, because the
    /// ticker is in the path rather than in the body.
    /// </summary>
    private sealed record HistoryBarRow
    {
        public string? Date { get; init; }

        public decimal Open { get; init; }

        public decimal High { get; init; }

        public decimal Low { get; init; }

        public decimal Close { get; init; }

        [JsonPropertyName("adjusted_close")]
        public decimal? AdjustedClose { get; init; }

        /// <summary>Decimal for the same reason the bulk row's is: the vendor publishes some as fractional.</summary>
        public decimal Volume { get; init; }
    }

    /// <summary>
    /// One minute bar as the intraday endpoint sends it.
    ///
    /// <b>Every field is nullable, and that is the shape rather than caution.</b> The vendor emits a
    /// row for a minute in which nothing traded, with nulls in the four price fields, so a
    /// non-nullable decimal would refuse the whole response over a quiet minute on one name. That is
    /// the lesson the fundamentals row already carries one field along, where a capitalisation of the
    /// string "NA" took the sector walk down: the shape that matters is the one nobody asked for.
    ///
    /// <b>Only the timestamp is read for the stamp.</b> `datetime` is a formatted string with no
    /// offset in it, so reading it would require agreeing a zone with the vendor by convention.
    /// `gmtoffset` is declared here rather than dropped, so a response that started carrying a
    /// non-zero one is visible in a captured fixture rather than silently reinterpreted.
    ///
    /// Volume is decimal for the same reason the daily rows' is: the vendor publishes some as
    /// fractional, and a long would refuse the response rather than the field.
    /// </summary>
    private sealed record IntradayBarRow
    {
        public long? Timestamp { get; init; }

        [JsonPropertyName("gmtoffset")]
        public int? GmtOffset { get; init; }

        public string? Datetime { get; init; }

        public decimal? Open { get; init; }

        public decimal? High { get; init; }

        public decimal? Low { get; init; }

        public decimal? Close { get; init; }

        public decimal? Volume { get; init; }
    }

    /// <summary>
    /// The quote endpoint answers with an envelope keyed by symbol rather than with an array, which
    /// is the one shape difference between it and every other endpoint the lab reads.
    /// </summary>
    private sealed record QuoteEnvelope
    {
        public Dictionary<string, QuoteRow>? Data { get; init; }
    }

    /// <summary>
    /// One name's quote. Every field is nullable, because the vendor carries a name it has no
    /// current book for and answers with the fields it has: a null bid is the vendor saying it has
    /// no bid, which is a fact worth storing rather than a parse failure.
    ///
    /// The three time fields are milliseconds since the epoch, unlike every other stamp this vendor
    /// sends.
    /// </summary>
    private sealed record QuoteRow
    {
        public decimal? BidPrice { get; init; }

        public decimal? AskPrice { get; init; }

        public long? BidSize { get; init; }

        public long? AskSize { get; init; }

        public long? BidTime { get; init; }

        public long? AskTime { get; init; }

        public decimal? LastTradePrice { get; init; }

        public long? LastTradeTime { get; init; }
    }

    /// <summary>
    /// A split row and a dividend row from the same endpoint. One shape for both, because the
    /// vendor returns them from one path and only the figure column differs.
    /// </summary>
    private sealed record BulkActionRow
    {
        public string? Code { get; init; }

        public string? Date { get; init; }

        /// <summary>New shares over old, as text: "4.000000/1.000000". Null on a dividend row.</summary>
        public string? Split { get; init; }

        /// <summary>Cash per share. Null on a split row.</summary>
        public decimal? Dividend { get; init; }
    }
}

/// <summary>
/// One vendor response held verbatim, with the endpoint and the query it answered. The query
/// never carries the token.
/// </summary>
/// <summary>
/// One response exactly as the vendor sent it, with the status it came back with.
///
/// The status is part of the response rather than a detail of fetching it. A fixture that stored
/// only the bodies could hold no case where the vendor refused, which is how thirty captured
/// `fundamentals` responses came to exercise a parse against nothing that could fail.
/// </summary>
public sealed record CapturedResponse(string Endpoint, string Query, string Body, int Status);

/// <summary>A request the vendor refused or could not answer. Never carries the URL, which carries the token.</summary>
public sealed class VendorException : Exception
{
    public VendorException(string message) : base(message)
    {
    }

    public VendorException(string message, Exception inner) : base(message, inner)
    {
    }
}
