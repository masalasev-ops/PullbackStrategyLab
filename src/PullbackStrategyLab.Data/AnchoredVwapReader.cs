using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way the anchored average price is read, on the same terms every other store reader
/// answers: every read takes an as-of date and only observations the lab could have had by the end
/// of that date are visible.
///
/// <b>Two bounds, and they are different questions.</b> `observed_at` is when the level was
/// computed; `through_session` is the last session it includes. A read bounded only on the first
/// would answer with a level computed tonight over minutes from a session the as-of has not reached,
/// which is the point-in-time fault the whole rule exists for, and it would be invisible because the
/// price would be an ordinary price.
///
/// <b>The anchor is part of the question, not part of the answer.</b> A caller asks for a level
/// anchored at a named session, because a level anchored at a different swing is not this setup's
/// quantity even for the same name on the same night. A reader that returned "the latest anchored
/// level for this ticker" would hand back a number for the wrong move and nothing would say so.
/// </summary>
public sealed class AnchoredVwapReader
{
    private readonly StoreConnectionFactory _connections;

    public AnchoredVwapReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>One name's level for one anchor, as last observed by the end of the as-of.</summary>
    public StoredAnchoredVwap? Latest(string ticker, DateOnly anchorSession, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Latest(connection, ticker, anchorSession, asOf);
    }

    /// <summary>The same read from a connection the caller already holds.</summary>
    public static StoredAnchoredVwap? Latest(
        SqliteConnection connection, string ticker, DateOnly anchorSession, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, anchor_session, anchor_ts, anchor_kind, through_session, setup_as_of,
                   value, bars, volume, absent_because, observed_at
              FROM anchored_vwap
             WHERE ticker = @ticker
               AND anchor_session = @anchor_session
               AND through_session <= @as_of
               AND observed_at <= @observed_before
             ORDER BY through_session DESC, observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@anchor_session", StoreText.DateToStorageText(anchorSession));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredAnchoredVwap(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.IsDBNull(2) ? null : StoreText.StorageTextToTimestamp(reader.GetString(2)),
                reader.GetString(3),
                StoreText.StorageTextToDate(reader.GetString(4)),
                StoreText.StorageTextToDate(reader.GetString(5)),
                reader.IsDBNull(6) ? null : StoreText.StorageTextToPrice(reader.GetString(6)),
                reader.GetInt32(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                StoreText.StorageTextToTimestamp(reader.GetString(10)))
            : null;
    }

    /// <summary>
    /// What the engine recorded for one session, as last observed by the end of the as-of, or null
    /// where none ran.
    /// </summary>
    public static StoredVwapRun? LatestRun(SqliteConnection connection, DateOnly sessionDate, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, setup_as_of, names, sessions_priced, bars_annotated,
                   anchors_asked, anchors_priced, outcome, stopped_because, observed_at
              FROM vwap_run
             WHERE session_date = @session_date
               AND observed_at <= @observed_before
             ORDER BY observed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredVwapRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                StoreText.StorageTextToTimestamp(reader.GetString(9)))
            : null;
    }
}

/// <summary>
/// One anchored level as the store holds it.
///
/// <c>Value</c> null with <c>AbsentBecause</c> filled is the engine saying it had this anchor and
/// could not reach it, which is a different fact from no row at all.
/// </summary>
public sealed record StoredAnchoredVwap(
    string Ticker,
    DateOnly AnchorSession,
    DateTimeOffset? AnchorAt,
    string AnchorKind,
    DateOnly ThroughSession,
    DateOnly SetupAsOf,
    decimal? Value,
    int Bars,
    long Volume,
    string? AbsentBecause,
    DateTimeOffset ObservedAt);

/// <summary>What one night's engine recorded about itself.</summary>
public sealed record StoredVwapRun(
    DateOnly SessionDate,
    DateOnly SetupAsOf,
    int Names,
    int SessionsPriced,
    int BarsAnnotated,
    int AnchorsAsked,
    int AnchorsPriced,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
