using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The market indexes, which is what the regime label is read from at 2.5.
///
/// Three symbols on the per-ticker endpoint rather than one bulk request. The bulk endpoint
/// would carry all three inside the whole market's response, and it is priced per market day at
/// a hundred a time: asking it for three symbols would be a hundred calls to learn three
/// numbers. The per-ticker endpoint is one call a symbol whatever the depth, so the nightly
/// update and the backfill are the same request and the whole history arrives either way.
/// see: The vendor is EODHD, and the endpoint mix is what the call budget is built on
///
/// Append-only and idempotent for its window, on the same terms as the daily bars: a rerun
/// writes a row only where the vendor's figures differ from the latest stored observation.
/// </summary>
public sealed class IndexIngestor
{
    public const string Name = "index-bars";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public IndexIngestor(
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

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        IndexIngestResult result = await IngestAsync(asOf, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, from {result.From:yyyy-MM-dd}, {result.Symbols} symbol(s)");
        Console.WriteLine($"{Name}: {result.BarsPublished} bars published, {result.Inserted} written, {result.Unchanged} already stored unchanged");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<IndexIngestResult> IngestAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "index_bar");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly from = asOf.AddYears(-DailyBarIngestor.BackfillYears);

        int fetched = 0;
        int published = 0;
        int inserted = 0;
        int unchanged = 0;
        bool stoppedShort = false;

        foreach (string symbol in _options.IndexSymbols)
        {
            VendorResult<IReadOnlyList<VendorDailyBar>> history = await _vendor
                .GetDailyHistoryAsync(symbol, from, asOf, run, cancellationToken).ConfigureAwait(false);

            if (history.BudgetExhausted)
            {
                stoppedShort = true;
                break;
            }

            fetched++;
            IReadOnlyList<VendorDailyBar> bars = history.Require();
            published += bars.Count;

            using SqliteTransaction transaction = connection.BeginTransaction();

            foreach (VendorDailyBar bar in bars)
            {
                StoredDailyBar? stored = IndexBarReader.Latest(connection, symbol, bar.BarDate, observedAt);

                if (stored is not null &&
                    stored.SameFigures(bar.Open, bar.High, bar.Low, bar.Close, bar.AdjustedClose, bar.Volume))
                {
                    unchanged++;
                    continue;
                }

                Insert(connection, transaction, symbol, bar, observedAt);
                inserted++;
            }

            transaction.Commit();
        }

        RunOutcome outcome = stoppedShort ? RunOutcome.Partial : RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        return new IndexIngestResult(
            from, asOf, fetched, published, inserted, unchanged,
            summary.RowsWritten, summary.CallsUsed, outcome);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string symbol,
        VendorDailyBar bar,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. There is no update and no delete against this table anywhere in the lab,
        // and the same named check that watches the daily bars watches this one.
        command.CommandText = """
            INSERT INTO index_bar (symbol, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@symbol, @bar_date, @open, @high, @low, @close, @adj_close, @volume, @observed_at)
            ON CONFLICT (symbol, bar_date, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@symbol", symbol);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(bar.BarDate));
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(bar.Open));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(bar.High));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(bar.Low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(bar.Close));
        command.Parameters.AddWithValue("@adj_close", StoreText.PriceToStorageText(bar.AdjustedClose));
        command.Parameters.AddWithValue("@volume", bar.Volume);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

public sealed record IndexIngestResult(
    DateOnly From,
    DateOnly AsOf,
    int Symbols,
    int BarsPublished,
    int Inserted,
    int Unchanged,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
