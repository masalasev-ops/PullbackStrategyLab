using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// The seam 3.6 counts the short side's twenty sessions from, now that the clause it waits on
/// exists.
///
/// <b>Three clause sets rather than two, and the third is why this file exists.</b> Before 4.4 the
/// anchored disjunct had not been built and every verdict said so. After 4.4 it is built and still
/// has nothing to read whenever the store holds no minutes back to the swing, which is the ordinary
/// case for a long time. Those are different facts about identical-looking passing verdicts, and 3.6
/// counts sessions by exactly this reading, so folding them together would start the short side's
/// twenty on a night the clause ran on nothing.
///
/// <b>Every case here goes through the detector rather than through a hand-built note.</b> A test
/// that constructs the string it then parses proves the parser and not the writer, and the writer is
/// the half that could stop recording.
/// </summary>
public sealed class CeilingClauseSeamTests
{
    /// <summary>
    /// Evidence that reaches `reached-ceiling` with a stated distance to the nearer average, and
    /// whatever anchored distance the case wants.
    /// </summary>
    private static ShortPullbackRules.ShortEvidence Evidence(
        decimal? toAverages, decimal? toAnchored, bool reconstructed = false) => new()
        {
            DistanceToNearestAverageRanges = toAverages,
            DistanceToAnchoredRanges = toAnchored,
            Reconstructed = reconstructed,
        };

    private static CheckResult Ceiling(ShortPullbackRules.ShortEvidence evidence) =>
        ShortPullbackRules.Evaluate(evidence).Single(r => r.Name == "reached-ceiling");

    [Fact]
    public void A_verdict_with_an_anchored_level_records_the_full_disjunction()
    {
        CheckResult verdict = Ceiling(Evidence(toAverages: 0.9m, toAnchored: 0.2m));

        Assert.Equal(ShortPullbackRules.ClausesRunWithTheAnchor, verdict.Note);
        Assert.Equal(CeilingClauses.WithTheAnchor, ShortPullbackRules.ClauseSetOf([verdict]));
    }

    [Fact]
    public void A_verdict_without_one_records_that_the_clause_had_nothing_to_read()
    {
        CheckResult verdict = Ceiling(Evidence(toAverages: 0.9m, toAnchored: null));

        Assert.Equal(ShortPullbackRules.ClausesRunWithoutTheAnchor, verdict.Note);
        Assert.Equal(CeilingClauses.AnchorUnavailable, ShortPullbackRules.ClauseSetOf([verdict]));
    }

    [Fact]
    public void A_reconstructed_row_records_an_absence_nothing_will_close()
    {
        // The third way a verdict runs two clauses, and the only one that is permanent. A forward
        // row with no level becomes anchorable as the store accumulates minutes; a reconstructed
        // 2024 session does not, because the vendor holds minute bars for a bounded window and there
        // is nothing to buy. Reading the two alike would tell somebody the clause is coming.
        CheckResult verdict = Ceiling(Evidence(toAverages: 0.9m, toAnchored: null, reconstructed: true));

        Assert.Equal(ShortPullbackRules.ClausesRunInReconstruction, verdict.Note);
        Assert.Equal(CeilingClauses.AnchorImpossible, ShortPullbackRules.ClauseSetOf([verdict]));
    }

    [Fact]
    public void A_reconstructed_row_that_somehow_has_a_level_still_records_the_full_disjunction()
    {
        // The flag says where the row came from and the level says what ran, and the level wins. A
        // flag that could suppress a clause set the check actually used would be a second definition
        // of which gate produced a row, which is the one thing this record exists to be.
        Assert.Equal(
            CeilingClauses.WithTheAnchor,
            ShortPullbackRules.ClauseSetOf([Ceiling(Evidence(0.9m, 0.2m, reconstructed: true))]));
    }

    [Fact]
    public void The_four_clause_sets_are_four_values_and_none_reads_as_another()
    {
        // The property in one line. The row written before 4.4 is frozen text rather than something
        // the detector still produces, so it is stated here as the constant it is.
        CeilingClauses[] all =
        [
            ShortPullbackRules.ClauseSetOf(
                [new CheckResult("reached-ceiling", true, 0.31m, ShortPullbackRules.ClausesRun)]),
            ShortPullbackRules.ClauseSetOf([Ceiling(Evidence(0.9m, null))]),
            ShortPullbackRules.ClauseSetOf([Ceiling(Evidence(0.9m, null, reconstructed: true))]),
            ShortPullbackRules.ClauseSetOf([Ceiling(Evidence(0.9m, 0.2m))]),
        ];

        Assert.Equal(
            [
                CeilingClauses.TwoOfThree, CeilingClauses.AnchorUnavailable,
                CeilingClauses.AnchorImpossible, CeilingClauses.WithTheAnchor,
            ],
            all);
        Assert.Equal(4, all.Distinct().Count());

        // And only one of the four is the gate 3.6 counts a session of evidence from.
        Assert.Single(all, c => c == CeilingClauses.WithTheAnchor);
    }

