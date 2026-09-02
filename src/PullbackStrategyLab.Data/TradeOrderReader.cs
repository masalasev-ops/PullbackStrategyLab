using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The orders a session produced, placed and blocked alike, and the run rows RiskGate left behind.
///
/// <b>Blocked orders are read on the same footing as placed ones.</b> They are evidence about the
/// caps rather than an absence of evidence, and a reader that returned only the placed ones would
/// make a night on which three setups triggered into a night on which one did.
///
/// <b>The stamp is bounded because an order is an observation about a session.</b> A replay standing
/// at an old date that saw an order written after it would report a position the night could not have
/// held.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class TradeOrderReader
{
    private readonly StoreConnectionFactory _connections;

    public TradeOrderReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The orders of <paramref name="liveSession"/>, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredTradeOrder> ForLiveSession(DateOnly liveSession, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ForLiveSession(connection, liveSession, asOf);
    }

    /// <summary>
    /// The same read from a connection the caller already holds, in the order the caps were applied.
    ///
    /// Earliest trigger first, ticker breaking a tie, which is the order the contention rule fills
    /// in and the order the rows were written in. A reader that sorted any other way would make the
    /// sequence of blocks unreadable, since each one is a fact about what was already open.
    /// see: Plans are resting orders and fills go in time order when the caps bind
    /// </summary>
    public static IReadOnlyList<StoredTradeOrder> ForLiveSession(
        SqliteConnection connection, DateOnly liveSession, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT order_id, setup_id, live_session, ticker, direction, triggered_at, status,
                   planned_shares, shares, risk_at_stake, bound_by, blocked_because, observed_at
              FROM trade_order
             WHERE live_session = @live_session
               AND observed_at <= @observed_before
             ORDER BY triggered_at, ticker
            """;

        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        return Materialise(command);
    }

    /// <summary>The column order the two reads above share, materialised once.</summary>
    private static IReadOnlyList<StoredTradeOrder> Materialise(SqliteCommand command)
    {
        var orders = new List<StoredTradeOrder>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            orders.Add(new StoredTradeOrder(
                reader.GetString(0),
                reader.GetString(1),
                StoreText.StorageTextToDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                StoreText.StorageTextToTimestamp(reader.GetString(5)),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                StoreText.StorageTextToTimestamp(reader.GetString(12))));
        }

        return orders;
    }

    /// <summary>
    /// The orders behind a named set of setups, as at <paramref name="asOf"/>.
    ///
    /// <b>Here because a trade outlives the session its order was placed in.</b> PlanAudit reads
    /// what the gate did to a size against what the plan carried, and the trade it is auditing
    /// closed days after the order was placed. Reading by live session would return every order
    /// except the ones it needs, which is the same shape <see cref="TradePlanReader.ForSetups"/>
    /// was added for one checkpoint earlier.
    /// </summary>
    public static IReadOnlyList<StoredTradeOrder> ForSetups(
        SqliteConnection connection, IReadOnlyCollection<string> setupIds, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(setupIds);

        if (setupIds.Count == 0)
        {
            return [];
        }

        string slots = string.Join(", ", setupIds.Select((_, at) => $"@setup{at}"));

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT order_id, setup_id, live_session, ticker, direction, triggered_at, status,
                   planned_shares, shares, risk_at_stake, bound_by, blocked_because, observed_at
              FROM trade_order
             WHERE setup_id IN ({slots})
               AND observed_at <= @observed_before
             ORDER BY triggered_at, ticker
            """;

        int slot = 0;

        foreach (string setupId in setupIds)
        {
            command.Parameters.AddWithValue($"@setup{slot++}", setupId);
        }

        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        return Materialise(command);
    }

    /// <summary>
    /// Every order in the store with the instant it was written, unbounded, for provenance alone.
    ///
    /// <b>Exempt from the as-of bound by name, and it is the one read here that is.</b>
    /// `order-provenance` asks whether a row exists that RiskGate did not write, which is a question
    /// about the whole store rather than about what a session could have known: bounding it would let
    /// a row written outside a run scope hide behind the bound, which is the fault it exists to find.
    /// It returns provenance and no prices, so nothing can compute a figure about the market from it.
    /// </summary>
    public static IReadOnlyList<OrderProvenance> ProvenanceOfEveryOrder(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT order_id, observed_at
              FROM trade_order
             ORDER BY observed_at, order_id
            """;

        var rows = new List<OrderProvenance>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new OrderProvenance(
                reader.GetString(0), StoreText.StorageTextToTimestamp(reader.GetString(1))));
        }

        return rows;
    }

    /// <summary>
    /// The stage's own run rows for one session, most recent first.
    ///
    /// Unbounded, and `order_run` is exempted by name on the terms `trigger_run`, `plan_run`,
    /// `vwap_run` and `intraday_fetch` already carry: it says when RiskGate ran and what it refused,
    /// which is operational. The orders it counts are in `trade_order`, which is stamped and bounded.
    /// </summary>
    public static IReadOnlyList<StoredOrderRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, triggers, placed, reduced, blocked,
                   blocked_open_positions, blocked_open_shorts,
                   reduced_position_size, reduced_total_risk, blocked_below_one_share,
                   outcome, stopped_because, observed_at
              FROM order_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredOrderRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredOrderRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                StoreText.StorageTextToTimestamp(reader.GetString(12))));
        }

        return runs;
    }
}

/// <summary>One order, placed or blocked, as the store holds it.</summary>
public sealed record StoredTradeOrder(
    string OrderId,
    string SetupId,
    DateOnly LiveSession,
    string Ticker,
    string Direction,
    DateTimeOffset TriggeredAt,
    string Status,
    int PlannedShares,
    int Shares,
    decimal RiskAtStake,
    string? BoundBy,
    string? BlockedBecause,
    DateTimeOffset ObservedAt);

/// <summary>An order's identity and the instant it was written, which is all provenance needs.</summary>
public sealed record OrderProvenance(string OrderId, DateTimeOffset ObservedAt);

/// <summary>One run of RiskGate, with its refusals and reductions counted by cap.</summary>
public sealed record StoredOrderRun(
    DateOnly SessionDate,
    int Triggers,
    int Placed,
    int Reduced,
    int Blocked,
    int BlockedOpenPositions,
    int BlockedOpenShorts,
    int ReducedPositionSize,
    int ReducedTotalRisk,
    int BlockedBelowOneShare,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
