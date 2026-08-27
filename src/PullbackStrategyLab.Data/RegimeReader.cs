using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The market mood on one night.
///
/// It returns both raw scores as well as the label, and callers are expected to use them. The label
/// is three buckets over a continuous thing, and a proposal wanting the continuous form should not
/// have to reconstruct it.
///
/// <b>Nothing may read this as a condition in the baseline.</b> The label is recorded against every
/// setup and gates nothing, which is what keeps it available as a clean experiment; a test asserts
/// no component branches on it, because a filter is exactly what this would silently become.
/// see: The market-mood label is recorded on every setup and filters nothing in the baseline
/// </summary>
public static class RegimeReader
{
    /// <summary>The label for one session, or null where the night has not been labelled.</summary>
    public static StoredRegime? Read(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT as_of, index_score, breadth_score, label, long_ladder_count, short_ladder_count, indexes_above
              FROM regime_daily
             WHERE as_of = @as_of
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new StoredRegime(
            StoreText.StorageTextToDate(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6));
    }
}

/// <summary>One night's mood, with both scores and both raw counts beside the label.</summary>
public sealed record StoredRegime(
    DateOnly AsOf,
    int IndexScore,
    int BreadthScore,
    string Label,
    int LongLadderCount,
    int ShortLadderCount,
    int IndexesAbove);
