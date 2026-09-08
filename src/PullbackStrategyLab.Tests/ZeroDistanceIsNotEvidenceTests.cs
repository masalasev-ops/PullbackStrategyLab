using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A gate whose quantity is a distance records no value with the reason where the bars it measures
/// have no range at all, rather than the most favourable verdict it can give.
///
/// <b>The ruling this holds, taken by the operator on 2026-09-08.</b> A gate handed nothing reads as
/// empty and fails, which the corpus has asserted since the decision below. A gate reading nought
/// does not read as empty: it reads as the tightest possible pass. `exit-tight` at a distance of
/// nought claims the give-up sits nought daily ranges from the entry, which is the most favourable
/// answer in the lab, and it is produced by bars with no range in them rather than by a tight stop.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
///
/// <b>Behavioural rather than a scan, and permanent rather than a break-and-revert.</b> Each guard is
/// two lines and a scan would find them; what has to be asserted is which verdict comes back, and in
/// particular that the guard fires on the degenerate case and stays out of the way of the ordinary
/// ones. The corpus's own rule is that an assertion must fail when the thing it guards is removed,
/// so every guard here is exercised against its own negative case in the same test.
///
/// <b>The ruling deliberately does not rest on `dip-shape` refusing single-bar pullbacks.</b> That
/// refusal is a movable threshold in the selection family, so a version could widen it and the
/// refusal would become the only thing holding the record. A rule held by another rule's current
/// value is not held, which is why nothing here reads a shape threshold and why the guards sit on
/// the quantity rather than on a gate ordering.
/// </summary>
public sealed class ZeroDistanceIsNotEvidenceTests
{
    /// <summary>
    /// A pullback that ran over bars and collapsed to one price, which is the shape the ruling is
    /// about. Two bars, both at 13.57, so the entry level and the give-up point are the same price.
    /// </summary>
    private static PullbackGeometry.Pullback RangelessPullback(int bars = 2) =>
        new(ThrustIndex: 0, ExtremeIndex: 1, ThrustOrigin: 10m, ThrustExtreme: 14m,
            PullbackExtreme: 13.57m, PullbackBars: bars, RetraceDepth: 0.1m,
            Trigger: 13.57m, Stop: 13.57m);

    /// <summary>An ordinary pullback, with a span between the two prices.</summary>
    private static PullbackGeometry.Pullback OrdinaryPullback() =>
        new(ThrustIndex: 0, ExtremeIndex: 1, ThrustOrigin: 10m, ThrustExtreme: 14m,
            PullbackExtreme: 12.5m, PullbackBars: 3, RetraceDepth: 0.3m,
            Trigger: 13.50m, Stop: 12.40m);

    private static CheckResult Long(string gate, LongPullbackRules.LongEvidence e) =>
        LongPullbackRules.Evaluate(e).Single(c => c.Name == gate);

    private static CheckResult Short(string gate, ShortPullbackRules.ShortEvidence e) =>
        ShortPullbackRules.Evaluate(e).Single(c => c.Name == gate);

    // ---- the geometry's own distinction -------------------------------------------------------

    /// <summary>
    /// No pullback and a rangeless pullback are different facts, and the record has to keep them
    /// apart. With no bar after the extreme the two prices are seeded from the same bar and are
    /// equal by construction, and that is an absence the detector already nulls. Conflating the two
    /// would relabel 22,638 calibration rows whose pullback genuinely had not started.
    /// </summary>
    [Fact]
    public void No_pullback_is_not_the_same_fact_as_a_pullback_with_no_range()
    {
        Assert.False(RangelessPullback(bars: 0).HasNoRange);
        Assert.True(RangelessPullback(bars: 1).HasNoRange);
        Assert.True(RangelessPullback(bars: 5).HasNoRange);
        Assert.False(OrdinaryPullback().HasNoRange);
    }

    // ---- exit-tight, the gate the ruling is about ---------------------------------------------

    [Fact]
    public void Exit_tight_records_no_value_where_the_pullback_has_no_range()
    {
        CheckResult result = Long("exit-tight", new LongPullbackRules.LongEvidence
        {
            Pullback = RangelessPullback(),
            StopDistanceRanges = 0m,
        });

        Assert.False(result.Passed);
        Assert.Null(result.Value);
        Assert.Equal(CheckResult.NoRangeInThePullback, result.Note);
    }

    /// <summary>
    /// The same on the short side, because a rule that held on one direction only would be the
    /// pooling fault in a different coat.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public void Exit_tight_records_no_value_on_the_short_side_too()
    {
        CheckResult result = Short("exit-tight", new ShortPullbackRules.ShortEvidence
        {
            Bounce = RangelessPullback(),
            StopDistanceRanges = 0m,
        });

        Assert.False(result.Passed);
        Assert.Null(result.Value);
        Assert.Equal(CheckResult.NoRangeInThePullback, result.Note);
    }

    /// <summary>
    /// The second guard, on the value, which is what a replay reaches. `SelectionReplay` rebuilds
    /// evidence from frozen signals, so a stored distance of nought can arrive without the geometry
    /// that produced it, and the guard above would not fire. A distance of nought needs the entry
    /// and the give-up to be the same price, so it is exact.
    /// </summary>
    [Fact]
    public void Exit_tight_refuses_a_stored_nought_even_with_no_geometry_behind_it()
    {
        foreach (CheckResult result in new[]
        {
            Long("exit-tight", new LongPullbackRules.LongEvidence { StopDistanceRanges = 0m }),
            Short("exit-tight", new ShortPullbackRules.ShortEvidence { StopDistanceRanges = 0m }),
        })
        {
            Assert.False(result.Passed);
            Assert.Null(result.Value);
            Assert.Equal(CheckResult.NoRangeInThePullback, result.Note);
        }
    }

