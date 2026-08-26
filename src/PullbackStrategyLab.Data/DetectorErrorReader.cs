using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What a detector could not decide, one row per stock per night per direction.
///
/// <b>A silent skip is the failure the table exists to prevent.</b> Every count downstream is over
/// the setups that were recorded, so a name the detector could not read is simply absent: the night
/// looks lighter, the counts stay plausible, and nothing anywhere says a name was lost. The failure
/// table in ARCHITECTURE.html names it for that reason, and the run that lost one is recorded
/// `partial` rather than `clean`.
///
/// Nothing reads these rows to make a decision. They are counted, and read by a person asking what
/// last night lost, which is why this reader takes a date and returns both directions rather than
/// carrying the point-in-time machinery the evidence readers do.
/// </summary>
public static class DetectorErrorReader
{
    /// <summary>The table, named once, so a reader can find its two writers from here.</summary>
    public const string Table = "detector_error";

    /// <summary>What one night lost, both directions, ordered so two runs read the same.</summary>
    public static IReadOnlyList<StoredDetectorError> Read(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT as_of, ticker, direction, message, observed_at FROM detector_error
             WHERE as_of = @as_of
             ORDER BY ticker, direction
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var errors = new List<StoredDetectorError>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            errors.Add(new StoredDetectorError(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                StoreText.StorageTextToTimestamp(reader.GetString(4))));
        }

        return errors;
    }

    /// <summary>What a caught exception is recorded as, so two nights' rows are comparable.</summary>
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return $"{error.GetType().Name}: {error.Message}";
    }
}

/// <summary>One name a detector could not decide on one night.</summary>
public sealed record StoredDetectorError(
    DateOnly AsOf,
    string Ticker,
    string Direction,
    string Message,
    DateTimeOffset ObservedAt);
