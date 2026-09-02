using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Why each closed loss happened: a mechanism at the close and an aftermath ten sessions later.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a loss. The
/// positions below are opened and closed by the stages that own those operations rather than
/// inserted, and the daily bars the aftermath is read from are authored, so what is asserted
/// is a property of the pipeline rather than of a fixture.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class LossClassifierTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);
    private static readonly DateOnly NextSession = new(2026, 8, 27);
    private static readonly DateOnly Later = new(2026, 9, 30);

    private const double TenBasisPoints = 10d;

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public LossClassifierTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    // ---- the taxonomy, over every relationship rather than the ones a night produced ------------

    /// <summary>
    /// The mechanism is read from the exit fill's basis, and the detector the document carried until
    /// 4.10 fires on every ordinary stop-out.
    ///
    /// A round trip costs two crossings, so an ordinary stop loses slightly more than one unit of
    /// risk by construction, which 4.7 measured and asserted as an inequality. "Loss larger than one
    /// unit of risk" would put every stop-out in the gap bucket and leave the other two empty on
    /// every night the lab ever ran.
    /// see: A gap loss is detected from the exit fill's basis, not from the size of the loss
    /// </summary>
    [Fact]
    public void The_mechanism_is_the_fills_basis_and_not_the_size_of_the_loss()
    {
        Assert.Equal(LossMechanism.Gap, LossCause.MechanismOf(FillModel.Gapped));
        Assert.Equal(LossMechanism.Ordinary, LossCause.MechanismOf(FillModel.Slipped));
        Assert.Throws<ArgumentOutOfRangeException>(() => LossCause.MechanismOf("something-later"));

        // The size-based detector, run against the ordinary stop-out the fill model produces. A long
        // triggering at 100 with a ten-basis-point spread enters at 100.10 and exits a stop of 95 at
        // 94.905, so it loses 5.195 a share against a risk of 5.10.
        decimal entry = FillModel.Entry(SetupDirection.Long, 100m, null, TenBasisPoints).Price;
        decimal exit = FillModel.Exit(SetupDirection.Long, 95m, null, TenBasisPoints).Price;
        decimal risk = entry - 95m;

        Assert.True(entry - exit > risk,
            "An ordinary stop-out no longer loses more than one unit of risk, which is the premise "
            + "the corrected detection line rests on.");
        Assert.Equal(LossMechanism.Ordinary, LossCause.MechanismOf(FillModel.Slipped));
    }

    /// <summary>
    /// One unit of risk in return terms is the give-up distance over the trigger, and the boundary is
    /// at or above rather than above.
    ///
    /// One R is the point at which the trade would have paid for the risk it took, and a return that
    /// reached it exactly did pay for it.
    /// </summary>
    [Fact]
    public void The_boundary_is_one_r_in_return_terms_and_reaching_it_exactly_is_noise()
    {
        decimal oneR = LossCause.OneRInReturn(giveUpDistance: 5m, triggerPrice: 100m);
        Assert.Equal(0.05m, oneR);

        Assert.Equal(LossAftermath.Noise, LossCause.AftermathOf(0.05m, oneR));
        Assert.Equal(LossAftermath.Noise, LossCause.AftermathOf(0.06m, oneR));
        Assert.Equal(LossAftermath.FailedSetup, LossCause.AftermathOf(0.049m, oneR));
        Assert.Equal(LossAftermath.FailedSetup, LossCause.AftermathOf(-0.20m, oneR));
    }

    /// <summary>A result at or above nothing is not a loss, so the taxonomy is never asked about it.</summary>
    [Fact]
    public void A_result_that_is_not_a_loss_is_not_classified()
    {
        Assert.True(LossCause.IsALoss(-0.01m));
        Assert.False(LossCause.IsALoss(0m));
        Assert.False(LossCause.IsALoss(100m));
    }

    /// <summary>The three aftermaths and the two mechanisms are named once and nothing else is admitted.</summary>
    [Fact]
    public void The_taxonomy_is_two_mechanisms_and_three_aftermaths()
    {
        Assert.Equal(2, LossMechanism.All.Count);
        Assert.Equal(3, LossAftermath.All.Count);
        Assert.Contains(LossAftermath.Unclassified, LossAftermath.All);
    }

    // ---- the first pass ------------------------------------------------------------------------

    /// <summary>
    /// An ordinary stop-out is classified at the close with a mechanism and no aftermath, and the row
    /// says it is waiting rather than unclassified.
    /// </summary>
    [Fact]
    public void An_ordinary_stop_out_is_classified_at_the_close_and_waits_for_its_aftermath()
    {
        StopOut("AAPL", SetupDirection.Long);

        LossRunResult result = Classifier().Classify(Session);

        Assert.Equal(1, result.LossesClosed);
        Assert.Equal(1, result.MechanismsWritten);
        Assert.Equal(1, result.Ordinary);
        Assert.Equal(0, result.Gap);
        Assert.Equal(1, result.Longs);
        Assert.Equal(1, result.AwaitingAftermath);
        Assert.Equal(0, result.AftermathsWritten);
        Assert.Equal(0, result.Unclassified);

        StoredLossClass row = Losses(Session).Single();
        Assert.Equal(LossMechanism.Ordinary, row.Mechanism);
        Assert.Equal(FillModel.Slipped, row.ExitBasis);
        Assert.True(row.AwaitsItsHorizon);
        Assert.Null(row.Aftermath);
        Assert.Null(row.ForwardReturnSigned);
    }

    /// <summary>
    /// An overnight gap through the stop is a gap loss, read from the fill rather than from the size.
    /// </summary>
    [Fact]
    public void A_gap_through_the_stop_is_a_gap_loss()
    {
        GapOut("AAPL");

        LossRunResult result = Classifier(NextSession).Classify(NextSession);

        Assert.Equal(1, result.Gap);
        Assert.Equal(0, result.Ordinary);

        StoredLossClass row = Losses(NextSession).Single();
        Assert.Equal(LossMechanism.Gap, row.Mechanism);
        Assert.Equal(FillModel.Gapped, row.ExitBasis);
    }

    /// <summary>A win is not a loss and is not classified at all.</summary>
    [Fact]
    public void A_winning_trade_is_not_classified()
    {
        TrailOutAtAProfit("AAPL");

        LossRunResult result = Classifier(NextSession).Classify(NextSession);

        Assert.Equal(0, result.LossesClosed);
        Assert.Equal(0, result.MechanismsWritten);
        Assert.Empty(Losses(NextSession));
    }

    // ---- the second pass -----------------------------------------------------------------------
    //
    // Every case below is over the population the decision names: the return from the trigger
    // price, over the ten sessions after the session the trigger was touched in, on the adjusted
    // basis. Until 4.18 the classifier read `forward_return.return_signed`, which is measured from
    // the setup session's close over the ten sessions after the setup, and the tests here wrote
    // that row by hand, so they exercised a comparison over two populations and could not see it.
    // The cases now author the daily bars and let the stage measure.

    /// <summary>
    /// A ten-session return from the trigger that reached one unit of risk makes the stop-out noise,
    /// which points at execution rather than at the filter.
    /// </summary>
    [Fact]
    public void A_return_that_reached_one_r_makes_the_stop_out_noise()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 108m);

        LossRunResult result = Classifier(tenth).Classify(tenth);

        Assert.Equal(1, result.AftermathsWritten);
        Assert.Equal(1, result.Noise);
        Assert.Equal(0, result.FailedSetup);
        Assert.Equal(0, result.AwaitingAftermath);

        StoredLossClass row = Losses(Session, asOf: tenth).Single();
        Assert.Equal(LossAftermath.Noise, row.Aftermath);
        Assert.Equal(0.08m, row.ForwardReturnSigned);
        Assert.Equal(0.05m, row.OneRInReturn);
        Assert.Contains("from the trigger price of 100", row.AftermathBecause, StringComparison.Ordinal);
    }

    /// <summary>A follow-up flat or against the trade is a failed setup, which points at the filter.</summary>
    [Fact]
    public void A_flat_or_adverse_follow_up_is_a_failed_setup()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 98m);

        LossRunResult result = Classifier(tenth).Classify(tenth);

        Assert.Equal(1, result.FailedSetup);

        StoredLossClass row = Losses(Session, asOf: tenth).Single();
        Assert.Equal(LossAftermath.FailedSetup, row.Aftermath);
        Assert.Equal(-0.02m, row.ForwardReturnSigned);
    }

    /// <summary>
    /// The return is from the trigger and not from the setup's close, and the case is the one that
    /// tells the two apart.
    ///
    /// <b>The population the sign-off found the code measuring, stated as the case it fails.</b>
    /// The setup closed at 90 on its own evening, the trigger was 100 and the give-up point 95, so
    /// one R is 5%. Ten sessions after the trigger the name closed at 103: 3% from the trigger, which
    /// is below one R and a failed setup, and 14.4% from the setup's close, which would have been
    /// noise. A pullback long enters from below by construction, so the gap between the two
    /// populations is the whole trigger-minus-close distance and it pushes every loss the same way,
    /// toward the bucket that points away from the filter.
    /// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
    /// </summary>
    [Fact]
    public void The_return_is_measured_from_the_trigger_and_not_from_the_setups_close()
    {
        StopOut("AAPL", SetupDirection.Long);
        DailyBar("AAPL", Evening, close: 90m);
        Classifier().Classify(Session);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 103m);

        LossRunResult result = Classifier(tenth).Classify(tenth);

        Assert.Equal(1, result.FailedSetup);
        Assert.Equal(0, result.Noise);

        StoredLossClass row = Losses(Session, asOf: tenth).Single();
        Assert.Equal(LossAftermath.FailedSetup, row.Aftermath);
        Assert.Equal(0.03m, row.ForwardReturnSigned);
    }

    /// <summary>
    /// The window starts at the trigger's session and not at the setup's, so the tenth close read
    /// is the tenth after the session the trigger was touched in.
    /// </summary>
    [Fact]
    public void The_window_is_the_ten_sessions_after_the_triggers_session()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        // Nine sessions after the trigger's is one short of the horizon, whatever the setup's
        // evening contributes: the row waits.
        for (int at = 1; at <= 9; at++)
        {
            DailyBar("AAPL", Session.AddDays(at), close: 120m);
        }

        LossRunResult waiting = Classifier(Session.AddDays(9)).Classify(Session.AddDays(9));
        Assert.Equal(0, waiting.AftermathsWritten);
        Assert.Equal(1, waiting.AwaitingAftermath);

        // The tenth closes it, and the figure is the tenth session's close and no earlier one.
        DailyBar("AAPL", Session.AddDays(10), close: 104m);

        LossRunResult closed = Classifier(Session.AddDays(10)).Classify(Session.AddDays(10));
        Assert.Equal(1, closed.AftermathsWritten);
        Assert.Equal(0.04m, Losses(Session, asOf: Session.AddDays(10)).Single().ForwardReturnSigned);
    }

    /// <summary>
    /// A short's return is signed the other way, so a close below the trigger is the favourable
    /// direction and reaches one R from it.
    /// </summary>
    [Fact]
    public void A_shorts_return_is_signed_the_other_way()
    {
        Plan("AAPL", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("AAPL", SetupDirection.Short, shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 100.5m, 101m, 99.5m, 100m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 101m, 106m, 101m, 105m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 105m);
        RunTheNight(Session);
        Classifier().Classify(Session);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 94m);

        LossRunResult result = Classifier(tenth).Classify(tenth);

        Assert.Equal(1, result.Noise);
        Assert.Equal(0.06m, Losses(Session, asOf: tenth).Single().ForwardReturnSigned);
    }

    /// <summary>
    /// Both ends are put on the adjusted basis, so a split inside the ten sessions is not a move.
    ///
    /// The trigger is a raw price on the trigger session, whose bar carries the factor that puts it
    /// on the adjusted basis, and the tenth close is read adjusted. A two-for-one split after the
    /// trigger halves every raw price, and a return read raw would place every such loss as a failed
    /// setup on a move that never happened.
    /// </summary>
    [Fact]
    public void A_split_inside_the_horizon_is_not_read_as_a_move()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        // The trigger session's bar, restated after the split: a raw close of 95 that is 47.5 on the
        // adjusted basis, so the factor is one half. The ten sessions after it are already adjusted.
        DailyBarObserved("AAPL", Session, close: 95m, adjustedClose: 47.5m, observedOn: Session.AddDays(10));

        for (int at = 1; at <= 10; at++)
        {
            DailyBar("AAPL", Session.AddDays(at), close: 54m);
        }

        DateOnly tenth = Session.AddDays(10);
        LossRunResult result = Classifier(tenth).Classify(tenth);

        // 54 against a trigger of 100 on the adjusted basis, being 50, is +8%: noise, and not the
        // -46% a raw read would have called a failed setup.
        Assert.Equal(1, result.Noise);
        Assert.Equal(0.08m, Losses(Session, asOf: tenth).Single().ForwardReturnSigned);
    }

    /// <summary>
    /// Both questions are asked of every loss, so a gap loss that later recovers satisfies both.
    ///
    /// That sentence is only true if the aftermath is put to a gap loss at all. Asking it only of the
    /// losses that were not gaps is what a single ranked list would have done.
    /// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
    /// </summary>
    [Fact]
    public void A_gap_loss_that_later_recovers_is_a_gap_and_noise_at_once()
    {
        GapOut("AAPL");
        Classifier(NextSession).Classify(NextSession);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 130m);

        Classifier(tenth).Classify(tenth);

        StoredLossClass row = Losses(NextSession, asOf: tenth).Single();
        Assert.Equal(LossMechanism.Gap, row.Mechanism);
        Assert.Equal(LossAftermath.Noise, row.Aftermath);
    }

    /// <summary>
    /// A horizon that closed with no close to read it from is unclassified, which is a real category
    /// rather than a silent skip, and it is not what a row still waiting looks like.
    ///
    /// The store can count eleven sessions from the trigger's and still hold no bar for the
    /// trigger session itself, which is a fetch that missed a day; that is the one shape the count
    /// closes on and the figure cannot be read for.
    /// see: A loss awaiting its horizon carries no aftermath, and that is not the same as being unclassified
    /// </summary>
    [Fact]
    public void A_closed_horizon_with_no_figure_is_unclassified_and_a_waiting_row_is_not()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 99m, 99m, 94m, 95m);
        Quotes("AAPL", Session);
        RunTheNight(Session);
        Classifier().Classify(Session);

        // Eleven sessions after the trigger's and none for the trigger session itself.
        for (int at = 1; at <= 11; at++)
        {
            DailyBar("AAPL", Session.AddDays(at), close: 100m);
        }

        DateOnly asOf = Session.AddDays(11);
        LossRunResult closed = Classifier(asOf).Classify(asOf);

        Assert.Equal(1, closed.Unclassified);
        Assert.Equal(0, closed.AwaitingAftermath);

        StoredLossClass row = Losses(Session, asOf: asOf).Single();
        Assert.Equal(LossAftermath.Unclassified, row.Aftermath);
        Assert.Null(row.ForwardReturnSigned);
        Assert.Equal(LossClassifier.HorizonClosedWithNoFigure, row.AftermathBecause);
    }

    /// <summary>
    /// A horizon still open leaves the row waiting rather than unclassified, which is the state the
    /// count reports separately.
    /// </summary>
    [Fact]
    public void A_horizon_still_open_leaves_the_row_waiting()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        for (int at = 1; at <= 4; at++)
        {
            DailyBar("AAPL", Session.AddDays(at), close: 100m);
        }

        LossRunResult result = Classifier(Session.AddDays(4)).Classify(Session.AddDays(4));

        Assert.Equal(0, result.Unclassified);
        Assert.Equal(0, result.AftermathsWritten);
        Assert.Equal(1, result.AwaitingAftermath);
        Assert.True(Losses(Session, asOf: Session.AddDays(4)).Single().AwaitsItsHorizon);
    }

    // ---- point in time, over a table that is updated -------------------------------------------

    /// <summary>
    /// An aftermath written after the as-of reads as absent, and the mechanism beside it does not.
    ///
    /// Two stamps and two bounds, so a replay standing between the close and the horizon sees a
    /// mechanism and no aftermath, which is what stood then.
    /// </summary>
    [Fact]
    public void An_aftermath_written_after_the_as_of_reads_as_absent()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        DateOnly tenth = TenSessionsAfterTheTrigger("AAPL", closeAtTheTenth: 108m);
        Classifier(tenth).Classify(tenth);

        StoredLossClass afterwards = Losses(Session, asOf: tenth).Single();
        Assert.Equal(LossAftermath.Noise, afterwards.Aftermath);

        StoredLossClass between = Losses(Session, asOf: NextSession).Single();
        Assert.Equal(LossMechanism.Ordinary, between.Mechanism);
        Assert.Null(between.Aftermath);
        Assert.Null(between.ForwardReturnSigned);
        Assert.True(between.AwaitsItsHorizon);
    }

    /// <summary>A rerun writes nothing on either pass.</summary>
    [Fact]
    public void A_rerun_writes_nothing_on_either_pass()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        LossRunResult again = Classifier().Classify(Session);

        Assert.Equal(1, again.LossesClosed);
        Assert.Equal(0, again.MechanismsWritten);
        Assert.Equal(0, again.AftermathsWritten);
        Assert.Single(Losses(Session));
    }

    /// <summary>A night with no loss and nothing waiting is clean and says so.</summary>
    [Fact]
    public void A_night_with_nothing_to_classify_is_clean()
    {
        LossRunResult result = Classifier().Classify(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(LossClassifier.NothingToClassify, result.StoppedBecause);
    }

    // ---- the night's own record ----------------------------------------------------------------

    /// <summary>
    /// The run row counts the two passes apart, and reports waiting separately from unclassified.
    ///
    /// A night that wrote three mechanisms and no aftermaths is an ordinary night early in a horizon;
    /// one that wrote three aftermaths and no mechanisms is an ordinary night ten sessions later.
    /// </summary>
    [Fact]
    public void The_run_row_counts_the_two_passes_apart()
    {
        StopOut("AAPL", SetupDirection.Long);
        Classifier().Classify(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredLossRun run = LossClassReader.RunsFor(connection, Session).First();

        Assert.Equal(1, run.LossesClosed);
        Assert.Equal(1, run.MechanismsWritten);
        Assert.Equal(1, run.Ordinary);
        Assert.Equal(0, run.Gap);
        Assert.Equal(1, run.AwaitingAftermath);
        Assert.Equal(0, run.AftermathsWritten);
        Assert.Equal(0, run.Unclassified);
        Assert.Equal("clean", run.Outcome);
    }

    // ---- scaffolding ---------------------------------------------------------------------------

    /// <summary>A long opened and stopped out in one session, which is the ordinary loss.</summary>
    private void StopOut(string ticker, string direction)
    {
        Plan(ticker, direction, trigger: 100m, giveUp: 95m);
        Order(ticker, direction, shares: 150);
        Minute(ticker, Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute(ticker, Session, new TimeOnly(11, 0), 99m, 99m, 94m, 95m);
        Quotes(ticker, Session);
        DailyBar(ticker, Session, close: 95m);
        RunTheNight(Session);
    }

    /// <summary>A long held overnight and gapped through its stop, which is the gap loss.</summary>
    private void GapOut(string ticker)
    {
        Plan(ticker, SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order(ticker, SetupDirection.Long, shares: 150);
        Minute(ticker, Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes(ticker, Session);
        DailyBar(ticker, Session, close: 100m);
        RunTheNight(Session);

        Minute(ticker, NextSession, new TimeOnly(9, 30), 88m, 89m, 87m, 88.5m);
        Quotes(ticker, NextSession);
        DailyBar(ticker, NextSession, close: 88m);
        RunTheNight(NextSession);
    }

    /// <summary>A long the trail exited well above its entry, which is not a loss at all.</summary>
    private void TrailOutAtAProfit(string ticker)
    {
        Plan(ticker, SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order(ticker, SetupDirection.Long, shares: 150);
        Minute(ticker, Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes(ticker, Session);
        DailyBar(ticker, Session, close: 99m);
        Indicators(ticker, Session, ema9: 102m, ema50: 90m);
        RunTheNight(Session);

        Minute(ticker, NextSession, new TimeOnly(9, 30), 120m, 121m, 119m, 120m);
        Quotes(ticker, NextSession);
        DailyBar(ticker, NextSession, close: 120m);
        RunTheNight(NextSession);
    }

    /// <summary>The four stages before the classifier, in the order the runbook schedules them.</summary>
    private void RunTheNight(DateOnly session)
    {
        Stage<PaperBroker>(session, new TimeOnly(21, 15), (s, c, o) => new PaperBroker(_connections, new RunLogger(c, o), c, o)).Fill(session);
        Stage<PositionManager>(session, new TimeOnly(21, 20), (s, c, o) => new PositionManager(_connections, new RunLogger(c, o), c, o)).Manage(session);
        Stage<TradeJournal>(session, new TimeOnly(21, 25), (s, c, o) => new TradeJournal(_connections, new RunLogger(c, o), c, o)).Close(session);
    }

    private T Stage<T>(
        DateOnly session,
        TimeOnly time,
        Func<DateOnly, FixedClock, IOptions<PullbackStrategyLabOptions>, T> build)
    {
        ArgumentNullException.ThrowIfNull(build);

        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });
        var clock = new FixedClock(SessionBoundaries.At(session, time, SessionBoundaries.UsEquities));

        return build(session, clock, options);
    }

    private LossClassifier Classifier(DateOnly? on = null)
    {
        DateOnly session = on ?? Session;
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });
        var clock = new FixedClock(
            SessionBoundaries.At(session, new TimeOnly(21, 35), SessionBoundaries.UsEquities));

        return new LossClassifier(_connections, new RunLogger(clock, options), clock, options);
    }

    private IReadOnlyList<StoredLossClass> Losses(DateOnly session, DateOnly? asOf = null)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return LossClassReader.ClosedIn(connection, session, asOf ?? Later);
    }

    private static string SetupIdOf(string ticker, string direction) =>
        $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";

    /// <summary>
    /// Ten sessions of bars after the trigger's session, the last at <paramref name="closeAtTheTenth"/>,
    /// and the date of that tenth session, which is the first evening the horizon is closed on.
    /// </summary>
    private DateOnly TenSessionsAfterTheTrigger(string ticker, decimal closeAtTheTenth)
    {
        for (int at = 1; at <= LossClassifier.HorizonDays; at++)
        {
            DailyBar(ticker, Session.AddDays(at), close: at == LossClassifier.HorizonDays ? closeAtTheTenth : 100m);
        }

        return Session.AddDays(LossClassifier.HorizonDays);
    }

    /// <summary>A bar restated on a later evening, with its adjusted close apart from its raw one.</summary>
    private void DailyBarObserved(string ticker, DateOnly date, decimal close, decimal adjustedClose, DateOnly observedOn)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @close, @close, @close, @close, @adj_close, 1000000, @observed_at)
            ON CONFLICT (ticker, bar_date, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(date));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
        command.Parameters.AddWithValue("@adj_close", StoreText.PriceToStorageText(adjustedClose));
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(observedOn, new TimeOnly(17, 30), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }

    private void Plan(string ticker, string direction, decimal trigger, decimal giveUp)
    {
        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);

        using SqliteConnection connection = _connections.OpenWrite();
        string setupId = SetupIdOf(ticker, direction);

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Evening.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        using (SqliteCommand setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO setup
                    (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                     trigger_price, stop_price, stop_distance_ranges)
                VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @trigger, @stop, @ranges)
                ON CONFLICT (setup_id) DO NOTHING;
                """;
            setup.Parameters.AddWithValue("@id", setupId);
            setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
            setup.Parameters.AddWithValue("@ticker", ticker);
            setup.Parameters.AddWithValue("@direction", direction);
            setup.Parameters.AddWithValue("@trigger", StoreText.PriceToStorageText(trigger));
            setup.Parameters.AddWithValue("@stop", StoreText.PriceToStorageText(giveUp));
            setup.Parameters.AddWithValue("@ranges", StoreText.RatioToStorageText(0.30m));
            setup.ExecuteNonQuery();
        }

        using SqliteCommand plan = connection.CreateCommand();
        plan.CommandText = """
            INSERT INTO trade_plan (
                setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @setup_id, @as_of, @live_session, @ticker, @direction,
                @trigger, @give_up, @distance, @shares,
                @equity, @fraction, @budget, @at_stake, @observed_at);
            """;
        plan.Parameters.AddWithValue("@setup_id", setupId);
        plan.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
        plan.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
        plan.Parameters.AddWithValue("@ticker", ticker);
        plan.Parameters.AddWithValue("@direction", direction);
        plan.Parameters.AddWithValue("@trigger", StoreText.PriceToStorageText(trigger));
        plan.Parameters.AddWithValue("@give_up", StoreText.PriceToStorageText(giveUp));
        plan.Parameters.AddWithValue("@distance", StoreText.PriceToStorageText(distance));
        plan.Parameters.AddWithValue("@shares", shares);
        plan.Parameters.AddWithValue("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity));
        plan.Parameters.AddWithValue("@fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade));
        plan.Parameters.AddWithValue("@budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget));
        plan.Parameters.AddWithValue(
            "@at_stake", StoreText.PriceToStorageText(PositionSizing.RiskAtStake(shares, distance)));
        plan.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Evening, new TimeOnly(18, 30), SessionBoundaries.UsEquities)));
        plan.ExecuteNonQuery();
    }

    private void Order(string ticker, string direction, int shares)
    {
        string setupId = SetupIdOf(ticker, direction);
        DateTimeOffset touchedAt = SessionBoundaries.At(
            Session, new TimeOnly(10, 0), SessionBoundaries.UsEquities);

        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand resolution = connection.CreateCommand())
        {
            resolution.CommandText = """
                INSERT INTO trigger_resolution (
                    setup_id, live_session, ticker, direction, outcome, touched_at,
                    minutes_walked, observed_at)
                VALUES (@setup_id, @live_session, @ticker, @direction, 'touched', @touched_at, 1, @observed_at);
                """;
            resolution.Parameters.AddWithValue("@setup_id", setupId);
            resolution.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
            resolution.Parameters.AddWithValue("@ticker", ticker);
            resolution.Parameters.AddWithValue("@direction", direction);
            resolution.Parameters.AddWithValue("@touched_at", StoreText.TimestampToStorageText(touchedAt));
            resolution.Parameters.AddWithValue(
                "@observed_at",
                StoreText.TimestampToStorageText(
                    SessionBoundaries.At(Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities)));
            resolution.ExecuteNonQuery();
        }

        using SqliteCommand order = connection.CreateCommand();
        order.CommandText = """
            INSERT INTO trade_order (
                order_id, setup_id, live_session, ticker, direction, triggered_at, status,
                planned_shares, shares, risk_at_stake, observed_at)
            VALUES (@id, @id, @live_session, @ticker, @direction, @triggered_at, 'placed',
                    @shares, @shares, @risk, @observed_at);
            """;
        order.Parameters.AddWithValue("@id", setupId);
        order.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
        order.Parameters.AddWithValue("@ticker", ticker);
        order.Parameters.AddWithValue("@direction", direction);
        order.Parameters.AddWithValue("@triggered_at", StoreText.TimestampToStorageText(touchedAt));
        order.Parameters.AddWithValue("@shares", shares);
        order.Parameters.AddWithValue("@risk", StoreText.PriceToStorageText(shares * 5m));
        order.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, new TimeOnly(21, 10), SessionBoundaries.UsEquities)));
        order.ExecuteNonQuery();
    }

    private void Minute(
        string ticker, DateOnly session, TimeOnly at,
        decimal open, decimal high, decimal low, decimal close)
    {
        DateTimeOffset barTs = SessionBoundaries.At(session, at, SessionBoundaries.UsEquities);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intraday_bar (
                ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                open, high, low, close, volume, observed_at)
            VALUES (@ticker, @bar_ts, @session_date, '1m', 'regular', 'raw',
                    @open, @high, @low, @close, 10000, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_ts", StoreText.TimestampToStorageText(barTs));
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(open));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(high));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, new TimeOnly(20, 30), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }

    private void DailyBar(string ticker, DateOnly date, decimal close)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @close, @close, @close, @close, @close, 1000000, @observed_at)
            ON CONFLICT (ticker, bar_date, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(date));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(date, new TimeOnly(17, 30), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }

    private void Indicators(string ticker, DateOnly date, decimal ema9, decimal ema50)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                 dollar_volume_median_20, range_avg_20)
            VALUES (@ticker, @as_of, @computed_at, @ema_9, @ema_21, @ema_50, @atr, @adr, @dollars, @range)
            ON CONFLICT (ticker, as_of, computed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(date));
        command.Parameters.AddWithValue(
            "@computed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(date, new TimeOnly(18, 0), SessionBoundaries.UsEquities)));
        command.Parameters.AddWithValue("@ema_9", StoreText.PriceToStorageText(ema9));
        command.Parameters.AddWithValue("@ema_21", StoreText.PriceToStorageText((ema9 + ema50) / 2m));
        command.Parameters.AddWithValue("@ema_50", StoreText.PriceToStorageText(ema50));
        command.Parameters.AddWithValue("@atr", StoreText.PriceToStorageText(2m));
        command.Parameters.AddWithValue("@adr", StoreText.RatioToStorageText(0.02m));
        command.Parameters.AddWithValue("@dollars", StoreText.PriceToStorageText(50_000_000m));
        command.Parameters.AddWithValue("@range", StoreText.PriceToStorageText(2m));
        command.ExecuteNonQuery();
    }

    private void Quotes(string ticker, DateOnly session)
    {
        Pass(session, "after_open");
        Pass(session, "before_close");
        Snapshot(ticker, session, "after_open", TenBasisPoints);
        Snapshot(ticker, session, "before_close", 6d);
    }

    private void Pass(DateOnly session, string pass)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO spread_pass (
                session_date, setup_as_of, pass, requested, answered, quoted, unquoted,
                rows_written, outcome, observed_at)
            VALUES (@session_date, @setup_as_of, @pass, 1, 1, 1, 0, 1, 'clean', @observed_at)
            ON CONFLICT (session_date, pass, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(session.AddDays(-1)));
        command.Parameters.AddWithValue("@pass", pass);
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, new TimeOnly(10, 15), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }

    private void Snapshot(string ticker, DateOnly session, string pass, double basisPoints)
    {
        DateTimeOffset snapshotAt = SessionBoundaries.At(
            session, pass == "after_open" ? new TimeOnly(10, 15) : new TimeOnly(15, 45),
            SessionBoundaries.UsEquities);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO spread_snapshot (
                ticker, session_date, setup_as_of, pass, snapshot_ts, bid, ask,
                bid_ts, ask_ts, spread_bps, quote_lag_seconds, absent_because, observed_at)
            VALUES (@ticker, @session_date, @setup_as_of, @pass, @snapshot_ts, @bid, @ask,
                    @bid_ts, @ask_ts, @spread_bps, 900, NULL, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(session.AddDays(-1)));
        command.Parameters.AddWithValue("@pass", pass);
        command.Parameters.AddWithValue("@snapshot_ts", StoreText.TimestampToStorageText(snapshotAt));
        command.Parameters.AddWithValue("@bid", StoreText.PriceToStorageText(99.9m));
        command.Parameters.AddWithValue("@ask", StoreText.PriceToStorageText(100.1m));
        command.Parameters.AddWithValue(
            "@bid_ts", StoreText.TimestampToStorageText(snapshotAt.AddSeconds(-32)));
        command.Parameters.AddWithValue("@ask_ts", StoreText.TimestampToStorageText(snapshotAt));
        command.Parameters.AddWithValue("@spread_bps", basisPoints);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(snapshotAt));
        command.ExecuteNonQuery();
    }
}
