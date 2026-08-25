using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Pulls the whole market's closing prices in one bulk request and stores the bars for the
/// names in the universe.
///
/// Append-only, without exception. A vendor correction arrives as a new row with a later
/// observed_at rather than as an edit, so a replay of last Monday sees what the lab actually
/// saw on Monday, including the figure that turned out to be wrong. Editing the row instead
/// would rewrite history in a way nothing afterwards could detect.
///
/// Idempotent for its date, which is the done condition and is not the same as append-only.
/// Re-running writes a row only where the vendor's figures differ from the latest stored
/// observation, so a rerun after a failed stage costs nothing and changes nothing.
/// </summary>
public sealed class DailyBarIngestor
{
    public const string Name = "daily-bars";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public DailyBarIngestor(
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

        DateOnly barDate = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        DailyBarIngestResult result = await IngestAsync(barDate, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: {barDate:yyyy-MM-dd}, {result.Published} published for the market, {result.InUniverse} in the universe");
        Console.WriteLine($"{Name}: {result.Inserted} written, {result.Unchanged} already stored unchanged, {result.Corrections} corrections");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<DailyBarIngestResult> IngestAsync(DateOnly barDate, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "daily_bar");

        // One observed_at for the whole run, so a night's ingest is one observation rather than
        // several thousand instants that happen to be close together.
        DateTimeOffset observedAt = run.StartedAt;

        VendorResult<IReadOnlyList<VendorDailyBar>> published = await _vendor
            .GetBulkEndOfDayAsync(_options.Vendor.Exchange, barDate, run, cancellationToken).ConfigureAwait(false);

        if (published.BudgetExhausted)
        {
            RunSummary stopped = run.Complete(RunOutcome.Partial);
            return new DailyBarIngestResult(barDate, 0, 0, 0, 0, 0, stopped.RowsWritten, stopped.CallsUsed, RunOutcome.Partial);
        }

        IReadOnlyList<VendorDailyBar> market = published.Require();
        HashSet<string> universe = ReadUniverse(connection);
        // Bounded by this run's own instant, not by the bar date. A date being backfilled was
        // observed today, so a bound of the bar date would find nothing and every rerun would
        // write the same figures again under a new observation.
        IReadOnlyDictionary<string, StoredDailyBar> alreadyStored = DailyBarReader.ReadDate(connection, barDate, observedAt);

        int inUniverse = 0;
        int inserted = 0;
        int unchanged = 0;
        int corrections = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (VendorDailyBar bar in market)
            {
                if (!universe.Contains(bar.Ticker))
                {
                    // Bars are stored for the names the lab can trade. A name joining the
                    // universe later gets its history from the per-ticker endpoint, which is
                    // priced per ticker and returns the whole of it for one call.
                    continue;
                }

                inUniverse++;

                alreadyStored.TryGetValue(bar.Ticker, out StoredDailyBar? stored);

                if (stored is not null &&
                    stored.SameFigures(bar.Open, bar.High, bar.Low, bar.Close, bar.AdjustedClose, bar.Volume))
                {
                    unchanged++;
                    continue;
                }

                if (stored is not null)
                {
                    corrections++;
                }

                Insert(connection, transaction, bar, observedAt);
                inserted++;
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new DailyBarIngestResult(
            barDate, market.Count, inUniverse, inserted, unchanged, corrections,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    private static HashSet<string> ReadUniverse(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM universe_member WHERE removed_on IS NULL;";

        var universe = new HashSet<string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            universe.Add(reader.GetString(0));
        }

        return universe;
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, VendorDailyBar bar, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. There is no update and no delete against this table anywhere in the
        // lab, and a named check greps the source to keep it that way.
        command.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @open, @high, @low, @close, @adj_close, @volume, @observed_at)
            ON CONFLICT (ticker, bar_date, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", bar.Ticker);
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

public sealed record DailyBarIngestResult(
    DateOnly BarDate,
    int Published,
    int InUniverse,
    int Inserted,
    int Unchanged,
    int Corrections,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
