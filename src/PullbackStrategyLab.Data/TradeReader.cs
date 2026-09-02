using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The closed trades, the audits beside them, and the two stages' own run rows.
///
/// <b>One stamp each and both bounded, because neither table is ever updated.</b> A trade is written
/// when a position closes and an audit when a trade exists, and nothing revisits either: a
/// correction to a trade would be a second answer to what a night produced, which is what the
/// append-only records in this store refuse everywhere else. So the two stamps here are simpler than
/// <c>position</c>'s three and are bounded on exactly the same terms.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>The trade is read to decide an answer, which is what puts it on the bounded list.</b>
/// LossClassifier at 4.10 classifies a closed loss and the scoreboard scores what closed; a replay
/// standing at an old date that saw a trade written after it would classify a loss the night could
/// not have had.
/// </summary>
public sealed class TradeReader
{
    private const string TradeColumns = """
        trade_id, position_id, setup_id, ticker, direction, opened_session, closed_session,
        held_calendar_days, held_sessions, entry_price, exit_price, exit_reason, shares,
        trimmed_shares, value_at_entry, risk_realised, gross_pnl, borrow_rate_assumed, borrow_cost,
        net_pnl, result_r, exit_armed_session, armed_sessions_waited, observed_at
        """;

    private const string AuditColumns = """
        trade_id, setup_id, ticker, direction, planned_trigger, executed_entry, entry_difference,
        entry_difference_bps, entry_basis, exit_resting_price, executed_exit, exit_difference,
        exit_difference_bps, exit_basis, exit_reason, planned_give_up, give_up_difference,
        give_up_difference_bps, planned_shares, executed_shares, shares_difference, reduced_because,
        risk_intended, risk_realised, risk_difference, observed_at
        """;

    private readonly StoreConnectionFactory _connections;

    public TradeReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The trades closed in <paramref name="session"/>, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredTrade> ClosedIn(DateOnly session, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ClosedIn(connection, session, asOf);
    }

