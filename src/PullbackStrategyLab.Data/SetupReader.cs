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

    /// <summary>
    /// The sessions the evidence store holds a setup of one direction on, from
    /// <paramref name="from"/> up to and including the as-of.
    ///
    /// <b>This is what makes a replay one read per session rather than one per row.</b> The harness
    /// walks the history session by session, and a stage that asked the store for a row at a time
    /// would be the reason a screen took minutes instead of seconds.
    ///
    /// Bounded on the as-of like every read here. `setup` carries no observation stamp because
    /// `as_of` <i>is</i> the session it belongs to, so bounding the session is the whole of the
    /// point-in-time question for this table.
    /// </summary>
    public static IReadOnlyList<DateOnly> Sessions(
        SqliteConnection connection, string direction, DateOnly from, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT as_of
              FROM setup
             WHERE direction = @direction
               AND as_of >= @from
               AND as_of <= @as_of
             ORDER BY as_of
            """;

        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return sessions;
    }

    /// <summary>
    /// The whole flagged population up to and including <paramref name="asOf"/>, as counts.
    ///
    /// <b>Distinct from <see cref="Read(SqliteConnection, DateOnly)"/>, which is one session.</b>
    /// The pack's population section is about what the lab has accumulated rather than what it
    /// flagged last night, and reading the single-session method for that would have reported a
    /// population of nought on every date the store held no setups, while the store held hundreds.
    ///
    /// <b>Counts rather than rows.</b> The section states figures, the population grows without
    /// bound, and pulling every row into memory to count it would make the pack's cost scale with
    /// the history for no gain. The two sides are separate columns of one row and there is no field
    /// for a figure over both (see: Long and short are never pooled into one figure).
    ///
    /// Bounded on the as-of like every read here: `setup` carries no observation stamp because
    /// `as_of` is the session it belongs to.
    /// </summary>
    public static SetupPopulation PopulationTo(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   SUM(CASE WHEN direction = 'long' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN direction = 'short' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN passed_all = 1 THEN 1 ELSE 0 END),
                   COUNT(DISTINCT as_of),
                   MIN(as_of),
                   MAX(as_of)
              FROM setup
             WHERE as_of <= @as_of
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();

        // COUNT is never null and every SUM here is, on an empty table. Read as nought rather than
        // through a coalesce in the statement, so the empty case is visible in the code that has to
        // handle it.
        int total = reader.GetInt32(0);

        return new SetupPopulation(
            total,
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : StoreText.StorageTextToDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : StoreText.StorageTextToDate(reader.GetString(6)));
    }

    /// <summary>The evidence store. Written forward, one session at a time.</summary>
    public const string SetupTable = "setup";

    /// <summary>The calibration store. Read by nobody, and the reader above says so by name.</summary>
    public const string CalibrationTable = "calibration_setup";

    private static IReadOnlyList<StoredSetup> Read(SqliteConnection connection, string table, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqliteIdentifier.Validate(table);

        // The evidence table carries the night's incomplete inputs and the calibration table does
        // not, so the column is selected by name for one and as NULL for the other rather than the
        // two reads being split into two methods that would then drift. The value is a constant
        // chosen by comparing against a constant, so nothing from outside reaches the statement.
        string degraded = string.Equals(table, SetupTable, StringComparison.Ordinal)
            ? "degraded_because"
            : "NULL";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT setup_id, as_of, ticker, direction, check_results, passed_all,
                   rank, capped_out, trigger_price, stop_price, stop_distance_ranges,
                   agreement, agreement_note, {degraded}
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
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
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
    string? AgreementNote,
    string? DegradedBecause);

/// <summary>
/// The flagged population as counts: how many, per side, over how many sessions and what span.
///
/// <c>Longs</c> and <c>Shorts</c> are two fields with no total beside them, which is the pooling
/// rule made structural rather than remembered. <c>Total</c> is the count of rows and is not a
/// figure about either side.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record SetupPopulation(
    int Total,
    int Longs,
    int Shorts,
    int PassedEveryGate,
    int Sessions,
    DateOnly? FirstSession,
    DateOnly? LastSession);
