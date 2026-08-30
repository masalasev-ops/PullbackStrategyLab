using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web.Shell;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// Both halves of checkpoint 3.6's trigger, on the store and on the panel a person reads it from.
///
/// <b>The trigger is two conditions and the panel reported one.</b> 3.6 fires on at least twenty
/// sessions <b>and</b> at least 262 effective observations, per direction and per control set.
/// BUILD_PLAN says both are needed because they are settled by different things: twenty sessions is
/// what the block bootstrap needs before an interval exists at all, 262 observations is what the
/// decision needs, and no number of rows substitutes for a session. It also says the panel is what
/// fires the checkpoint, because it shows both every night, and that a trigger a reader cannot see
/// is a date in disguise.
///
/// <b>What was actually there.</b> <see cref="PairedInterval.Estimate"/> has carried
/// <c>Nights</c> since the interval was written. <c>ScoreboardBuilder</c> read five of its six
/// fields and dropped that one, the <c>scoreboard</c> table had no column for it, and
/// <see cref="PanelView.Count"/> rendered the row count, the effective count and the minimum. The
/// session count reached a reader only inside <c>withheld_because</c>, in prose, and that column is
/// null the moment an interval exists. So the count was visible exactly while it did not matter and
/// absent from the moment it did.
///
/// <b>And the sharper half: the page said the whole condition on half the evidence.</b>
/// <see cref="PanelView.Reached"/> compared the effective count alone and then rendered "the
/// minimum sample is reached". A fortnight of very wide nights reaches 262 observations before it
/// reaches twenty sessions, so the page could have announced the project's own decision point on a
/// panel the bootstrap had refused to give an interval to at all. That is the corpus's sixth
/// failure shape: nothing upstream is wrong, every count is correct, and the sentence on the
/// surface is still false.
/// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
/// </summary>
public sealed class DecisionTriggerTests
{
    /// <summary>
    /// The session floor is the bootstrap's own precondition rather than a second authored number.
    ///
    /// Stated as a test rather than trusted, because the two live in different classes and a later
    /// session moving the block length would otherwise leave the trigger reading against a floor
    /// the interval no longer has.
    /// </summary>
    [Fact]
    public void The_session_minimum_is_twice_the_block_length()
    {
        Assert.Equal(MeasurementParameters.BootstrapBlockSessions * 2, MeasurementParameters.MinimumSessions);
        Assert.Equal(20, MeasurementParameters.MinimumSessions);
    }

    /// <summary>
    /// The case the old property got wrong, and the reason this file exists.
    ///
    /// Evidence far above the minimum sample, on five sessions. The bootstrap cannot produce an
    /// interval over five sessions, so this panel is withheld, and the old <c>Reached</c> would
    /// have rendered "the minimum sample is reached" beside the withheld figure.
    /// </summary>
    [Fact]
    public void Evidence_alone_does_not_reach_the_trigger_when_the_sessions_are_short()
    {
        PanelView panel = Band1(effective: 900, sessions: 5);

        Assert.True(panel.ReachedObservations);
        Assert.False(panel.ReachedSessions);
        Assert.False(panel.Reached);
        Assert.Equal("15 more session(s)", panel.ShortOf);
    }

    /// <summary>
    /// The mirror, which no reading of the old property got wrong and which is asserted so the
    /// repair cannot be a swap of one half for the other.
    /// </summary>
    [Fact]
    public void Sessions_alone_do_not_reach_the_trigger_when_the_evidence_is_short()
    {
        PanelView panel = Band1(effective: 40, sessions: 240);

        Assert.False(panel.ReachedObservations);
        Assert.True(panel.ReachedSessions);
        Assert.False(panel.Reached);
        Assert.Equal("222 more effective observation(s)", panel.ShortOf);
    }

    /// <summary>Both short, and both named, because a reader waiting on two things is told both.</summary>
    [Fact]
    public void Both_shortfalls_are_named_when_both_are_short()
    {
        PanelView panel = Band1(effective: 40, sessions: 5);

        Assert.False(panel.Reached);
        Assert.Equal("15 more session(s) and 222 more effective observation(s)", panel.ShortOf);
    }

    /// <summary>Both met is the only state that reaches the trigger, and nothing is short of it.</summary>
    [Fact]
    public void The_trigger_is_reached_only_when_both_conditions_are()
    {
        PanelView panel = Band1(effective: 262, sessions: 20);

        Assert.True(panel.Reached);
        Assert.Null(panel.ShortOf);
    }