    /// <summary>The same read from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredTrade> ClosedIn(
        SqliteConnection connection, DateOnly session, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TradeColumns}
              FROM trade
             WHERE closed_session = @session
               AND observed_at <= @observed_before
             ORDER BY direction, ticker
            """;

        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        Bound(command, asOf);

        return MaterialiseTrades(command);
    }

    /// <summary>
    /// Every trade the lab has closed, as at <paramref name="asOf"/>, most recent first.
    ///
    /// The journal page's read at 4.11 and the scoreboard's from phase 5. Ordered by the session the
    /// trade ended in rather than the one it opened in, because the page is about what has happened.
    /// </summary>
    public static IReadOnlyList<StoredTrade> AllClosed(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TradeColumns}
              FROM trade
             WHERE observed_at <= @observed_before
             ORDER BY closed_session DESC, direction, ticker
            """;

        Bound(command, asOf);

        return MaterialiseTrades(command);
    }

    /// <summary>The audits of a named set of trades, as at <paramref name="asOf"/>.</summary>
    public static IReadOnlyList<StoredPlanAudit> AuditsOf(
        SqliteConnection connection, IReadOnlyCollection<string> tradeIds, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tradeIds);

        if (tradeIds.Count == 0)
        {
            return [];
        }

        string slots = string.Join(", ", tradeIds.Select((_, at) => $"@trade{at}"));

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AuditColumns}
              FROM plan_audit
             WHERE trade_id IN ({slots})
               AND observed_at <= @observed_before
             ORDER BY direction, ticker
            """;

        int slot = 0;

        foreach (string tradeId in tradeIds)
        {
            command.Parameters.AddWithValue($"@trade{slot++}", tradeId);
        }

        Bound(command, asOf);

        var audits = new List<StoredPlanAudit>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            audits.Add(new StoredPlanAudit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                StoreText.StorageTextToPrice(reader.GetString(4)),
                StoreText.StorageTextToPrice(reader.GetString(5)),
                StoreText.StorageTextToPrice(reader.GetString(6)),
                reader.GetDouble(7),
                reader.GetString(8),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                StoreText.StorageTextToPrice(reader.GetString(10)),
                StoreText.StorageTextToPrice(reader.GetString(11)),
                reader.GetDouble(12),
                reader.GetString(13),
                reader.GetString(14),
                StoreText.StorageTextToPrice(reader.GetString(15)),
                StoreText.StorageTextToPrice(reader.GetString(16)),
                reader.GetDouble(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetInt32(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                StoreText.StorageTextToPrice(reader.GetString(22)),
                StoreText.StorageTextToPrice(reader.GetString(23)),
                StoreText.StorageTextToPrice(reader.GetString(24)),
                StoreText.StorageTextToTimestamp(reader.GetString(25))));
        }

        return audits;
    }

    /// <summary>
    /// TradeJournal's own run rows for one session, most recent first.
    ///
    /// Unbounded, and <c>trade_run</c> is exempted by name on the terms <c>manage_run</c> and
    /// <c>fill_run</c> already carry: it says when the stage ran and what it journalled, which is
    /// operational. The trades it counts are in <c>trade</c>, which is stamped and bounded.
    /// </summary>
    public static IReadOnlyList<StoredTradeRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, closed_in_session, journalled, longs, shorts, shorts_charged,
                   trimmed, armed_exits, outcome, stopped_because, observed_at
              FROM trade_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredTradeRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredTradeRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                StoreText.StorageTextToTimestamp(reader.GetString(10))));
        }

        return runs;
    }

    /// <summary>PlanAudit's own run rows for one session, on the same terms.</summary>
    public static IReadOnlyList<StoredAuditRun> AuditRunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, trades_read, audited, longs, shorts, reduced_by_a_cap,
                   gapped_at_an_end, outcome, stopped_because, observed_at
              FROM audit_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredAuditRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredAuditRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                StoreText.StorageTextToTimestamp(reader.GetString(9))));
        }

        return runs;
    }

    private static IReadOnlyList<StoredTrade> MaterialiseTrades(SqliteCommand command)
    {
        var trades = new List<StoredTrade>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            trades.Add(new StoredTrade(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                StoreText.StorageTextToDate(reader.GetString(5)),
                StoreText.StorageTextToDate(reader.GetString(6)),
                reader.GetInt32(7),
                reader.GetInt32(8),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                StoreText.StorageTextToPrice(reader.GetString(10)),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                StoreText.StorageTextToPrice(reader.GetString(14)),
                StoreText.StorageTextToPrice(reader.GetString(15)),
                StoreText.StorageTextToPrice(reader.GetString(16)),
                reader.IsDBNull(17) ? null : StoreText.StorageTextToPrice(reader.GetString(17)),
                reader.IsDBNull(18) ? null : StoreText.StorageTextToPrice(reader.GetString(18)),
                StoreText.StorageTextToPrice(reader.GetString(19)),
                reader.GetDouble(20),
                reader.IsDBNull(21) ? null : StoreText.StorageTextToDate(reader.GetString(21)),
                reader.IsDBNull(22) ? null : reader.GetInt32(22),
                StoreText.StorageTextToTimestamp(reader.GetString(23))));
        }

        return trades;
    }

    private static void Bound(SqliteCommand command, DateOnly asOf) =>
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));
}

/// <summary>
/// One closed trade as the store holds it.
///
/// <see cref="ResultR"/> is after borrow and <c>position.realised_r</c> is before it. They are equal
/// on every long and differ by the borrow line on every short, and both names stay because one name
/// over two numbers is the fault this corpus keeps finding.
/// </summary>
public sealed record StoredTrade(
    string TradeId,
    string PositionId,
    string SetupId,
    string Ticker,
    string Direction,
    DateOnly OpenedSession,
    DateOnly ClosedSession,
    int HeldCalendarDays,
    int HeldSessions,
    decimal EntryPrice,
    decimal ExitPrice,
    string ExitReason,
    int Shares,
    int TrimmedShares,
    decimal ValueAtEntry,
    decimal RiskRealised,
    decimal GrossPnl,
    decimal? BorrowRateAssumed,
    decimal? BorrowCost,
    decimal NetPnl,
    double ResultR,
    DateOnly? ExitArmedSession,
    int? ArmedSessionsWaited,
    DateTimeOffset ObservedAt);

/// <summary>
/// One trade's plan held against what happened, in three pairs that answer three questions.
///
/// The first is execution at both ends, the second is the plan's stop against where the trade
/// actually ended, and the third is what the gate did to the size the plan carried.
/// </summary>
public sealed record StoredPlanAudit(
    string TradeId,
    string SetupId,
    string Ticker,
    string Direction,
    decimal PlannedTrigger,
    decimal ExecutedEntry,
    decimal EntryDifference,
    double EntryDifferenceBasisPoints,
    string EntryBasis,
    decimal ExitRestingPrice,
    decimal ExecutedExit,
    decimal ExitDifference,
    double ExitDifferenceBasisPoints,
    string ExitBasis,
    string ExitReason,
    decimal PlannedGiveUp,
    decimal GiveUpDifference,
    double GiveUpDifferenceBasisPoints,
    int PlannedShares,
    int ExecutedShares,
    int SharesDifference,
    string? ReducedBecause,
    decimal RiskIntended,
    decimal RiskRealised,
    decimal RiskDifference,
    DateTimeOffset ObservedAt);

/// <summary>One run of TradeJournal.</summary>
public sealed record StoredTradeRun(
    DateOnly SessionDate,
    int ClosedInSession,
    int Journalled,
    int Longs,
    int Shorts,
    int ShortsCharged,
    int Trimmed,
    int ArmedExits,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);

/// <summary>One run of PlanAudit.</summary>
public sealed record StoredAuditRun(
    DateOnly SessionDate,
    int TradesRead,
    int Audited,
    int Longs,
    int Shorts,
    int ReducedByACap,
    int GappedAtAnEnd,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
