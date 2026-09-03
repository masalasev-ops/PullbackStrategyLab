using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No order row exists whose writer was not RiskGate.
///
/// <b>Two halves, and the second is the point of this check existing at all.</b> The scan asserts
/// that one type in the shipped source issues a write against `trade_order` and that it is RiskGate.
/// The behavioural half runs the gate against an authored session and reads the rows back, asking of
/// each one whether it was written inside a RiskGate run: a row whose instant falls in no run of that
/// stage was written by something else, whatever the source says.
/// see: RiskGate is the sole writer of orders, for both directions and every version
///
/// <b>It is `writer-ownership`'s missing behavioural form, for orders alone.</b> That check attributes
/// every write in the shipped source to the type SCHEMA declares for it, and until 4.6 nothing
/// exercised the attribution: the 2.11 sweep listed it as one of three assertions no test backed, and
/// this is the one the corpus scheduled a component for. A scan cannot see a component that writes
/// through a path it does not recognise, and it cannot see a row that arrived some other way at all.
///
/// <b>The provenance question is asked of the whole store rather than as of a date.</b> A row written
/// outside a run scope would hide behind a point-in-time bound, which is the one fault this exists to
/// find, so <see cref="TradeOrderReader.ProvenanceOfEveryOrder"/> is exempt from that bound by name.
/// </summary>
public sealed class OrderProvenanceCheck : IDisposable
{
    /// <summary>The evening the plans were written on. A Tuesday, so the next weekday is the next day.</summary>
    private static readonly DateOnly Evening = new(2026, 8, 25);

    private static readonly DateOnly Session = new(2026, 8, 26);

