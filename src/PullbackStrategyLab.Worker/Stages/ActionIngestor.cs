using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Pulls the day's splits and, when asked for them, dividends, and records the rebuild a split
/// forces.
///
/// Splits matter more than they sound. The stored adjusted closes were adjusted as of the night
/// each was observed, so the evening after a four-for-one, everything already in the store is on
/// the old scale and everything arriving is on the new one. An average taken across that
/// boundary is arithmetic on two different units, and the number it produces is wrong by a
/// factor while looking entirely reasonable. Nothing downstream can detect it.
///
/// So this stage does not fix anything. It records that a fix is owed, one row per split, and
/// the rebuild demand is what makes calculations for that stock refuse to run until it is
/// honoured. Demanding the rebuild and performing it are deliberately different components:
/// IndicatorEngine at 1.6 stamps rebuilt_at, and until then the demand simply stands.
/// </summary>
public sealed class ActionIngestor
{
    public const string Name = "actions";

    /// <summary>
    /// Asks for dividends as well. Off by default because the dividend pull is weekly rather
    /// than nightly, and the schedule is what makes it weekly: the flag lives in one launchd
    /// plist and one scheduled task rather than in a date calculation here.
    /// </summary>
    public const string WithDividendsFlag = "--with-dividends";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ActionIngestor(
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
        bool withDividends = args.Contains(WithDividendsFlag, StringComparer.Ordinal);

        DateOnly effectiveDate = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        ActionIngestResult result = await IngestAsync(effectiveDate, withDividends, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: {effectiveDate:yyyy-MM-dd}, {result.SplitsPublished} splits and {result.DividendsPublished} dividends published for the market");
        Console.WriteLine($"{Name}: {result.InUniverse} in the universe, {result.Inserted} written, {result.AlreadyStored} already stored");
        Console.WriteLine($"{Name}: {result.RebuildsDemanded} rebuild(s) demanded, {result.RatioConflicts} ratio conflict(s)");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<ActionIngestResult> IngestAsync(
        DateOnly effectiveDate,
        bool withDividends = false,
        CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "corporate_action", "indicator_rebuild");

        DateTimeOffset observedAt = run.StartedAt;
        string exchange = _options.Vendor.Exchange;

        VendorResult<IReadOnlyList<VendorCorporateAction>> splits = await _vendor
            .GetBulkSplitsAsync(exchange, effectiveDate, run, cancellationToken).ConfigureAwait(false);

        if (splits.BudgetExhausted)
        {
            // Nothing is stored and nothing is demanded. A split half-ingested is worse than one
            // not ingested at all: the second is a gap the rerun closes, and the first is a
            // store that believes it has tonight's splits.
            RunSummary stopped = run.Complete(RunOutcome.Partial);
            return ActionIngestResult.Stopped(effectiveDate, stopped);
        }

        IReadOnlyList<VendorCorporateAction> published = splits.Require();
        bool dividendsRequested = false;

        if (withDividends)
        {
            VendorResult<IReadOnlyList<VendorCorporateAction>> dividends = await _vendor
                .GetBulkDividendsAsync(exchange, effectiveDate, run, cancellationToken).ConfigureAwait(false);

            if (!dividends.BudgetExhausted)
            {
                dividendsRequested = true;
                published = [.. published, .. dividends.Require()];
            }
        }

        HashSet<string> universe = ReadUniverse(connection);
        IReadOnlyDictionary<string, StoredCorporateAction> alreadyStored =
            CorporateActionReader.ReadDate(connection, effectiveDate);

        int inUniverse = 0;
        int inserted = 0;
        int unchanged = 0;
        int ratioConflicts = 0;
        var rebuilds = new List<VendorCorporateAction>();

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (VendorCorporateAction action in published)
            {
                if (!universe.Contains(action.Ticker))
                {
                    // Actions are stored for the names the lab can trade, and the store's own
                    // foreign key would refuse the rest: a name never screened has no security row.
                    continue;
                }

                inUniverse++;

                if (alreadyStored.TryGetValue(CorporateActionReader.Key(action.Ticker, action.Type), out StoredCorporateAction? stored))
                {
                    unchanged++;

                    if (stored.Ratio != action.Ratio)
                    {
                        // The grain is one row per ticker, date and type, so the stored row stands
                        // and this is not a correction the table can absorb. Counted and printed
                        // rather than swallowed, because it means the factor that stock's history
                        // was rebuilt against may be the wrong one.
                        //
                        // If the rebuild has not been honoured yet, the demand is still standing
                        // and this changes nothing: the stock is already blocked. If it has, this
                        // stage cannot re-open it, because rebuilt_at belongs to IndicatorEngine
                        // and a stage that could clear another component's column would be the
                        // second writer the whole scheme exists to prevent. Reopening is owed at
                        // 1.6, where the component that owns the column exists.
                        ratioConflicts++;
                    }

                    continue;
                }

                Insert(connection, transaction, action, observedAt);
                inserted++;

                if (action.RescalesHistory)
                {
                    rebuilds.Add(action);
                }
            }

            foreach (VendorCorporateAction rebuild in rebuilds)
            {
                DemandRebuild(connection, transaction, rebuild, observedAt);
            }

            transaction.Commit();
        }

        // Partial when dividends were asked for and the ceiling refused them. The splits that
        // did land are stored, because they are the half that matters and the run entry says
        // the night was incomplete.
        RunOutcome outcome = withDividends && !dividendsRequested ? RunOutcome.Partial : RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        return new ActionIngestResult(
            effectiveDate,
            published.Count(a => a.Type == CorporateActionType.Split),
            published.Count(a => a.Type == CorporateActionType.Dividend),
            inUniverse,
            inserted,
            unchanged,
            ratioConflicts,
            rebuilds.Select(r => r.Ticker).Distinct(StringComparer.Ordinal).Count(),
            summary.RowsWritten,
            summary.CallsUsed,
            outcome);
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

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, VendorCorporateAction action, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // The grain is one row per ticker, date and type, so a rerun of the night finds the row
        // and does nothing rather than writing a second observation. Unlike a bar, an action is
        // an event rather than a measurement: the first observation of it is the one that stands.
        command.CommandText = """
            INSERT INTO corporate_action (ticker, effective_date, type, ratio, observed_at)
            VALUES (@ticker, @effective_date, @type, @ratio, @observed_at)
            ON CONFLICT (ticker, effective_date, type) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", action.Ticker);
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(action.EffectiveDate));
        command.Parameters.AddWithValue("@type", action.Type.ToStorageText());
        command.Parameters.AddWithValue("@ratio", StoreText.RatioToStorageText(action.Ratio));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void DemandRebuild(SqliteConnection connection, SqliteTransaction transaction, VendorCorporateAction action, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and never a clear. IndicatorEngine stamps rebuilt_at when it has recomputed
        // the ticker from scratch, and SCHEMA declares that update as its own. A demand this stage
        // could also close would put both halves of the handshake in one component, and a
        // component that can both raise and satisfy its own condition raises nothing.
        command.CommandText = """
            INSERT INTO indicator_rebuild (ticker, effective_date, requested_at, rebuilt_at)
            VALUES (@ticker, @effective_date, @requested_at, NULL)
            ON CONFLICT (ticker, effective_date) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", action.Ticker);
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(action.EffectiveDate));
        command.Parameters.AddWithValue("@requested_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

public sealed record ActionIngestResult(
    DateOnly EffectiveDate,
    int SplitsPublished,
    int DividendsPublished,
    int InUniverse,
    int Inserted,
    int AlreadyStored,
    int RatioConflicts,
    int RebuildsDemanded,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome)
{
    public static ActionIngestResult Stopped(DateOnly effectiveDate, RunSummary summary) =>
        new(effectiveDate, 0, 0, 0, 0, 0, 0, 0, summary.RowsWritten, summary.CallsUsed, RunOutcome.Partial);
}
