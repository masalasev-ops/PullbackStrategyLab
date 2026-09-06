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
    public IReadOnlyList<StoredSetupSignal> Read(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, asOf, sessionZone);
    }

    /// <summary>The same read, from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredSetupSignal> Read(SqliteConnection connection, DateOnly asOf, string sessionZone)
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
            "@computed_before", StoreText.EndOfSession(asOf, sessionZone));

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

    /// <summary>
    /// Every signal that describes one session's setups, including the ones a later backfill
    /// computed from what that session held.
    ///
    /// <b>A second read rather than a relaxed one, and the difference is the whole point.</b>
    /// <see cref="Read(SqliteConnection, DateOnly, string)"/> answers "what did the night's decision
    /// rest on", and bounds the instant the value was frozen, because a signal computed months later
    /// is not something the detector could have compared against. This answers "what may the lab now
    /// compute about that night", and does not, because a backfilled value is a function of the
    /// night's own inputs: SignalBackfiller passes each setup's own session as the as-of, so every
    /// read behind it is bounded where the night's read was bounded, and only the moment of
    /// computation is later.
    ///
    /// <b>Point-in-time is a property of the inputs, not of the clock the arithmetic ran on.</b>
    /// That is what makes widening the library cheap: a signal admitted today can be computed for
    /// every night the lab ever recorded, which is what the architecture means by the entire history
    /// becoming replayable immediately. Bounding this read on the stamp would leave a backfilled
    /// signal visible to nothing, and the backfill would enrich a store nobody could read.
    /// see: A reader's signature does not establish point-in-time; the query does
    ///
    /// <b>What may use it, stated because the wrong caller would be a real defect.</b> A research
    /// read replaying stored history may, because it is asking what a rule would have selected given
    /// the evidence. Anything reconstructing what the night itself saw may not, and that is the read
    /// above: a screen that showed a backfilled value as the night's own evidence would claim the
    /// detector compared against a number that did not exist yet.
    /// see: A backfilled signal is read by a replay and not by a surface, because point-in-time is a property of the inputs
    /// </summary>
    public static IReadOnlyList<StoredSetupSignal> ReadIncludingBackfilled(
        SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.setup_id, s.signal_name, s.value, s.computed_at
              FROM setup_signal s
              JOIN setup u ON u.setup_id = s.setup_id
             WHERE u.as_of = @session
             ORDER BY s.setup_id, s.signal_name
            """;

        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(asOf));

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
