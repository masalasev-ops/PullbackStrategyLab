using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Checks;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A rebuild for a past date sees what that date could see, and nothing stamped afterwards.
///
/// <b>This is the half of point-in-time that only a rebuild can exercise.</b> Reading tonight, every
/// row in the store was stamped by tonight and no bound can exclude anything, so a missing bound
/// looks exactly like a bound that holds. It is the second run over a past date that can see a later
/// observation, and until 3.8 nothing in the lab had ever done one. Phase 3 added four observation
/// stamps and none of them was in <see cref="PointInTimeCheck.Stamped"/>; the eight reads that turned
/// red when they were added were all of this shape, latent rather than wrong.
///
/// The repair this checkpoint adds is the first operation that rebuilds for a past date, which is why
/// these bounds landed before it rather than at 4.1.
/// </summary>
public sealed class StampBoundTests
{
    /// <summary>
    /// A control drawn after the session is invisible to a rebuild of that session.
    ///
    /// The control draw is the one of the four whose absence was hardest to see, because a control
    /// row is transitively bounded by the setup date its query already bounds. That holds only while
    /// the draw happens on the setup's own night, which is a property of the schedule rather than of
    /// the query, and a schedule is not an assertion.
    /// </summary>
    [Fact]
    public void A_control_drawn_after_the_session_is_invisible_to_a_rebuild_of_that_session()
    {
        using var population = new AccumulationPopulation();
        population.Fill();
        population.Build();

        // Stated before the bound is exercised, because a panel that was already withheld would
        // satisfy the assertion below for a reason that has nothing to do with the draw.
        Assert.Empty(WithheldBand1(population));

        // Every control redrawn a year later, which is what a draw repeated after the fact looks
        // like. Nothing else about the store moves.
        using (SqliteConnection connection = population.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE control_setup SET drawn_at = @late";
            command.Parameters.AddWithValue("@late", "2027-01-01T00:00:00.000Z");
            command.ExecuteNonQuery();
        }

        ClearPanels(population);
        population.Build();

        // Every band 1 panel withheld, because each setup's difference is its own return less the
        // mean of its controls' and there are no longer any controls the night could see. n_rows is
        // the flagged count rather than the paired count, so it does not move and is not the
        // observable: what moves is whether the comparison exists at all.
        Assert.Equal(4, WithheldBand1(population).Count);
    }

    /// <summary>
    /// A ceiling recomputed after the session is invisible to a rebuild of that session.
    ///
    /// The bound is recomputed weekly, so one week carries more than one row over its life. Bounding
    /// the as-of alone picks the right week and still reads whichever version happens to be latest.
    /// </summary>
    [Fact]
    public void A_ceiling_recomputed_after_the_session_is_invisible_to_a_rebuild_of_that_session()
    {
        using var population = new AccumulationPopulation();
        population.Fill();

        Ceiling(population, computedAt: "2026-01-01T00:00:00.000Z", bound: "0.5000");
        population.Build();

        // The panel states the gap rather than the bound: 0.5000 against an achieved 0.2500.
        Assert.Equal("0.2500", CeilingGapFigure(population));

        // The same week recomputed, stamped after the session being rebuilt. Its key is the week and
        // the direction, so the recomputation replaces the row rather than sitting beside it, which
        // is what makes the stamp the only thing separating the two.
        Ceiling(population, computedAt: "2027-01-01T00:00:00.000Z", bound: "0.9000", replace: true);
        ClearPanels(population);
        population.Build();

        // Withheld rather than 0.6500. The rebuild cannot see a bound computed after the night, and
        // withheld is the honest answer for a night with no bound it may read.
        Assert.Equal("withheld", CeilingGapFigure(population));
    }

    /// <summary>
    /// And the list itself, in both directions, so a stamp cannot be dropped to make a read pass.
    ///
    /// The eight reads above were fixed by adding five names to a dictionary, which is exactly the
    /// edit a later session would undo to make a failing read go green. Naming them here means the
    /// undo fails a test that says what was lost rather than one that says a count moved.
    /// </summary>
    [Theory]
    [InlineData("control_setup", "drawn_at")]
    [InlineData("forward_return", "filled_at")]
    [InlineData("ceiling_bound", "computed_at")]
    [InlineData("scoreboard", "computed_at")]
    [InlineData("setup", "corrected_at")]
    [InlineData("detector_error", "observed_at")]
    public void The_stamps_added_at_3_8_are_named_by_the_check(string table, string column)
    {
        Assert.True(
            PointInTimeCheck.Stamped.TryGetValue(table, out string? stamp),
            $"{table} carries {column} and point-in-time no longer names it, so every read of it is unbounded "
            + "and nothing says so.");

        Assert.Equal(column, stamp);
    }

    /// <summary>
    /// Clears the panels a previous build wrote, which is what a genuine rebuild is.
    ///
    /// <b>Needed because the scoreboard cannot be rebuilt in place</b>, and that is worth knowing
    /// rather than working around silently: the insert is `ON CONFLICT (as_of, panel, direction) DO
    /// NOTHING`, so a second build for a date that already has panels writes none of them and the
    /// first build's rows stand. So the eight unbounded reads were latent behind a second guard as
    /// well as behind the schedule. They were still wrong, and the way they reach a row is a store
    /// restored from a snapshot and re-run, or panels deleted and rebuilt, which is exactly this.
    /// </summary>
    private static void ClearPanels(AccumulationPopulation population)
    {
        using SqliteConnection connection = population.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scoreboard";
        command.ExecuteNonQuery();
    }

    /// <summary>The band 1 panels that could not be computed, by panel and direction.</summary>
    private static IReadOnlyList<string> WithheldBand1(AccumulationPopulation population)
    {
        using SqliteConnection connection = population.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT panel || ' ' || direction FROM scoreboard
             WHERE panel LIKE 'band1.%' AND as_of = @as_of AND figure = 'withheld'
             ORDER BY panel, direction
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(population.FillOn));

        var withheld = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            withheld.Add(reader.GetString(0));
        }

        return withheld;
    }

    private static void Ceiling(AccumulationPopulation population, string computedAt, string bound, bool replace = false)
    {
        using SqliteConnection connection = population.OpenWrite();

        if (replace)
        {
            using SqliteCommand clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM ceiling_bound";
            clear.ExecuteNonQuery();
        }

        foreach (string direction in new[] { "long", "short" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ceiling_bound (as_of, direction, horizon_days, subjects, bound, achieved, computed_at)
                VALUES (@as_of, @direction, 10, 10, @bound, '0.2500', @computed_at)
                """;
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(population.FillOn));
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@bound", bound);
            command.Parameters.AddWithValue("@computed_at", computedAt);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>The figure the ceiling-gap panel wrote, which is the gap or the word withheld.</summary>
    private static string? CeilingGapFigure(AccumulationPopulation population)
    {
        using SqliteConnection connection = population.OpenRead();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT figure FROM scoreboard
             WHERE panel = 'band2.ceilingGap' AND direction = 'long' AND as_of = @as_of
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(population.FillOn));
        return command.ExecuteScalar() as string;
    }
}
