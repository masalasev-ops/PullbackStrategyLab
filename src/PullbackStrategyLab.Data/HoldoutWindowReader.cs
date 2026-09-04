using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The holdout register as the store holds it: which windows are recorded and which of those are
/// spent.
///
/// Every read takes an as-of date and there is no overload that does not, on the same terms as
/// every other reader here. A window matures on a date and a spend happens on a date, so a read of
/// an old evening that saw either arriving later would report a budget the lab did not have.
/// </summary>
public sealed class HoldoutWindowReader
{
    private readonly StoreConnectionFactory _connections;

    public HoldoutWindowReader(StoreConnectionFactory connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    /// <summary>Every window the register holds as of a date, oldest first.</summary>
    public IReadOnlyList<StoredHoldoutWindow> Read(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, asOf, sessionZone);
    }

    /// <summary>The same read, from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredHoldoutWindow> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Bounded three times and on three different facts, and all three are needed.
        //
        // A window that had not matured by the as-of is not a window the lab had, which is the
        // calendar bound. A window the register had not yet recorded is not one the lab could have
        // spent either, which is the observation bound: a registry that ran late records a window
        // with a stamp later than the evenings it was already mature on, and a read that ignored the
        // stamp would report a budget nobody held. And a spend recorded after the as-of is not a
        // spend the lab had made, which is the third.
        //
        // <b>The first two are separate on purpose and the difference is the whole of what tells an
        // empty register from a defective one.</b> Matured is what the calendar says; recorded is
        // what the store holds; a caller comparing them is what notices a registry that never ran.
        command.CommandText = """
            SELECT w.window_id, w.ordinal, w.quarter_start, w.quarter_end, w.matures_on,
                   s.spent_on, s.outcome, s.spent_at
              FROM holdout_window w
              LEFT JOIN holdout_spend s
                     ON s.window_id = w.window_id
                    AND s.spent_at <= @spent_before
             WHERE w.matures_on <= @as_of
               AND w.recorded_at <= @recorded_before
             ORDER BY w.ordinal
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@spent_before", StoreText.EndOfSession(asOf, sessionZone));
        command.Parameters.AddWithValue("@recorded_before", StoreText.EndOfSession(asOf, sessionZone));

        var windows = new List<StoredHoldoutWindow>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            windows.Add(new StoredHoldoutWindow(
                new HoldoutWindow(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    StoreText.StorageTextToDate(reader.GetString(2)),
                    StoreText.StorageTextToDate(reader.GetString(3)),
                    StoreText.StorageTextToDate(reader.GetString(4))),
                reader.IsDBNull(5)
                    ? null
                    : new HoldoutSpend(
                        reader.GetString(5),
                        reader.GetString(6),
                        StoreText.StorageTextToTimestamp(reader.GetString(7)))));
        }

        return windows;
    }

    /// <summary>
    /// The earliest session the evidence store holds a setup for, which is what the whole schedule
    /// is computed from, or null where it holds none.
    ///
    /// <b>Read from the store rather than authored as a go-live date.</b> A constant would be a
    /// second statement of when the lab started, and the day the two disagreed the register would
    /// name quarters nobody has evidence for. This one cannot disagree with the evidence, because
    /// it is the evidence.
    /// </summary>
    public static DateOnly? FirstSession(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(as_of) FROM setup WHERE as_of <= @as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        object? value = command.ExecuteScalar();

        return value is string text && text.Length > 0 ? StoreText.StorageTextToDate(text) : null;
    }
}

/// <summary>One window as the register holds it, with its spend where it has one.</summary>
public sealed record StoredHoldoutWindow(HoldoutWindow Window, HoldoutSpend? Spend)
{
    /// <summary>Whether this window is still available to spend.</summary>
    public bool IsAvailable => Spend is null;
}

/// <summary>What a window was spent on, and what came of it.</summary>
public sealed record HoldoutSpend(string SpentOn, string Outcome, DateTimeOffset SpentAt);
