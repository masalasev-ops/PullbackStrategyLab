using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// The two detectors share one table and are separated by direction and by nothing else.
///
/// SCHEMA declares them disjoint by `direction`, which is a claim about rows rather than about
/// columns: either detector could write either value and the store would accept it, because the
/// column check allows both. So the disjointness is asserted here, in both directions, over the
/// rows a real run produced rather than over rows written by hand.
///
/// <b>Why it matters more than it looks.</b> Everything downstream counts by direction, and long
/// and short results are never pooled because a short carries a borrow assumption a long does not.
/// A row attributed to the wrong side would move a figure from one column to the other, and both
/// columns would still look entirely reasonable.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class SetupDirectionTests
{
    /// <summary>
    /// The identity a detector mints carries its own direction and never the other's.
    ///
    /// Pure, so it holds whether or not a run has happened. This is the half that would still pass
    /// if the fixture recorded nothing, which is why the behavioural half below exists too.
    /// </summary>
    [Fact]
    public void Each_detector_mints_identities_of_its_own_direction_only()
    {
        var date = new DateOnly(2026, 8, 24);

        Assert.NotEqual(LongSetupDetector.Direction, ShortSetupDetector.Direction);
        Assert.EndsWith($"-{LongSetupDetector.Direction}", LongSetupDetector.SetupId("AAA", date), StringComparison.Ordinal);
        Assert.EndsWith($"-{ShortSetupDetector.Direction}", ShortSetupDetector.SetupId("AAA", date), StringComparison.Ordinal);

        Assert.DoesNotContain(ShortSetupDetector.Direction, LongSetupDetector.SetupId("AAA", date), StringComparison.Ordinal);
        Assert.DoesNotContain(LongSetupDetector.Direction, ShortSetupDetector.SetupId("AAA", date), StringComparison.Ordinal);
    }

    /// <summary>
    /// Over the rows the fixture run actually wrote, both ways.
    ///
    /// Both ways is the point. Reading one direction would catch a long detector writing a short
    /// row and miss the short detector writing a long one, and the two are the same defect.
    /// </summary>
    [Fact]
    public void No_setup_row_carries_a_direction_its_detector_does_not_own()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        PhaseReplayResult result = replay.Run();

        using SqliteConnection connection = replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setup_id, ticker, as_of, direction FROM setup ORDER BY setup_id";

        var problems = new List<string>();
        int rows = 0;
        int longRows = 0;
        int shortRows = 0;

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string setupId = reader.GetString(0);
                string ticker = reader.GetString(1);
                DateOnly asOf = StoreText.StorageTextToDate(reader.GetString(2));
                string direction = reader.GetString(3);

                rows++;

                switch (direction)
                {
                    case LongSetupDetector.Direction:
                        longRows++;
                        break;
                    case ShortSetupDetector.Direction:
                        shortRows++;
                        break;
                    default:
                        problems.Add($"{setupId} carries the direction \"{direction}\", which neither detector owns");
                        continue;
                }

                // The identity and the column have to agree, because the identity is what attributes
                // a row to the detector that minted it. A row whose id says one thing and whose
                // column says another is a row nothing can attribute, and everything downstream
                // reads the column.
                string minted = direction == LongSetupDetector.Direction
                    ? LongSetupDetector.SetupId(ticker, asOf)
                    : ShortSetupDetector.SetupId(ticker, asOf);

                // The fixture's one authored setup is minted by the replay rather than by a
                // detector, and its id says so by not carrying a date. Named rather than skipped
                // silently, so a second unattributed row would have to be named too.
                if (!string.Equals(setupId, minted, StringComparison.Ordinal)
                    && !string.Equals(setupId, $"{PhaseReplay.AuthoredSetupTicker}-long", StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{setupId} carries direction \"{direction}\", whose detector would have minted \"{minted}\"");
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} row(s) attributed to the wrong detector:\n  " + string.Join("\n  ", problems));

        // A run that recorded nothing would satisfy every assertion above, so the counts are stated
        // rather than left to be inferred: the property is only being held if there were rows to
        // hold it over, and both sides have to have written one.
        Assert.True(longRows > 0 && shortRows > 0,
            $"{longRows} long row(s) and {shortRows} short row(s). With one side empty this asserts "
            + "the disjointness of a set against nothing.");

        Assert.Equal(rows, longRows + shortRows);

        // And the detectors' own counts partition the table, which is what says neither one wrote
        // into the other's half.
        int reportedLong = Figure(result, "detect.long.recorded");
        int reportedShort = Figure(result, "detect.short.recorded");

        Assert.Equal(reportedShort, shortRows);
        Assert.Equal(reportedLong + 1, longRows);
    }

    private static int Figure(PhaseReplayResult result, string id) =>
        int.Parse(
            result.Measurements.Single(m => string.Equals(m.Id, id, StringComparison.Ordinal)).Value,
            System.Globalization.CultureInfo.InvariantCulture);
}
