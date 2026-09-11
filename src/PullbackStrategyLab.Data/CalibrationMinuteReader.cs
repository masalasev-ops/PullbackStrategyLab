using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Trading;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The calibration minutes the backfill bought, read one name and one session at a time, from 7.9.
///
/// <b>Bounded on the observation instant, on the terms the live minutes are.</b> The measurement that
/// reads these resolves an entry the way a night would have, and a read standing at an instant before
/// the minutes were bought would be answering with bars nobody held; within a minute the latest
/// observation wins.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public static class CalibrationMinuteReader
{
    /// <summary>One name's regular-session minutes for one session, in order, as observed by the end of <paramref name="asOf"/>.</summary>
    public static IReadOnlyList<StoredIntradayBar> Read(
        SqliteConnection connection, string ticker, DateOnly sessionDate, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                   open, high, low, close, volume, observed_at
              FROM calibration_minute_bar b
             WHERE b.ticker = @ticker
               AND b.session_date = @session_date
               AND b.session_window = 'regular'
               AND b.observed_at <= @observed_before
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM calibration_minute_bar l
                      WHERE l.ticker = b.ticker
                        AND l.bar_ts = b.bar_ts
                        AND l.observed_at <= @observed_before)
             ORDER BY b.bar_ts;
            """;

        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var bars = new List<StoredIntradayBar>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            bars.Add(new StoredIntradayBar(
                reader.GetString(0),
                StoreText.StorageTextToTimestamp(reader.GetString(1)),
                StoreText.StorageTextToDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                StoreText.StorageTextToPrice(reader.GetString(6)),
                StoreText.StorageTextToPrice(reader.GetString(7)),
                StoreText.StorageTextToPrice(reader.GetString(8)),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                reader.GetInt64(10),
                StoreText.StorageTextToTimestamp(reader.GetString(11))));
        }

        return bars;
    }

    /// <summary>The sessions the research table holds a minute of one name for, in a window, oldest first.</summary>
    public static IReadOnlyList<DateOnly> SessionsHeld(
        SqliteConnection connection, string ticker, DateOnly from, DateOnly to, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT session_date
              FROM calibration_minute_bar
             WHERE ticker = @ticker
               AND session_date >= @from
               AND session_date <= @to
               AND observed_at <= @observed_before
             ORDER BY session_date;
            """;

        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(to));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return sessions;
    }

    /// <summary>
    /// One name's hourly closes over the calibration sessions held before <paramref name="sessionDate"/>,
    /// on the window and the terms <see cref="IntradayBarReader.HourlyClosesBefore"/> takes them for the
    /// live minutes, so a calibration entry starts its averages from the history a live one would.
    /// </summary>
    public static IReadOnlyList<decimal> HourlyClosesBefore(
        SqliteConnection connection, string ticker, DateOnly sessionDate, DateOnly asOf, string sessionZone)
    {
        var closes = new List<decimal>();

        foreach (DateOnly held in SessionsHeld(
                     connection, ticker, sessionDate.AddDays(-(IntradayBarReader.HistoryDays - 1)), sessionDate.AddDays(-1), asOf, sessionZone))
        {
            IReadOnlyList<StoredIntradayBar> bars = Read(connection, ticker, held, asOf, sessionZone);
            closes.AddRange(EntryRule.HourlyCloses(bars.Select(b => (b.OpenedAt, b.Close)), held, sessionZone));
        }

        return closes;
    }
}
