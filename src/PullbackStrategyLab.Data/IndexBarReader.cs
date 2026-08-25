using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored index bars are read, on the same terms as the daily bars: every read
/// takes an as-of date, only observations made by the end of that date are visible, and within
/// a date the latest such observation wins.
///
/// A separate reader rather than a parameter on the bar reader, because the two tables have
/// different key columns and a reader that took the table name would be one string away from
/// answering a question about the wrong market.
/// </summary>
public sealed class IndexBarReader
{
    private readonly StoreConnectionFactory _connections;

    public IndexBarReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// One symbol's bars up to and including <paramref name="asOf"/>, oldest first, at most
    /// <paramref name="sessions"/> of them.
    /// </summary>
    public IReadOnlyList<StoredDailyBar> Read(string symbol, DateOnly asOf, int sessions)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, symbol, asOf, sessions);
    }

    public static IReadOnlyList<StoredDailyBar> Read(SqliteConnection connection, string symbol, DateOnly asOf, int sessions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessions);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bar_date, open, high, low, close, adj_close, volume, observed_at
              FROM index_bar b
             WHERE b.symbol = @symbol
               AND b.bar_date <= @as_of
               AND b.observed_at <= @observed_before
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM index_bar l
                      WHERE l.symbol = b.symbol
                        AND l.bar_date = b.bar_date
                        AND l.observed_at <= @observed_before)
             ORDER BY b.bar_date DESC
             LIMIT @sessions;
            """;
        command.Parameters.AddWithValue("@symbol", symbol);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.DateToStorageText(asOf) + "T23:59:59.999Z");
        command.Parameters.AddWithValue("@sessions", sessions);

        var bars = new List<StoredDailyBar>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            bars.Add(Map(symbol, reader));
        }

        bars.Reverse();
        return bars;
    }

    /// <summary>
    /// One symbol's bar on one date, as last observed at or before
    /// <paramref name="observedBefore"/>. What the ingestor compares each returned bar against.
    /// </summary>
    public static StoredDailyBar? Latest(SqliteConnection connection, string symbol, DateOnly barDate, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bar_date, open, high, low, close, adj_close, volume, observed_at
              FROM index_bar
             WHERE symbol = @symbol
               AND bar_date = @bar_date
               AND observed_at <= @observed_before
             ORDER BY observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@symbol", symbol);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(symbol, reader) : null;
    }

    private static StoredDailyBar Map(string symbol, SqliteDataReader reader) => new(
        symbol,
        StoreText.StorageTextToDate(reader.GetString(0)),
        StoreText.StorageTextToPrice(reader.GetString(1)),
        StoreText.StorageTextToPrice(reader.GetString(2)),
        StoreText.StorageTextToPrice(reader.GetString(3)),
        StoreText.StorageTextToPrice(reader.GetString(4)),
        StoreText.StorageTextToPrice(reader.GetString(5)),
        reader.GetInt64(6),
        StoreText.StorageTextToTimestamp(reader.GetString(7)));
}
