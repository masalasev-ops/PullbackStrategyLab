using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The register of rule versions, read as at a date.
///
/// <b>Bounded on `created_at`, and that is not a formality.</b> A version registered on Tuesday was
/// not running on Monday, so a replay of Monday that saw it would fan a plan out to a version the
/// night had never heard of and then measure the difference. The bound is what makes a replay of an
/// evening return the versions that evening actually had.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>It does not bound on `status`, and that is deliberate.</b> AcceptanceGate settles a version
/// long after the nights it accumulated over, so bounding a night's live set on today's status would
/// erase a resolved version from the nights it ran on. What decides whether a version is live on a
/// night is that it existed by then and belongs to the generation in force, which is what
/// <see cref="LiveOn"/> answers.
/// </summary>
public sealed class VariantReader
{
    private readonly StoreConnectionFactory _connections;

    public VariantReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    private const string Columns =
        "variant_id, generation, family, definition, target, minimum_sample, minimum_sample_unit, "
        + "status, resolved_at, created_at";

    /// <summary>Every version the lab had registered by the end of <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredVariant> RegisteredBy(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return RegisteredBy(connection, asOf, sessionZone);
    }

    public static IReadOnlyList<StoredVariant> RegisteredBy(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM variant
             WHERE created_at <= @observed_before
             ORDER BY generation, family, variant_id
            """;

        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));
        return Materialise(command);
    }

    /// <summary>
    /// The versions a night fans a plan out to, which is the current generation's registered set.
    ///
    /// <b>The baseline is one of them rather than a thing beside them.</b> It is a version with a
    /// definition, a target and a minimum sample like any other, and a night that treated it as the
    /// absence of a version would have a plan belonging to nothing.
    /// </summary>
    public static IReadOnlyList<StoredVariant> LiveOn(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        IReadOnlyList<StoredVariant> registered = RegisteredBy(connection, asOf, sessionZone);

        if (registered.Count == 0)
        {
            return [];
        }

        // The generation in force is the highest any registered version belongs to. Editing the
        // baseline starts a new one and closes the versions of the old as unresolved, so the older
        // generation's rows stay readable and stop being fanned out to.
        int generation = registered.Max(v => v.Generation);
        return [.. registered.Where(v => v.Generation == generation)];
    }

    /// <summary>The baseline of the generation in force, or null before one is registered.</summary>
    public static StoredVariant? BaselineOn(
        SqliteConnection connection, DateOnly asOf, string sessionZone) =>
        LiveOn(connection, asOf, sessionZone)
            .SingleOrDefault(v => v.Family == VariantFamily.Baseline);

    private static IReadOnlyList<StoredVariant> Materialise(SqliteCommand command)
    {
        var variants = new List<StoredVariant>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            variants.Add(new StoredVariant(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : StoreText.StorageTextToTimestamp(reader.GetString(8)),
                StoreText.StorageTextToTimestamp(reader.GetString(9))));
        }

        return variants;
    }
}

/// <summary>
/// One registered rule version.
///
/// <see cref="MinimumSample"/> is an integer and <see cref="MinimumSampleUnit"/> says what it counts,
/// because a selection version's minimum is in effective observations and an execution version's is
/// in rows, and the two are not comparable.
/// </summary>
public sealed record StoredVariant(
    string VariantId,
    int Generation,
    string Family,
    string Definition,
    string Target,
    int MinimumSample,
    string MinimumSampleUnit,
    string Status,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset CreatedAt)
{
    public bool IsBaseline => Family == VariantFamily.Baseline;

    /// <summary>How the register reads on a screen and in a run line, unit included.</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{VariantId} (generation {Generation}, {Family}, {Status}): {MinimumSample} {MinimumSampleUnit}");
}
