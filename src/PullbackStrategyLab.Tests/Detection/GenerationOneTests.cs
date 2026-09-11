using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// Generation 1's clauses where they differ from generation 0's, each against a case written by hand.
///
/// The clauses generation 1 carries unchanged are generation 0's own checks called, and their tests
/// are generation 0's. What is here is what the trace changed: the ladder without the price clause,
/// the dip with no length window, the ceiling as levels that coincide, the weekly screen and the
/// weekly squeeze, and the recording floor without `moves-enough`.
/// </summary>
public sealed class GenerationOneTests
{
    private static GenerationOneFigures Figures(
        decimal? nine = 11m, decimal? twentyOne = 10m, decimal? fifty = 9m,
        decimal? high = 12m, decimal? low = 10.5m, decimal? close = 11.5m,
        decimal? weekly = 0.02m, decimal? squeeze = 0.8m, decimal? confluence = 0.3m) =>
        new(nine, twentyOne, fifty, high, low, close, weekly, squeeze, confluence);

    private static CheckResult Result(IReadOnlyList<CheckResult> results, string name) =>
        results.Single(r => r.Name == name);

    [Fact]
    public void The_ladder_is_the_three_averages_and_no_longer_asks_the_price_to_sit_above_the_nine()
    {
        // Price 8.5 is below every average, which generation 0's "rising" grade refuses. His ladder is
        // a partition between the averages only, so generation 1 passes it.
        IReadOnlyList<CheckResult> results = GenerationOneRules.EvaluateLong(
            new LongPullbackRules.LongEvidence { LadderGrade = "mixed" },
            Figures(close: 8.5m, low: 8m, high: 9m));

        Assert.True(Result(results, "uptrend").Passed);
        Assert.False(Result(LongPullbackRules.Evaluate(new LongPullbackRules.LongEvidence { LadderGrade = "mixed" }), "uptrend").Passed);
    }

    [Fact]
    public void A_dip_of_any_length_passes_where_it_gave_back_no_more_than_the_authors_share()
    {
        var pullback = new PullbackGeometry.Pullback(
            ThrustIndex: 0, ExtremeIndex: 1, ThrustOrigin: 8m, ThrustExtreme: 10m, PullbackExtreme: 9.4m,
            PullbackBars: 12, RetraceDepth: 0.3m, Trigger: 9.6m, Stop: 9.3m);

        Assert.True(Result(GenerationOneRules.EvaluateLong(new LongPullbackRules.LongEvidence { Pullback = pullback }, Figures()), "dip-shape").Passed);
        Assert.False(Result(LongPullbackRules.Evaluate(new LongPullbackRules.LongEvidence { Pullback = pullback }), "dip-shape").Passed);
    }

    [Fact]
    public void The_ceiling_needs_two_levels_to_coincide_and_one_level_is_not_a_confluence()
    {
        Assert.Null(SourcedForms.ConfluenceDistance([0.1m, null, null]));
        Assert.Equal(0.4m, SourcedForms.ConfluenceDistance([0.1m, 0.4m, null]));
        Assert.Equal(0.2m, SourcedForms.ConfluenceDistance([0.9m, 0.1m, 0.2m]));

        IReadOnlyList<CheckResult> near = GenerationOneRules.EvaluateShort(new ShortPullbackRules.ShortEvidence(), Figures(confluence: 0.4m));
        IReadOnlyList<CheckResult> far = GenerationOneRules.EvaluateShort(new ShortPullbackRules.ShortEvidence(), Figures(confluence: 0.6m));

        Assert.True(Result(near, "reached-ceiling").Passed);
        Assert.False(Result(far, "reached-ceiling").Passed);
    }

    [Fact]
    public void The_weekly_screen_confirms_a_long_above_the_twenty_one_week_and_a_short_below_it()
    {
        Assert.True(Result(GenerationOneRules.EvaluateLong(new LongPullbackRules.LongEvidence(), Figures(weekly: 0m)), "weekly-trend").Passed);
        Assert.False(Result(GenerationOneRules.EvaluateLong(new LongPullbackRules.LongEvidence(), Figures(weekly: -0.01m)), "weekly-trend").Passed);
        Assert.True(Result(GenerationOneRules.EvaluateShort(new ShortPullbackRules.ShortEvidence(), Figures(weekly: -0.01m)), "weekly-trend").Passed);
        Assert.False(Result(GenerationOneRules.EvaluateShort(new ShortPullbackRules.ShortEvidence(), Figures(weekly: 0m)), "weekly-trend").Passed);
    }

    [Fact]
    public void A_week_is_monday_to_sunday_and_the_week_in_progress_closes_at_the_last_session()
    {
        // Thursday and Friday of one week, Monday of the next.
        IReadOnlyList<decimal> weekly = SourcedForms.WeeklyCloses(
            [new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 7)],
            [10m, 11m, 12m]);

        Assert.Equal([11m, 12m], weekly);
    }

    [Fact]
    public void A_flat_weekly_series_has_no_gap_to_average_and_so_no_squeeze_reading()
    {
        Assert.Null(SourcedForms.WeeklySqueezeRatio([.. Enumerable.Repeat(10m, 45)]));
        Assert.Null(SourcedForms.WeeklySqueezeRatio([.. Enumerable.Repeat(10m, 30)]));
    }

    [Fact]
    public void Moves_enough_is_recorded_and_never_required_and_leaves_the_recording_floor()
    {
        Assert.Contains("moves-enough", GenerationOneChecks.RecordedNotRequired);
        Assert.DoesNotContain("moves-enough", GenerationOneChecks.RecordingFloorLong);
        Assert.DoesNotContain("moves-enough", GenerationOneChecks.RecordingFloorShort);

        CheckResult[] results =
        [
            new("tradable", true, null),
            new("moves-enough", false, 0.03m),
            new("uptrend", true, null),
        ];

        Assert.True(GenerationOneChecks.PassedAll(results));
    }

    [Fact]
    public void The_entry_minute_clauses_are_not_on_generation_one_s_selection_lists()
    {
        Assert.DoesNotContain("exit-tight", GenerationOneChecks.Long);
        Assert.DoesNotContain("trigger-near", GenerationOneChecks.Long);
        Assert.DoesNotContain("exit-tight", GenerationOneChecks.Short);
        Assert.Contains("weekly-trend", GenerationOneChecks.Long);
        Assert.Contains("weekly-trend", GenerationOneChecks.Short);
    }
}