    /// <summary>
    /// A panel no checkpoint fires on carries neither minimum and answers null, which is not false.
    ///
    /// "This panel answers no question a checkpoint waits on" and "it waits and has not arrived" are
    /// different sentences, and the template renders nothing at all for the first.
    /// </summary>
    [Fact]
    public void A_panel_with_no_minimum_answers_null_rather_than_false()
    {
        var panel = new PanelView(
            "band0.setupsOnFile", null, "44", null, null, 44, null, "every flagged setup", null, null);

        Assert.Null(panel.Reached);
        Assert.Null(panel.ReachedSessions);
        Assert.Null(panel.ReachedObservations);
        Assert.Null(panel.ShortOf);
    }

    /// <summary>
    /// The count says all three numbers, and the session count is one of them.
    ///
    /// Asserted on the rendered string rather than on the fields, because the property is what a
    /// person reads: the fields were on the estimate the whole time and the string is where they
    /// were lost.
    /// </summary>
    [Fact]
    public void The_count_states_the_sessions_beside_the_rows_and_the_effective_observations()
    {
        PanelView panel = Band1(effective: 40, sessions: 5, rows: 1_740);

        Assert.Equal(
            "n 1,740 rows, 40 effective of 262 needed, over 5 session(s) of 20 needed",
            panel.Count);
    }

    /// <summary>
    /// A panel carrying no session count still says the two numbers it has.
    ///
    /// The rows written before migration 034 carry null in both new columns, and a page that threw
    /// or dropped the whole count line on them would make the repair a regression for every night
    /// already recorded.
    /// </summary>
    [Fact]
    public void A_panel_recorded_before_the_column_existed_still_states_what_it_has()
    {
        var panel = new PanelView(
            "band1.vsTight", "long", "withheld", null, null, 0, 0, "every flagged setup", 262, null);

        Assert.Equal("n 0 rows, 0 effective of 262 needed", panel.Count);
        Assert.Null(panel.ReachedSessions);
        Assert.False(panel.Reached);
    }

    /// <summary>
    /// A row written before the session column existed never reads as reached, however much
    /// evidence it carries, and says why it cannot.
    ///
    /// <b>This is the old defect wearing a legacy row.</b> Falling back to whichever half is present
    /// would announce the trigger on evidence alone for exactly the rows that carry no session
    /// count. Every such row in the live store reads nought effective today, so the case is
    /// hypothetical; each shape in CLAUDE.md's list of failures was hypothetical until it was not.
    /// </summary>
    [Fact]
    public void A_row_written_before_the_column_existed_is_never_reached_however_much_evidence_it_has()
    {
        var panel = new PanelView(
            "band1.vsTight", "long", "0.0110", "-0.0030", "0.0250", 3_180, 900,
            "every flagged setup", MeasurementParameters.MinimumEffectiveObservations, null);

        Assert.True(panel.ReachedObservations);
        Assert.Null(panel.ReachedSessions);
        Assert.False(panel.Reached);
        Assert.Equal("a session count, which this panel was recorded before the store kept", panel.ShortOf);
    }

    private static PanelView Band1(int effective, int sessions, int rows = 1_000) =>
        new("band1.vsTight", "long", "withheld", null, null, rows, effective, "every flagged setup",
            MeasurementParameters.MinimumEffectiveObservations, null,
            sessions, MeasurementParameters.MinimumSessions);
}

/// <summary>
/// The session count on the store, taken from a real build over a population whose horizons have
/// closed.
///
/// <b>Separate from the view tests above and not merged with them.</b> Those hold what the page
/// says about numbers handed to it; this holds that the numbers arrive at all. A build that
/// computed the session count and discarded it would pass every test in the class above, because
/// each of them constructs its own panel.
/// </summary>
public sealed class DecisionTriggerStoreTests
{
    /// <summary>
    /// Every band 1 panel over the closed-horizon population carries both counts and both floors.
    ///
    /// The population runs 24 authored nights and every setup in it has all four horizons written,
    /// so the difference series is 24 nights long: the assertion is that none of them was lost
    /// between the population and the panel, which is a different claim from how many were authored.
    /// </summary>
    [Fact]
    public void Every_band_one_panel_records_the_session_count_it_was_built_over()
    {
        using var population = new AccumulationPopulation();
        population.Fill();
        population.Build();

        foreach (string direction in new[] { "long", "short" })
        {
            foreach (string set in new[] { "loose", "tight" })
            {
                AccumulationPopulation.Panel? panel = population.Band1(direction, set);

                Assert.NotNull(panel);
                Assert.Equal(AccumulationPopulation.Nights, panel.Sessions);
                Assert.Equal(MeasurementParameters.MinimumSessions, panel.MinimumSessions);
            }
        }
    }
}
