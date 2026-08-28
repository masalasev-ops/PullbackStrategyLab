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
/// Sector, industry and market capitalisation, resolved on first sighting and cached for ever.
///
/// <b>Lazy, and that is what keeps it affordable.</b> Asking for every universe member would be two
/// thousand calls; asking only for names a scan surfaced, once each, is about fifty a night in the
/// steady state and falls as the cache fills. The three facts move slowly enough that re-asking
/// nightly would spend a call a name to learn nothing.
///
/// It updates four columns of `security` and nothing else, which is what SCHEMA declares.
/// UniverseBuilder inserts the row; this stage fills in what the symbol list does not carry.
///
/// <b>A name the vendor has nothing on is stamped anyway.</b> Otherwise it would be re-asked every
/// night for ever, one call a night to learn the same nothing. The stamp records that the question
/// was asked; the three columns stay null, which is the true answer.
///
/// <b>A name that throws costs that name and no other.</b> On 2026-08-27 the walk asked 149 names,
/// resolved 148, and died on the 149th, whose capitalisation came back as the string "NA". One
/// ticker took the other 86 with it, and the cost is not the calls: `clusters` runs three minutes
/// later over whatever `security` holds, so fifteen of that night's forty-four setups recorded a
/// cluster verdict of failed with no value, and a setup row cannot be improved once its outcome is
/// visible. A stage that dies mid-walk leaves the stages downstream reading a store it half filled.
///
/// So the walk continues and counts. A skipped name keeps its null stamp and is asked again
/// tomorrow, because a transient refusal must not permanently mark a good name as unknown, and the
/// bound on re-asking is that the count is on the record where somebody reads it rather than that
/// the stage gave up. The outcome is `partial`, which is what this stage already says when the
/// ceiling stops it short: calls spent, list not finished.
/// </summary>
public sealed class SectorResolver
{
    public const string Name = "sectors";

    /// <summary>How many names one run will look up, so a first night cannot spend the whole budget.</summary>
    public const int DefaultLimit = 200;

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SectorResolver(
        IMarketDataVendor vendor,
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

        string? date = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        DateOnly asOf = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        SectorResult result = await ResolveAsync(asOf, DefaultLimit, cancellationToken).ConfigureAwait(false);

        // Three counts over one pool, and they sum to it. The line this replaces said "86 asked, 85
        // resolved, 1 the vendor had nothing on" and a reader then wrote that all 234 of the night's
        // names carry a sector, when at most 233 can: a name the vendor holds nothing on is stamped
        // and has no sector, which is a third state neither of the first two figures reports.
        int unstamped = result.Unresolved - result.Resolved - result.VendorHadNothing;

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Unresolved} name(s) on a scan with no sector");
        Console.WriteLine(
            $"{Name}: {result.Resolved} resolved, {result.VendorHadNothing} recorded as a name the vendor holds "
            + $"nothing on, {unstamped} left unstamped, summing to {result.Unresolved}");
        Console.WriteLine(
            $"{Name}: pass {result.Pass} of the night, {result.Asked} asked, of which {result.Skipped} skipped and "
            + "asked again tomorrow");

        // Both units, because the ceiling's own unit is ambiguous and the two differ by the cost of
        // whichever endpoint a stage happens to use. Fifteen names is fifteen requests here and
        // fifteen calls, and the same fifteen against a bulk endpoint would be fifteen hundred.
        Console.WriteLine(
            $"{Name}: {result.Requests} request(s), {result.CallsUsed} call(s) against the ceiling at "
            + $"{EodhdClient.FundamentalsCost} per request");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<SectorResult> ResolveAsync(
        DateOnly asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "security");

        DateTimeOffset resolvedAt = run.StartedAt;

        // Which pass of the night this is. The slot runs the stage twice, and a night where the
        // first pass died early and the second asked for everything reads exactly like a quiet
        // night unless the passes are told apart.
        int pass = PassNumber(connection, run.StartedAt);
        IReadOnlyList<string> unresolved = Unresolved(connection, asOf, _options.SessionZone);

        int asked = 0;
        int resolved = 0;
        int nothing = 0;
        int skipped = 0;
        bool stoppedShort = false;

