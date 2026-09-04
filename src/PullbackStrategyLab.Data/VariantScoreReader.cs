using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// A version's difference series: one row per night per side, read as at a date.
///
/// <b>The series and not a summary of it, and that is the whole of what this returns.</b> The
/// research ledger shows a version's nightly differences, which is what the build order calls the
/// difference series being visible. It does not average them: the mean over nights against the
/// pre-registered target is what AcceptanceGate settles at <b>6.7</b>, and a read surface computing
/// it first would be the arithmetic the phase turns on, implemented twice, with the page as the
/// last place anybody looked.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>Bounded on `computed_at`.</b> A night scored on Tuesday was not scored on Monday, and a ledger
/// opened on an old date is a reading of what the lab knew then rather than a filter over what it
/// knows now.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Direction is in the key and never summed out of it.</b> A version that helps the long side
/// while hurting the short reads as no difference at all once the two are added, so the two sides
/// come back as separate rows and stay that way to the page.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class VariantScoreReader
{
    private readonly StoreConnectionFactory _connections;

    public VariantScoreReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        "variant_id, session_date, direction, generation, family, horizon_days, flagged, "
        + "baseline_selected, variant_selected, both_selected, variant_only, baseline_only, "
        + "baseline_mean_return, variant_mean_return, mean_difference, "
        + "baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at";

    /// <summary>Every scored night the lab had by the end of <paramref name="asOf"/>, oldest first.</summary>
    public IReadOnlyList<StoredVariantScore> ScoredBy(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ScoredBy(connection, asOf, sessionZone);
    }

    public static IReadOnlyList<StoredVariantScore> ScoredBy(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM variant_score
             WHERE computed_at <= @computed_before
             ORDER BY variant_id, direction, session_date
            """;

        command.Parameters.AddWithValue("@computed_before", StoreText.EndOfSession(asOf, sessionZone));

        var scores = new List<StoredVariantScore>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            scores.Add(new StoredVariantScore(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                StoreText.StorageTextToTimestamp(reader.GetString(19))));
        }

        return scores;
    }

    /// <summary>
    /// What the last scoring run of or before <paramref name="asOf"/> did, or null where none has run.
    ///
    /// <b>Null is a state and not an absence of information.</b> A ledger showing no difference on a
    /// version cannot tell a night the scorer found nothing to difference from a night the scorer
    /// never ran, and those are the two halves of the shape this corpus keeps finding: a green run
    /// is a statement about the build and never about the lab.
    /// </summary>
    public static StoredScoreRun? LastRunBy(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // `score_run` is operational rather than evidential and carries no bound of its own, on the
        // terms every other run table here stands on. The date in the predicate is the session the
        // run was for, which is what a ledger opened on an old date is asking about.
        command.CommandText = """
            SELECT session_date, observed_at, versions_live, versions_scored, nights_scored,
                   nights_waiting, longs, shorts, unscoreable, outcome, stopped_because
              FROM score_run
             WHERE session_date <= @as_of
             ORDER BY session_date DESC, observed_at DESC
             LIMIT 1
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? new StoredScoreRun(
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
/// One night of one version against the baseline, on one side.
///
/// The three return figures are text because they are decimals in the store and a double would
/// round them on the way past. They are null together, on exactly the rows carrying
/// <see cref="WithheldBecause"/>, which the store holds as a CHECK in both directions.
/// </summary>
public sealed record StoredVariantScore(
    string VariantId,
    DateOnly SessionDate,
    string Direction,
    int Generation,
    string Family,
    int HorizonDays,
    int Flagged,
    int BaselineSelected,
    int VariantSelected,
    int BothSelected,
    int VariantOnly,
    int BaselineOnly,
    string? BaselineMeanReturn,
    string? VariantMeanReturn,
    string? MeanDifference,
    int BaselineOutsideCap,
    int VariantOutsideCap,
    int Unscoreable,
    string? WithheldBecause,
    DateTimeOffset ComputedAt);

/// <summary>What one run of the scorer did.</summary>
public sealed record StoredScoreRun(
    DateOnly SessionDate,
    DateTimeOffset ObservedAt,
    int VersionsLive,
    int VersionsScored,
    int NightsScored,
    int NightsWaiting,
    int Longs,
    int Shorts,
    int Unscoreable,
    string Outcome,
    string? StoppedBecause);
