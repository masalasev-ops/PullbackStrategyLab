using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What a system with perfect foresight could have won over the same rows, against what was
/// actually won.
///
/// <b>Read per direction and never over both.</b> The bound is computed from the excursions of one
/// side's setups and the achieved fraction from the same rows, so a figure over the two together
/// would be a fraction of a population that does not exist.
/// see: Long and short are never pooled into one figure
///
/// <b>The first production read of this table, added at 6.4.</b> CeilingCalculator has written it
/// since 019 and nothing read it outside the suite, which is why the pack's ceiling-gap section is
/// the first thing to need a reader. Stated because a table with a writer and no reader looks like
/// an oversight and was a schedule.
/// </summary>
public static class CeilingBoundReader
{
    /// <summary>
    /// The bound in force for each direction at <paramref name="asOf"/>.
    ///
    /// Bounded on the instant the bound was computed rather than on its as-of, so a pack cut for an
    /// old session shows the bound the lab held then. The two differ whenever a bound is recomputed
    /// for an earlier date, which is the case the point-in-time rule exists for.
    /// </summary>
    public static IReadOnlyList<StoredCeilingBound> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);

        using SqliteCommand command = connection.CreateCommand();

        // The latest as-of per direction at or before the bound. Ordered by direction so the two
        // sides read in a settled order, which the pack needs because its body is compared byte for
        // byte.
        command.CommandText = """
            SELECT c.as_of, c.direction, c.horizon_days, c.subjects, c.bound, c.achieved
              FROM ceiling_bound c
             WHERE c.computed_at <= @bound
               AND c.as_of = (
                   SELECT MAX(i.as_of) FROM ceiling_bound i
                    WHERE i.direction = c.direction AND i.computed_at <= @bound)
             ORDER BY c.direction
            """;

        command.Parameters.AddWithValue("@bound", bound);

        var bounds = new List<StoredCeilingBound>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            bounds.Add(new StoredCeilingBound(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                StoreText.StorageTextToRatio(reader.GetString(4)),
                StoreText.StorageTextToRatio(reader.GetString(5))));
        }

        return bounds;
    }
}

/// <summary>
/// One side's ceiling: the fraction perfect foresight could have won, the fraction actually won,
/// and how many rows both were taken over.
///
/// <c>Subjects</c> travels with the two fractions because neither can be read without it. A gap of
/// twenty points over eight setups and the same gap over eight hundred are different statements.
/// </summary>
public sealed record StoredCeilingBound(
    DateOnly AsOf,
    string Direction,
    int HorizonDays,
    int Subjects,
    decimal Bound,
    decimal Achieved)
{
    /// <summary>How much room selection has left, in the same units as the two fractions.</summary>
    public decimal Gap => Bound - Achieved;
}
