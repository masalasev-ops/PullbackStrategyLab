using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Pulls the day's splits and dividends, and raises the rebuild every action that moves the
/// adjusted close demands.
///
/// The stored adjusted closes were adjusted as of the night each was observed, so the evening
/// after a four-for-one everything already in the store is on the old scale and everything
/// arriving is on the new one. An average taken across that boundary is arithmetic on two
/// different units, and the number it produces is wrong by a factor while looking entirely
/// reasonable. A dividend does the same thing by a smaller factor, and smaller is not a
/// category this design has.
/// see: An unprocessed corporate action of any kind blocks calculation, not only a split
///
/// So this stage does not fix anything. It records that a fix is owed, one demand per action
/// as that action was observed, and the demand is what makes calculations for that stock
/// refuse to run until it is satisfied. Raising the demand and satisfying it are deliberately
/// different components: IndicatorEngine stamps rebuilt_at once it has recomputed the ticker
/// against a history observed after the action.
///
/// Both tables are append-only. A vendor restating a ratio writes a new observation, which
/// raises a new demand rather than failing to reopen an old one.
/// see: A rebuild demand is keyed on the action as observed, and a restated action raises a new one
/// </summary>
public sealed class ActionIngestor
{
    public const string Name = "actions";

    /// <summary>
    /// Asks for splits and nothing else. For a rerun on an evening where the budget is short and
    /// the splits are the half that cannot wait; the stocks paying a dividend that evening then
    /// go unblocked, which is the cost and is why this is not the default.
    /// </summary>
    public const string SplitsOnlyFlag = "--splits-only";

    /// <summary>
    /// True, and load-bearing. The data budget states the dividend request as nightly and this is
    /// the code side of that claim, which `pinned-constants` compares.
    ///
    /// It was weekly, and weekly contradicted the rule it exists to serve. A dividend effective on
    /// a Tuesday went unobserved until the weekly run, so the stock computed for up to four
    /// sessions on an adjusted series that had already moved and nothing blocked it: exactly the
    /// plausible wrong number the whole rebuild path exists to prevent, arrived at by economising
    /// eighty calls a night on a budget that did not need economising.
    /// see: An unprocessed corporate action of any kind blocks calculation, not only a split
    /// </summary>
    public const bool RequestsDividendsByDefault = true;

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
        bool withDividends = RequestsDividendsByDefault && !args.Contains(SplitsOnlyFlag, StringComparer.Ordinal);

        DateOnly effectiveDate = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        ActionIngestResult result = await IngestAsync(effectiveDate, withDividends, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: {effectiveDate:yyyy-MM-dd}, {result.SplitsPublished} splits and {result.DividendsPublished} dividends published for the market");
        Console.WriteLine($"{Name}: {result.InUniverse} in the universe, {result.Inserted} written, {result.Unchanged} already stored unchanged, {result.Restatements} restatements");
        Console.WriteLine($"{Name}: {result.DemandsRaised} rebuild demand(s) raised over {result.TickersBlocked} ticker(s)");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<ActionIngestResult> IngestAsync(
        DateOnly effectiveDate,
        bool withDividends = RequestsDividendsByDefault,
        CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "corporate_action", "indicator_rebuild");

        // One observed_at for the whole run, so a night's ingest is one observation rather than
        // several hundred instants that happen to be close together. It is also the demand's
        // key, which is what ties a demand to the observation that raised it.
        DateTimeOffset observedAt = run.StartedAt;
        string exchange = _options.Vendor.Exchange;

        VendorResult<IReadOnlyList<VendorCorporateAction>> splits = await _vendor
            .GetBulkSplitsAsync(exchange, effectiveDate, run, cancellationToken).ConfigureAwait(false);

        if (splits.BudgetExhausted)
        {
            // Nothing is stored and nothing is demanded. A night half-ingested is worse than one
            // not ingested at all: the second is a gap the rerun closes, and the first is a
            // store that believes it has tonight's actions.
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
        IReadOnlyDictionary<string, StoredCorporateAction> lastObserved =
            CorporateActionReader.ReadDate(connection, effectiveDate, observedAt);

        int inUniverse = 0;
        int inserted = 0;
        int unchanged = 0;
        int restatements = 0;
        var demands = new List<VendorCorporateAction>();

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

                if (lastObserved.TryGetValue(CorporateActionReader.Key(action.Ticker, action.Type), out StoredCorporateAction? stored))
                {
                    if (stored.Ratio == action.Ratio)
                    {
                        unchanged++;
                        continue;
                    }

                    // The vendor has restated the action. It arrives as a new observation rather
                    // than as an edit, exactly as a corrected bar does, and it raises a demand of
                    // its own: whatever was computed against the old ratio was computed against a
                    // number the vendor no longer publishes.
                    restatements++;
                }

                Insert(connection, transaction, action, observedAt);
                inserted++;

                if (action.MovesAdjustedClose)
                {
                    demands.Add(action);
                }
            }

            foreach (VendorCorporateAction demand in demands)
            {
                RaiseDemand(connection, transaction, demand, observedAt);
            }

            transaction.Commit();
        }

        // Partial when dividends were asked for and the ceiling refused them. The splits that
        // did land are stored, because they are the half that cannot wait, and the run entry says
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
            restatements,
            demands.Count,
            demands.Select(d => d.Ticker).Distinct(StringComparer.Ordinal).Count(),
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

        // Insert only. There is no update and no delete against this table anywhere in the lab:
        // a restatement is a new observation, so the store still says what the lab believed on
        // the night it acted.
        command.CommandText = """
            INSERT INTO corporate_action (ticker, effective_date, type, ratio, observed_at)
            VALUES (@ticker, @effective_date, @type, @ratio, @observed_at)
            ON CONFLICT (ticker, effective_date, type, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", action.Ticker);
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(action.EffectiveDate));
        command.Parameters.AddWithValue("@type", action.Type.ToStorageText());
        command.Parameters.AddWithValue("@ratio", StoreText.RatioToStorageText(action.Ratio));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RaiseDemand(SqliteConnection connection, SqliteTransaction transaction, VendorCorporateAction action, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and never a clear. IndicatorEngine stamps rebuilt_at once it has
        // recomputed the ticker, and SCHEMA declares that update as its own. A stage that could
        // both raise and close a demand raises nothing, and the failure would be silent: created
        // and closed in the same pass, every check still green, no calculation ever blocked.
        command.CommandText = """
            INSERT INTO indicator_rebuild (ticker, effective_date, type, observed_at, rebuilt_at)
            VALUES (@ticker, @effective_date, @type, @observed_at, NULL)
            ON CONFLICT (ticker, effective_date, type, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", action.Ticker);
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(action.EffectiveDate));
        command.Parameters.AddWithValue("@type", action.Type.ToStorageText());
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

public sealed record ActionIngestResult(
    DateOnly EffectiveDate,
    int SplitsPublished,
    int DividendsPublished,
    int InUniverse,
    int Inserted,
    int Unchanged,
    int Restatements,
    int DemandsRaised,
    int TickersBlocked,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome)
{
    public static ActionIngestResult Stopped(DateOnly effectiveDate, RunSummary summary) =>
        new(effectiveDate, 0, 0, 0, 0, 0, 0, 0, 0, summary.RowsWritten, summary.CallsUsed, RunOutcome.Partial);
}
