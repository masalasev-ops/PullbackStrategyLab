using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Research;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Judging one gate under a version's rule, over the signals a night froze.
///
/// <b>What is being held here is that nothing judges twice.</b> Every verdict comes out of the same
/// two rule classes the detector runs, so a threshold comparison written a second time in a replay
/// would show up as a disagreement rather than as a passing test.
/// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
/// </summary>
public sealed class SelectionReplayTests
{
    /// <summary>Every signal a rebuilt long row carries, at values that clear every long gate.</summary>
    private static Dictionary<string, decimal> Signals(decimal retrace = 0.20m, int bars = 3) =>
        new(StringComparer.Ordinal)
        {
            ["close_adjusted"] = 100m,
            ["dollar_volume_median_20"] = 100_000_000m,
            ["market_cap"] = 5_000_000_000m,
            ["listing_age_sessions"] = 500m,
            ["adr_20"] = 0.08m,
            ["days_since_thrust"] = 3m,
            ["pullback_bars"] = bars,
            ["retrace_depth"] = retrace,
            ["closes_beyond_floor"] = 0m,
            ["range_today_over_avg"] = 0.7m,
            ["trigger_distance_ranges"] = 0.5m,
            ["stop_distance_ranges"] = 0.3m,
            ["cluster_count"] = 0m,
        };

    // ---- which thresholds a version may move ---------------------------------------------

    /// <summary>
    /// Every selection threshold on the long side compares one frozen signal, so all ten are
    /// movable. Stated as the long side's own count and never added to the short side's.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public void All_ten_long_selection_thresholds_are_movable()
    {
        IReadOnlyList<RuleThreshold> movable = SelectionReplay.Movable(SelectionRule.Long);

        Assert.Equal(10, movable.Count);
        Assert.All(movable, t => Assert.Equal(ThresholdFamily.Selection, t.Family));

        // The two that are not selection thresholds are excluded for a different reason from the
        // short side's exclusions below, and the distinction is the point of the two tests.
        Assert.DoesNotContain(movable, t => t.Name == SelectionRule.GiveUpRanges);
        Assert.DoesNotContain(movable, t => t.Name == SelectionRule.ClusterThreshold);
    }

    /// <summary>
    /// Ten of the short side's twelve selection thresholds are movable, and the two that are not are
    /// named: their gates compare a quantity that is arithmetic over several frozen signals.
    /// see: A version whose moved gate cannot be judged from the frozen signals is refused at admission
    /// </summary>
    [Fact]
    public void Ten_of_the_short_sides_twelve_are_movable_and_the_two_that_are_not_are_named()
    {
        IReadOnlyList<RuleThreshold> selection =
            [.. SelectionRule.Short.Thresholds.Where(t => t.Family == ThresholdFamily.Selection)];
        IReadOnlyList<RuleThreshold> movable = SelectionReplay.Movable(SelectionRule.Short);

        Assert.Equal(12, selection.Count);
        Assert.Equal(10, movable.Count);

        Assert.DoesNotContain(movable, t => t.Name == SelectionRule.MaximumSqueezeRatio);
        Assert.DoesNotContain(movable, t => t.Name == SelectionRule.CeilingReachRanges);

        Assert.False(SelectionReplay.IsReplayable(SelectionRule.Short, "averages-squeezing"));
        Assert.False(SelectionReplay.IsReplayable(SelectionRule.Short, "reached-ceiling"));
    }

    /// <summary>
    /// A gate with no threshold at all is not replayable either, and that is a different fact from
    /// the two above: nothing here can rebuild a grade, and no version can move one.
    /// </summary>
    [Fact]
    public void A_gate_that_compares_a_grade_is_not_replayable()
    {
        Assert.False(SelectionReplay.IsReplayable(SelectionRule.Long, "uptrend"));
        Assert.False(SelectionReplay.IsReplayable(SelectionRule.Short, "downtrend"));
    }

    // ---- what a rebuilt row judges -------------------------------------------------------

    /// <summary>
    /// The moved threshold decides the verdict and nothing else about the row changes, which is what
    /// makes a difference series attributable to one threshold.
    /// </summary>
    [Fact]
    public void The_moved_threshold_is_what_changes_the_verdict()
    {
        Dictionary<string, decimal> row = Signals(retrace: 0.45m);

        CheckResult? asBaseline = SelectionReplay.Judge(SelectionRule.Long, "dip-shape", row);
        CheckResult? asVersion = SelectionReplay.Judge(
            SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.50m), "dip-shape", row);

