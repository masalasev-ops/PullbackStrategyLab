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
/// Three authored plans carrying the entry rule, their minutes, and the stages that resolve them, from
/// 7.8. Shared by the tests and the fixture replay so the figures the replay records are the cases the
/// tests assert.
///
/// <b>AUTHORED, and hand-derivable by design.</b> Every prior hourly close is the same price, so both
/// hourly averages sit exactly on it and the zone is one number: 100 for the long, 50 for the short.
/// The session minutes are written so each rule fires on a stated minute:
///
/// <b>LNG, long.</b> 09:30 and 09:31 above the zone; 09:32 flushes to 99.80, arming; 09:33 does not
/// break 09:32's high of 100.70; 09:34 reaches 09:33's high of 100.20 and enters there. The session's
/// low through 09:34 is 99.70, 0.50 under the entry, 0.50% of it, so the stop stays at the session's
/// low and 1,500 shares risk the $750 budget. The position cap buys 349 at 100.20. **09:40 then prints
/// 98.00**, a lower low after the entry, which the stop must not use.
///
/// <b>SHT, short.</b> The mirror around 50: 09:32 flushes up to 50.20; 09:35 reaches 09:34's low of
/// 50.00 and enters there, with the session's high through 09:35 at 50.30, so 0.30 of stop and 2,500
/// shares, capped at 700. **09:40 prints 52.00** above the entry.
///
/// <b>NRC, long, no reclaim.</b> It flushes at 09:32 and every minute after makes a lower high, so no
/// previous candle's high is ever reached and no order is written.
/// </summary>
public sealed class EntryRuleCases : IDisposable
{
    /// <summary>The session the plans are live in. A Tuesday.</summary>
    public static readonly DateOnly Session = new(2026, 8, 25);

    /// <summary>The evening the plans were written on.</summary>
    public static readonly DateOnly Evening = new(2026, 8, 24);

    public const string Long = "LNG";
    public const string Short = "SHT";
    public const string NoReclaim = "NRC";

    private readonly TemporaryDirectory _root = new();
    private readonly FixedClock _clock = new(SessionBoundaries.At(Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities));

    public EntryRuleCases()
    {
        Connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(Connections).Apply();

        using SqliteConnection connection = Connections.OpenWrite();
        TestVersions.SeedBaseline(connection);

        Seed(connection, Long, SetupDirection.Long, zone: 100m, ceiling: 0.025m,
        [
            (0, 101.00m, 101.50m, 100.80m, 101.20m),
            (1, 101.20m, 101.30m, 100.60m, 100.70m),
            (2, 100.70m, 100.70m, 99.80m, 99.90m),
            (3, 99.90m, 100.20m, 99.70m, 100.10m),
            (4, 100.10m, 100.90m, 100.00m, 100.80m),
            (5, 100.80m, 101.00m, 100.50m, 100.60m),
            (10, 100.00m, 100.10m, 98.00m, 98.50m),
            (11, 98.50m, 99.00m, 98.20m, 98.90m),
        ]);

        Seed(connection, Short, SetupDirection.Short, zone: 50m, ceiling: 0.02m,
        [
            (0, 49.00m, 49.20m, 48.90m, 49.00m),
            (1, 49.00m, 49.50m, 49.10m, 49.40m),
            (2, 49.40m, 50.20m, 49.60m, 50.10m),
            (3, 50.10m, 50.30m, 49.90m, 50.00m),
            (4, 50.00m, 50.20m, 50.00m, 50.10m),
            (5, 50.10m, 50.10m, 49.70m, 49.80m),
            (10, 50.50m, 52.00m, 50.40m, 51.80m),
            (11, 51.80m, 51.90m, 51.00m, 51.20m),
        ]);

        Seed(connection, NoReclaim, SetupDirection.Long, zone: 100m, ceiling: 0.025m,
        [
            (0, 101.00m, 101.20m, 100.90m, 101.00m),
            (1, 101.00m, 101.00m, 100.50m, 100.60m),
            (2, 100.60m, 100.60m, 99.50m, 99.60m),
            (3, 99.60m, 99.55m, 99.00m, 99.10m),
            (4, 99.10m, 99.05m, 98.50m, 98.60m),
            (5, 98.60m, 98.55m, 98.00m, 98.10m),
        ]);
    }

    public StoreConnectionFactory Connections { get; }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private RunLogger Logger() => new(_clock, Options());

    /// <summary>The resolver, then the sizer, then the gate, as the night runs them.</summary>
    public (TriggerRunResult Triggers, EntrySizeResult Sizes, OrderRunResult Orders) Run()
    {
        TriggerRunResult triggers = new TriggerResolver(Connections, Logger(), _clock, Options()).Resolve(Session);
        _clock.Advance(TimeSpan.FromMinutes(1));
        EntrySizeResult sizes = new EntrySizer(Connections, Logger(), _clock, Options()).Size(Session);
        _clock.Advance(TimeSpan.FromMinutes(4));
        OrderRunResult orders = new RiskGate(Connections, Logger(), _clock, Options()).Apply(Session);

        return (triggers, sizes, orders);
    }

