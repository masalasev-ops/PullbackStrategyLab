using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The signal library as the store holds it: the specification seeded from SCHEMA's Signals
/// section, plus the verdicts the admission test has recorded against it.
///
/// <b>Bounded on `observed_at`, and the whole row is invisible past the bound rather than only its
/// verdict.</b> The first draft of this reader bounded `decided_at` on the reasoning that the
/// specification half is not a measurement. That is wrong for the read this exists to serve: the
/// packer screens every signal the library holds and the correction threshold is computed over that
/// set, so a pack cut for an old date that could see a signal seeded afterwards would state a
/// threshold for a set that night never screened. `decided_at` is the verdict's own date and
/// travels with the row rather than being a second stamp to bound on.
/// see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
/// see: The correction threshold scales with signals screened, not signals shown
/// </summary>
public static class SignalDefinitionReader
{
    /// <summary>
    /// Every signal the library held at <paramref name="asOf"/>, with the verdict it carried then.
    ///
    /// A row observed after the bound is invisible entirely, so a pack cut for an old date screens
    /// the set that date screened.
    /// </summary>
    public static IReadOnlyList<StoredSignalDefinition> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);

        using SqliteCommand command = connection.CreateCommand();

        // Ordered by name so the library section of a pack renders in a settled order. The pack is
        // compared byte for byte, so an unordered read here would break byte-stability in a way
        // that only showed up on a store whose page order happened to differ.
        command.CommandText = """
            SELECT d.signal_name, d.formula, d.source_columns, d.status, d.is_null_control,
                   d.long_outcome, d.short_outcome, d.decided_at
              FROM signal_definition d
             WHERE d.observed_at <= @bound
             ORDER BY d.signal_name
            """;

        command.Parameters.AddWithValue("@bound", bound);

        var definitions = new List<StoredSignalDefinition>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            definitions.Add(new StoredSignalDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : StoreText.StorageTextToTimestamp(reader.GetString(7))));
        }

        return definitions;
    }
}

/// <summary>
/// One signal as the store holds it: what it is, and what each side's admission said about it.
///
/// The two verdicts are separate fields and are never combined into one. A signal admitted for
/// shorts and undecided for longs is one signal with two answers, and a single field would have to
/// pick one of them.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record StoredSignalDefinition(
    string Name,
    string Formula,
    string SourceColumns,
    string Status,
    bool IsNullControl,
    string? LongOutcome,
    string? ShortOutcome,
    DateTimeOffset? DecidedAt);
