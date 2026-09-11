using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The plans a session was written with, and the run rows the stage left behind.
///
/// <b>Two bounds, and they are different questions.</b> The date names which plans are wanted, and
/// `observed_at` bounds what the lab could have known by the as-of. A plan is written on the evening
/// of N for session N+1, so there are two dates a caller could mean by the first and both are
/// stored: <see cref="ForLiveSession"/> answers "what was resting when this session opened", which is
/// what a resolver asks, and <see cref="WrittenOn"/> answers "what did that evening publish", which
/// is what the watchlist and a replay of an evening ask. Neither derives the other by stepping a
/// calendar.
///
/// <b>The stamp is bounded because something reads a plan to decide an answer.</b> A replay standing
/// at an old session that saw a plan written after it would resolve a fill the night itself could
/// not have. The plan is immutable and keyed on the setup, so there is one row per candidate and the
/// bound will rarely exclude anything; that is a fact about today's writer rather than a property of
/// the read, and a read that trusted it would stop being point-in-time the day a backfill existed.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Two shapes of a plan from 7.8, and a caller says which it wants.</b> A plan carrying the entry rule
/// names no price until its entry minute, so what the evening committed to and what the session
/// executed are different rows. <see cref="StoredTradePlan"/> is the executed shape, every figure
/// present: an evening's plan as it was written, or a rule plan joined to its entry resolution, which is
/// what every stage downstream of the entry reads. <see cref="CommittedTradePlan"/> is the plan as the
/// evening wrote it, prices absent on a rule plan, which is what the resolver, the sizer and a surface
/// publishing the evening read. A rule plan whose entry was refused, or has not been resolved, has no
/// executed shape and is not returned as one.
/// see: Order prices and the share count resolve at the entry minute
/// </summary>
public sealed class TradePlanReader
{
    private readonly StoreConnectionFactory _connections;

    public TradePlanReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The plans resting when <paramref name="liveSession"/> opened, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredTradePlan> ForLiveSession(DateOnly liveSession, DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ForLiveSession(connection, liveSession, asOf, sessionZone);
    }

    /// <summary>The plans written on the evening of <paramref name="writtenOn"/>, as that evening committed them.</summary>
    public IReadOnlyList<CommittedTradePlan> WrittenOn(DateOnly writtenOn, DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return WrittenOn(connection, writtenOn, asOf, sessionZone);
    }

    /// <summary>The plans resting in <paramref name="liveSession"/> in their executed shape: priced, sized, and only those that are.</summary>
    public static IReadOnlyList<StoredTradePlan> ForLiveSession(
        SqliteConnection connection, DateOnly liveSession, DateOnly asOf, string sessionZone) =>
        Read(connection, "live_session", liveSession, asOf, sessionZone);

    /// <summary>What the evening of <paramref name="writtenOn"/> committed to, rule plans included with no price.</summary>
    public static IReadOnlyList<CommittedTradePlan> WrittenOn(
        SqliteConnection connection, DateOnly writtenOn, DateOnly asOf, string sessionZone) =>
        ReadCommitted(connection, "as_of", writtenOn, asOf, sessionZone);

    /// <summary>Every plan resting in <paramref name="liveSession"/> as it was committed, which is what an entry is resolved from.</summary>
    public static IReadOnlyList<CommittedTradePlan> CommittedForLiveSession(
        SqliteConnection connection, DateOnly liveSession, DateOnly asOf, string sessionZone) =>
        ReadCommitted(connection, "live_session", liveSession, asOf, sessionZone);

