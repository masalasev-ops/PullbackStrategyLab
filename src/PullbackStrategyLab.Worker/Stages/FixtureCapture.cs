using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Captures the golden fixture's inputs: one real vendor response per endpoint the lab uses,
/// held verbatim, with the endpoint, the query and the instant recorded beside it.
///
/// Why verbatim rather than a dump of the store. A fixture built from stored rows has already
/// been through this build's parser, so it agrees with the parser by construction and can never
/// disagree with it. Twice in phase 1 a hand-built fixture passed while the live run failed, both
/// times because the fixture encoded a belief about the vendor rather than the vendor's actual
/// behaviour, and a fixture derived from the store would have encoded the same beliefs one layer
/// down.
/// see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one
///
/// A one-time operation, so its calls are recorded and the nightly total does not see them.
/// </summary>
public sealed class FixtureCapture
{
    public const string Name = "capture-fixture";

    /// <summary>
    /// 250 sessions, which is BUILD_PLAN's figure for the fixture's depth and comfortably more
    /// than the 150-session warm-up needs. A calendar year is about 252 sessions, so the window
    /// is asked for in calendar days and trimmed by whatever the market did not trade.
    /// </summary>
    public const int FixtureSessions = 250;

    /// <summary>Where the captured responses are written. A repository path, given rather than inferred.</summary>
    public const string OutFlag = "--out";

