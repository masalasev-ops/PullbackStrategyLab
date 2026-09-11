using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Two versions planning one setup, walked through the four stages that keyed a night's plans by
/// setup until 7.11: RiskGate, PaperBroker, PositionManager and PlanAudit.
///
/// <b>The obligation 7.8 raised, closed.</b> A plan is one setup under one version, so on a night two
/// versions select the same name there are two plans for one setup, and each of the four built a
/// dictionary keyed on the setup, met a duplicate key and threw rather than gating, filling, managing
/// or auditing either. Nothing live has had two versions, so it bit nobody; generation 1 is where
/// versions of it first become possible.
///
/// <b>AUTHORED.</b> One long setup planned at 100 over 95 by the baseline and by one open selection
/// version, both touched at 10:00, the session trading through the give-up point at 11:00, so each
/// plan becomes an order, a position, a close and an audited trade.
/// </summary>
public sealed class TwoVersionsOneSetupTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);
    private const string Ticker = "TWO";
    private const string Version = "V-two";

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public TwoVersionsOneSetupTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
        Seed();
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Two_plans_on_one_setup_each_become_an_order_a_position_a_close_and_an_audit()
    {
        OrderRunResult orders = new RiskGate(_connections, Logger(new TimeOnly(21, 10), out FixedClock gate), gate, Options()).Apply(Session);
        Assert.Equal(2, orders.Triggers);
        Assert.Equal(2, orders.Placed + orders.Reduced);

        FillRunResult fills = new PaperBroker(_connections, Logger(new TimeOnly(21, 15), out FixedClock broker), broker, Options()).Fill(Session);
        Assert.Equal(2, fills.EntriesFilled);

        ManageRunResult managed = new PositionManager(_connections, Logger(new TimeOnly(21, 20), out FixedClock manager), manager, Options()).Manage(Session);
        Assert.Equal(2, managed.ClosedGiveUp);

        TradeRunResult trades = new TradeJournal(_connections, Logger(new TimeOnly(21, 25), out FixedClock journal), journal, Options()).Close(Session);
        Assert.Equal(2, trades.Journalled);

        AuditRunResult audited = new PlanAudit(_connections, Logger(new TimeOnly(21, 30), out FixedClock audit), audit, Options()).Audit(Session);
        Assert.Equal(2, audited.Audited);

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<StoredPosition> positions =
            PositionReader.ForOpenedSession(connection, Session, Session, SessionBoundaries.UsEquities);
        Assert.Equal(
            [Version, TestVersions.Baseline],
            positions.Select(p => p.VariantId).Order(StringComparer.Ordinal));
    }

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private RunLogger Logger(TimeOnly at, out FixedClock clock)
    {
        clock = new FixedClock(SessionBoundaries.At(Session, at, SessionBoundaries.UsEquities));
        return new RunLogger(clock, Options());
    }

    private void Seed()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        TestVersions.SeedBaseline(connection);

        string setupId = $"{Evening:yyyy-MM-dd}-{Ticker}-{SetupDirection.Long}";
        string Stamp(DateOnly day, TimeOnly at) =>
            StoreText.TimestampToStorageText(SessionBoundaries.At(day, at, SessionBoundaries.UsEquities));

        Execute(connection, $"""
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES ('{Version}', 0, 'selection', 'a second version', 't', 1802, 'effective_paired_setup_observations',
                    'open', NULL, '2026-08-01T22:00:00.000Z', 'long', 'dip-shape', 'maximum-retrace', '0.40', '0.50');
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('{Ticker}', '{Ticker}', 'NASDAQ', 'Common Stock', '2026-07-01');
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES ('{setupId}', '2026-08-25', '{Ticker}', 'long', '[]', 1, 0, '100.0000', '95.0000', '0.300000');
            """);

        foreach (string variant in new[] { TestVersions.Baseline, Version })
        {
            string planId = PlanIdentity.For(setupId, variant);

            Execute(connection, $"""
                INSERT INTO trade_plan (
                    plan_id, variant_id, setup_id, as_of, live_session, ticker, direction,
                    trigger_price, give_up_price, give_up_distance, shares,
                    equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
                VALUES ('{planId}', '{variant}', '{setupId}', '2026-08-25', '2026-08-26', '{Ticker}', 'long',
                        '100.0000', '95.0000', '5.0000', 150,
                        '100000.0000', '0.007500', '750.0000', '750.0000', '{Stamp(Evening, new TimeOnly(18, 30))}');
                INSERT INTO trigger_resolution (
                    plan_id, variant_id, setup_id, live_session, ticker, direction, outcome, touched_at,
                    minutes_walked, observed_at)
                VALUES ('{planId}', '{variant}', '{setupId}', '2026-08-26', '{Ticker}', 'long', 'touched',
                        '{Stamp(Session, new TimeOnly(10, 0))}', 2, '{Stamp(Session, new TimeOnly(21, 5))}');
                """);
        }

        foreach ((TimeOnly at, string o, string h, string l, string c) in new[]
                 {
                     (new TimeOnly(10, 0), "99.0000", "101.0000", "99.0000", "100.5000"),
                     (new TimeOnly(11, 0), "99.0000", "99.0000", "94.0000", "95.0000"),
                 })
        {
            Execute(connection, $"""
                INSERT INTO intraday_bar (ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                                          open, high, low, close, volume, observed_at)
                VALUES ('{Ticker}', '{Stamp(Session, at)}', '2026-08-26', '1m', 'regular', 'raw',
                        '{o}', '{h}', '{l}', '{c}', 10000, '{Stamp(Session, new TimeOnly(20, 30))}');
                """);
        }

        Execute(connection, $"""
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES ('{Ticker}', '2026-08-26', '99.0000', '101.0000', '94.0000', '95.0000', '95.0000', 1000000,
                    '{Stamp(Session, new TimeOnly(17, 30))}');
            """);

        foreach (string pass in new[] { "after_open", "before_close" })
        {
            string at = Stamp(Session, pass == "after_open" ? new TimeOnly(10, 15) : new TimeOnly(15, 45));

            Execute(connection, $"""
                INSERT INTO spread_pass (session_date, setup_as_of, pass, requested, answered, quoted, unquoted,
                                         rows_written, outcome, observed_at)
                VALUES ('2026-08-26', '2026-08-25', '{pass}', 1, 1, 1, 0, 1, 'clean', '{at}');
                INSERT INTO spread_snapshot (ticker, session_date, setup_as_of, pass, snapshot_ts, bid, ask,
                                             bid_ts, ask_ts, spread_bps, quote_lag_seconds, absent_because, observed_at)
                VALUES ('{Ticker}', '2026-08-26', '2026-08-25', '{pass}', '{at}', '99.9000', '100.1000',
                        '{at}', '{at}', 10, 900, NULL, '{at}');
                """);
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