    /// <summary>
    /// The plans behind a named set of setups, as at <paramref name="asOf"/>.
    ///
    /// <b>Here because a position outlives the session its plan was live in.</b> PaperBroker walks a
    /// session holding positions opened days earlier, and the give-up price those positions are
    /// measured against is their own plan's rather than anything the store copied forward. Reading
    /// by live session would return every plan except the ones it needs.
    ///
    /// One parameter slot per setup, on the shape <see cref="IntradayBarReader"/> already uses for a
    /// list of tickers: an interpolated <c>IN</c> list is the one place a reader could put an
    /// outside string into a statement, and there is no reason to have one.
    /// </summary>
    public static IReadOnlyList<StoredTradePlan> ForSetups(
        SqliteConnection connection, IReadOnlyCollection<string> setupIds, DateOnly asOf, string sessionZone)
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
            SELECT p.plan_id, p.variant_id, p.setup_id, p.as_of, p.live_session, p.ticker, p.direction,
                   COALESCE(p.trigger_price, r.entry_price), COALESCE(p.give_up_price, r.stop_price),
                   COALESCE(p.give_up_distance, r.stop_distance), COALESCE(p.shares, r.shares),
                   p.equity, p.risk_fraction, p.risk_budget, COALESCE(p.risk_at_stake, r.risk_at_stake),
                   p.observed_at, p.entry_rule
              FROM trade_plan p
              LEFT JOIN entry_resolution r
                ON r.plan_id = p.plan_id
               AND r.refused_because IS NULL
               AND r.observed_at <= @observed_before
             WHERE p.setup_id IN ({slots})
               AND p.observed_at <= @observed_before
               AND COALESCE(p.trigger_price, r.entry_price) IS NOT NULL
             ORDER BY p.direction, p.ticker
            """;

        int slot = 0;

        foreach (string setupId in setupIds)
        {
            command.Parameters.AddWithValue($"@setup{slot++}", setupId);
        }

        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        return Materialise(command);
    }

    /// <summary>
    /// One column or the other, chosen by comparing against a constant so nothing from outside
    /// reaches the statement. The same shape <see cref="SetupReader"/> uses to pick its table.
    /// </summary>
    private static IReadOnlyList<StoredTradePlan> Read(
        SqliteConnection connection, string column, DateOnly date, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string bounded = string.Equals(column, "live_session", StringComparison.Ordinal)
            ? "live_session"
            : "as_of";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT p.plan_id, p.variant_id, p.setup_id, p.as_of, p.live_session, p.ticker, p.direction,
                   COALESCE(p.trigger_price, r.entry_price), COALESCE(p.give_up_price, r.stop_price),
                   COALESCE(p.give_up_distance, r.stop_distance), COALESCE(p.shares, r.shares),
                   p.equity, p.risk_fraction, p.risk_budget, COALESCE(p.risk_at_stake, r.risk_at_stake),
                   p.observed_at, p.entry_rule
              FROM trade_plan p
              LEFT JOIN entry_resolution r
                ON r.plan_id = p.plan_id
               AND r.refused_because IS NULL
               AND r.observed_at <= @observed_before
             WHERE p.{bounded} = @date
               AND p.observed_at <= @observed_before
               AND COALESCE(p.trigger_price, r.entry_price) IS NOT NULL
             ORDER BY p.direction, p.ticker
            """;