    /// <summary>
    /// Camel case, matching the vendor's own shape and the options the client reads responses
    /// with. The manifest sits beside files written by the vendor, so it reads like them.
    /// </summary>
    private static readonly JsonSerializerOptions Manifest =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly EodhdClient _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public FixtureCapture(
        EodhdClient vendor,
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _vendor = vendor;
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        int out_ = Array.IndexOf(args, OutFlag);
        if (out_ < 0 || out_ + 1 >= args.Length)
        {
            // Stated rather than guessed. The fixture is a committed repository artefact and the
            // worker's content root is wherever its binary sits, so there is no directory this
            // could infer that would be right on both machines.
            Console.Error.WriteLine($"{Name}: give the destination, {OutFlag} <directory>. It is a path in the repository, not under the data root.");
            return 2;
        }

        string? date = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && a != args[out_ + 1]);
        DateOnly asOf = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        CaptureResult result = await CaptureAsync(asOf, args[out_ + 1], cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Responses} response(s) captured to {result.Directory}");
        Console.WriteLine($"{Name}: {result.Tickers} ticker(s), {result.Bytes:N0} bytes");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, outside the daily ceiling");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// The manifest a previous capture left, or null. Missing and unreadable are the same answer
    /// here: capture everything, which costs calls and is never wrong.
    /// </summary>
    private static CaptureManifest? ReadManifest(string directory)
    {
        string file = Path.Combine(directory, "manifest.json");

        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CaptureManifest>(File.ReadAllText(file), Manifest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<CaptureResult> CaptureAsync(DateOnly asOf, string outDirectory, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        // A one-time operation, on the same footing as the history backfill. It writes no store
        // row, and the run entry exists so the calls it spent are on the record.
        using RunScope run = _runLogger.Begin(connection, Name, CallCounting.OutsideTheDailyCeiling);

        string directory = Path.GetFullPath(outDirectory);
        Directory.CreateDirectory(directory);

        IReadOnlyList<string> tickers = FixtureTickers.All;
        DateOnly from = asOf.AddDays(-(FixtureSessions * 7 / 5) - 14);

        var entries = new List<CaptureEntry>();
        long bytes = 0;
        int reused = 0;

        // What a previous capture into this directory already holds, so re-running to add one
        // endpoint costs that endpoint rather than the whole fixture again. The captured responses
        // are fifteen megabytes and most of them cost a hundred calls each; re-spending that to
        // learn nothing new is the kind of cost that stops a fixture being extended at all.
        CaptureManifest? existing = ReadManifest(directory);

        async Task<bool> Capture(string name, string path, string? query, int cost)
        {
            string existingFile = name + ".json";
            CaptureEntry? already = existing?.Responses
                .FirstOrDefault(e => string.Equals(e.File, existingFile, StringComparison.Ordinal));

            if (already is not null && File.Exists(Path.Combine(directory, existingFile)))
            {
                // Kept verbatim, instant and all. Re-stamping it with tonight's time would say the
                // response was captured now, which is exactly the provenance the tier records.
                entries.Add(already);
                bytes += already.Bytes;
                reused++;
                return true;
            }

            VendorResult<CapturedResponse> response = await _vendor
                .GetRawAsync(path, query, cost, run, cancellationToken).ConfigureAwait(false);

            if (response.BudgetExhausted)
            {
                return false;
            }

            CapturedResponse captured = response.Require();
            string file = name + ".json";
            await File.WriteAllTextAsync(Path.Combine(directory, file), captured.Body, cancellationToken).ConfigureAwait(false);

            bytes += captured.Body.Length;
            entries.Add(new CaptureEntry(
                file,
                captured.Endpoint,
                captured.Query,
                StoreText.TimestampToStorageText(run.StartedAt),
                captured.Body.Length));

            return true;
        }

        string exchange = _options.Vendor.Exchange;
        string barDate = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // One response per endpoint the lab reads. Every path a live run exercises gets one, so
        // no path is left with authored evidence alone.
        bool complete =
            await Capture("exchange-symbol-list", $"exchange-symbol-list/{exchange}", null, EodhdClient.SymbolListCost).ConfigureAwait(false)
            && await Capture("bulk-end-of-day", $"eod-bulk-last-day/{exchange}", $"date={barDate}", EodhdClient.BulkEndOfDayCost).ConfigureAwait(false)
            && await Capture("bulk-splits", $"eod-bulk-last-day/{exchange}", $"type=splits&date={barDate}", EodhdClient.BulkSplitCost).ConfigureAwait(false)
            && await Capture("bulk-dividends", $"eod-bulk-last-day/{exchange}", $"type=dividends&date={barDate}", EodhdClient.BulkDividendCost).ConfigureAwait(false);

        int capturedTickers = 0;

        // The lazy sector lookup, one call a name. Captured for the fixture's own tickers only:
        // the endpoint a live run exercises needs a captured input, and one response per endpoint
        // is what the decision asks for rather than one per name in the market.
        if (complete)
        {
            foreach (string ticker in tickers)
            {
                if (!await Capture(
                        $"fundamentals-{ticker}",
                        $"fundamentals/{ticker}.{exchange}",
                        "filter=General::Sector,General::Industry,Highlights::MarketCapitalization",
                        EodhdClient.FundamentalsCost).ConfigureAwait(false))
                {
                    complete = false;
                    break;
                }
            }
        }

        if (complete)
        {
            foreach (string ticker in tickers.Concat(_options.IndexSymbols))
            {
                bool ok = await Capture(
                    $"history-{ticker}",
                    $"eod/{ticker}.{exchange}",
                    $"period=d&from={from:yyyy-MM-dd}&to={barDate}",
                    EodhdClient.DailyHistoryCost).ConfigureAwait(false);

                if (!ok)
                {
                    complete = false;
                    break;
                }

                capturedTickers++;
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(new CaptureManifest("CAPTURED", barDate, _options.Vendor.Name, entries), Manifest),
            cancellationToken).ConfigureAwait(false);

        RunOutcome outcome = complete ? RunOutcome.Clean : RunOutcome.Partial;
        RunSummary summary = run.Complete(outcome);

        Console.WriteLine($"{Name}: {reused} response(s) reused from the existing capture, {entries.Count - reused} newly captured");
        return new CaptureResult(directory, entries.Count, capturedTickers, bytes, summary.CallsUsed, outcome);
    }
}

/// <summary>
/// The thirty names the golden fixture holds, and why each is there. Named in code rather than
/// chosen at capture time, because a fixture whose membership is decided by whatever the screen
/// returned that evening is a fixture that changes when the market does.
/// </summary>
public static class FixtureTickers
{
    /// <summary>
    /// The three from 1.6, whose values are already derived independently and recorded: IESC
    /// carries a real split inside the window, LITE is the order-of-magnitude case at a high
    /// price and a high daily range, PAYO is the clean control near the price floor.
    /// </summary>
    public static IReadOnlyList<string> Derived { get; } = ["IESC", "LITE", "PAYO"];

    /// <summary>
    /// The rest, spread across price and range so a fault that only shows at one end of either
    /// has somewhere to show. Ordinary large names, mid-priced movers and low-priced quiet ones.
    /// </summary>
    public static IReadOnlyList<string> Spread { get; } =
    [
        "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "TSLA", "JPM", "XOM", "JNJ",
        "WMT", "PG", "KO", "PFE", "T", "F", "BAC", "CSCO", "INTC", "AMD",
        "COIN", "PLTR", "SOFI", "RIVN", "CCL", "NCLH", "HOOD",
    ];

    public static IReadOnlyList<string> All { get; } = [.. Derived, .. Spread];
}

public sealed record CaptureEntry(string File, string Endpoint, string Query, string CapturedAt, int Bytes);

public sealed record CaptureManifest(string Tier, string AsOf, string Vendor, IReadOnlyList<CaptureEntry> Responses);

public sealed record CaptureResult(
    string Directory,
    int Responses,
    int Tickers,
    long Bytes,
    int CallsUsed,
    RunOutcome Outcome);
