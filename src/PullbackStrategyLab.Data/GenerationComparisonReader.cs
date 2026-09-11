using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Generation 1's record against generation 0's on one night, from 7.11: the join of `setup` and
/// `setup_generation_zero` on the night, the name and the side.
///
/// <b>For comparison and nothing else.</b> Nothing in the nightly pipeline reads generation 0's
/// companion, which is what keeps its names out of the fetch, the cap and the plans; this is where the
/// two generations' verdicts on one name are put side by side, so a name the two disagree about reads
/// as a name whose rule changed.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
///
/// <b>Bounded on the companion's stamp and on the night</b>, on the point-in-time rule every reader
/// here carries: a row of generation 0's observed after the as-of is not one the night could read.
/// </summary>
public static class GenerationComparisonReader
{
    /// <summary>Every name both generations flagged on one night, per side, with each generation's verdict on it.</summary>
    public static IReadOnlyList<GenerationPair> BothFlagged(SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.setup_id, s.ticker, s.direction, s.generation, s.passed_all, z.passed_all
              FROM setup s
              JOIN setup_generation_zero z
                ON z.as_of = s.as_of AND z.ticker = s.ticker AND z.direction = s.direction
             WHERE s.as_of = @as_of
               AND z.observed_at <= @observed_before
             ORDER BY s.direction, s.ticker
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var pairs = new List<GenerationPair>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            pairs.Add(new GenerationPair(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1));
        }

        return pairs;
    }

    /// <summary>How many names generation 0 flagged on one night and one side, in its companion.</summary>
    public static int GenerationZeroFlagged(SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM setup_generation_zero
             WHERE as_of = @as_of
               AND direction = @direction
               AND observed_at <= @observed_before
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>One name both generations flagged, with whether each passed every gating clause of its own list.</summary>
public sealed record GenerationPair(
    string SetupId,
    string Ticker,
    string Direction,
    int Generation,
    bool PassedAllGenerationOne,
    bool PassedAllGenerationZero);