    private readonly ITestOutputHelper _output;
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, new TimeOnly(21, 10), SessionBoundaries.UsEquities));

    public OrderProvenanceCheck(ITestOutputHelper output)
    {
        _output = output;
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        // A plan belongs to a version from 5.1 and the store's key says so, so the fixture
        // registers the baseline before anything writes a plan. The lab does not do this for
        // itself: registering a version is VariantAdmitter's, and a migration that seeded one
        // would start an experiment nobody chose to start.
        using (SqliteConnection seed = _connections.OpenWrite())
        {
            TestVersions.SeedBaseline(seed);
        }
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    [Trait("check", "order-provenance")]
    public void No_order_row_exists_whose_writer_was_not_the_risk_gate()
    {
        var coverage = new CheckCoverage("order-provenance", _output);
        var offenders = new List<string>();

        // 1. The source. Every write against the order tables, attributed to the type whose braces
        //    enclose it, which is what the 3.13 obligation repaired at this checkpoint.
        SourceWrite[] writes =
        [
            .. SourceWrites.InProductionSource.Where(
                w => string.Equals(w.Table, "trade_order", StringComparison.Ordinal)),
        ];

        foreach (SourceWrite write in writes.Where(w => !string.Equals(w.Type, nameof(RiskGate), StringComparison.Ordinal)))
        {
            offenders.Add(
                $"{write.File}:{write.Line} writes {write.Table} from {write.Type}, and RiskGate is the only "
                + "thing that may open a position.");
        }

        // 2. The behaviour. An authored session with more triggers than slots, so the gate places,
        //    reduces and blocks in one run, and every row it wrote is read back and asked who wrote
        //    it. The fixture cannot do this: it holds one market day with no plan resting in it.
        // see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
        OrderRunResult run = AuthoredSession();

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<OrderProvenance> orders = TradeOrderReader.ProvenanceOfEveryOrder(connection);
        IReadOnlyList<StageRunWindow> windows = RiskGateRuns(connection);

        foreach (OrderProvenance order in orders.Where(o => !WrittenByTheRiskGate(o, windows)))
        {
            offenders.Add(
                $"order {order.OrderId} was written at {order.ObservedAt:yyyy-MM-dd HH:mm:ss.fffK} and no run of "
                + $"{RiskGate.Name} was open at that instant, so something other than RiskGate wrote it.");
        }

        coverage
            .Examined("writes against the order tables in the shipped source", writes.Length)
            .Examined("order rows read back and asked who wrote them", orders.Count)
            .Examined("runs of the risk gate the store records", windows.Count)
            .Context("shipped source files read for store writes", SourceWrites.ProductionFilesRead)
            .Scan("every write against trade_order in the shipped source is RiskGate's",
                CheckCoverage.Backing.Test(
                    $"{nameof(OrderProvenanceCheck)}.{nameof(A_row_written_outside_a_run_of_the_gate_is_caught)}",
                    "the scan reads the source and cannot see a row that arrived some other way. The proof "
                    + "writes one directly into the store, outside any run scope, and requires the predicate to "
                    + "reject it"))
            .Report();

        // Stated in advance, on the rule a sweep expecting a non-zero count states that count. An
        // authored session of five triggers with four slots produces five rows, and a run that
        // produced none would pass every assertion above by having nothing to assert.
        Assert.Equal(5, run.Triggers);
        Assert.Equal(5, orders.Count);
        Assert.NotEmpty(windows);

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} order provenance failure(s):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The predicate, proved against a row nothing wrote through the gate.
    ///
    /// Permanent rather than a break-and-revert by hand: the row is inserted here, in a test, and the
    /// predicate has to reject it. An assertion must fail when the thing it guards is removed, and
    /// this is what removing it looks like.
    /// </summary>
    [Fact]
    public void A_row_written_outside_a_run_of_the_gate_is_caught()
    {
        AuthoredSession();

        using SqliteConnection write = _connections.OpenWrite();
        InsertOrderOutsideAnyRun(write, "smuggled", _clock.UtcNow.AddHours(3));

        IReadOnlyList<OrderProvenance> orders = TradeOrderReader.ProvenanceOfEveryOrder(write);
        IReadOnlyList<StageRunWindow> windows = RiskGateRuns(write);

        OrderProvenance smuggled = orders.Single(o => o.OrderId == "smuggled");

        Assert.False(WrittenByTheRiskGate(smuggled, windows),
            "a row written outside every run of the gate was accepted as the gate's, so this check would pass "
            + "with a second writer of orders in the source.");

        // And the rows the gate did write are still accepted, so the predicate is not simply refusing
        // everything. A guard that rejects its own subject is as dead as one that accepts anything.
        Assert.All(
            orders.Where(o => o.OrderId != "smuggled"),
            o => Assert.True(WrittenByTheRiskGate(o, windows)));
    }

    /// <summary>Whether an order was written while a run of the gate was open.</summary>
    private static bool WrittenByTheRiskGate(OrderProvenance order, IReadOnlyList<StageRunWindow> windows) =>
        windows.Any(w => order.ObservedAt >= w.StartedAt && (w.EndedAt is null || order.ObservedAt <= w.EndedAt));

    /// <summary>Every run of the gate the store records, as an instant range.</summary>
    private static IReadOnlyList<StageRunWindow> RiskGateRuns(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at, ended_at
              FROM run_log
             WHERE stage = @stage
             ORDER BY started_at
            """;
        command.Parameters.AddWithValue("@stage", RiskGate.Name);

        var windows = new List<StageRunWindow>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            windows.Add(new StageRunWindow(
                StoreText.StorageTextToTimestamp(reader.GetString(0)),
                reader.IsDBNull(1) ? null : StoreText.StorageTextToTimestamp(reader.GetString(1))));
        }

        return windows;
    }

    private sealed record StageRunWindow(DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

    /// <summary>
    /// Five plans, all touched, against four slots and a total-risk budget that binds.
    ///
    /// Authored so the run produces a placed order, a reduced one and a blocked one in a single
    /// pass: a store holding only placed rows would let a check that reads only placed rows pass.
    /// </summary>
    private OrderRunResult AuthoredSession()
    {
        string[] tickers = ["AAA", "BBB", "CCC", "DDD", "EEE"];

        for (int i = 0; i < tickers.Length; i++)
        {
            Plan(tickers[i], SetupDirection.Long, trigger: 50m, giveUp: 45m, touchedAt: new TimeOnly(9, 30 + i));
        }

        // A sixth plan that never triggered, so the gate writes no order for it. It exists for the
        // proof below: the order table's key refuses a second order for a plan, and its foreign key
        // refuses an order for a plan that does not exist, so a row smuggled past the gate needs a
        // plan of its own to hang off. A smuggled row that cannot be written proves nothing.
        Plan("FFF", SetupDirection.Long, trigger: 50m, giveUp: 45m, touchedAt: null);

        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new RiskGate(_connections, new RunLogger(_clock, options), _clock, options).Apply(Session);
    }

    private void Plan(string ticker, string direction, decimal trigger, decimal giveUp, TimeOnly? touchedAt)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        string setupId = $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";
        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Evening.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        using (SqliteCommand setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO setup
                    (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                     trigger_price, stop_price, stop_distance_ranges)
                VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @trigger, @stop, @ranges);
                """;
            setup.Parameters.AddWithValue("@id", setupId);
            setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
            setup.Parameters.AddWithValue("@ticker", ticker);
            setup.Parameters.AddWithValue("@direction", direction);
            setup.Parameters.AddWithValue("@trigger", StoreText.PriceToStorageText(trigger));
            setup.Parameters.AddWithValue("@stop", StoreText.PriceToStorageText(giveUp));
            setup.Parameters.AddWithValue("@ranges", StoreText.RatioToStorageText(0.30m));
            setup.ExecuteNonQuery();
        }

        using (SqliteCommand plan = connection.CreateCommand())
        {
            plan.CommandText = """
                INSERT INTO trade_plan (
                    plan_id, variant_id, setup_id, as_of, live_session, ticker, direction,
                    trigger_price, give_up_price, give_up_distance, shares,
                    equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
                VALUES (
                    @plan_id, @variant_id, @setup_id, @as_of, @live_session, @ticker, @direction,
                    @trigger, @give_up, @distance, @shares,
                    @equity, @fraction, @budget, @at_stake, @observed_at);
                """;
            plan.Parameters.AddWithValue(
                "@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection)));
            plan.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
            plan.Parameters.AddWithValue("@setup_id", setupId);
            plan.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
            plan.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
            plan.Parameters.AddWithValue("@ticker", ticker);
            plan.Parameters.AddWithValue("@direction", direction);
            plan.Parameters.AddWithValue("@trigger", StoreText.PriceToStorageText(trigger));
            plan.Parameters.AddWithValue("@give_up", StoreText.PriceToStorageText(giveUp));
            plan.Parameters.AddWithValue("@distance", StoreText.PriceToStorageText(distance));
            plan.Parameters.AddWithValue("@shares", shares);
            plan.Parameters.AddWithValue("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity));
            plan.Parameters.AddWithValue("@fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade));
            plan.Parameters.AddWithValue("@budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget));
            plan.Parameters.AddWithValue(
                "@at_stake", StoreText.PriceToStorageText(PositionSizing.RiskAtStake(shares, distance)));
            plan.Parameters.AddWithValue(
                "@observed_at",
                StoreText.TimestampToStorageText(
                    SessionBoundaries.At(Evening, new TimeOnly(18, 30), SessionBoundaries.UsEquities)));
            plan.ExecuteNonQuery();
        }

        if (touchedAt is null)
        {
            return;
        }

        using SqliteCommand resolution = connection.CreateCommand();
        resolution.CommandText = """
            INSERT INTO trigger_resolution (
                plan_id, setup_id, variant_id, live_session, ticker, direction, outcome,
                touched_at, minutes_walked, unresolved_because, observed_at)
            VALUES (@plan_id, @setup_id, @variant_id, @live_session, @ticker, @direction, 'touched', @touched_at, 390, NULL, @observed_at);
            """;
        resolution.Parameters.AddWithValue(
            "@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection)));
        resolution.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
        resolution.Parameters.AddWithValue("@setup_id", setupId);
        resolution.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
        resolution.Parameters.AddWithValue("@ticker", ticker);
        resolution.Parameters.AddWithValue("@direction", direction);
        resolution.Parameters.AddWithValue(
            "@touched_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, touchedAt.Value, SessionBoundaries.UsEquities)));
        resolution.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities)));
        resolution.ExecuteNonQuery();
    }

    /// <summary>
    /// An order row written by nothing, which is what this check exists to find.
    ///
    /// It borrows an existing plan's key rather than inventing one, because the table's foreign key
    /// to `trade_plan` would refuse an order for a plan that does not exist: a smuggled row that
    /// cannot be written is not a proof of anything.
    /// </summary>
    private static void InsertOrderOutsideAnyRun(
        SqliteConnection connection, string orderId, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_order (
                order_id, plan_id, setup_id, variant_id, live_session, ticker, direction,
                triggered_at, status, planned_shares, shares, risk_at_stake, bound_by,
                blocked_because, observed_at)
            SELECT @order_id, p.plan_id, p.setup_id, p.variant_id, p.live_session, p.ticker,
                   p.direction, @triggered_at, 'placed',
                   p.shares, p.shares, p.risk_at_stake, NULL, NULL, @observed_at
              FROM trade_plan p
             WHERE p.plan_id NOT IN (SELECT plan_id FROM trade_order)
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@order_id", orderId);
        command.Parameters.AddWithValue(
            "@triggered_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, new TimeOnly(9, 45), SessionBoundaries.UsEquities)));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}
