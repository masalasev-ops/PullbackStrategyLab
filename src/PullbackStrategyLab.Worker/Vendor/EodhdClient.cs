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

        SymbolRow[] rows = await GetAsync<SymbolRow[]>(
            $"exchange-symbol-list/{Uri.EscapeDataString(exchange)}",
            query: null,
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
