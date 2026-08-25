using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored bars are read, and the one place the point-in-time rule is implemented.
///
/// Every read takes an as-of date and there is no overload that does not. A read that could
/// omit it would compile, run, and quietly return a bar the lab could not have seen on the
/// night, which produces an encouraging result that means nothing. The as-of date is the
/// hardest thing in the system to notice getting wrong, so it is the one thing a caller
/// cannot leave out.
///
/// Within a date, the latest observation at or before the as-of date wins. A vendor
/// correction arriving on Tuesday does not change what Monday's replay sees.
/// </summary>
public sealed class DailyBarReader
{
    private readonly StoreConnectionFactory _connections;

    public DailyBarReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// A ticker's bars up to and including <paramref name="asOf"/>, oldest first, at most
    /// <paramref name="sessions"/> of them. Only observations made at or before the end of the
    /// as-of date are visible.
    /// </summary>
    public IReadOnlyList<StoredDailyBar> Read(string ticker, DateOnly asOf, int sessions)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, asOf, sessions);
    }

    public static IReadOnlyList<StoredDailyBar> Read(SqliteConnection connection, string ticker, DateOnly asOf, int sessions)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessions);

        using SqliteCommand command = connection.CreateCommand();

        // observed_at is compared against the end of the as-of date rather than against the
        // date itself, because an observation made during that evening's run is one the lab
        // did have. Anything later is not.
        command.CommandText = """
            SELECT bar_date, open, high, low, close, adj_close, volume, observed_at
              FROM daily_bar b
             WHERE b.ticker = @ticker
               AND b.bar_date <= @as_of
               AND b.observed_at <= @observed_before
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM daily_bar l
                      WHERE l.ticker = b.ticker
                        AND l.bar_date = b.bar_date
                        AND l.observed_at <= @observed_before)
             ORDER BY b.bar_date DESC
             LIMIT @sessions;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", EndOf(asOf));
        command.Parameters.AddWithValue("@sessions", sessions);

        var bars = new List<StoredDailyBar>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            bars.Add(new StoredDailyBar(
                ticker,
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToPrice(reader.GetString(1)),
                StoreText.StorageTextToPrice(reader.GetString(2)),
                StoreText.StorageTextToPrice(reader.GetString(3)),
                StoreText.StorageTextToPrice(reader.GetString(4)),
                StoreText.StorageTextToPrice(reader.GetString(5)),
                reader.GetInt64(6),
                StoreText.StorageTextToTimestamp(reader.GetString(7))));
        }

        // Read newest first so the limit takes the most recent window, then handed back oldest
        // first because every average in the lab is computed forwards.
        bars.Reverse();
        return bars;
    }

    /// <summary>
    /// The latest observation of every ticker's bar on one date, made at or before
    /// <paramref name="observedBefore"/>. One query for the whole market rather than one per
    /// name: the ingestor compares a few thousand bars a night and a query each would make the
    /// comparison cost more than the request that fetched them.
    ///
    /// The bound is an instant rather than a date, and that distinction is the whole point.
    /// The ingestor asks "has the vendor changed anything since we last looked", so its bound
    /// is now; a signal asks "what did the lab know on the night", so its bound is that night.
    /// Passing the bar date as both is how a backfilled date looks unobserved to the ingestor
    /// that just wrote it, and rewrites the same figures under a new observation every run.
    /// </summary>
    public static IReadOnlyDictionary<string, StoredDailyBar> ReadDate(SqliteConnection connection, DateOnly barDate, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.ticker, b.bar_date, b.open, b.high, b.low, b.close, b.adj_close, b.volume, b.observed_at
              FROM daily_bar b
             WHERE b.bar_date = @bar_date
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM daily_bar l
                      WHERE l.ticker = b.ticker
                        AND l.bar_date = b.bar_date
                        AND l.observed_at <= @observed_before);
            """;
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var latest = new Dictionary<string, StoredDailyBar>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            latest[reader.GetString(0)] = new StoredDailyBar(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                StoreText.StorageTextToPrice(reader.GetString(2)),
                StoreText.StorageTextToPrice(reader.GetString(3)),
                StoreText.StorageTextToPrice(reader.GetString(4)),
                StoreText.StorageTextToPrice(reader.GetString(5)),
                StoreText.StorageTextToPrice(reader.GetString(6)),
                reader.GetInt64(7),
                StoreText.StorageTextToTimestamp(reader.GetString(8)));
        }

        return latest;
    }

    /// <summary>
    /// One ticker's bar on one date, as last observed at or before <paramref name="observedBefore"/>,
    /// or null if there is none. What the per-ticker backfill compares each returned bar against:
    /// it walks one name's whole series rather than one date's whole market, so the market-wide
    /// read would fetch a few thousand rows to answer a question about one.
    /// </summary>
    public static StoredDailyBar? Latest(SqliteConnection connection, string ticker, DateOnly barDate, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bar_date, open, high, low, close, adj_close, volume, observed_at
              FROM daily_bar
             WHERE ticker = @ticker
               AND bar_date = @bar_date
               AND observed_at <= @observed_before
             ORDER BY observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredDailyBar(
                ticker,
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToPrice(reader.GetString(1)),
                StoreText.StorageTextToPrice(reader.GetString(2)),
                StoreText.StorageTextToPrice(reader.GetString(3)),
                StoreText.StorageTextToPrice(reader.GetString(4)),
                StoreText.StorageTextToPrice(reader.GetString(5)),
                reader.GetInt64(6),
                StoreText.StorageTextToTimestamp(reader.GetString(7)))
            : null;
    }

    /// <summary>The last instant of a date, in the form observed_at is stored in.</summary>
    private static string EndOf(DateOnly date) =>
        StoreText.DateToStorageText(date) + "T23:59:59.999Z";
}

/// <summary>
/// One stored bar. Prices are decimal here and TEXT in the store, and the crossing between
/// the two happens in <see cref="StoreText"/> and nowhere else.
/// </summary>
public sealed record StoredDailyBar(
    string Ticker,
    DateOnly BarDate,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal AdjustedClose,
    long Volume,
    DateTimeOffset ObservedAt)
{
    /// <summary>The day's range as a fraction of its close. What the daily-range floor is measured in.</summary>
    public decimal RangeFraction => Close == 0m ? 0m : (High - Low) / Close;

    /// <summary>True when the two bars carry the same figures, whatever their observed_at says.</summary>
    public bool SameFigures(decimal open, decimal high, decimal low, decimal close, decimal adjustedClose, long volume) =>
        Open == open && High == high && Low == low && Close == close && AdjustedClose == adjustedClose && Volume == volume;
}
