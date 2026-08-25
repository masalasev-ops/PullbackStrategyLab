using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The per-ticker history refetch, and the way a corporate action is actually honoured.
///
/// It is a mode of <see cref="DailyBarIngestor"/> rather than a component of its own, because
/// SCHEMA declares one inserter of <c>daily_bar</c> and a second type issuing that statement
/// would be a second writer of the table however sensibly it were named. What lives here is the
/// selection and the reporting; the write is the ingestor's.
///
/// Two jobs, one mechanism. RUNBOOK's step 4 fetches every survivor's history once, and a
/// rebuild fetches the history of the tickers with a demand outstanding. Both are the same
/// request: the vendor returns the whole series adjusted as it adjusts it today, so the series
/// arrives on one basis and the mixed-scale problem is gone rather than patched.
///
/// Priced per ticker regardless of depth, which is why the depth is years rather than the
/// minimum that would converge.
/// </summary>
public sealed partial class DailyBarIngestor
{
    public const string BackfillName = "backfill";

    /// <summary>Every universe member. RUNBOOK's step 4, priced at one call per surviving name.</summary>
    public const string AllFlag = "--all";

    /// <summary>Only the tickers carrying a rebuild demand that is still open.</summary>
    public const string RebuildFlag = "--rebuild";

    /// <summary>
    /// Three years. The endpoint charges per ticker rather than per day, so depth is free, and
    /// RUNBOOK asks for two to three years: the first 150 sessions are warm-up because a 50-day
    /// exponential average needs roughly three times its period to converge, and the rest is
    /// what the calibration run counts over.
    /// </summary>
    public const int BackfillYears = 3;

    public async Task<int> RunBackfillAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = _clock.SessionDate(_clock.UtcNow, _options.SessionZone);
        string[] named = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

        BackfillSelection selection = args.Contains(AllFlag, StringComparer.Ordinal)
            ? BackfillSelection.EveryUniverseMember
            : args.Contains(RebuildFlag, StringComparer.Ordinal)
                ? BackfillSelection.TickersWithAnOpenDemand
                : BackfillSelection.Named;

        if (selection == BackfillSelection.Named && named.Length == 0)
        {
            Console.Error.WriteLine(
                $"{BackfillName}: name the tickers, or pass {AllFlag} for every universe member or {RebuildFlag} "
                + "for the ones carrying an open rebuild demand. There is no default, because the default would be "
                + "one call per name in the universe.");
            return 2;
        }

