using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Four authored calibration rows with bought minutes, daily history and a closed horizon, and the
/// measurement over them, from 7.9. Shared by the tests and the fixture replay.
///
/// <b>AUTHORED, and hand-derivable by design.</b> Every daily bar before the flag is the zone price
/// with a high and a low 2% either side, so the true range is 4% of the zone every session: the ATR is
/// 4 at a zone of 100 and 2 at 50, the daily range is 0.04, and the ceiling is the tighter of 2% and 5%,
/// being 2%. The entry sessions are <see cref="EntryRuleCases"/>'s minutes:
///
/// <b>LNG, long</b>, enters at 100.20 with a stop at 99.70, then trades to 98.00 the same session and
/// over the horizon closes at 104.00: ahead, and stopped out first. <b>WIN, long</b>, the same entry and
/// stop, and nothing after the entry trades below 99.90, closing the horizon at 105.00: ahead and kept.
/// <b>NRC, long</b>, flushes and never reclaims. <b>SHT, short</b>, enters at 50.00 with a stop at 50.30,
/// trades to 52.00 after, and closes the horizon at 48.00: ahead, and stopped out first.
///
/// So the long side reads three rows, two entered and one never reclaimed, a bound of one in two and
/// one in two achieved; the short side one row, entered, a bound of nought.
/// </summary>
public sealed class CalibrationEntryCases : IDisposable
{
    public static readonly DateOnly Flagged = new(2026, 8, 24);
    public static readonly DateOnly Entry = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly FixedClock _clock = new(SessionBoundaries.At(new DateOnly(2026, 9, 11), new TimeOnly(20, 0), SessionBoundaries.UsEquities));

    public CalibrationEntryCases()
    {
        Connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(Connections).Apply();

        using SqliteConnection connection = Connections.OpenWrite();

        (int T, decimal Open, decimal High, decimal Low, decimal Close)[] entryLong =
        [
            (0, 101.00m, 101.50m, 100.80m, 101.20m),
            (1, 101.20m, 101.30m, 100.60m, 100.70m),
            (2, 100.70m, 100.70m, 99.80m, 99.90m),
            (3, 99.90m, 100.20m, 99.70m, 100.10m),
            (4, 100.10m, 100.90m, 100.00m, 100.80m),
            (5, 100.80m, 101.00m, 100.50m, 100.60m),
        ];

        Seed(connection, "LNG", SetupDirection.Long, 100m,
            [.. entryLong, (10, 100.00m, 100.10m, 98.00m, 98.50m), (11, 98.50m, 99.00m, 98.20m, 98.90m)],
            horizon: (104.50m, 98.00m, 104.00m));

        Seed(connection, "WIN", SetupDirection.Long, 100m,
            [.. entryLong, (10, 100.80m, 101.00m, 100.10m, 100.90m), (11, 100.90m, 101.20m, 100.50m, 101.10m)],
            horizon: (105.50m, 99.90m, 105.00m));

        Seed(connection, "NRC", SetupDirection.Long, 100m,
        [
            (0, 101.00m, 101.20m, 100.90m, 101.00m),
            (1, 101.00m, 101.00m, 100.50m, 100.60m),
            (2, 100.60m, 100.60m, 99.50m, 99.60m),
            (3, 99.60m, 99.55m, 99.00m, 99.10m),
            (4, 99.10m, 99.05m, 98.50m, 98.60m),
            (5, 98.60m, 98.55m, 98.00m, 98.10m),
        ], horizon: (101.00m, 97.00m, 99.00m));

        Seed(connection, "SHT", SetupDirection.Short, 50m,
        [
            (0, 49.00m, 49.20m, 48.90m, 49.00m),
            (1, 49.00m, 49.50m, 49.10m, 49.40m),
            (2, 49.40m, 50.20m, 49.60m, 50.10m),
            (3, 50.10m, 50.30m, 49.90m, 50.00m),
            (4, 50.00m, 50.20m, 50.00m, 50.10m),
            (5, 50.10m, 50.10m, 49.70m, 49.80m),
            (10, 50.50m, 52.00m, 50.40m, 51.80m),
            (11, 51.80m, 51.90m, 51.00m, 51.20m),
        ], horizon: (52.00m, 47.50m, 48.00m));
    }

    public StoreConnectionFactory Connections { get; }

    public string Root => _root.Path;

    public void Dispose() => _root.Dispose();

