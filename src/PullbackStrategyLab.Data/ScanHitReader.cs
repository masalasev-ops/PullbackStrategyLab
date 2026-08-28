using Microsoft.Data.Sqlite;

using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Scan hits, point in time like every other reader here.
///
/// The two reads the lab makes are different shapes. One scan on one night in rank order is what
/// the screens and the capper want. One ticker across a window is what the thrust check wants:
/// "appeared on an upward mover scan within the last ten days" is a question about a name's recent
/// history rather than about tonight's list.
/// </summary>
public sealed class ScanHitReader
{
    private readonly StoreConnectionFactory _connections;

    public ScanHitReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>One night's hits on one scan, in rank order.</summary>
    public IReadOnlyList<StoredScanHit> Read(DateOnly asOf, string scan)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, asOf, scan);
    }

    /// <summary>The same read, from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredScanHit> Read(SqliteConnection connection, DateOnly asOf, string scan)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(scan);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, as_of, scan, rank, magnitude, cluster_count
              FROM scan_hit
             WHERE as_of = @as_of AND scan = @scan
               AND (observed_at <= @observed_before OR (observed_at IS NULL AND as_of = @as_of))
             ORDER BY rank
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));
        command.Parameters.AddWithValue("@scan", scan);

        return Read(command);
    }

    /// <summary>
    /// One ticker's hits over the window ending at the as-of date, most recent first.
    ///
    /// The window is inclusive of both ends and is stated in sessions by the caller, but measured
    /// here in dates: a scan writes at most one row per ticker per scan per night, so counting
    /// calendar days back would silently widen the window across a long weekend. The rows carry
    /// their own dates and the caller decides what counts as recent.
    /// </summary>
    public static IReadOnlyList<StoredScanHit> ForTicker(
        SqliteConnection connection,
        string ticker,
        DateOnly asOf,
        DateOnly from)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, as_of, scan, rank, magnitude, cluster_count
              FROM scan_hit
             WHERE ticker = @ticker AND as_of >= @from AND as_of <= @as_of
               AND (observed_at <= @observed_before OR (observed_at IS NULL AND as_of = @as_of))
             ORDER BY as_of DESC, scan
            """;

        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        return Read(command);
    }

    private static IReadOnlyList<StoredScanHit> Read(SqliteCommand command)
    {
        var hits = new List<StoredScanHit>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            hits.Add(new StoredScanHit(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                StoreText.StorageTextToRatio(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }

        return hits;
    }
}

/// <summary>One scan hit as the store holds it.</summary>
public sealed record StoredScanHit(
    string Ticker,
    DateOnly AsOf,
    string Scan,
    int Rank,
    decimal Magnitude,
    int? ClusterCount);