        BackfillResult result = await BackfillAsync(selection, named, asOf, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{BackfillName}: {result.Selected} ticker(s) selected, {result.Fetched} fetched, from {result.From:yyyy-MM-dd}");
        Console.WriteLine($"{BackfillName}: {result.BarsPublished} bars published, {result.Inserted} written, {result.Unchanged} already stored unchanged");
        Console.WriteLine($"{BackfillName}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");
        Console.WriteLine($"{BackfillName}: {(result.CountedAgainstCeiling ? "counted against" : "outside")} the daily ceiling");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<BackfillResult> BackfillAsync(
        BackfillSelection selection,
        IReadOnlyList<string> named,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(named);

        using SqliteConnection connection = _connections.OpenWrite();

        // The whole-universe run is RUNBOOK's step 4, a one-time operation rather than part of
        // the evening, so it is not charged against the guard the evening needs. The rebuild and
        // the named forms are nightly work and are charged like everything else.
        CallCounting counting = selection == BackfillSelection.EveryUniverseMember
            ? CallCounting.OutsideTheDailyCeiling
            : CallCounting.AgainstTheDailyCeiling;

        using RunScope run = _runLogger.Begin(connection, BackfillName, counting, "daily_bar", "history_refetch");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly from = asOf.AddYears(-BackfillYears);

        IReadOnlyList<string> tickers = selection switch
        {
            BackfillSelection.EveryUniverseMember => ReadUniverseList(connection),
            BackfillSelection.TickersWithAnOpenDemand => IndicatorRebuildReader.Open(connection, asOf)
                .Select(d => d.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            _ => named,
        };

        int fetched = 0;
        int published = 0;
        int inserted = 0;
        int unchanged = 0;
        bool stoppedShort = false;

        foreach (string ticker in tickers)
        {
            VendorResult<IReadOnlyList<VendorDailyBar>> history = await _vendor
                .GetDailyHistoryAsync(ticker, from, asOf, run, cancellationToken).ConfigureAwait(false);

            if (history.BudgetExhausted)
            {
                // Stop rather than overrun. Every ticker already fetched keeps its history: a
                // backfill is per ticker, so stopping between names leaves no name half done,
                // and the rerun picks up the rest.
                stoppedShort = true;
                break;
            }

            fetched++;
            IReadOnlyList<VendorDailyBar> bars = history.Require();
            published += bars.Count;

            // One transaction per ticker. The unit that has to be all or nothing is a name's
            // series, because a series written half on the new basis and half on the old is the
            // exact fault this whole path exists to remove.
            using SqliteTransaction transaction = connection.BeginTransaction();
            int writtenForTicker = 0;

            foreach (VendorDailyBar bar in bars)
            {
                StoredDailyBar? stored = DailyBarReader.Latest(connection, bar.Ticker, bar.BarDate, observedAt);

                if (stored is not null &&
                    stored.SameFigures(bar.Open, bar.High, bar.Low, bar.Close, bar.AdjustedClose, bar.Volume))
                {
                    unchanged++;
                    continue;
                }

                Insert(connection, transaction, bar, observedAt);
                writtenForTicker++;
            }

            inserted += writtenForTicker;

            // The refetch itself, recorded in the same transaction as what it wrote. It has to be
            // written even when nothing changed, because the fact the engine needs is that the
            // series was looked at, not that it moved.
            RecordRefetch(connection, transaction, ticker, observedAt, from, asOf, writtenForTicker);

            transaction.Commit();
        }

        RunOutcome outcome = stoppedShort ? RunOutcome.Partial : RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        return new BackfillResult(
            from, asOf, tickers.Count, fetched, published, inserted, unchanged,
            summary.RowsWritten, summary.CallsUsed, outcome,
            counting == CallCounting.AgainstTheDailyCeiling);
    }

    private static void RecordRefetch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateTimeOffset refetchedAt,
        DateOnly from,
        DateOnly to,
        int barsWritten)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and one row per ticker per refetch. Append-only like everything else it
        // sits beside: the question worth answering later is when each name's series was last put
        // on one basis, and an overwritten row cannot answer it.
        command.CommandText = """
            INSERT INTO history_refetch (ticker, refetched_at, from_date, to_date, bars_written)
            VALUES (@ticker, @refetched_at, @from_date, @to_date, @bars_written)
            ON CONFLICT (ticker, refetched_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@refetched_at", StoreText.TimestampToStorageText(refetchedAt));
        command.Parameters.AddWithValue("@from_date", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@to_date", StoreText.DateToStorageText(to));
        command.Parameters.AddWithValue("@bars_written", barsWritten);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadUniverseList(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM universe_member WHERE removed_on IS NULL ORDER BY ticker;";

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }
}

/// <summary>Which tickers a backfill run covers. There is no "everything by default".</summary>
public enum BackfillSelection
{
    /// <summary>The ones given on the command line.</summary>
    Named,

    /// <summary>Every current universe member. RUNBOOK's step 4, one call per name.</summary>
    EveryUniverseMember,

    /// <summary>Every ticker carrying a rebuild demand that is still open.</summary>
    TickersWithAnOpenDemand,
}

public sealed record BackfillResult(
    DateOnly From,
    DateOnly AsOf,
    int Selected,
    int Fetched,
    int BarsPublished,
    int Inserted,
    int Unchanged,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    bool CountedAgainstCeiling);
