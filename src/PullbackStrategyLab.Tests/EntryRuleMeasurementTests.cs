using System.Text.Json;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Generation 1's entry rule measured over calibration minutes, from 7.9, over
/// <see cref="CalibrationEntryCases"/>' authored rows and hand-derived figures.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class EntryRuleMeasurementTests
{
    [Fact]
    public void Each_side_says_how_many_rows_entered_and_why_the_rest_did_not()
    {
        using var cases = new CalibrationEntryCases();
        EntryRuleReport report = cases.Measure();

        EntryRuleReading.Side longSide = report.Sides.Single(s => s.Direction == SetupDirection.Long);
        Assert.Equal(3, longSide.Rows);
        Assert.Equal(2, longSide.Entered);
        Assert.Equal(1, longSide.NoReclaim);
        Assert.Equal(0, longSide.NoMinutes);
        Assert.Equal(2, longSide.StopsAtSessionExtreme);
        Assert.Equal(0, longSide.StopsPastCeiling);
        Assert.Equal(0.02m, longSide.MedianCeiling);

        EntryRuleReading.Side shortSide = report.Sides.Single(s => s.Direction == SetupDirection.Short);
        Assert.Equal(1, shortSide.Rows);
        Assert.Equal(1, shortSide.Entered);
        Assert.Equal(0.006m, shortSide.MedianStopFraction);
    }

    /// <summary>
    /// The win-rate ceiling over the stops that entered, measured from the entry: on the long side both
    /// ended ahead and one was stopped out first, so one in two; the short ended ahead and was stopped.
    /// </summary>
    [Fact]
    public void The_bound_is_taken_over_the_stops_the_entry_resolved()
    {
        using var cases = new CalibrationEntryCases();
        EntryRuleReport report = cases.Measure();

        EntryRuleReading.Side longSide = report.Sides.Single(s => s.Direction == SetupDirection.Long);
        Assert.Equal(2, longSide.BoundSubjects);
        Assert.Equal(0.5m, longSide.Bound);
        Assert.Equal(0.5m, longSide.Achieved);

        EntryRuleReading.Side shortSide = report.Sides.Single(s => s.Direction == SetupDirection.Short);
        Assert.Equal(1, shortSide.BoundSubjects);
        Assert.Equal(0m, shortSide.Bound);
    }

    /// <summary>The report is a file that names the level set it was computed over and says the anchored level was not evaluated.</summary>
    [Fact]
    public void The_report_names_its_level_set_and_the_level_it_did_not_evaluate()
    {
        using var cases = new CalibrationEntryCases();
        EntryRuleReport report = cases.Measure();

        string file = Path.Combine(cases.Root, report.Path);
        Assert.True(File.Exists(file));
        Assert.False(Path.IsPathRooted(report.Path));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal(EntryRuleReading.LevelSet, json.RootElement.GetProperty("levelSet").GetString());
        Assert.Contains("not evaluated", json.RootElement.GetProperty("anchoredLevel").GetString(), StringComparison.Ordinal);

        string text = File.ReadAllText(Path.ChangeExtension(file, ".txt"));
        Assert.Contains(EntryRuleReading.LevelSet, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outcome_is_measured_from_the_entry_and_the_excursion_is_in_atr()
    {
        EntryOutcome.Reading? kept = EntryOutcome.Of(
            SetupDirection.Long, entryPrice: 100.20m, averageTrueRange: 4m,
            [(100.90m, 100.00m)], [(105.50m, 99.90m, 105.00m)]);

        Assert.NotNull(kept);
        Assert.Equal(-0.3m / 4m, kept.MaximumAdverseExcursionAtr);
        Assert.Equal((105.00m - 100.20m) / 100.20m, kept.ReturnSigned);

        EntryOutcome.Reading? shorted = EntryOutcome.Of(
            SetupDirection.Short, entryPrice: 50m, averageTrueRange: 2m,
            [(52.00m, 50.40m)], [(52.00m, 47.50m, 48.00m)]);

        Assert.NotNull(shorted);
        Assert.Equal(-1m, shorted.MaximumAdverseExcursionAtr);
        Assert.Equal(0.04m, shorted.ReturnSigned);

        Assert.Null(EntryOutcome.Of(SetupDirection.Long, 100m, 4m, [], []));
    }

    [Fact]
    public void A_row_whose_entry_session_holds_no_minute_is_counted_as_such()
    {
        EntryRuleReading.Side side = EntryRuleReading.Of(SetupDirection.Long,
        [
            new("a", SetupDirection.Long, EntryRuleReading.Outcome.NoMinutes, null, null, null, null, null),
            new("b", SetupDirection.Long, EntryRuleReading.Outcome.Refused, "wide", EntryStop.SessionExtremeBasis, 0.03m, 0.02m, null),
            new("c", SetupDirection.Short, EntryRuleReading.Outcome.Entered, null, EntryStop.SessionExtremeBasis, 0.01m, 0.02m, null),
        ]);

        Assert.Equal(2, side.Rows);
        Assert.Equal(1, side.NoMinutes);
        Assert.Equal(1, side.Refused);
        Assert.Equal(1, side.StopsPastCeiling);
        Assert.Null(side.Bound);
    }
}
