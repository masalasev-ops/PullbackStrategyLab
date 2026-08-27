using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Builds the tradable list and snapshots who was listed each night.
///
/// The order is the point, and it is the order RUNBOOK's backfill states: the symbol list,
/// then bulk end-of-day over the screening window, then the floors, and only then does
/// anything cost one call per surviving name. Screening on cheap bulk data before fetching
/// per-ticker history is what keeps the whole thing inside the ceiling.
///
/// The snapshot is written every night without exception. It is the record that keeps replay
/// free of survivorship bias, and unlike everything else here it cannot be reconstructed
/// later: a delisted name is simply absent from tomorrow's symbol list.
/// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
/// </summary>
public sealed class UniverseBuilder
{
    public const string Name = "universe-build";

    /// <summary>
    /// How far back to walk looking for trading days. The window is twenty sessions and a
    /// month of calendar days comfortably contains that, holidays included. Walking further
    /// would cost a bulk request per extra day for nothing.
    /// </summary>
    private const int CalendarDaysSearched = 45;

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public UniverseBuilder(
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
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        UniverseBuildResult result = await BuildAsync(asOf, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}");
        Console.WriteLine($"{Name}: {result.ListedCommonStock} common stock listed, {result.Screened} screened over {result.SessionsScreened} sessions");
        Console.WriteLine($"{Name}: {result.Survivors} survivors, {result.Added} added, {result.Removed} removed");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public async Task<UniverseBuildResult> BuildAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "security", "universe_member", "universe_snapshot");

        string exchange = _options.Vendor.Exchange;
        UniverseOptions floors = _options.Universe;

        // 1. The symbol list. One request, and the only place the instrument type is available.
        VendorResult<IReadOnlyList<VendorSymbol>> symbols = await _vendor
            .GetExchangeSymbolListAsync(exchange, run, cancellationToken).ConfigureAwait(false);

        if (symbols.BudgetExhausted)
        {
            return StopShort(connection, run, asOf, 0, 0);
        }

        Dictionary<string, VendorSymbol> listed = symbols.Require()
            .Where(s => string.Equals(s.Type, floors.SecurityType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Ticker, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 2. Bulk end-of-day over the screening window, one request per market day. Priced per
        //    market day, so the window is twenty sessions rather than the history a per-ticker
        //    request would return for the same money.
        var closes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var dollarVolumes = new Dictionary<string, List<decimal>>(StringComparer.Ordinal);
        int sessionsScreened = 0;
        bool stoppedShort = false;

        DateOnly date = asOf;
        for (int searched = 0; searched < CalendarDaysSearched && sessionsScreened < floors.LiquidityWindowSessions; searched++, date = date.AddDays(-1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                // Skipped locally rather than asked for. A weekend request costs what a trading
                // day costs and returns nothing.
                continue;
            }

            VendorResult<IReadOnlyList<VendorDailyBar>> bars = await _vendor
                .GetBulkEndOfDayAsync(exchange, date, run, cancellationToken).ConfigureAwait(false);

            if (bars.BudgetExhausted)
            {
                stoppedShort = true;
                break;
            }

            IReadOnlyList<VendorDailyBar> day = bars.Require();
            if (day.Count == 0)
            {
                // A holiday. It cost a request either way, which is why the search window is
                // bounded rather than open-ended.
                continue;
            }

            sessionsScreened++;

            foreach (VendorDailyBar bar in day)
            {
                if (!listed.ContainsKey(bar.Ticker))
                {
                    continue;
                }

                // The most recent session sets the price the floor is applied to, and it is
                // reached first because the walk goes backwards.
                closes.TryAdd(bar.Ticker, bar.Close);

                if (!dollarVolumes.TryGetValue(bar.Ticker, out List<decimal>? series))
                {
                    series = [];
                    dollarVolumes[bar.Ticker] = series;
                }

                series.Add(bar.DollarVolume);
            }
        }

        if (stoppedShort || sessionsScreened < floors.LiquidityWindowSessions)
        {
            // A median over five sessions is not the floor this lab screens on, and a universe
            // built from one would be a different tradable set with nothing on the surface to
            // show it. Membership is left as it was, and tonight's snapshot is written from it.
            return StopShort(connection, run, asOf, listed.Count, sessionsScreened);
        }

        // 3. The floors. Measured, never estimated: the survivor count is the one number the
        //    backfill's own cost depends on.
        var survivors = new List<string>();
        int screened = 0;

        foreach ((string ticker, decimal close) in closes)
        {
            screened++;

            if (close < floors.PriceFloor)
            {
                continue;
            }

            List<decimal> series = dollarVolumes[ticker];

            // A name that did not trade on most of the window has no median worth taking. It
            // fails the liquidity floor rather than being given the benefit of a short series.
            if (series.Count < sessionsScreened)
            {
                continue;
            }

            if (Median(series) < floors.LiquidityFloorLong)
            {
                continue;
            }

            survivors.Add(ticker);
        }

        survivors.Sort(StringComparer.Ordinal);

        // 4. Write. security first, because the other two reference it.
        int added = 0;
        int removed = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string ticker in survivors)
            {
                UpsertSecurity(connection, transaction, listed[ticker], asOf);
            }

            added = AddMembers(connection, transaction, survivors, asOf);
            removed = RemoveDepartedMembers(connection, transaction, survivors, asOf);
            WriteSnapshot(connection, transaction, survivors, asOf, sessionsScreened);

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new UniverseBuildResult(
            asOf,
            listed.Count,
            screened,
            sessionsScreened,
            survivors.Count,
            added,
            removed,
            summary.RowsWritten,
            summary.CallsUsed,
            RunOutcome.Clean);
    }

