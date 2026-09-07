using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The pack versions the lab has cut, and what each cut of the pack held.
///
/// <b>A version is a definition and a run is a reading, which is why they are two tables and only
/// one of them has a generation key.</b> The same tuple computed twice is the same version and
/// finds the row it already has; the same date packed twice is two readings and writes two run
/// rows, because the evidence underneath moved.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
/// </summary>
public static class PackVersionReader
{
    /// <summary>
    /// Every version created at or before <paramref name="asOf"/>, in ordinal order.
    ///
    /// Bounded on creation, so a page opened for an old session shows the versions that existed
    /// then. A proposal cites a version, and a hit-rate table for a session must not show versions
    /// cut after it.
    /// </summary>
    public static IReadOnlyList<StoredPackVersion> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.version, v.fingerprint, v.sections, v.signals_screened, v.signals_screened_count,
                   v.correction_form, v.correction_level, v.family_wise_threshold, v.model_identifier
              FROM pack_version v
             WHERE v.created_at <= @bound
             ORDER BY v.version
            """;

        command.Parameters.AddWithValue("@bound", bound);

        var versions = new List<StoredPackVersion>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            versions.Add(new StoredPackVersion(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetString(5),
                StoreText.StorageTextToStatistic(reader.GetString(6)),
                reader.IsDBNull(7) ? null : StoreText.StorageTextToStatistic(reader.GetString(7)),
                reader.GetString(8)));
        }

        return versions;
    }

    /// <summary>
    /// The version a fingerprint already has, or null where the tuple is new.
    ///
    /// Unbounded on purpose, and it is the one read here that is: this asks whether a row exists
    /// rather than what the lab knew on a date, and bounding it would let the packer write a second
    /// version row for a tuple the store already holds. The unique index would then refuse the
    /// insert, so the bound would turn a correct reuse into a failure.
    /// </summary>
    public static int? VersionFor(SqliteConnection connection, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM pack_version WHERE fingerprint = @fingerprint";
        command.Parameters.AddWithValue("@fingerprint", fingerprint);

        object? found = command.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt32(found);
    }

    /// <summary>
    /// The latest cut of the pack at or before <paramref name="asOf"/>, or null where none was
    /// taken.
    ///
    /// The latest generation rather than every one: a date packed twice writes two run rows and the
    /// second is what the lab holds. The stale one stays readable as it stood.
    /// see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
    /// </summary>
    public static StoredPackRun? LatestRun(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.as_of, r.version, r.body_digest, r.body_bytes, r.sections_rendered,
                   r.sections_empty, r.signals_screened, r.false_discovery_bar,
                   r.long_setups, r.short_setups, r.null_control_planted, r.outcome,
                   r.refused_because
              FROM pack_run r
             WHERE r.observed_at <= @bound
             ORDER BY r.as_of DESC, r.observed_at DESC
             LIMIT 1
            """;

        command.Parameters.AddWithValue("@bound", bound);

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new StoredPackRun(
            StoreText.StorageTextToDate(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.IsDBNull(7) ? null : StoreText.StorageTextToStatistic(reader.GetString(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10) == 1,
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }
}

/// <summary>One version: what the model was shown and what it was judged under.</summary>
public sealed record StoredPackVersion(
    int Version,
    string Fingerprint,
    string Sections,
    string SignalsScreened,
    int SignalsScreenedCount,
    string CorrectionForm,
    double CorrectionLevel,
    double? FamilyWiseThreshold,
    string ModelIdentifier);

/// <summary>
/// One cut of the pack: which version it was, what it held, and the populations it was built over.
///
/// <c>SectionsEmpty</c> against <c>SectionsRendered</c> is the figure that makes an almost-empty
/// pack legible as almost-empty rather than as a pack. Five of the nine rest on outcomes that have
/// not closed, so it is expected to read five for months.
/// </summary>
public sealed record StoredPackRun(
    DateOnly AsOf,
    int? Version,
    string? BodyDigest,
    int? BodyBytes,
    int SectionsRendered,
    int SectionsEmpty,
    int SignalsScreened,
    double? FalseDiscoveryBar,
    int LongSetups,
    int ShortSetups,
    bool NullControlPlanted,
    string Outcome,
    string? RefusedBecause)
{
    /// <summary>
    /// Whether this night wrote a pack at all.
    ///
    /// A refused night carries no version and no digest, because a pack missing a section is not a
    /// smaller pack: the correction is computed over the signals screened, so a short pack would
    /// carry a threshold for a set it did not screen. The reason is on the row instead.
    /// </summary>
    public bool PackWasBuilt => RefusedBecause is null;
}
