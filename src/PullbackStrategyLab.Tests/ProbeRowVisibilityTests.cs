using System.Globalization;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The point-in-time probe row staying invisible to every figure taken before it is written.
///
/// <b>What it is for.</b> `PhaseReplay` writes one authored row into the store on purpose: a
/// correction stamped a day after the run, so a read can be taken from both sides of its own
/// instant. The call that writes it carried a comment saying it is last and that nothing above it
/// may see one, and for twelve checkpoints that comment was the whole guard. At 3.12 a second
/// method was added directly underneath it and inherited the probe. No figure moved, because none
/// of the three it reported could see a `daily_bar` row, so nothing failed and nothing could have.
///
/// <b>A comment is not an assertion.</b> This is the sentence measured: the store-integrity figures
/// are taken while no row exists that was observed later than the run, and the count of such rows
/// is reported as a figure so that a measurement added below the probe reads 1 rather than reading
/// nothing at all. This is the harness that produces the figures every sign-off quotes.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
/// </summary>
public sealed class ProbeRowVisibilityTests
{
    [Fact]
    public void No_store_integrity_figure_is_taken_over_a_store_holding_the_point_in_time_probe()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        PhaseReplayResult result = replay.Run();

        Support.Measurement[] integrity = [.. result.Measurements
            .Where(m => m.Id.StartsWith("store.", StringComparison.Ordinal))];

        // Stated in advance rather than derived from the run. An empty set passes every assertion
        // below it, and a renamed prefix would hand this one.
        Assert.Equal(4, integrity.Length);

        // The figure that carries the property. Nought means the probe had not been written when
        // these were taken, which is the ordering the comment above the call claims and could not
        // enforce. Move StoreIntegrityFigures back below PointInTimeFigures and this reads 1.
        Assert.Equal("0", Single(integrity, "store.observationsAfterTheAsOf"));

        // And the three beside it are what they were, so this test says the probe is outside the
        // method rather than merely that one new number is nought.
        // Read from the build rather than restated, because this assertion is that the replay still
        // produces the figure, not that the figure is right: a literal here is a second place the
        // schema version lives and it goes stale on the next migration, which is how it turned red
        // at 3.13 for a reason that had nothing to do with the probe. The fixture expectation
        // derives the same number by hand from the filenames on purpose, and that independence is
        // where it belongs.
        Assert.Equal(
            MigrationRunner.LatestVersion.ToString(CultureInfo.InvariantCulture),
            Single(integrity, "store.schemaVersion"));
        Assert.Equal("124", Single(integrity, "store.rowsPointingAtSetup"));
        Assert.Equal("0", Single(integrity, "store.foreignKeyViolations"));

        // The probe does exist by the end of the run, so the nought above is the ordering holding
        // rather than the row never having been written at all. Without this the whole file would
        // pass against a replay that had dropped the point-in-time figures entirely.
        Assert.Equal("2", Single(
            result.Measurements, $"pointInTime.{PhaseReplay.CorrectedTicker}.observations"));
    }

    private static string Single(IEnumerable<Support.Measurement> measurements, string id) =>
        measurements.Single(m => string.Equals(m.Id, id, StringComparison.Ordinal)).Value;
}
