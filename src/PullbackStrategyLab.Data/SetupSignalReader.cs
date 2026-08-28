using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The frozen signal row: what was knowable on the night a setup was flagged.
///
/// Written once and never updated, which is the whole reason the row exists. Months later a
/// replay sees exactly what the decision rested on and nothing that arrived afterwards, so a
/// signal whose value could be revised would make every later comparison meaningless without
/// anything looking wrong.
///
/// Every read takes an as-of date and there is no overload that does not.
/// </summary>
public sealed class SetupSignalReader
{
    private readonly StoreConnectionFactory _connections;

    public SetupSignalReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>Every signal frozen against the setups of one session, by setup then name.</summary>
    public IReadOnlyList<StoredSetupSignal> Read(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, asOf);
    }

    /// <summary>The same read, from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredSetupSignal> Read(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        // The join bounds the setup's session. It does not bound the instant the signal was frozen,
        // and those are different facts: SCHEMA declares SignalBackfiller as a second writer whose
        // job is "adding signals to old setups", so a signal computed months later would otherwise
        // be returned as though the night had had it. The stamp is what says what the night had.
        command.CommandText = """
            SELECT s.setup_id, s.signal_name, s.value, s.computed_at
              FROM setup_signal s
              JOIN setup u ON u.setup_id = s.setup_id
             WHERE u.as_of = @as_of
               AND s.computed_at <= @computed_before
             ORDER BY s.setup_id, s.signal_name
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue(
            "@computed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        var signals = new List<StoredSetupSignal>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            signals.Add(new StoredSetupSignal(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToTimestamp(reader.GetString(3))));
        }

        return signals;
    }

    /// <summary>The signal names already frozen for one setup, which is what makes a rerun write nothing.</summary>
    public static IReadOnlySet<string> NamesFor(SqliteConnection connection, string setupId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(setupId);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT signal_name FROM setup_signal WHERE setup_id = @setup_id";
        command.Parameters.AddWithValue("@setup_id", setupId);

        var names = new HashSet<string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}

/// <summary>One frozen signal. The value is text, because the library holds words as well as numbers.</summary>
public sealed record StoredSetupSignal(string SetupId, string SignalName, string Value, DateTimeOffset ComputedAt);
