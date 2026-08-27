using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// A detector that cannot read one stock records it and carries on.
///
/// The behaviour ARCHITECTURE's failure table names, and the reason it is worth a table of its own:
/// a silent skip shrinks the recorded universe without anyone noticing. Every count downstream is
/// over the setups that were recorded, so a name the detector could not read is simply absent. The
/// night looks lighter, the counts stay plausible, and nothing says a name was lost.
///
/// The failure is authored by making one name's stored data unreadable rather than by injecting a
/// fault into the detector, because what is under test is what happens to the run when the store
/// hands back something the reader cannot parse. That is the shape a real one takes.
/// </summary>
public sealed class DetectorErrorTests
{
    [Fact]
    public void A_name_the_detector_cannot_read_gets_an_error_row_and_the_run_goes_partial()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        // One name's close, written as something no price parses from. A stored figure the reader
        // refuses is the ordinary shape of this failure: a restated bar, a partial write, a column
        // that changed meaning.
        string broken = FixtureTickers.All[0];

        using (SqliteConnection write = replay.OpenWrite())
        {
            using SqliteCommand damage = write.CreateCommand();
            damage.CommandText = """
                UPDATE daily_bar SET close = 'not a price'
                 WHERE ticker = @ticker AND bar_date = @as_of
                """;
            damage.Parameters.AddWithValue("@ticker", broken);
            damage.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(replay.AsOf));
            Assert.True(damage.ExecuteNonQuery() > 0, $"No bar for {broken} on {replay.AsOf} to damage.");
        }

        // Both detectors, because the behaviour is claimed of both and one of them holding it while
        // the other silently skips is the defect wearing a disguise. Asserted by running them rather
        // than by reading the source: a source scan for the insert passes on a helper nothing calls,
        // which is what it did on the first attempt at this.
        DetectResult longAgain = replay.DetectLong();
        DetectResult shortAgain = replay.DetectShort();

        Assert.Equal(1, longAgain.Errored);
        Assert.Equal(1, shortAgain.Errored);
        Assert.Equal(RunOutcome.Partial, longAgain.Outcome);
        Assert.Equal(RunOutcome.Partial, shortAgain.Outcome);

        using SqliteConnection read = replay.OpenStore();
        IReadOnlyList<StoredDetectorError> errors = DetectorErrorReader.Read(read, replay.AsOf);

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e =>
        {
            Assert.Equal(broken, e.Ticker);
            Assert.NotEmpty(e.Message);
        });

        Assert.Contains(errors, e => e.Direction == LongSetupDetector.Direction);
        Assert.Contains(errors, e => e.Direction == ShortSetupDetector.Direction);

        // And the rest of the night still ran. A detector that stopped at the first unreadable name
        // would lose every name after it, which is the same defect one order of magnitude larger.
        Assert.True(longAgain.Examined > 0 && shortAgain.Examined > 0,
            "A run recorded the error and examined nothing else, so one unreadable name stopped the night.");
    }
}
