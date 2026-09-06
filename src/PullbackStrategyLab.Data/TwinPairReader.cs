using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The twin pairs the lab has found, and what each run's window actually held.
///
/// <b>The run rows are the half a reader must not skip.</b> A page showing pairs alone would show
/// nothing for months and could not say whether that is because no pair qualified or because there
/// was no window to look in. The window figure is a property of the run rather than of any pair, so
/// it is read from the run row and shown beside the count even when the count is nought.
/// see: The twin-pair threshold is reviewed at the first full window rather than at a phase
///
/// <b>The latest generation at or before the bound, never every generation.</b> A rerun of a date
/// writes a new generation beside the old, so an unfiltered read would show one date's pairs twice
/// and count them twice.
/// see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
/// </summary>
public static class TwinPairReader
{
    /// <summary>
    /// The most recent run of each side at or before <paramref name="asOf"/>, and the pairs that run
    /// found.
    ///
    /// Bounded on the run's own instant, so a page opened for an old session shows what the lab
    /// held then rather than what it holds now.
    /// </summary>
    public static IReadOnlyList<TwinSideReading> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);
        var readings = new List<TwinSideReading>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            // One row per side: the latest run at or before the bound, by session then by instant,
            // which is what "the generation in force" means for this table.
            command.CommandText = """
                SELECT r.direction, r.as_of, r.window_setups, r.window_wanted, r.signals_compared,
                       r.candidate_pairs, r.pairs_found, r.empty_because, r.observed_at
                  FROM twin_run r
                 WHERE r.observed_at <= @bound
                   AND r.observed_at = (
                       SELECT MAX(i.observed_at) FROM twin_run i
                        WHERE i.direction = r.direction AND i.observed_at <= @bound)
                 ORDER BY r.direction
                """;

            command.Parameters.AddWithValue("@bound", bound);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                readings.Add(new TwinSideReading(
                    reader.GetString(0),
                    StoreText.StorageTextToDate(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    []));
            }
        }

        return [.. readings.Select(r => r with { Pairs = Pairs(connection, r, bound) })];
    }

    /// <summary>The pairs one run wrote, ordered by distance so the closest pair reads first.</summary>
    private static IReadOnlyList<StoredTwinPair> Pairs(
        SqliteConnection connection, TwinSideReading side, string bound)
    {
        if (side.PairsFound == 0)
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.pair_id, p.left_setup_id, p.right_setup_id, p.distance, p.gap_points,
                   p.left_outcome, p.right_outcome, p.signals_compared, p.window_setups
              FROM twin_pair p
             WHERE p.direction = @direction
               AND p.as_of = @as_of
               AND p.observed_at <= @bound
               AND p.observed_at = (
                   SELECT MAX(i.observed_at) FROM twin_pair i
                    WHERE i.direction = p.direction AND i.as_of = p.as_of AND i.observed_at <= @bound)
             ORDER BY p.distance, p.pair_id
            """;

        command.Parameters.AddWithValue("@direction", side.Direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(side.AsOf));
        command.Parameters.AddWithValue("@bound", bound);

        var pairs = new List<StoredTwinPair>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            pairs.Add(new StoredTwinPair(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToStatistic(reader.GetString(3)),
                StoreText.StorageTextToStatistic(reader.GetString(4)),
                StoreText.StorageTextToStatistic(reader.GetString(5)),
                StoreText.StorageTextToStatistic(reader.GetString(6)),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return pairs;
    }
}

/// <summary>
/// One side's latest reading: what its window held, what it looked at, and what it found.
///
/// The window figures travel with the count because the count cannot be read without them. Nought
/// twins over a window of four and nought over a window of two hundred and fifty are different
/// statements, and only the second says anything about the thresholds.
/// </summary>
public sealed record TwinSideReading(
    string Direction,
    DateOnly AsOf,
    int WindowSetups,
    int WindowWanted,
    int SignalsCompared,
    long CandidatePairs,
    int PairsFound,
    string? EmptyBecause,
    IReadOnlyList<StoredTwinPair> Pairs);

/// <summary>One stored pair, with what its two figures were computed over.</summary>
public sealed record StoredTwinPair(
    string PairId,
    string LeftSetupId,
    string RightSetupId,
    double Distance,
    double GapPoints,
    double LeftOutcome,
    double RightOutcome,
    int SignalsCompared,
    int WindowSetups);