        Assert.False(asBaseline!.Passed);
        Assert.True(asVersion!.Passed);
        Assert.Equal(0.45m, asBaseline.Value);
        Assert.Equal(asBaseline.Value, asVersion.Value);
    }

    /// <summary>A row missing a signal the gate reads is judged as null rather than as a failure.</summary>
    [Fact]
    public void A_row_missing_a_signal_the_gate_reads_is_not_judged()
    {
        Dictionary<string, decimal> row = Signals();
        row.Remove("retrace_depth");

        Assert.Null(SelectionReplay.Judge(SelectionRule.Long, "dip-shape", row));
    }

    /// <summary>A gate the replay cannot rebuild is judged as null however complete the row is.</summary>
    [Fact]
    public void A_gate_whose_quantity_is_arithmetic_is_not_judged()
    {
        Assert.Null(SelectionReplay.Judge(SelectionRule.Short, "reached-ceiling", Signals()));
        Assert.Null(SelectionReplay.Judge(SelectionRule.Short, "averages-squeezing", Signals()));
    }

    /// <summary>
    /// The shape gates read the bar count and the retrace and no other member of the geometry
    /// record, so the placeholders the rebuild fills the rest with cannot reach a verdict.
    ///
    /// <b>This is the assertion that stops the rebuild being silently wrong the day a shape gate
    /// starts reading a price.</b> It judges one row twice through two geometries whose seven other
    /// fields differ and requires one verdict, which is a property of the gate rather than of this
    /// rebuild and fails the moment the gate reads something else.
    /// </summary>
    [Fact]
    public void The_shape_gates_read_the_bar_count_and_the_retrace_and_nothing_else()
    {
        var thin = new PullbackGeometry.Pullback(0, 0, 0m, 0m, 0m, 3, 0.45m, 0m, 0m);
        var fat = new PullbackGeometry.Pullback(7, 9, 55m, 90m, 74m, 3, 0.45m, 91m, 73m);

        foreach (SelectionRule rule in new[]
                 {
                     SelectionRule.Long,
                     SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.50m),
                 })
        {
            CheckResult over(PullbackGeometry.Pullback p) =>
                LongPullbackRules.Evaluate(
                    new LongPullbackRules.LongEvidence { Pullback = p }, rule)
                    .Single(r => r.Name == "dip-shape");

            Assert.Equal(over(thin).Passed, over(fat).Passed);
            Assert.Equal(over(thin).Value, over(fat).Value);
        }

        var shortThin = new PullbackGeometry.Pullback(0, 0, 0m, 0m, 0m, 3, 0.45m, 0m, 0m);
        var shortFat = new PullbackGeometry.Pullback(4, 6, 90m, 55m, 71m, 3, 0.45m, 54m, 72m);

        CheckResult bounce(PullbackGeometry.Pullback p) =>
            ShortPullbackRules.Evaluate(
                new ShortPullbackRules.ShortEvidence { Bounce = p }, SelectionRule.Short)
                .Single(r => r.Name == "bounce-shape");

        Assert.Equal(bounce(shortThin).Passed, bounce(shortFat).Passed);
        Assert.Equal(bounce(shortThin).Value, bounce(shortFat).Value);
    }

    // ---- admission ------------------------------------------------------------------------

    /// <summary>
    /// A version moving a threshold whose gate cannot be judged is refused, and the reason says why
    /// rather than reading as a rejection on the merits.
    /// see: A version whose moved gate cannot be judged from the frozen signals is refused at admission
    /// </summary>
    [Fact]
    public void A_version_moving_an_unjudgeable_gate_is_refused_with_the_reason()
    {
        AdmissionVerdict verdict = SelectionReplay.AssertAdmissible(
            SelectionRule.Short.With(SelectionRule.CeilingReachRanges, 0.75m), SelectionRule.Short);

        Assert.False(verdict.IsAdmitted);
        Assert.Contains(SelectionReplay.NotReplayable, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("reached-ceiling", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A version moving a judgeable threshold is admitted, and the gate that moved is named.</summary>
    [Fact]
    public void A_version_moving_a_judgeable_threshold_is_admitted()
    {
        AdmissionVerdict verdict = SelectionReplay.AssertAdmissible(
            SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.50m), SelectionRule.Long);

        Assert.True(verdict.IsAdmitted);
        Assert.Equal("dip-shape", verdict.Gate);
        Assert.Equal(SelectionRule.MaximumRetrace, verdict.Threshold);
        Assert.Equal(0.40m, verdict.From);
        Assert.Equal(0.50m, verdict.To);
    }

    /// <summary>
    /// The replayability refusal comes after the one-threshold assertion, so a candidate that fails
    /// both is refused for the first reason and not for this one.
    /// </summary>
    [Fact]
    public void A_candidate_failing_the_one_threshold_rule_is_refused_for_that_reason_first()
    {
        SelectionRule two = SelectionRule.Short
            .With(SelectionRule.CeilingReachRanges, 0.75m)
            .With(SelectionRule.MaximumRetrace, 0.55m);

        AdmissionVerdict verdict = SelectionReplay.AssertAdmissible(two, SelectionRule.Short);

        Assert.False(verdict.IsAdmitted);
        Assert.Contains(RuleAdmission.MoreThanOneMoved, verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(SelectionReplay.NotReplayable, verdict.Reason, StringComparison.Ordinal);
    }
}