        foreach (string ticker in unresolved.Take(limit))
        {
            VendorResult<VendorFundamentals?> answer;

            try
            {
                answer = await _vendor.GetFundamentalsAsync(ticker, run, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is VendorException or JsonException or HttpRequestException)
            {
                // One name the vendor refused, answered unreadably, or could not be reached for.
                // Counted, named on stdout so the night's log carries it, and left unstamped so it
                // is asked again tomorrow: a refusal that happens once must not permanently record
                // a good name as one the vendor has nothing on.
                //
                // Narrow on purpose. Anything that is not the vendor answering badly still takes the
                // stage down, because a store that will not accept a write or a cancellation are not
                // conditions the next ticker would survive either.
                skipped++;
                run.CountSkipped();
                Console.WriteLine($"{Name}: skipped {ticker}, {e.Message}");
                continue;
            }

            if (answer.BudgetExhausted)
            {
                // The ceiling bound before the list ran out. A partial run, said to be partial: the
                // names not reached keep their null sector and are asked again tomorrow.
                stoppedShort = true;
                break;
            }

            asked++;
            VendorFundamentals? found = answer.Value;

            if (found is null)
            {
                nothing++;
            }
            else
            {
                resolved++;
            }

            Stamp(connection, ticker, found, resolvedAt);
        }

        // Partial rather than clean whenever the list was not finished, whether the ceiling stopped
        // it or a name did. A run that spent calls and left names unresolved is not a clean slot,
        // and rows_written cannot say so: this stage only issues UPDATE, so the delta is 0 on a
        // perfect run and 0 on the run that died after 149 calls on 2026-08-27.
        RunOutcome outcome = stoppedShort || skipped > 0 ? RunOutcome.Partial : RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        return new SectorResult(
            asOf, unresolved.Count, asked, resolved, nothing, resolved + nothing, skipped,
            asked + skipped, pass, summary.RowsWritten, summary.CallsUsed, outcome);
    }

    /// <summary>
    /// Which pass of the night this run is, counting the runs of this stage already begun today.
    ///
    /// The UTC date rather than the session date, because that is the day the call ceiling is
    /// counted over and a pass is an attempt against that budget.
    /// </summary>
    private static int PassNumber(SqliteConnection connection, DateTimeOffset startedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM run_log WHERE stage = @stage AND started_at LIKE @today";
        command.Parameters.AddWithValue("@stage", Name);
        command.Parameters.AddWithValue(
            "@today",
            DateOnly.FromDateTime(startedAt.UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "%");

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    /// <summary>
    /// Names that appeared on a scan tonight and have never been asked about.
    ///
    /// Keyed on `sector_resolved_at` rather than on `sector` being null, because a name the vendor
    /// has nothing on has a null sector for ever and would otherwise be re-asked every night.
    /// </summary>
    private static IReadOnlyList<string> Unresolved(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT h.ticker
              FROM scan_hit h
              JOIN security s ON s.ticker = h.ticker
             WHERE h.as_of = @as_of AND s.sector_resolved_at IS NULL
               AND (h.observed_at <= @observed_before OR (h.observed_at IS NULL AND h.as_of = @as_of))
             ORDER BY h.ticker
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }

    private static void Stamp(
        SqliteConnection connection,
        string ticker,
        VendorFundamentals? found,
        DateTimeOffset resolvedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE security
               SET sector = @sector,
                   industry = @industry,
                   market_cap = @market_cap,
                   sector_resolved_at = @resolved_at
             WHERE ticker = @ticker
            """;

        command.Parameters.AddWithValue("@sector", (object?)found?.Sector ?? DBNull.Value);
        command.Parameters.AddWithValue("@industry", (object?)found?.Industry ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@market_cap",
            found?.MarketCap is decimal cap ? StoreText.PriceToStorageText(cap) : DBNull.Value);
        command.Parameters.AddWithValue("@resolved_at", StoreText.TimestampToStorageText(resolvedAt));
        command.Parameters.AddWithValue("@ticker", ticker);

        command.ExecuteNonQuery();
    }
}

/// <summary>What one sector run resolved, and what the vendor had nothing on.</summary>
/// <summary>
/// What one sector run resolved, and what it did not.
///
/// <b>Three counts over one pool and they sum to it.</b> <c>Resolved</c>, <c>VendorHadNothing</c> and
/// the unstamped remainder partition <c>Unresolved</c>, so a reader can say how many names carry a
/// sector without inferring it from two figures that do not cover the third state. The line this
/// replaced reported 86 asked and 85 resolved and a reader concluded that all 234 of the night's
/// names carry a sector; at most 233 can, because a name the vendor holds nothing on is stamped and
/// has none.
///
/// <b><c>Requests</c> and <c>CallsUsed</c> are different units on purpose.</b> A request is one thing
/// asked of the vendor; a call is what the ceiling counts, which for this endpoint is one per request
/// and for a bulk endpoint is a hundred. Reporting one of them makes the other unrecoverable, and the
/// ceiling's own unit is the thing a reader most often gets wrong.
/// </summary>
public sealed record SectorResult(
    DateOnly AsOf,
    int Unresolved,
    int Asked,
    int Resolved,
    int VendorHadNothing,
    int Stamped,
    int Skipped,
    int Requests,
    int Pass,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
