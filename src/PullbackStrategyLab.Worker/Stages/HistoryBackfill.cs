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
    /// Every name the exchange has delisted, minus the ones a previous night already fetched.
    ///
    /// <b>It is charged against the daily ceiling on purpose, and that is what spreads it.</b>
    /// The whole-universe backfill is charged outside the ceiling because it is a one-time
    /// operation run in one sitting. This one cannot be: the list is several times the size of
    /// the universe, so it takes more than one night's spare, and a run that ignored the ceiling
    /// would spend the evening's own budget. Charged against it, the run takes whatever the
    /// night's stages left, stops on <c>BudgetExhausted</c> rather than overrunning, and the
    /// next night resumes where it stopped.
    ///
    /// <b>Resume needs no state of its own.</b> `history_refetch` already carries one row per
    /// ticker per refetch and is already written by this path, so a name is finished exactly when
    /// it has a row, including the names whose history came back empty. Storing a second list of
    /// what is done would be a copy that can disagree with the record it copies.
    /// see: Delisted daily history is bought so a reconstructed walk is not confined to survivors
    /// </summary>
    public const string DelistedFlag = "--delisted";

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
                : args.Contains(DelistedFlag, StringComparer.Ordinal)
                    ? BackfillSelection.DelistedNames
                    : BackfillSelection.Named;

        if (selection == BackfillSelection.Named && named.Length == 0)
        {
            Console.Error.WriteLine(
                $"{BackfillName}: name the tickers, or pass {AllFlag} for every universe member, {RebuildFlag} "
                + $"for the ones carrying an open rebuild demand, or {DelistedFlag} for the names the exchange has "
                + "removed. There is no default, because the default would be one call per name in the universe.");
            return 2;
        }

        BackfillResult result = await BackfillAsync(selection, named, asOf, cancellationToken).ConfigureAwait(false);

        if (selection == BackfillSelection.DelistedNames)
        {
            // The night's own bill, printed where the operator reads it. The run log carries the
            // same figures per run, which is what makes "spend per night" a record rather than a
            // recollection.
            Console.WriteLine($"{BackfillName}: {result.Candidates} delisted name(s) recorded, "
                + $"{result.AlreadyFetched} already fetched on an earlier night");
        }

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
        // The delisted run is charged against the ceiling although it is one-time work, because
        // it is one-time work that does not fit in one sitting: it takes what the evening left
        // and stops, which is what spreads it over nights instead of over the evening's budget.
        CallCounting counting = selection == BackfillSelection.EveryUniverseMember
            ? CallCounting.OutsideTheDailyCeiling
            : CallCounting.AgainstTheDailyCeiling;

        using RunScope run = _runLogger.Begin(connection, BackfillName, counting, "daily_bar", "history_refetch");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly from = asOf.AddYears(-BackfillYears);

        int candidates = 0;
        int alreadyFetched = 0;
        IReadOnlyList<string> tickers;

        if (selection == BackfillSelection.DelistedNames)
        {
            // Read from the store rather than from the vendor, and that is a safety property
            // rather than a saved call. `daily_bar` has a foreign key to `security`, so a name
            // this run has bars for and no security row for fails on every insert. Taking the
            // list from the store means the set this run can fetch is exactly the set the store
            // can hold: a night where `delisted-list` did not run finds nothing new and stops,
            // instead of spending its calls and storing none of what it bought.
            //
            // A delisted name is a security that has never been a universe member. The listed
            // path writes a security row only for a survivor and every survivor is offered
            // membership, so a member that departs keeps its row with `removed_on` set and is
            // excluded here, while a name only `delisted-list` ever saw has no membership row at
            // all. `A_name_the_universe_once_held_is_not_bought_as_a_delisted_one` is what holds
            // that, because it is a property of the other stage's writes and not of this one's.
            string[] wanted = ReadDelistedSecurities(connection, _options.Universe.SecurityType);
            candidates = wanted.Length;

            // The resume, read from the record the fetch itself writes rather than from a copy
            // of it. A name whose history came back empty still has a row, so it is not asked
            // for a second time on the strength of having produced nothing.
            HashSet<string> done = ReadRefetchedTickers(connection);
            alreadyFetched = wanted.Count(done.Contains);
            tickers = [.. wanted.Where(t => !done.Contains(t))];
        }
        else
        {
            tickers = selection switch
            {
                BackfillSelection.EveryUniverseMember => ReadUniverseList(connection),
                BackfillSelection.TickersWithAnOpenDemand => IndicatorRebuildReader.Open(connection, asOf)
                    .Select(d => d.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                _ => named,
            };
        }

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
            counting == CallCounting.AgainstTheDailyCeiling,
            candidates, alreadyFetched);
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

    /// <summary>
    /// Every ticker `history_refetch` has a row for, which is every ticker a backfill of any mode
    /// has finished. It is the delisted run's resume point and it is deliberately not bounded on
    /// a date: the question is whether this name's whole series has been taken, and taking it
    /// twice is a call spent on bars already stored.
    /// </summary>
    /// <summary>
    /// Every security the universe has never held, of the configured type. That is the delisted
    /// population as the store knows it, written by `delisted-list` and by nothing else.
    /// </summary>
    private static string[] ReadDelistedSecurities(SqliteConnection connection, string securityType)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.ticker
            FROM security s
            LEFT JOIN universe_member m ON m.ticker = s.ticker
            WHERE m.ticker IS NULL AND s.type = @type
            ORDER BY s.ticker;
            """;
        command.Parameters.AddWithValue("@type", securityType);

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return [.. tickers];
    }

    private static HashSet<string> ReadRefetchedTickers(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT ticker FROM history_refetch;";

        var tickers = new HashSet<string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
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

    /// <summary>
    /// Every name the exchange has delisted, minus the ones already fetched. One call each,
    /// charged against the ceiling, resumed across nights from `history_refetch`.
    /// </summary>
    DelistedNames,
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
    bool CountedAgainstCeiling,

    /// <summary>Delisted securities of the configured type. Nought in every other mode.</summary>
    int Candidates = 0,

    /// <summary>Of those, the ones an earlier night already fetched. Nought in every other mode.</summary>
    int AlreadyFetched = 0);
