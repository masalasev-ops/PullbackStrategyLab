using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The names a forward night examined and did not record, kept with their verdicts from 7.3.
///
/// Population: the golden fixture's night, replayed into a store of its own, 7,202 universe members a
/// side of which each side records one. Every figure here is read back from the store the detectors
/// wrote, because a count the detector reports and a row it wrote are two different claims and only
/// the second is what a replayed floor would read.
/// </summary>
public sealed class BelowFloorTests
{
    [Fact]
    public void Every_name_a_side_examined_is_in_setup_or_below_the_floor_and_never_both()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        using SqliteConnection connection = replay.OpenStore();

        foreach (string direction in new[] { "long", "short" })
        {
            long below = Scalar(connection, "SELECT COUNT(*) FROM below_floor WHERE direction = @d", direction);
            long both = Scalar(connection, """
                SELECT COUNT(*) FROM below_floor b
                  JOIN setup s ON s.as_of = b.as_of AND s.ticker = b.ticker AND s.direction = b.direction
                 WHERE b.direction = @d
                   -- The detectors' own rows, by the id they write. The fixture authors one long
                   -- setup for the vectorizer, IESC, which the captured night does not flag and
                   -- which is therefore below the floor as the detector reads it: an authored row
                   -- beside a captured verdict, and not a name the detector put on both sides.
                   AND s.setup_id = b.as_of || '-' || b.ticker || '-' || b.direction
                """, direction);

            Assert.Equal(7201, below);
            Assert.Equal(0, both);
        }

        // Every row names at least one floor clause it failed, and names only floor clauses.
        Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM below_floor WHERE failed_floor = ''", "long"));

        using SqliteCommand clauses = connection.CreateCommand();
        clauses.CommandText = "SELECT DISTINCT failed_floor FROM below_floor WHERE direction = 'long'";
        using SqliteDataReader reader = clauses.ExecuteReader();

        while (reader.Read())
        {
            Assert.All(reader.GetString(0).Split(','), name => Assert.Contains(name, LongSetupDetector.RecordingFloor));
        }
    }

    [Fact]
    public void A_night_detected_twice_keeps_one_row_a_name()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        replay.DetectLong();

        using SqliteConnection connection = replay.OpenStore();
        Assert.Equal(7201, Scalar(connection, "SELECT COUNT(*) FROM below_floor WHERE direction = @d", "long"));
    }

    [Fact]
    public void A_calibration_walk_writes_no_below_floor_row()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        long before;
        using (SqliteConnection connection = replay.OpenStore())
        {
            before = Scalar(connection, "SELECT COUNT(*) FROM below_floor WHERE direction = @d", "long");
        }

        replay.CalibrateLong(replay.AsOf, replay.AsOf);

        using SqliteConnection after = replay.OpenStore();
        Assert.Equal(before, Scalar(after, "SELECT COUNT(*) FROM below_floor WHERE direction = @d", "long"));
    }

    private static long Scalar(SqliteConnection connection, string sql, string direction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@d", direction);
        return (long)command.ExecuteScalar()!;
    }
}