    public EntryRuleReport Measure()
    {
        IOptions<PullbackStrategyLabOptions> options =
            Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new EntryRuleMeasurement(
            Connections, new RunLogger(_clock, options), _clock, options, new PullbackStrategyLabPaths(_root.Path)).Measure();
    }

    private static void Seed(
        SqliteConnection connection,
        string ticker,
        string direction,
        decimal zone,
        IReadOnlyList<(int T, decimal Open, decimal High, decimal Low, decimal Close)> minutes,
        (decimal High, decimal Low, decimal Close) horizon)
    {
        Execute(connection,
            "INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2025-01-01');",
            ("@t", ticker));

        Execute(connection, """
            INSERT INTO calibration_setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@id, @as_of, @t, @d, '[]', 1);
            """,
            ("@id", $"{Flagged:yyyy-MM-dd}-{ticker}-{direction}"), ("@as_of", StoreText.DateToStorageText(Flagged)), ("@t", ticker), ("@d", direction));

        // Sixty weekdays to the flag at the zone, 2% either side, then the entry session and ten more.
        foreach (DateOnly day in Weekdays(Flagged, back: 60))
        {
            Daily(connection, ticker, day, zone, zone * 1.02m, zone * 0.98m, zone);
        }

        Daily(connection, ticker, Entry, zone, zone * 1.02m, zone * 0.98m, zone);

        DateOnly next = Entry;
        for (int i = 0; i < 10; i++)
        {
            do
            {
                next = next.AddDays(1);
            }
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);

            Daily(connection, ticker, next, zone, horizon.High, horizon.Low, horizon.Close);
        }

        // Nine prior sessions of hourly minutes at the zone, the stub included: the warm-up exactly.
        foreach (DateOnly day in Weekdays(Flagged, back: 9))
        {
            for (int bar = 0; bar < 7; bar++)
            {
                Minute(connection, ticker, day,
                    SessionBoundaries.At(day, SessionBoundaries.RegularSessionOpen, SessionBoundaries.UsEquities).AddHours(bar),
                    zone, zone, zone, zone);
            }
        }

        foreach ((int t, decimal open, decimal high, decimal low, decimal close) in minutes)
        {
            Minute(connection, ticker, Entry,
                SessionBoundaries.At(Entry, SessionBoundaries.RegularSessionOpen, SessionBoundaries.UsEquities).AddMinutes(t),
                open, high, low, close);
        }
    }

    /// <summary>The <paramref name="back"/> weekdays ending at <paramref name="through"/>.</summary>
    private static IEnumerable<DateOnly> Weekdays(DateOnly through, int back)
    {
        var days = new List<DateOnly>();
        for (DateOnly d = through; days.Count < back; d = d.AddDays(-1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(d);
            }
        }

        days.Reverse();
        return days;
    }

    private static void Daily(SqliteConnection connection, string ticker, DateOnly day, decimal open, decimal high, decimal low, decimal close) =>
        Execute(connection, """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@t, @d, @o, @h, @l, @c, @c, 1000000, @observed);
            """,
            ("@t", ticker),
            ("@d", StoreText.DateToStorageText(day)),
            ("@o", StoreText.PriceToStorageText(open)),
            ("@h", StoreText.PriceToStorageText(high)),
            ("@l", StoreText.PriceToStorageText(low)),
            ("@c", StoreText.PriceToStorageText(close)),
            ("@observed", StoreText.TimestampToStorageText(SessionBoundaries.At(day, new TimeOnly(18, 0), SessionBoundaries.UsEquities))));

    private static void Minute(
        SqliteConnection connection, string ticker, DateOnly session, DateTimeOffset at,
        decimal open, decimal high, decimal low, decimal close) =>
        Execute(connection, """
            INSERT INTO calibration_minute_bar (
                ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                open, high, low, close, volume, observed_at)
            VALUES (@t, @ts, @session, '1m', 'regular', 'raw', @o, @h, @l, @c, 1000, '2026-09-11T12:00:00.000Z');
            """,
            ("@t", ticker),
            ("@ts", StoreText.TimestampToStorageText(at)),
            ("@session", StoreText.DateToStorageText(session)),
            ("@o", StoreText.PriceToStorageText(open)),
            ("@h", StoreText.PriceToStorageText(high)),
            ("@l", StoreText.PriceToStorageText(low)),
            ("@c", StoreText.PriceToStorageText(close)));

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
