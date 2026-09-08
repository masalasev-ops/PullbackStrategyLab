using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What the gate read of each version, as at a date.
///
/// <b>The latest reading of each version at or before the bound, and not every reading.</b> A
/// reading is a reading: the series underneath grows every night, so a version accumulates one row a
/// night and a ledger listing all of them would show the same version fifty times. What a reader
/// wants is where the version stands, and what makes the older rows worth keeping is that the
/// sequence is the record of a version that never matured.
///
/// <b>Bounded on `observed_at`.</b> A reading taken tomorrow is invisible to a replay of tonight,
/// and a ledger opened on an old date is a reading of what the lab knew then.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class AcceptanceReadingReader
{
    private readonly StoreConnectionFactory _connections;

    public AcceptanceReadingReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        "variant_id, observed_at, session_date, direction, generation, age_days, "
        + "nights_scored, nights_with_a_figure, nights_identical, nights_in_series, disagreements, "
        + "effective_observations, minimum_sample, minimum_sample_unit, matured, "
        + "mean_difference, interval_low, interval_high, baseline_win_rate, variant_win_rate, "
        + "verdict, settled_because, withheld_because, population";

    /// <summary>Where each version stood at the end of <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredAcceptanceReading> LatestBy(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return LatestBy(connection, asOf, sessionZone);
    }

    public static IReadOnlyList<StoredAcceptanceReading> LatestBy(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // The bound is inside the correlated maximum as well as outside it. Bounding only the outer
        // query would take the newest reading of all time and then discard it, leaving a version
        // whose latest reading is later than the bound with no row at all rather than with the
        // reading the bound could see.
        command.CommandText = $"""
            SELECT {Columns}
              FROM acceptance_reading a
             WHERE a.observed_at <= @observed_before
               AND a.observed_at = (SELECT MAX(b.observed_at)
                                      FROM acceptance_reading b
                                     WHERE b.variant_id = a.variant_id
                                       AND b.observed_at <= @observed_before)
             ORDER BY a.variant_id
            """;

        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var readings = new List<StoredAcceptanceReading>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            readings.Add(new StoredAcceptanceReading(
                reader.GetString(0),
                StoreText.StorageTextToTimestamp(reader.GetString(1)),
                StoreText.StorageTextToDate(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetString(13),
                reader.GetInt32(14) == 1,
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetString(20),
                reader.IsDBNull(21) ? null : reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                reader.GetString(23)));
        }

        return readings;
    }

    /// <summary>
    /// What the last run of or before <paramref name="asOf"/> did, or null where none has run.
    ///
    /// Null is a state. A ledger showing no reading on a version cannot otherwise tell a night the
    /// gate found nothing to settle from a night the gate never ran, and those are the two halves of
    /// the shape this corpus keeps finding.
    /// </summary>
    public static StoredAcceptanceRun? LastRunBy(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Operational rather than evidential and carrying no stamp bound of its own, on the terms
        // every other run table here stands on. The date in the predicate is the session the run was
        // for, which is what a ledger opened on an old date is asking about.
        command.CommandText = """
            SELECT session_date, observed_at, versions_live, versions_read, baselines_passed,
                   versions_matured, accepted, rejected, left_open, outcome, stopped_because
              FROM acceptance_run
             WHERE session_date <= @as_of
             ORDER BY session_date DESC, observed_at DESC
             LIMIT 1
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredAcceptanceRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToTimestamp(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            : null;
    }
}

/// <summary>
/// One reading the gate took of one version.
///
/// The five figures are text because they are decimals in the store. The interval's three are null
/// together and the two rates are null together, which the store holds as CHECKs rather than this
/// record holding it as care.
/// </summary>
public sealed record StoredAcceptanceReading(
    string VariantId,
    DateTimeOffset ObservedAt,
    DateOnly SessionDate,
    string Direction,
    int Generation,
    int AgeDays,
    int NightsScored,
    int NightsWithAFigure,
    int NightsIdentical,
    int NightsInSeries,
    int Disagreements,
    int EffectiveObservations,
    int MinimumSample,
    string MinimumSampleUnit,
    bool Matured,
    string? MeanDifference,
    string? IntervalLow,
    string? IntervalHigh,
    string? BaselineWinRate,
    string? VariantWinRate,
    string Verdict,
    string? SettledBecause,
    string? WithheldBecause,
    string Population);

/// <summary>What one run of the gate did, which is how a night that settled nothing is told from one that never ran.</summary>
public sealed record StoredAcceptanceRun(
    DateOnly SessionDate,
    DateTimeOffset ObservedAt,
    int VersionsLive,
    int VersionsRead,
    int BaselinesPassed,
    int VersionsMatured,
    int Accepted,
    int Rejected,
    int LeftOpen,
    string Outcome,
    string? StoppedBecause);