        command.Parameters.AddWithValue("@date", StoreText.DateToStorageText(date));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        return Materialise(command);
    }

    /// <summary>The column order the three reads above share, materialised once.</summary>
    private static IReadOnlyList<StoredTradePlan> Materialise(SqliteCommand command)
    {
        var plans = new List<StoredTradePlan>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            plans.Add(new StoredTradePlan(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToDate(reader.GetString(3)),
                StoreText.StorageTextToDate(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                StoreText.StorageTextToPrice(reader.GetString(7)),
                StoreText.StorageTextToPrice(reader.GetString(8)),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                reader.GetInt32(10),
                StoreText.StorageTextToPrice(reader.GetString(11)),
                StoreText.StorageTextToRatio(reader.GetString(12)),
                StoreText.StorageTextToPrice(reader.GetString(13)),
                StoreText.StorageTextToPrice(reader.GetString(14)),
                StoreText.StorageTextToTimestamp(reader.GetString(15)))
            {
                EntryRule = reader.GetString(16),
            });
        }

        return plans;
    }

    /// <summary>The plans as committed, one column or the other chosen against a constant.</summary>
    private static IReadOnlyList<CommittedTradePlan> ReadCommitted(
        SqliteConnection connection, string column, DateOnly date, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        string bounded = string.Equals(column, "live_session", StringComparison.Ordinal)
            ? "live_session"
            : "as_of";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT plan_id, variant_id, setup_id, as_of, live_session, ticker, direction, entry_rule,
                   trigger_price, give_up_price, give_up_distance, shares, stop_ceiling,
                   equity, risk_fraction, risk_budget, risk_at_stake, observed_at
              FROM trade_plan
             WHERE {bounded} = @date
               AND observed_at <= @observed_before
             ORDER BY direction, ticker
            """;

        command.Parameters.AddWithValue("@date", StoreText.DateToStorageText(date));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var plans = new List<CommittedTradePlan>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            plans.Add(new CommittedTradePlan(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToDate(reader.GetString(3)),
                StoreText.StorageTextToDate(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : StoreText.StorageTextToPrice(reader.GetString(8)),
                reader.IsDBNull(9) ? null : StoreText.StorageTextToPrice(reader.GetString(9)),
                reader.IsDBNull(10) ? null : StoreText.StorageTextToPrice(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.IsDBNull(12) ? null : StoreText.StorageTextToRatio(reader.GetString(12)),
                StoreText.StorageTextToPrice(reader.GetString(13)),
                StoreText.StorageTextToRatio(reader.GetString(14)),
                StoreText.StorageTextToPrice(reader.GetString(15)),
                reader.IsDBNull(16) ? null : StoreText.StorageTextToPrice(reader.GetString(16)),
                StoreText.StorageTextToTimestamp(reader.GetString(17))));
        }

        return plans;
    }

    /// <summary>
    /// The stage's own run rows for one session, most recent first.
    ///
    /// Unbounded, and `plan_run` is exempted by name for it: the row says when one evening's plan
    /// stage ran and what it refused, which is operational on the same terms as `vwap_run` and
    /// `intraday_fetch`. Nothing computes a figure about the market from it, and the plans it counts
    /// are in `trade_plan`, which is stamped and bounded.
    /// </summary>
    public static IReadOnlyList<StoredPlanRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, live_session, candidates, planned,
                   refused_absent_geometry, refused_equal_prices, refused_below_one_share,
                   outcome, stopped_because, observed_at
              FROM plan_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredPlanRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredPlanRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToDate(reader.GetString(1)),
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
}

/// <summary>
/// One plan in its executed shape: an evening's plan as written, or a rule plan with the figures its entry
/// minute resolved. <see cref="TriggerPrice"/> is the price the order rests at and
/// <see cref="GiveUpPrice"/> the one stop the trade has, whichever of the two produced them.
/// </summary>
public sealed record StoredTradePlan(
    string PlanId,
    string VariantId,
    string SetupId,
    DateOnly AsOf,
    DateOnly LiveSession,
    string Ticker,
    string Direction,
    decimal TriggerPrice,
    decimal GiveUpPrice,
    decimal GiveUpDistance,
    int Shares,
    decimal Equity,
    decimal RiskFraction,
    decimal RiskBudget,
    decimal RiskAtStake,
    DateTimeOffset ObservedAt)
{
    /// <summary>Which rule the plan carried: the evening's prices, or the flush and reclaim resolved at the entry minute.</summary>
    public string EntryRule { get; init; } = "evening-prices";
}

/// <summary>
/// One plan as the evening committed it. On a plan carrying the entry rule the prices, the size and the
/// risk at stake are absent, because they resolve at the entry minute, and <see cref="StopCeiling"/> is
/// the widest stop the entry may take.
/// </summary>
public sealed record CommittedTradePlan(
    string PlanId,
    string VariantId,
    string SetupId,
    DateOnly AsOf,
    DateOnly LiveSession,
    string Ticker,
    string Direction,
    string EntryRule,
    decimal? TriggerPrice,
    decimal? GiveUpPrice,
    decimal? GiveUpDistance,
    int? Shares,
    decimal? StopCeiling,
    decimal Equity,
    decimal RiskFraction,
    decimal RiskBudget,
    decimal? RiskAtStake,
    DateTimeOffset ObservedAt)
{
    /// <summary>Whether the plan carries the rule rather than the evening's prices.</summary>
    public bool CarriesTheRule => StopCeiling is not null;
}

/// <summary>One run of the plan stage, with its refusals broken out by reason.</summary>
public sealed record StoredPlanRun(
    DateOnly SessionDate,
    DateOnly LiveSession,
    int Candidates,
    int Planned,
    int RefusedAbsentGeometry,
    int RefusedEqualPrices,
    int RefusedBelowOneShare,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