    /// <summary>
    /// The negative case, which is what makes the assertions above mean anything. An ordinary tight
    /// stop still passes and still records its number, so the guard is not a gate that fails
    /// everything.
    /// </summary>
    [Fact]
    public void Exit_tight_still_passes_an_ordinary_tight_stop_and_records_its_distance()
    {
        CheckResult result = Long("exit-tight", new LongPullbackRules.LongEvidence
        {
            Pullback = OrdinaryPullback(),
            StopDistanceRanges = 0.4m,
        });

        Assert.True(result.Passed);
        Assert.Equal(0.4m, result.Value);
        Assert.Null(result.Note);
    }

    /// <summary>An absent distance keeps its own reason, which is a different fact from a nought.</summary>
    [Fact]
    public void An_absent_distance_still_says_absent_rather_than_rangeless()
    {
        CheckResult result = Long("exit-tight", new LongPullbackRules.LongEvidence());

        Assert.False(result.Passed);
        Assert.Null(result.Value);
        Assert.Equal(CheckResult.NoStopOrRange, result.Note);
    }

    // ---- contraction ---------------------------------------------------------------------------

    [Fact]
    public void Contraction_records_no_value_where_the_sessions_own_bar_has_no_range()
    {
        CheckResult result = Long("contraction", new LongPullbackRules.LongEvidence
        {
            RangeTodayOverAverage = 0m,
        });

        Assert.False(result.Passed);
        Assert.Null(result.Value);
        Assert.Equal(CheckResult.NoRangeInTheSession, result.Note);
    }

    [Fact]
    public void Contraction_still_passes_an_ordinary_quiet_session()
    {
        CheckResult result = Long("contraction", new LongPullbackRules.LongEvidence
        {
            RangeTodayOverAverage = 0.75m,
        });

        Assert.True(result.Passed);
        Assert.Equal(0.75m, result.Value);
    }

    // ---- trigger-near, the one that is guarded on the geometry and not on the value -------------

    /// <summary>
    /// A trigger distance of nought is a true and wanted reading where the bars have range: it says
    /// the close sits exactly on the entry level. Over the calibration store on 2026-09-08, 38 rows
    /// of 32,533 read exactly nought and 37 of them are ordinary bars ranging from 0.14% to 15.6% of
    /// their close on millions of shares. So this gate reads the pullback rather than the value, and
    /// this test is the reason why: the value guard the other two carry would discard those 37.
    /// </summary>
    [Fact]
    public void Trigger_near_still_passes_a_close_sitting_exactly_on_the_entry()
    {
        CheckResult result = Long("trigger-near", new LongPullbackRules.LongEvidence
        {
            Pullback = OrdinaryPullback(),
            TriggerDistanceRanges = 0m,
        });

        Assert.True(result.Passed);
        Assert.Equal(0m, result.Value);
    }

    [Fact]
    public void Trigger_near_records_no_value_where_the_pullback_has_no_range()
    {
        CheckResult result = Long("trigger-near", new LongPullbackRules.LongEvidence
        {
            Pullback = RangelessPullback(),
            TriggerDistanceRanges = 0m,
        });

        Assert.False(result.Passed);
        Assert.Null(result.Value);
        Assert.Equal(CheckResult.NoRangeInThePullback, result.Note);
    }

    // ---- what the ruling deliberately leaves alone ---------------------------------------------

    /// <summary>
    /// `held-floor` and `no-reclaim` count closes beyond a floor. Nought there means no violations
    /// and is the ordinary passing answer, on 30,442 of 32,533 long calibration rows and 16,596 of
    /// 16,917 short ones, so the ruling does not reach them: a nought is not evidence where the
    /// quantity is a distance, and it is the whole of the evidence where the quantity is a count.
    ///
    /// Asserted rather than left implicit, because the four gates that read nought on the row this
    /// ruling came from included this one, and a later session widening the guard to "any nought"
    /// would fail every clean setup in the lab with nothing here to stop it.
    /// </summary>
    [Fact]
    public void A_count_of_nought_violations_is_still_the_ordinary_pass()
    {
        CheckResult held = Long("held-floor", new LongPullbackRules.LongEvidence { ClosesBeyondFloor = 0 });
        Assert.True(held.Passed);
        Assert.Equal(0m, held.Value);

        CheckResult reclaim = Short("no-reclaim", new ShortPullbackRules.ShortEvidence { ClosesBeyondFloor = 0 });
        Assert.True(reclaim.Passed);
        Assert.Equal(0m, reclaim.Value);
    }

    /// <summary>
    /// The guard keys on the range and never on the volume, which the ruling states outright. Of the
    /// five rangeless bars sitting inside a pullback window on 2026-09-08, four carry a volume of
    /// nought and GH's 2024-05-23 traded 700 shares, so a volume test would have missed one of the
    /// five and caught nothing the range does not. Nothing in the guard can read a volume, and this
    /// asserts that a pullback with range is admitted whatever its bars traded.
    /// </summary>
    [Fact]
    public void Nothing_in_the_guard_keys_on_volume()
    {
        CheckResult result = Long("exit-tight", new LongPullbackRules.LongEvidence
        {
            Pullback = OrdinaryPullback(),
            StopDistanceRanges = 0.3m,
        });

        Assert.True(result.Passed);
    }
}
