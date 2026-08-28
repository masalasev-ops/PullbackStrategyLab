using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Setups as of a night, and the frozen signals beside them.
///
/// Every read takes an as-of date and there is no overload that does not, on the same terms as
/// every other reader here. A read that could omit it would compile, run, and return a setup the
/// lab could not have seen.
/// </summary>
public sealed class SetupReader
{
    private readonly StoreConnectionFactory _connections;

    public SetupReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>The setups flagged on one session, both directions, in ticker order.</summary>
    public IReadOnlyList<StoredSetup> Read(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, asOf);
    }

    /// <summary>The setups flagged on one session, from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredSetup> Read(SqliteConnection connection, DateOnly asOf) =>
        Read(connection, SetupTable, asOf);

    /// <summary>
    /// The same read against the calibration table, which no downstream component may make.
    ///
    /// Separated by an explicit table name rather than offered as a default, because the two
    /// tables hold different things: one is evidence and one carries survivorship bias by
    /// construction. A caller has to say which it means.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    public static IReadOnlyList<StoredSetup> ReadCalibration(SqliteConnection connection, DateOnly asOf) =>
        Read(connection, CalibrationTable, asOf);

    /// <summary>The evidence store. Written forward, one session at a time.</summary>
    public const string SetupTable = "setup";

    /// <summary>The calibration store. Read by nobody, and the reader above says so by name.</summary>
    public const string CalibrationTable = "calibration_setup";

    private static IReadOnlyList<StoredSetup> Read(SqliteConnection connection, string table, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqliteIdentifier.Validate(table);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT setup_id, as_of, ticker, direction, check_results, passed_all,
                   rank, capped_out, trigger_price, stop_price, stop_distance_ranges,
                   agreement, agreement_note
              FROM {table}
             WHERE as_of = @as_of
             ORDER BY direction, ticker
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var setups = new List<StoredSetup>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            setups.Add(new StoredSetup(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? null : StoreText.StorageTextToPrice(reader.GetString(8)),
                reader.IsDBNull(9) ? null : StoreText.StorageTextToPrice(reader.GetString(9)),
                reader.IsDBNull(10) ? null : StoreText.StorageTextToRatio(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return setups;
    }
}

/// <summary>One setup as the store holds it.</summary>
public sealed record StoredSetup(
    string SetupId,
    DateOnly AsOf,
    string Ticker,
    string Direction,
    string CheckResults,
    bool PassedAll,
    int? Rank,
    bool? CappedOut,
    decimal? TriggerPrice,
    decimal? StopPrice,
    decimal? StopDistanceRanges,
    string? Agreement,
    string? AgreementNote);
