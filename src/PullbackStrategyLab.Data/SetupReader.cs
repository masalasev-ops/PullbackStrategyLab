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
    /// <b>That last sentence was here and false until 7.13.</b> The record carried a total and a
    /// count of rows that passed every gate over both sides, and the pack printed both, so the
    /// sentence describing the shape was written beside two fields that broke it. What is returned
    /// now is per side including the session count each side's rate is over, and the only thing
    /// answered over both is whether there is a population at all.
    ///
    /// Bounded on the as-of like every read here: `setup` carries no observation stamp because
    /// `as_of` is the session it belongs to.
    /// </summary>
    public static SetupPopulation PopulationTo(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Every count here is per side, including the sessions each side's rate is taken over, and
        // no column of this statement adds the two together. A total was returned beside them until
        // 7.13 and the pack printed it, so the reading is per side from the statement outward rather
        // than at the surface (see: Long and short are never pooled into one figure).
        command.CommandText = """
            SELECT SUM(CASE WHEN direction = 'long' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN direction = 'short' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN direction = 'long' AND passed_all = 1 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN direction = 'short' AND passed_all = 1 THEN 1 ELSE 0 END),
                   COUNT(DISTINCT CASE WHEN direction = 'long' THEN as_of END),
                   COUNT(DISTINCT CASE WHEN direction = 'short' THEN as_of END),
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
        return new SetupPopulation(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : StoreText.StorageTextToDate(reader.GetString(7)),
            reader.IsDBNull(8) ? null : StoreText.StorageTextToDate(reader.GetString(8)));
    }

    /// <summary>The evidence store. Written forward, one session at a time.</summary>
    public const string SetupTable = "setup";

    /// <summary>
    /// The generation a night was first detected under, or null where it has not been detected, from
    /// 7.11: the highest generation any of its `setup` or `below_floor` rows carries.
    ///
    /// <b>A night keeps the generation its first detection scored it under.</b> The switch reads the
    /// register, and a rerun of a night after generation 1 was registered that evening would otherwise
    /// score its second pass under the other gate set, leaving one night with two lists in it. Across both
    /// sides, so a registration that lands between the long and the short slot cannot split a night either.
    /// </summary>
    public static int? GenerationRecordedOn(SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(generation) FROM (
                SELECT generation FROM setup WHERE as_of = @as_of
                UNION ALL
                SELECT generation FROM below_floor WHERE as_of = @as_of AND observed_at <= @observed_before)
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

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

        // The gate set a row was scored with, from 7.11, on the same terms: the evidence table records
        // it and a calibration walk is generation 0's by construction.
        string generation = string.Equals(table, SetupTable, StringComparison.Ordinal)
            ? "generation"
            : "0";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT setup_id, as_of, ticker, direction, check_results, passed_all,
                   rank, capped_out, trigger_price, stop_price, stop_distance_ranges,
                   agreement, agreement_note, {degraded}, {generation}
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
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetInt32(14)));
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
    string? DegradedBecause,
    // Which generation's gate set scored the row, from 7.11: nought before the switch night, one after.
    int Generation = 0);

/// <summary>
/// The flagged population as counts: how many, per side, over how many sessions and what span.
///
/// <c>Longs</c> and <c>Shorts</c> are two fields with no total beside them, which is the pooling
/// rule made structural rather than remembered. <c>Total</c> is the count of rows and is not a
/// figure about either side.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record SetupPopulation(
    int Longs,
    int Shorts,
    int PassedEveryGateLong,
    int PassedEveryGateShort,
    int LongSessions,
    int ShortSessions,
    int Sessions,
    DateOnly? FirstSession,
    DateOnly? LastSession)
{
    /// <summary>
    /// Whether the record holds any setup at all, which is the one question both sides answer at once.
    ///
    /// <b>A test rather than a figure, and that is the whole of the difference.</b> The two sides are
    /// never added into one number a reader could quote; whether either side has a row is a property
    /// of the record and is what says a population section has nothing to describe
    /// (see: Long and short are never pooled into one figure).
    /// </summary>
    public bool IsEmpty => Longs == 0 && Shorts == 0;
}
