using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// One long position and one short, each walked from its entry to its exit over four sessions by the
/// shipped PaperBroker and PositionManager, from 7.10. Shared by the tests and the fixture replay.
///
/// <b>AUTHORED, and hand-derivable by design.</b> The fixture's night passes no candidate, so no
/// position of it is ever opened; these two are written so every trim, arm and exit falls on a bar
/// chosen for it, and the spread is ten basis points on every session.
///
/// <b>EXL, long</b>, planned at 100 over 95, 150 shares, fills at 100.10, so R is 5.10 a share and the
/// levels are 115.40 and 125.60. It trims 22 at 115.40 on the first session at 11:00 and 22 at 125.60
/// on the second at 10:00, closes the third at 113 under a 9-day average of 116, which arms the trail,
/// and sells its 106 remaining at the fourth session's 09:30 open of 112.
///
/// <b>EXS, short</b>, planned at 50 under 52.50, 300 shares, fills at 49.95, so R is 2.55 a share and
/// the levels are 42.30 and 37.20. It trims 45 at 42.30 on the first session at 11:00, reaches nothing
/// on the second, trims 45 at 37.20 on the third at 10:00, which is its third session held and arms the
/// hold limit, and buys its 210 remaining at the fourth session's 09:30 open of 37.50.
/// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class ExitCases : IDisposable
{
    public const string Long = "EXL";
    public const string Short = "EXS";

    public static readonly DateOnly Evening = new(2026, 8, 25);

    /// <summary>The four sessions walked, Wednesday to the following Monday.</summary>
    public static readonly DateOnly[] Sessions =
    [
        new(2026, 8, 26), new(2026, 8, 27), new(2026, 8, 28), new(2026, 8, 31),
    ];

    private readonly TemporaryDirectory _root = new();

    public ExitCases()
    {
        Connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(Connections).Apply();

        using (SqliteConnection seed = Connections.OpenWrite())
        {
            TestVersions.SeedBaseline(seed);
        }

        DateOnly s1 = Sessions[0], s2 = Sessions[1], s3 = Sessions[2], s4 = Sessions[3];

        Plan(Long, SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order(Long, SetupDirection.Long, new TimeOnly(10, 0), shares: 150);
        Minute(Long, s1, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute(Long, s1, new TimeOnly(11, 0), 110m, 116m, 110m, 115m);
        DailyBar(Long, s1, 115m);
        Indicators(Long, s1, ema9: 110m);
        Minute(Long, s2, new TimeOnly(10, 0), 120m, 126m, 119m, 125m);
        DailyBar(Long, s2, 124m);
        Indicators(Long, s2, ema9: 118m);
        Minute(Long, s3, new TimeOnly(10, 0), 118m, 119m, 112m, 113m);
        DailyBar(Long, s3, 113m);
        Indicators(Long, s3, ema9: 116m);
        Minute(Long, s4, new TimeOnly(9, 30), 112m, 113m, 111m, 112.5m);

        Plan(Short, SetupDirection.Short, trigger: 50m, giveUp: 52.5m);
        Order(Short, SetupDirection.Short, new TimeOnly(10, 0), shares: 300);
        Minute(Short, s1, new TimeOnly(10, 0), 50.5m, 50.5m, 49.5m, 49.8m);
        Minute(Short, s1, new TimeOnly(11, 0), 45m, 45m, 42m, 42.5m);
        DailyBar(Short, s1, 42.5m);
        Minute(Short, s2, new TimeOnly(10, 0), 40m, 40.5m, 39m, 39.5m);
        DailyBar(Short, s2, 39.5m);
        Minute(Short, s3, new TimeOnly(10, 0), 39m, 39.5m, 36.5m, 37m);
        DailyBar(Short, s3, 37m);
        Minute(Short, s4, new TimeOnly(9, 30), 37.5m, 38m, 37m, 37.8m);

        foreach (DateOnly session in Sessions)
        {
            Pass(session, "after_open");
            Pass(session, "before_close");

            foreach (string ticker in new[] { Long, Short })
            {
                Snapshot(ticker, session, "after_open", 10d);
                Snapshot(ticker, session, "before_close", 6d);
            }
        }
    }

    public StoreConnectionFactory Connections { get; }

    public void Dispose() => _root.Dispose();

    /// <summary>The broker on the first session, then the manager on each of the four, in order.</summary>
    public IReadOnlyList<ManageRunResult> Run()
    {
        new PaperBroker(Connections, Logger(Sessions[0], new TimeOnly(21, 15), out FixedClock brokerClock), brokerClock, Options())
            .Fill(Sessions[0]);

        return
        [
            .. Sessions.Select(session =>
                new PositionManager(Connections, Logger(session, new TimeOnly(21, 20), out FixedClock clock), clock, Options())
                    .Manage(session)),
        ];
    }

    /// <summary>One side's position as at <paramref name="asOf"/>.</summary>
    public StoredPosition Position(string ticker, DateOnly asOf)
    {
        using SqliteConnection connection = Connections.OpenReadOnly();
        return PositionReader.ForOpenedSession(connection, Sessions[0], asOf, SessionBoundaries.UsEquities)
            .Single(p => p.Ticker == ticker);
    }

    /// <summary>Every fill of one side after its entry, in the order they happened.</summary>
    public IReadOnlyList<StoredFill> Exits(string ticker)
    {
        using SqliteConnection connection = Connections.OpenReadOnly();
        return
        [
            .. Sessions
                .SelectMany(s => PositionReader.FillsOf(connection, s, Sessions[^1], SessionBoundaries.UsEquities))
                .Where(f => f.Ticker == ticker && f.Leg != "entry")
                .OrderBy(f => f.FilledAt),
        ];
    }

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private RunLogger Logger(DateOnly session, TimeOnly at, out FixedClock clock)
    {
        clock = new FixedClock(SessionBoundaries.At(session, at, SessionBoundaries.UsEquities));
        return new RunLogger(clock, Options());
    }

    private static string SetupIdOf(string ticker, string direction) => $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";

    private void Plan(string ticker, string direction, decimal trigger, decimal giveUp)
    {
        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);
        string setupId = SetupIdOf(ticker, direction);

        using SqliteConnection connection = Connections.OpenWrite();

        Execute(connection,
            "INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2026-07-01');",
            ("@t", ticker));

        Execute(connection, """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @trigger, @stop, @ranges);
            """,
            ("@id", setupId), ("@as_of", StoreText.DateToStorageText(Evening)), ("@ticker", ticker),
            ("@direction", direction), ("@trigger", StoreText.PriceToStorageText(trigger)),
            ("@stop", StoreText.PriceToStorageText(giveUp)), ("@ranges", StoreText.RatioToStorageText(0.30m)));

        Execute(connection, """
            INSERT INTO trade_plan (
                plan_id, variant_id, setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @plan_id, @variant_id, @setup_id, @as_of, @live_session, @ticker, @direction,
                @trigger, @give_up, @distance, @shares,
                @equity, @fraction, @budget, @at_stake, @observed_at);
            """,
            ("@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection))),
            ("@variant_id", TestVersions.Baseline),
            ("@setup_id", setupId),
            ("@as_of", StoreText.DateToStorageText(Evening)),
            ("@live_session", StoreText.DateToStorageText(Sessions[0])),
            ("@ticker", ticker),
            ("@direction", direction),
            ("@trigger", StoreText.PriceToStorageText(trigger)),
            ("@give_up", StoreText.PriceToStorageText(giveUp)),
            ("@distance", StoreText.PriceToStorageText(distance)),
            ("@shares", shares),
            ("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity)),
            ("@fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade)),
            ("@budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget)),
            ("@at_stake", StoreText.PriceToStorageText(PositionSizing.RiskAtStake(shares, distance))),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(Evening, new TimeOnly(18, 30), SessionBoundaries.UsEquities))));
    }

    private void Order(string ticker, string direction, TimeOnly at, int shares)
    {
        string setupId = SetupIdOf(ticker, direction);
        DateTimeOffset touchedAt = SessionBoundaries.At(Sessions[0], at, SessionBoundaries.UsEquities);

        using SqliteConnection connection = Connections.OpenWrite();
        string planId = PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection));

        Execute(connection, """
            INSERT INTO trigger_resolution (
                plan_id, variant_id, setup_id, live_session, ticker, direction, outcome, touched_at,
                minutes_walked, observed_at)
            VALUES (@plan_id, @variant_id, @setup_id, @live_session, @ticker, @direction, 'touched', @touched_at, 1, @observed_at);
            """,
            ("@plan_id", planId), ("@variant_id", TestVersions.Baseline), ("@setup_id", setupId),
            ("@live_session", StoreText.DateToStorageText(Sessions[0])), ("@ticker", ticker),
            ("@direction", direction), ("@touched_at", StoreText.TimestampToStorageText(touchedAt)),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(Sessions[0], new TimeOnly(21, 5), SessionBoundaries.UsEquities))));

        Execute(connection, """
            INSERT INTO trade_order (
                order_id, plan_id, setup_id, variant_id, live_session, ticker, direction,
                triggered_at, status, planned_shares, shares, risk_at_stake, observed_at)
            VALUES (@plan_id, @plan_id, @setup_id, @variant_id, @live_session, @ticker, @direction,
                    @triggered_at, 'placed', @shares, @shares, @risk, @observed_at);
            """,
            ("@plan_id", planId), ("@setup_id", setupId), ("@variant_id", TestVersions.Baseline),
            ("@live_session", StoreText.DateToStorageText(Sessions[0])), ("@ticker", ticker),
            ("@direction", direction), ("@triggered_at", StoreText.TimestampToStorageText(touchedAt)),
            ("@shares", shares), ("@risk", StoreText.PriceToStorageText(750m)),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(Sessions[0], new TimeOnly(21, 10), SessionBoundaries.UsEquities))));
    }

    private void Minute(string ticker, DateOnly session, TimeOnly at, decimal open, decimal high, decimal low, decimal close)
    {
        using SqliteConnection connection = Connections.OpenWrite();
        Execute(connection, """
            INSERT INTO intraday_bar (
                ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                open, high, low, close, volume, observed_at)
            VALUES (@ticker, @bar_ts, @session_date, '1m', 'regular', 'raw', @open, @high, @low, @close, 10000, @observed_at);
            """,
            ("@ticker", ticker),
            ("@bar_ts", StoreText.TimestampToStorageText(SessionBoundaries.At(session, at, SessionBoundaries.UsEquities))),
            ("@session_date", StoreText.DateToStorageText(session)),
            ("@open", StoreText.PriceToStorageText(open)),
            ("@high", StoreText.PriceToStorageText(high)),
            ("@low", StoreText.PriceToStorageText(low)),
            ("@close", StoreText.PriceToStorageText(close)),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, new TimeOnly(20, 30), SessionBoundaries.UsEquities))));
    }

    /// <summary>A daily bar with no split behind it, so the printed close is the adjusted one.</summary>
    private void DailyBar(string ticker, DateOnly date, decimal close)
    {
        using SqliteConnection connection = Connections.OpenWrite();
        Execute(connection, """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @close, @close, @close, @close, @close, 1000000, @observed_at);
            """,
            ("@ticker", ticker),
            ("@bar_date", StoreText.DateToStorageText(date)),
            ("@close", StoreText.PriceToStorageText(close)),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(date, new TimeOnly(17, 30), SessionBoundaries.UsEquities))));
    }

    private void Indicators(string ticker, DateOnly date, decimal ema9)
    {
        using SqliteConnection connection = Connections.OpenWrite();
        Execute(connection, """
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                 dollar_volume_median_20, range_avg_20)
            VALUES (@ticker, @as_of, @computed_at, @ema_9, @ema_9, @ema_9, 2, @adr, 50000000, 2);
            """,
            ("@ticker", ticker),
            ("@as_of", StoreText.DateToStorageText(date)),
            ("@computed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(date, new TimeOnly(18, 0), SessionBoundaries.UsEquities))),
            ("@ema_9", StoreText.PriceToStorageText(ema9)),
            ("@adr", StoreText.RatioToStorageText(0.02m)));
    }

    private void Pass(DateOnly session, string pass)
    {
        using SqliteConnection connection = Connections.OpenWrite();
        Execute(connection, """
            INSERT INTO spread_pass (
                session_date, setup_as_of, pass, requested, answered, quoted, unquoted,
                rows_written, outcome, observed_at)
            VALUES (@session_date, @setup_as_of, @pass, 2, 2, 2, 0, 2, 'clean', @observed_at);
            """,
            ("@session_date", StoreText.DateToStorageText(session)),
            ("@setup_as_of", StoreText.DateToStorageText(session.AddDays(-1))),
            ("@pass", pass),
            ("@observed_at", StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, new TimeOnly(10, 15), SessionBoundaries.UsEquities))));
    }

    private void Snapshot(string ticker, DateOnly session, string pass, double basisPoints)
    {
        DateTimeOffset snapshotAt = SessionBoundaries.At(
            session, pass == "after_open" ? new TimeOnly(10, 15) : new TimeOnly(15, 45), SessionBoundaries.UsEquities);

        using SqliteConnection connection = Connections.OpenWrite();
        Execute(connection, """
            INSERT INTO spread_snapshot (
                ticker, session_date, setup_as_of, pass, snapshot_ts, bid, ask,
                bid_ts, ask_ts, spread_bps, quote_lag_seconds, absent_because, observed_at)
            VALUES (@ticker, @session_date, @setup_as_of, @pass, @snapshot_ts, '99.90', '100.10',
                    @bid_ts, @snapshot_ts, @spread_bps, 900, NULL, @snapshot_ts);
            """,
            ("@ticker", ticker),
            ("@session_date", StoreText.DateToStorageText(session)),
            ("@setup_as_of", StoreText.DateToStorageText(session.AddDays(-1))),
            ("@pass", pass),
            ("@snapshot_ts", StoreText.TimestampToStorageText(snapshotAt)),
            ("@bid_ts", StoreText.TimestampToStorageText(snapshotAt.AddSeconds(-5))),
            ("@spread_bps", basisPoints));
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
