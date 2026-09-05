using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored minute bars are read, on the same terms as the daily and index bars: every
/// read takes an as-of date, only observations made by the end of that date are visible, and within
/// a bar's own minute the latest such observation wins.
///
/// <b>A session bound as well as an as-of bound, and they are different questions.</b> The as-of is
/// what the lab could have known; the session is which trading day's minutes are being asked for.
/// A reader that took only the as-of would answer with every minute of every session up to it,
/// which is never what anything wants and is several hundred thousand rows.
/// </summary>
public sealed class IntradayBarReader
{
    private readonly StoreConnectionFactory _connections;

    public IntradayBarReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// One name's minutes for one session, in order, as last observed by the end of
    /// <paramref name="asOf"/>.
    ///
    /// <paramref name="regularOnly"/> bounds the answer to the exchange's regular session. It is a
    /// parameter rather than the only behaviour because the store holds extended-hours minutes
    /// deliberately: they are as unrecoverable as any other and a later question may want them.
    /// </summary>
    public IReadOnlyList<StoredIntradayBar> Read(
        string ticker, DateOnly sessionDate, DateOnly asOf, string sessionZone, bool regularOnly = true)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, sessionDate, asOf, sessionZone, regularOnly);
    }

    /// <summary>The same read from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredIntradayBar> Read(
        SqliteConnection connection, string ticker, DateOnly sessionDate, DateOnly asOf, string sessionZone, bool regularOnly = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                   open, high, low, close, volume, observed_at
              FROM intraday_bar b
             WHERE b.ticker = @ticker
               AND b.session_date = @session_date
               AND b.observed_at <= @observed_before
               AND (@regular_only = 0 OR b.session_window = 'regular')
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM intraday_bar l
                      WHERE l.ticker = b.ticker
                        AND l.bar_ts = b.bar_ts
                        AND l.observed_at <= @observed_before)
             ORDER BY b.bar_ts;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));
        command.Parameters.AddWithValue("@regular_only", regularOnly ? 1 : 0);

        var bars = new List<StoredIntradayBar>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            bars.Add(Map(reader));
        }

        return bars;
    }

    /// <summary>
    /// Every named ticker's minutes for one session, in time order across the names, as last
    /// observed by the end of <paramref name="asOf"/>.
    ///
    /// <b>One read for the session rather than one per name, because the replay is one walk.</b>
    /// A resolver asking name by name gets each name's day correctly and no ordering between them,
    /// and the contention rule fills the earliest trigger of the session, which is a comparison
    /// across names. The order here is by minute and then by ticker, so a caller grouping on the
    /// timestamp gets whole minutes in sequence.
    ///
    /// The as-of bound and the latest-observation rule are the same as the per-name read, which is
    /// why they are written once below: a second spelling of a point-in-time bound is a second
    /// chance to get it wrong.
    /// </summary>
    public static IReadOnlyList<StoredIntradayBar> ReadSession(
        SqliteConnection connection,
        IReadOnlyCollection<string> tickers,
        DateOnly sessionDate,
        DateOnly asOf,
        string sessionZone,
        bool regularOnly = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tickers);

        if (tickers.Count == 0)
        {
            return [];
        }

        // One parameter per name rather than a joined string, so nothing from outside the lab reaches
        // the statement text. The names come from `setup` and `security` and are already constrained,
        // which is a fact about today's callers and not a property of this read.
        string[] slots = [.. tickers.Select((_, i) => $"@t{i}")];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                   open, high, low, close, volume, observed_at
              FROM intraday_bar b
             WHERE b.ticker IN ({string.Join(", ", slots)})
               AND b.session_date = @session_date
               AND b.observed_at <= @observed_before
               AND (@regular_only = 0 OR b.session_window = 'regular')
               AND b.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM intraday_bar l
                      WHERE l.ticker = b.ticker
                        AND l.bar_ts = b.bar_ts
                        AND l.observed_at <= @observed_before)
             ORDER BY b.bar_ts, b.ticker;
            """;

        int slot = 0;
        foreach (string ticker in tickers)
        {
            command.Parameters.AddWithValue($"@t{slot++}", ticker);
        }

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));
        command.Parameters.AddWithValue("@regular_only", regularOnly ? 1 : 0);

        var bars = new List<StoredIntradayBar>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            bars.Add(Map(reader));
        }

        return bars;
    }

    /// <summary>
    /// The sessions in a window for which the store holds a minute of one name, oldest first.
    ///
    /// <b>The sessions and not the bars, because the question is which sessions are missing.</b> A
    /// trade's picture has to say how much of a held position nothing can draw, and deriving that
    /// from the hold length would be an estimate: minutes are bought for the session a plan is live
    /// in and for a later session only where the name was flagged again that evening, so how many
    /// of the middle sessions have minutes is a fact about the store rather than about the trade.
    ///
    /// Bounded on the observation instant on the same terms as every other read here.
    /// </summary>
    public static IReadOnlyList<DateOnly> SessionsHeld(
        SqliteConnection connection,
        string ticker,
        DateOnly from,
        DateOnly to,
        DateOnly asOf,
        string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT session_date
              FROM intraday_bar
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
    /// What the night's fetch recorded for one session, as last observed by the end of
    /// <paramref name="asOf"/>, or null where no fetch ran.
    ///
    /// A night with no row here is a night nobody ran, which is a different fact from a night that
    /// ran and asked for nothing, and the two are distinguishable only because the stage writes a
    /// row either way.
    ///
    /// <b>It had no caller outside the suite until 6.10, and that is most of why the night of
    /// 2026-09-04 was invisible.</b> The row held the honest counts that evening, 92 asked, 92
    /// answered with nothing and 0 bars written, and nothing read them, so the one place the fault
    /// was written down was a table nobody opened. <c>LabNight</c> now reads it onto the morning
    /// screen beside the slot that wrote it.
    /// </summary>
    public static StoredIntradayFetch? LatestFetch(
        SqliteConnection connection, DateOnly sessionDate, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, setup_as_of, requested, fetched, empty, bars_written, stored,
                   window_sessions, outcome, stopped_because, observed_at
              FROM intraday_fetch
             WHERE session_date = @session_date
               AND observed_at <= @observed_before
             ORDER BY observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredIntradayFetch(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                StoreText.StorageTextToTimestamp(reader.GetString(10)))
            : null;
    }

    /// <summary>
    /// Whether this minute is already stored with the figures the vendor has just sent.
    ///
    /// What the ingestor compares each returned bar against, so a rerun writes nothing where the
    /// vendor's answer has not moved. Bounded on the run's own instant rather than on a session, so
    /// a second run inside one evening compares against what the first stored.
    ///
    /// It is not a public read of the store in the sense the point-in-time rule is about: it answers
    /// "have I already written exactly this" for the one component that writes the table, which is
    /// why it takes an instant rather than an as-of date.
    /// </summary>
    public static bool IsStoredUnchanged(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ticker,
        Vendored bar,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT open, high, low, close, volume
              FROM intraday_bar
             WHERE ticker = @ticker
               AND bar_ts = @bar_ts
               AND observed_at <= @observed_before
             ORDER BY observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_ts", StoreText.TimestampToStorageText(bar.OpenedAt));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return false;
        }

        return StoreText.StorageTextToPrice(reader.GetString(0)) == bar.Open
            && StoreText.StorageTextToPrice(reader.GetString(1)) == bar.High
            && StoreText.StorageTextToPrice(reader.GetString(2)) == bar.Low
            && StoreText.StorageTextToPrice(reader.GetString(3)) == bar.Close
            && reader.GetInt64(4) == bar.Volume;
    }

    /// <summary>
    /// The shape the unchanged comparison needs, so Data does not have to reference the Worker's
    /// vendor types to answer a question about its own table.
    /// </summary>
    public interface Vendored
    {
        DateTimeOffset OpenedAt { get; }

        decimal Open { get; }

        decimal High { get; }

        decimal Low { get; }

        decimal Close { get; }

        long Volume { get; }
    }

    private static StoredIntradayBar Map(SqliteDataReader reader) => new(
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
        StoreText.StorageTextToTimestamp(reader.GetString(11)));
}

/// <summary>One minute bar as the store holds it.</summary>
public sealed record StoredIntradayBar(
    string Ticker,
    DateTimeOffset OpenedAt,
    DateOnly SessionDate,
    string IntervalCode,
    string SessionWindow,
    string PriceBasis,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    DateTimeOffset ObservedAt);

/// <summary>
/// What one night's fetch recorded about itself.
///
/// <paramref name="Stored"/> is what the night's asking left the store holding for the window it
/// bought, being the bars written plus the bars already there unchanged, and it is the quantity the
/// outcome turns on. <paramref name="WindowSessions"/> is how many sessions that window covered,
/// which says whether short's twenty-session count was running that night.
/// </summary>
public sealed record StoredIntradayFetch(
    DateOnly SessionDate,
    DateOnly SetupAsOf,
    int Requested,
    int Fetched,
    int Empty,
    int BarsWritten,
    int Stored,
    int WindowSessions,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