    [Fact]
    public void Nothing_the_detector_writes_reads_as_unrecorded()
    {
        // The done condition of 4.4, asserted over every shape the check can return rather than over
        // the two a reader would think of. `Unrecorded` is the state whose gate cannot be
        // established at all, and it is the one thing worse than either answer.
        ShortPullbackRules.ShortEvidence[] shapes =
        [
            Evidence(0.2m, 0.1m),
            Evidence(0.2m, null),
            Evidence(0.9m, 0.9m),
            Evidence(null, 0.2m),
            Evidence(null, null),
            Evidence(0.9m, null, reconstructed: true),
            Evidence(0.2m, 0.1m, reconstructed: true),
        ];

        foreach (ShortPullbackRules.ShortEvidence shape in shapes)
        {
            Assert.NotEqual(CeilingClauses.Unrecorded, ShortPullbackRules.ClauseSetOf([Ceiling(shape)]));
        }
    }

    [Fact]
    public void An_unrecognised_clause_record_fails_closed_rather_than_reading_as_the_widest_gate()
    {
        // The read was "the two-clause note, else anything non-empty is the finished gate" until
        // 4.4. That was correct while there were two records and became a trap the moment a third
        // existed: a verdict that could not be anchored would have read as the full disjunction and
        // 3.6 would have counted it. An unknown note is now a row whose gate cannot be established.
        Assert.Equal(
            CeilingClauses.Unrecorded,
            ShortPullbackRules.ClauseSetOf([new CheckResult("reached-ceiling", true, 0.31m, "some later wording")]));

        Assert.Equal(
            CeilingClauses.Unrecorded,
            ShortPullbackRules.ClauseSetOf([new CheckResult("reached-ceiling", true, 0.31m)]));
    }

    // ---- the clause itself ---------------------------------------------------------------

    [Fact]
    public void The_anchored_clause_widens_the_gate_and_never_narrows_it()
    {
        // A disjunction gains a disjunct, so a row the two averages already passed cannot be failed
        // by the third, and a row they failed can be passed by it. Both directions, because the
        // whole of 2.11's asymmetry is that the missing disjunct made the gate strictly harder.
        Assert.True(Ceiling(Evidence(toAverages: 0.9m, toAnchored: 0.2m)).Passed);
        Assert.False(Ceiling(Evidence(toAverages: 0.9m, toAnchored: null)).Passed);
        Assert.True(Ceiling(Evidence(toAverages: 0.2m, toAnchored: 0.9m)).Passed);
        Assert.True(Ceiling(Evidence(toAverages: 0.2m, toAnchored: null)).Passed);
    }

    [Fact]
    public void A_ceiling_with_no_average_at_all_is_unevaluated_whatever_the_anchor_says()
    {
        // The anchored clause is a third level and not a substitute for the first two. With no daily
        // range or no averages the check cannot express a distance in the units the threshold is
        // written in, and it says so rather than answering from the one level it has.
        CheckResult verdict = Ceiling(Evidence(toAverages: null, toAnchored: 0.2m));

        Assert.Null(verdict.Value);
        Assert.Equal(CeilingClauses.NotEvaluated, ShortPullbackRules.ClauseSetOf([verdict]));
    }

    // ---- the anchor the clause is measured from --------------------------------------------

    [Fact]
    public void The_swing_is_the_extreme_of_the_span_the_move_ran_from_and_not_of_the_whole_window()
    {
        // Nine sessions: a high at index 2, the thrust low at index 5, a bounce after it that runs
        // back above the earlier part of the window. A search over the whole window would find the
        // bounce's own high at index 8 and anchor the level to a bar the move has not fallen from.
        PullbackGeometry.Bar[] bars =
        [
            Bar(100m), Bar(104m), Bar(112m), Bar(106m), Bar(98m),
            Bar(90m), Bar(96m), Bar(105m), Bar(118m),
        ];

        PullbackGeometry.Pullback shape = PullbackGeometry.Of(bars, 5, 4, isLong: false)!;

        Assert.Equal(5, shape.ExtremeIndex);
        Assert.Equal(2, PullbackGeometry.SwingIndexOf(bars, shape, 4, isLong: false));
    }

    [Fact]
    public void The_span_decides_the_swing_so_the_scan_cannot_be_left_out()
    {
        // The same bars read as a one-session thrust. `gainer` and `gapper` flag one session where
        // `leader` and `laggard` flag twenty, so a swing searched without the scan is a swing
        // searched over the wrong window, and the two answers here are four bars apart.
        PullbackGeometry.Bar[] bars =
        [
            Bar(100m), Bar(104m), Bar(112m), Bar(106m), Bar(98m),
            Bar(90m), Bar(96m), Bar(105m), Bar(118m),
        ];

        PullbackGeometry.Pullback oneSession = PullbackGeometry.Of(bars, 5, 1, isLong: false)!;

        Assert.Equal(5, PullbackGeometry.SwingIndexOf(bars, oneSession, 1, isLong: false));
    }

    [Fact]
    public void The_long_mirror_is_the_swing_low_over_the_same_span()
    {
        // The mirror is a parameter here as everywhere else in the geometry. Nothing reads it yet,
        // and it is asserted so a long-side anchor is not written a second time when something does.
        PullbackGeometry.Bar[] bars =
        [
            Bar(118m), Bar(112m), Bar(96m), Bar(104m), Bar(110m),
            Bar(120m), Bar(114m), Bar(108m), Bar(100m),
        ];

        PullbackGeometry.Pullback shape = PullbackGeometry.Of(bars, 5, 4, isLong: true)!;

        Assert.Equal(5, shape.ExtremeIndex);
        Assert.Equal(2, PullbackGeometry.SwingIndexOf(bars, shape, 4, isLong: true));
    }

    private static PullbackGeometry.Bar Bar(decimal close) =>
        new(close, close + 1m, close - 1m, close, close + 1m, close - 1m);
}