    /// <summary>
    /// A run that could not screen the whole window. Membership is left exactly as the last
    /// complete screen set it, because a universe rebuilt from a truncated window is a
    /// different tradable set and nothing on the surface would show it.
    ///
    /// The snapshot is still written, from the membership that stands. It is written every
    /// night without exception: a missing night cannot be reconstructed, and this is the one
    /// record that has to survive a degraded run.
    /// </summary>
    private static UniverseBuildResult StopShort(SqliteConnection connection, RunScope run, DateOnly asOf, int listed, int sessions)
    {
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            // Carried, and recorded as carried. A night that cannot screen writes the membership
            // that stands rather than writing nothing, because membership drifts by a handful a
            // month while a skipped night removes a whole session from the series. What it must
            // not do is look like a screened night: `screen_carried` is the column that stops a
            // later count reading this as fresh, and `screened_over_sessions` says how little the
            // screen could actually see.
            command.CommandText = """
                INSERT INTO universe_snapshot (as_of, ticker, screened_over_sessions, screen_carried)
                SELECT @as_of, ticker, @sessions, 1 FROM universe_member WHERE removed_on IS NULL
                ON CONFLICT (as_of, ticker) DO NOTHING;
                """;
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@sessions", sessions);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Partial);
        return new UniverseBuildResult(
            asOf, listed, 0, sessions, 0, 0, 0, summary.RowsWritten, summary.CallsUsed, RunOutcome.Partial);
    }

    private static void UpsertSecurity(SqliteConnection connection, SqliteTransaction transaction, VendorSymbol symbol, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. SCHEMA declares UniverseBuilder as the inserter of security and
        // SectorResolver as its only updater, on four named columns, so an upsert here would
        // give this component an undeclared update on a table somebody else owns. The row is
        // one per listed instrument ever, and first_seen is the date it was first observed.
        command.CommandText = """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @name, @exchange, @type, @first_seen)
            ON CONFLICT (ticker) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", symbol.Ticker);
        command.Parameters.AddWithValue("@name", symbol.Name);
        command.Parameters.AddWithValue("@exchange", symbol.Exchange);
        command.Parameters.AddWithValue("@type", symbol.Type);
        command.Parameters.AddWithValue("@first_seen", StoreText.DateToStorageText(asOf));
        command.ExecuteNonQuery();
    }

    private static int AddMembers(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> survivors, DateOnly asOf)
    {
        int added = 0;

        foreach (string ticker in survivors)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            // Membership is state. A name that left and came back clears its removed_on rather
            // than gaining a second row, so there is one row per ticker and the grain holds.
            command.CommandText = """
                INSERT INTO universe_member (ticker, added_on, removed_on)
                VALUES (@ticker, @as_of, NULL)
                ON CONFLICT (ticker) DO UPDATE SET
                    added_on = CASE WHEN universe_member.removed_on IS NULL THEN universe_member.added_on ELSE @as_of END,
                    removed_on = NULL;
                """;
            command.Parameters.AddWithValue("@ticker", ticker);
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            added += command.ExecuteNonQuery();
        }

        return added;
    }

    private static int RemoveDepartedMembers(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> survivors, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // The row stays and gains a date. A setup recorded while the name was a member still
        // resolves, which is the difference between membership as state and membership as a filter.
        command.CommandText = $"""
            UPDATE universe_member
               SET removed_on = @as_of
             WHERE removed_on IS NULL
               AND ticker NOT IN ({Placeholders(survivors.Count)});
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        for (int i = 0; i < survivors.Count; i++)
        {
            command.Parameters.AddWithValue($"@t{i}", survivors[i]);
        }

        return command.ExecuteNonQuery();
    }

    private static void WriteSnapshot(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> survivors,
        DateOnly asOf, int sessions)
    {
        foreach (string ticker in survivors)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            // Append-only, and idempotent for its date: rerunning a night rewrites the same
            // rows rather than doubling them or failing.
            command.CommandText = """
                INSERT INTO universe_snapshot (as_of, ticker, screened_over_sessions, screen_carried)
                VALUES (@as_of, @ticker, @sessions, 0)
                ON CONFLICT (as_of, ticker) DO NOTHING;
                """;
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@ticker", ticker);
            command.Parameters.AddWithValue("@sessions", sessions);
            command.ExecuteNonQuery();
        }
    }

    private static string Placeholders(int count) =>
        count == 0 ? "NULL" : string.Join(", ", Enumerable.Range(0, count).Select(i => $"@t{i}"));

    /// <summary>
    /// The median rather than the mean, because one earnings day at twenty times normal volume
    /// would carry a name over the floor it does not otherwise clear.
    ///
    /// The one in Core, not a second one here. This stood as a byte-for-byte copy of
    /// <see cref="Averages.Median"/> until the 1.12 review, which is the arrangement
    /// IndicatorEngine's own comment argues against three files away: the screen and the stored
    /// dollar-volume median are the same statistic, and computing it in two places is how they
    /// come to disagree without anything saying so.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public static decimal Median(IReadOnlyList<decimal> values) => Averages.Median(values);
}

public sealed record UniverseBuildResult(
    DateOnly AsOf,
    int ListedCommonStock,
    int Screened,
    int SessionsScreened,
    int Survivors,
    int Added,
    int Removed,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