    public IReadOnlyList<StoredEntryResolution> Resolutions()
    {
        using SqliteConnection connection = Connections.OpenReadOnly();
        return EntryResolutionReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities);
    }

    public IReadOnlyList<StoredTriggerResolution> Triggers()
    {
        using SqliteConnection connection = Connections.OpenReadOnly();
        return TriggerResolutionReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities);
    }

    public IReadOnlyList<StoredTradeOrder> Orders()
    {
        using SqliteConnection connection = Connections.OpenReadOnly();
        return TradeOrderReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities);
    }

    /// <summary>The instant of a minute of the session, <paramref name="t"/> minutes after the open.</summary>
    public static DateTimeOffset Minute(int t) =>
        SessionBoundaries.At(Session, SessionBoundaries.RegularSessionOpen, SessionBoundaries.UsEquities).AddMinutes(t);

    /// <summary>
    /// The name, a plan carrying the rule, nine prior sessions of hourly minutes at the zone price, and
    /// the session's own minutes.
    /// </summary>
    private static void Seed(
        SqliteConnection connection,
        string ticker,
        string direction,
        decimal zone,
        decimal ceiling,
        IReadOnlyList<(int T, decimal Open, decimal High, decimal Low, decimal Close)> minutes)
    {
        Execute(connection,
            "INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2025-01-01');",
            ("@t", ticker));

        string setupId = $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";

        Execute(connection, """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out)
            VALUES (@id, @as_of, @t, @d, '[]', 1, 0);
            """,
            ("@id", setupId), ("@as_of", StoreText.DateToStorageText(Evening)), ("@t", ticker), ("@d", direction));

        Execute(connection, """
            INSERT INTO trade_plan (
                plan_id, setup_id, variant_id, as_of, live_session, ticker, direction, entry_rule,
                stop_ceiling, equity, risk_fraction, risk_budget, observed_at)
            VALUES (@plan, @id, @v, @as_of, @live, @t, @d, @rule, @ceiling, @equity, @fraction, @budget, @observed);
            """,
            ("@plan", PlanIdentity.For(setupId, TestVersions.Baseline)),
            ("@id", setupId),
            ("@v", TestVersions.Baseline),
            ("@as_of", StoreText.DateToStorageText(Evening)),
            ("@live", StoreText.DateToStorageText(Session)),
            ("@t", ticker),
            ("@d", direction),
            ("@rule", EntryRule.FlushReclaim),
            ("@ceiling", StoreText.RatioToStorageText(ceiling)),
            ("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity)),
            ("@fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade)),
            ("@budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget)),
            ("@observed", StoreText.TimestampToStorageText(SessionBoundaries.At(Evening, new TimeOnly(18, 30), SessionBoundaries.UsEquities))));

        // Nine prior weekdays, one minute at the open of each of the seven hourly bars, the stub's
        // included: sixty-three closes at the zone price, the warm-up exactly.
        DateOnly day = Session;
        int sessions = 0;

        while (sessions < 9)
        {
            day = day.AddDays(-1);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            for (int bar = 0; bar < 7; bar++)
            {
                DateTimeOffset at = SessionBoundaries.At(day, SessionBoundaries.RegularSessionOpen, SessionBoundaries.UsEquities).AddHours(bar);
                Minute(connection, ticker, day, at, zone, zone, zone, zone);
            }

            sessions++;
        }

        foreach ((int t, decimal open, decimal high, decimal low, decimal close) in minutes)
        {
            Minute(connection, ticker, Session, Minute(t), open, high, low, close);
        }
    }

    private static void Minute(
        SqliteConnection connection, string ticker, DateOnly session, DateTimeOffset at,
        decimal open, decimal high, decimal low, decimal close) =>
        Execute(connection, """
            INSERT INTO intraday_bar (
                ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                open, high, low, close, volume, observed_at)
            VALUES (@t, @ts, @session, '1m', 'regular', 'raw', @o, @h, @l, @c, 1000, @observed);
            """,
            ("@t", ticker),
            ("@ts", StoreText.TimestampToStorageText(at)),
            ("@session", StoreText.DateToStorageText(session)),
            ("@o", StoreText.PriceToStorageText(open)),
            ("@h", StoreText.PriceToStorageText(high)),
            ("@l", StoreText.PriceToStorageText(low)),
            ("@c", StoreText.PriceToStorageText(close)),
            ("@observed", StoreText.TimestampToStorageText(SessionBoundaries.At(session, new TimeOnly(20, 30), SessionBoundaries.UsEquities))));

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
