using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two rule sets, and every exit.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a position, a
/// trim or an exit. The bars, quotes, closes and averages below are written to sit either side of
/// each rule the stage applies, which is the footing every gate boundary in this suite stands on.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class PositionManagerTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);
    private static readonly DateOnly NextSession = new(2026, 8, 27);
    private static readonly DateOnly ThirdSession = new(2026, 8, 28);
    private static readonly DateOnly FourthSession = new(2026, 8, 31);

    private const double TenBasisPoints = 10d;

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public PositionManagerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        // A plan belongs to a version from 5.1 and the store's key says so, so the fixture
        // registers the baseline before anything writes a plan. The lab does not do this for
        // itself: registering a version is VariantAdmitter's, and a migration that seeded one
        // would start an experiment nobody chose to start.
        using (SqliteConnection seed = _connections.OpenWrite())
        {
            TestVersions.SeedBaseline(seed);
        }
    }

    public void Dispose() => _root.Dispose();

    // ---- the give-up point, which the manager now runs ----------------------------------------

    /// <summary>A position whose give-up point the session reached is closed, and the loss is the round trip.</summary>
    [Fact]
    public void A_position_is_closed_when_the_session_reaches_its_give_up_point()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 99m, 99m, 94m, 95m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(1, result.ClosedGiveUp);
        Assert.Equal(0, result.ClosedTrail);
        Assert.Equal(0, result.ClosedReclaim);
        Assert.Equal(0, result.OpenAtEnd);

        StoredFill exit = Fills(Session).Single(f => f.Leg == "exit");
        Assert.Equal(FillModel.Slipped, exit.Basis);
        Assert.Equal(95m, exit.RestingPrice);
        Assert.Equal(94.905m, exit.Price);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Closed, position.Status);
        Assert.Equal(ExitReason.GaveUp, position.ExitReason);
        Assert.Equal(150 * (94.905m - 100.10m), position.RealisedPnl);

        // Slightly worse than one unit of risk, because the exit crossed the book too. The round
        // trip is the cost this lab exists to measure against, so an exit priced at nothing would
        // put a thumb on the scale of every R figure it produces.
        Assert.True(position.RealisedR < -1d,
            $"A stop that cost both crossings reported {position.RealisedR} R.");
    }

    /// <summary>
    /// A minute holding both the trigger and the give-up point fills and then stops.
    ///
    /// Two stages now, and the property is unchanged: PaperBroker opens the position at 10:00 and
    /// the manager, walking the same session afterwards, reaches the give-up point in the same
    /// minute. A bar carries a high and a low and no order between them, so either reading is
    /// available and the pessimistic one is taken.
    /// </summary>
    [Fact]
    public void A_minute_holding_both_levels_fills_and_then_stops()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 98m, 99m, 97m, 98m);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 94m, 96m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(1, result.ClosedGiveUp);
        Assert.Equal(1, result.ClosedInTheirOwnSession);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Closed, position.Status);
        Assert.Equal(position.OpenedAt, position.ClosedAt);
    }

    /// <summary>
    /// A position opened inside the session is not measured against the bars before its entry.
    ///
    /// <b>Found at the 4.13 sign-off by running it, and kept as the case that was run.</b> The walk
    /// carried no bound at the entry minute, so a long that filled at 10:15 was measured against the
    /// 09:30 bar, and on a morning that opened under its give-up point it was closed forty-five
    /// minutes before it was opened, with the loss booked. A pullback long enters from below by
    /// construction, so the bars before the trigger sit under it and this is the first run's fault
    /// and not a corner. The test beside it walks a 09:30 bar whose low happens to sit above the
    /// give-up point, which is why it could not see this.
    /// </summary>
    [Fact]
    public void A_bar_before_the_entry_cannot_close_the_position()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 15), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 94m, 95m, 93m, 94m);
        Minute("AAPL", Session, new TimeOnly(10, 15), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(15, 0), 100m, 101m, 99m, 100m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(0, result.ClosedGiveUp);
        Assert.Equal(0, result.ClosedInTheirOwnSession);
        Assert.Equal(1, result.OpenAtEnd);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Open, position.Status);
        Assert.Null(position.ClosedAt);
        Assert.DoesNotContain(Fills(Session), f => f.Leg == "exit");
    }

    /// <summary>
    /// The same bound on the short side, where the pre-entry bars are the ones a reclaim and the
    /// trim would otherwise read: a short opened at 10:15 is neither trimmed on a 09:30 bar that
    /// sat at its 3R level nor stopped on one that sat above its give-up point.
    /// </summary>
    [Fact]
    public void A_bar_before_a_short_entry_neither_trims_nor_stops_it()
    {
        Plan("AAPL", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("AAPL", SetupDirection.Short, at: new TimeOnly(10, 15), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 106m, 107m, 84m, 90m);
        Minute("AAPL", Session, new TimeOnly(10, 15), 100.5m, 101m, 99.5m, 100m);
        Minute("AAPL", Session, new TimeOnly(15, 0), 100m, 101m, 99m, 100m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(0, result.ClosedGiveUp);
        Assert.Equal(0, result.Trimmed);
        Assert.Equal(1, result.OpenAtEnd);
        Assert.Equal(PositionStatus.Open, Positions(Session).Single().Status);
    }

    /// <summary>
    /// A rerun of a session that armed the trail on its close writes nothing and closes nothing.
    ///
    /// <b>Found at the 4.13 sign-off by running it, and kept as the case that was run.</b>
    /// <c>ArmedInAnEarlierSession</c> was set from the presence of a reason rather than from the
    /// session that armed it, so the arm this session's own close made read on the rerun as an
    /// earlier session's, fired at this session's first minute, and closed the position there with
    /// reason trail: one session early, at a price the rule never named, under a summary that said
    /// a rerun writes nothing. The position still exits at the next session's open, which is what
    /// the second half asserts, so the repair did not buy idempotency by losing the exit.
    /// </summary>
    [Fact]
    public void A_rerun_of_the_session_that_armed_the_trail_closes_nothing()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(15, 0), 100m, 101m, 99m, 100m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);

        ManageRunResult first = Stage().Manage(Session);
        Assert.Equal(1, first.ExitsArmed);
        Assert.Equal(0, first.ClosedTrail);

        ManageRunResult second = Stage().Manage(Session);

        Assert.Equal(0, second.ExitsArmed);
        Assert.Equal(0, second.ClosedTrail);
        Assert.Equal(0, second.ClosedGiveUp);
        Assert.Equal(1, second.OpenAtEnd);
        Assert.Equal(0, second.RowsWritten);

        StoredPosition armed = Positions(Session).Single();
        Assert.Equal(PositionStatus.Open, armed.Status);
        Assert.Equal(Session, armed.ExitArmedSession);
        Assert.Equal(ExitReason.Trail, armed.ExitArmedReason);

        Minute("AAPL", NextSession, new TimeOnly(9, 30), 98m, 99m, 97m, 98.5m);
        Quotes("AAPL", NextSession);

        ManageRunResult next = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, next.ClosedTrail);
        StoredPosition closed = Positions(Session, asOf: NextSession).Single();
        Assert.Equal(PositionStatus.Closed, closed.Status);
        Assert.Equal(NextSession, closed.ClosedSession);
        Assert.Equal(ExitReason.Trail, closed.ExitReason);
    }

    /// <summary>
    /// An overnight jump past the give-up point fills at the next session's first regular minute
    /// open, is never clamped, and loses more than one R.
    ///
    /// 4.7's first done condition, end to end, now asserted against the stage that owns the exit:
    /// the loss is bigger than planned, the fill price of the gap is stated rather than only its
    /// sign, and the row is tagged so the size and frequency of these can be read afterwards.
    /// see: A minute that opens through a resting price fills at that open, whatever time of day it is
    /// </summary>
    [Fact]
    public void An_overnight_gap_through_the_give_up_point_fills_at_the_open_and_is_never_clamped()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(15, 0), 100m, 101m, 99m, 100m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        // The next session opens seven points below the give-up point.
        Minute("AAPL", NextSession, new TimeOnly(9, 30), 88m, 89m, 87m, 88.5m);
        Quotes("AAPL", NextSession);

        ManageRunResult next = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, next.OpenAtStart);
        Assert.Equal(1, next.ClosedGiveUp);
        Assert.Equal(1, next.Gapped);
        Assert.Equal(0, next.OpenAtEnd);
        Assert.Equal(0, next.ClosedInTheirOwnSession);

        StoredFill exit = Fills(NextSession).Single();
        Assert.Equal(FillModel.Gapped, exit.Basis);
        Assert.Equal(88m, exit.Price);
        Assert.Equal(0m, exit.Slippage);

        StoredPosition position = Positions(Session, asOf: NextSession).Single();
        Assert.Equal(PositionStatus.Closed, position.Status);
        Assert.Equal(NextSession, position.ClosedSession);
        Assert.Equal(150 * (88m - 100.10m), position.RealisedPnl);

        // The risk it was measured against was 5.10 a share, and it lost 12.10 a share.
        Assert.True(position.RealisedR < -2d,
            $"A gap of seven points past a five point stop reported {position.RealisedR} R, which is clamped.");
    }

    /// <summary>
    /// An intraday minute that opens through the give-up point fills at that open, which is the
    /// second half of what 4.7 left optimistic.
    /// see: A minute that opens through a resting price fills at that open, whatever time of day it is
    /// </summary>
    [Fact]
    public void An_intraday_minute_that_opens_through_the_give_up_point_fills_at_that_open()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 90m, 91m, 89m, 90.5m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(1, result.Gapped);

        StoredFill exit = Fills(Session).Single(f => f.Leg == "exit");
        Assert.Equal(FillModel.Gapped, exit.Basis);
        Assert.Equal(90m, exit.Price);
    }

    // ---- the long trail ------------------------------------------------------------------------

    /// <summary>
    /// A daily close below the 9-day average arms the trail, and the position exits at the next
    /// session's open charged the whole spread.
    ///
    /// The arming is recorded on the row rather than recomputed the next night, because the evidence
    /// is the previous session's close and a series the store later corrects would change the answer
    /// after the fact.
    /// see: The long trail is evaluated on the daily close and fills at the next open
    /// </summary>
    [Fact]
    public void A_close_below_the_nine_day_average_arms_the_trail_and_exits_at_the_next_open()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);

        ManageRunResult armed = Stage().Manage(Session);

        Assert.Equal(1, armed.ExitsArmed);
        Assert.Equal(1, armed.OpenAtEnd);

        StoredPosition resting = Positions(Session).Single();
        Assert.Equal(Session, resting.ExitArmedSession);
        Assert.Equal(ExitReason.Trail, resting.ExitArmedReason);
        Assert.Equal(PositionStatus.Open, resting.Status);

        Minute("AAPL", NextSession, new TimeOnly(9, 30), 98m, 99m, 97m, 98m);
        Minute("AAPL", NextSession, new TimeOnly(10, 0), 98m, 99m, 97m, 98m);
        Quotes("AAPL", NextSession);

        ManageRunResult exited = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, exited.ClosedTrail);
        Assert.Equal(0, exited.ClosedGiveUp);

        StoredFill exit = Fills(NextSession).Single();
        Assert.Equal("exit", exit.Leg);
        Assert.Equal(FillModel.Slipped, exit.Basis);
        Assert.Equal(98m, exit.RestingPrice);

        // A long sells to exit, so the whole spread is charged downward off the open it filled at.
        Assert.Equal(97.902m, exit.Price);
        Assert.Equal(ExitReason.Trail, Positions(Session, asOf: NextSession).Single().ExitReason);
    }

    /// <summary>A close sitting exactly on the average has not closed below it, so nothing is armed.</summary>
    [Fact]
    public void A_close_exactly_on_the_nine_day_average_does_not_arm_the_trail()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 102m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(0, result.ExitsArmed);
        Assert.Null(Positions(Session).Single().ExitArmedReason);
    }

    /// <summary>
    /// A session that opens through the give-up point of a position whose trail is armed is a
    /// give-up exit, not a trail exit.
    ///
    /// Both rules name the same minute at the same price, so what is being asserted is the order
    /// rather than the arithmetic. A gap through the stop names how the loss occurred, and recording
    /// it as a trail exit would hide a gap loss inside a rule exit where LossClassifier at 4.10
    /// could not tell the two apart.
    /// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
    /// </summary>
    [Fact]
    public void A_gap_through_the_give_up_point_beats_an_armed_trail_in_the_same_minute()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);
        Stage().Manage(Session);

        Minute("AAPL", NextSession, new TimeOnly(9, 30), 88m, 89m, 87m, 88.5m);
        Quotes("AAPL", NextSession);

        ManageRunResult exited = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, exited.ClosedGiveUp);
        Assert.Equal(0, exited.ClosedTrail);

        StoredPosition position = Positions(Session, asOf: NextSession).Single();
        Assert.Equal(ExitReason.GaveUp, position.ExitReason);
        Assert.Equal(88m, position.ExitPrice);
    }

    // ---- the trims ------------------------------------------------------------------------------

    /// <summary>
    /// A short reaching 3R is trimmed by 15% of the planned share count and stays open.
    ///
    /// The entry sold at 99.90 against a give-up point of 105, so the realised risk is 5.10 a share
    /// and 3R is 84.60. The plan was sized at 150 shares, so the trim is 22 of them, floored, and
    /// 15% of the planned count rather than of what remains is what keeps that number fixed. 5R is
    /// 74.40 and this session never reaches it.
    /// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
    /// </summary>
    [Fact]
    public void A_short_reaching_three_r_is_trimmed_by_fifteen_per_cent_of_the_planned_size()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Minute("TSLA", Session, new TimeOnly(11, 0), 90m, 90m, 84m, 84.5m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(1, result.Trimmed);
        Assert.Equal(0, result.ClosedGiveUp);
        Assert.Equal(1, result.OpenAtEnd);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Open, position.Status);
        Assert.Equal(22, position.TrimmedShares);
        Assert.Equal(1, position.Trims);
        Assert.Equal(128, position.SharesRemaining);

        // 3R below the price the entry actually got, then bought back a whole spread worse.
        StoredFill trim = Fills(Session).Single(f => f.Leg == "trim");
        Assert.Equal(84.60m, trim.RestingPrice);
        Assert.Equal(84.6846m, trim.Price);
        Assert.Equal(22, trim.Shares);
        Assert.Equal((99.90m - 84.6846m) * 22, position.TrimRealisedPnl);
    }

    /// <summary>
    /// A short trims again at 5R, never a third time, and the close covers what is left.
    ///
    /// 5R below 99.90 is 74.40, bought back a whole spread worse at 74.4744, and the 12:00 bar trades
    /// to 70 without a third trim because he names two levels and no third. A fraction of the original
    /// is a fixed share count, so both trims are 22. The realised money is both trims' plus the
    /// close's, which is why the trims' figures are on the row rather than only in fills nothing
    /// points at.
    /// </summary>
    [Fact]
    public void A_short_trims_again_at_five_r_and_the_close_covers_what_is_left()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Minute("TSLA", Session, new TimeOnly(11, 0), 90m, 90m, 84m, 84.5m);
        Minute("TSLA", Session, new TimeOnly(12, 0), 80m, 80m, 70m, 71m);
        Minute("TSLA", Session, new TimeOnly(15, 0), 100m, 106m, 100m, 105m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(2, result.Trimmed);
        Assert.Equal(1, result.ClosedGiveUp);

        StoredFill[] trims = [.. Fills(Session).Where(f => f.Leg == "trim").OrderBy(f => f.FilledAt)];
        Assert.Equal(2, trims.Length);
        Assert.Equal([84.60m, 74.40m], trims.Select(t => t.RestingPrice));
        Assert.Equal(74.4744m, trims[1].Price);
        Assert.EndsWith(":trim2", trims[1].FillId, StringComparison.Ordinal);

        StoredFill exit = Fills(Session).Single(f => f.Leg == "exit");
        Assert.Equal(106, exit.Shares);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(2, position.Trims);
        Assert.Equal(44, position.TrimmedShares);
        Assert.Equal(84.6846m, position.TrimPrice);

        decimal trimPnl = ((99.90m - 84.6846m) * 22) + ((99.90m - 74.4744m) * 22);
        decimal exitPnl = (99.90m - exit.Price) * 106;
        Assert.Equal(trimPnl, position.TrimRealisedPnl);
        Assert.Equal(trimPnl + exitPnl, position.RealisedPnl);
    }

    /// <summary>
    /// A second trim observed in a later session is invisible to an as-of between the two, and the
    /// first is not. The later trims carry a stamp of their own so a replay standing between them
    /// reads the state that existed then rather than the running total.
    /// </summary>
    [Fact]
    public void A_trim_taken_in_a_later_session_is_invisible_to_an_as_of_between_the_two()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Minute("TSLA", Session, new TimeOnly(11, 0), 90m, 90m, 84m, 84.5m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        Minute("TSLA", NextSession, new TimeOnly(9, 30), 80m, 80m, 74m, 75m);
        Quotes("TSLA", NextSession);
        ManageRunResult next = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, next.Trimmed);

        StoredPosition between = Positions(Session, asOf: Session).Single();
        Assert.Equal(1, between.Trims);
        Assert.Equal(22, between.TrimmedShares);
        Assert.Equal((99.90m - 84.6846m) * 22, between.TrimRealisedPnl);

        StoredPosition after = Positions(Session, asOf: NextSession).Single();
        Assert.Equal(2, after.Trims);
        Assert.Equal(44, after.TrimmedShares);
        Assert.Equal(106, after.SharesRemaining);
    }

    /// <summary>
    /// A long is trimmed at 3R and again at 5R, from 7.10, and one bar reaching both takes both.
    ///
    /// The long side's trims are his own words: "every time I think this stock is extended I sell
    /// 15%", at three or five R. The entry bought at 100.10 against 95, so the realised risk is 5.10
    /// and the levels are 115.40 and 125.60, each sold a whole spread lower. Until 7.10 a long was
    /// never trimmed, because generation 0's trim was the short side's alone.
    /// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
    /// </summary>
    [Fact]
    public void A_long_is_trimmed_at_three_r_and_again_at_five_r()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 110m, 130m, 110m, 128m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(2, result.Trimmed);
        Assert.Equal(1, result.OpenAtEnd);

        StoredFill[] trims = [.. Fills(Session).Where(f => f.Leg == "trim").OrderBy(f => f.RestingPrice)];
        Assert.Equal([115.40m, 125.60m], trims.Select(t => t.RestingPrice));
        Assert.Equal([115.2846m, 125.4744m], trims.Select(t => t.Price));

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(44, position.TrimmedShares);
        Assert.Equal(106, position.SharesRemaining);
    }

    // ---- the short's hold limit, from 7.10 -----------------------------------------------------

    /// <summary>
    /// A short held three sessions, the one it opened in counted, is armed on the third close and
    /// closed at the next open, charged the whole spread.
    ///
    /// "Generally I hold my shorts for only maybe two to three days", and he does not trail them the
    /// way he trails his longs, so the short side ends on the sessions held and the lab takes the
    /// upper of his two. The count is of the daily bars the store holds, so the Friday arms and the
    /// Monday fills.
    /// see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
    /// </summary>
    [Fact]
    public void A_short_held_three_sessions_is_closed_at_the_next_open()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Quotes("TSLA", Session);
        DailyBar("TSLA", Session, close: 99.5m);
        Broker().Fill(Session);

        Assert.Equal(0, Stage().Manage(Session).ExitsArmed);

        HeldAnotherSession("TSLA", NextSession);
        Assert.Equal(0, Stage(NextSession).Manage(NextSession).ExitsArmed);

        HeldAnotherSession("TSLA", ThirdSession);
        ManageRunResult armed = Stage(ThirdSession).Manage(ThirdSession);

        Assert.Equal(1, armed.ExitsArmed);
        StoredPosition resting = Positions(Session, asOf: ThirdSession).Single();
        Assert.Equal(ExitReason.HoldLimit, resting.ExitArmedReason);
        Assert.Equal(ThirdSession, resting.ExitArmedSession);

        Minute("TSLA", FourthSession, new TimeOnly(9, 30), 97m, 98m, 96m, 97.5m);
        Quotes("TSLA", FourthSession);
        ManageRunResult exited = Stage(FourthSession).Manage(FourthSession);

        Assert.Equal(1, exited.ClosedHoldLimit);
        Assert.Equal(0, exited.ClosedGiveUp);

        StoredFill exit = Fills(FourthSession).Single(f => f.Leg == "exit");
        Assert.Equal(97m, exit.RestingPrice);

        // A short buys to exit, so the whole spread is charged upward off the open it filled at.
        Assert.Equal(97.097m, exit.Price);
        Assert.Equal(ExitReason.HoldLimit, Positions(Session).Single().ExitReason);
    }

    /// <summary>
    /// A session the store holds no daily bar for is not a session held, so a holiday between the
    /// entry and the third close moves the exit a session later rather than earlier.
    /// see: A session is a date the store holds minutes for, and no calendar is authored here
    /// </summary>
    [Fact]
    public void A_session_the_store_holds_no_bar_for_is_not_a_session_held()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Quotes("TSLA", Session);
        DailyBar("TSLA", Session, close: 99.5m);
        Broker().Fill(Session);
        Stage().Manage(Session);

        // Minutes and quotes on the next session and no daily bar, which is the store's own record
        // of a date it holds nothing for.
        Minute("TSLA", NextSession, new TimeOnly(10, 0), 99m, 100m, 98m, 99m);
        Quotes("TSLA", NextSession);
        Stage(NextSession).Manage(NextSession);

        HeldAnotherSession("TSLA", ThirdSession);
        ManageRunResult third = Stage(ThirdSession).Manage(ThirdSession);

        Assert.Equal(0, third.ExitsArmed);
        Assert.Null(Positions(Session, asOf: ThirdSession).Single().ExitArmedReason);
    }

    /// <summary>A long is never ended by the hold limit, because it is the other side's rule.</summary>
    [Fact]
    public void A_long_is_never_ended_by_the_hold_limit()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 100.5m);
        Broker().Fill(Session);
        Stage().Manage(Session);

        HeldAnotherSession("AAPL", NextSession);
        Stage(NextSession).Manage(NextSession);
        HeldAnotherSession("AAPL", ThirdSession);
        ManageRunResult third = Stage(ThirdSession).Manage(ThirdSession);

        Assert.Equal(0, third.ExitsArmed);
        Assert.Equal(1, third.OpenAtEnd);
    }

    /// <summary>
    /// An hourly reclaim armed before the rule retired still fills at the next open and is counted
    /// under its own name. Nothing arms it from 7.10; a row that already carries the arm is a decision
    /// the lab made under the rule then in force, and dropping it would leave the position with an
    /// instruction nothing reads.
    /// </summary>
    [Fact]
    public void An_hourly_reclaim_armed_before_the_rule_retired_still_fills_at_the_next_open()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        using (SqliteConnection connection = _connections.OpenWrite())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE position SET exit_armed_session = @session, exit_armed_reason = @reason;";
            command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(Session));
            command.Parameters.AddWithValue("@reason", ExitReason.Reclaim);
            command.ExecuteNonQuery();
        }

        Minute("TSLA", NextSession, new TimeOnly(9, 30), 103m, 104m, 102m, 103.5m);
        Quotes("TSLA", NextSession);
        ManageRunResult exited = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, exited.ClosedReclaim);
        Assert.Equal(0, exited.ClosedHoldLimit);
        Assert.Equal(ExitReason.Reclaim, Positions(Session).Single().ExitReason);
    }

    // ---- what cannot be priced -----------------------------------------------------------------

    /// <summary>
    /// A position whose name the session quoted no usable book for is held rather than closed at a
    /// price nobody measured, and the hold is counted once however long it lasts.
    /// </summary>
    [Fact]
    public void A_position_the_session_quoted_no_book_for_is_held_and_counted_once()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        // The next session ran its passes and quoted this name nothing at all.
        Minute("AAPL", NextSession, new TimeOnly(9, 30), 96m, 96m, 94m, 94.5m);
        Minute("AAPL", NextSession, new TimeOnly(10, 0), 95m, 95m, 93m, 93.5m);
        Pass(NextSession, "after_open");
        Pass(NextSession, "before_close");
        Snapshot("AAPL", NextSession, "after_open", null, lag: null, straddleSeconds: null);

        ManageRunResult result = Stage(NextSession).Manage(NextSession);

        Assert.Equal(1, result.HeldNoQuote);
        Assert.Equal(1, result.OpenAtEnd);
        Assert.Equal(PositionStatus.Open, Positions(Session, asOf: NextSession).Single().Status);
    }

    /// <summary>A session neither pass ran in prices nothing and is recorded partial.</summary>
    [Fact]
    public void A_session_nobody_sampled_closes_nothing_and_is_recorded_partial()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        Minute("AAPL", NextSession, new TimeOnly(9, 30), 96m, 96m, 94m, 94.5m);

        ManageRunResult result = Stage(NextSession).Manage(NextSession);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(PositionManager.SessionWasNeverSampled, result.StoppedBecause);
        Assert.Equal(1, result.OpenAtEnd);
    }

    /// <summary>A session with positions in it and no stored minute is partial rather than quiet.</summary>
    [Fact]
    public void A_session_with_positions_and_no_stored_minute_is_recorded_partial()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);

        Quotes("AAPL", NextSession);

        ManageRunResult result = Stage(NextSession).Manage(NextSession);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(PositionManager.SessionHeldNoMinutes, result.StoppedBecause);
        Assert.Equal(0, result.MinutesWalked);
    }

    /// <summary>A night with nothing open is clean and says so.</summary>
    [Fact]
    public void A_night_with_no_position_is_clean()
    {
        ManageRunResult result = Stage().Manage(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(PositionManager.NothingToManage, result.StoppedBecause);
        Assert.Equal(0, result.OpenAtStart);
    }

    // ---- point in time, over a table that is updated -------------------------------------------

    /// <summary>
    /// A position closed after the as-of reads as open, and one trimmed after the as-of reads
    /// untrimmed.
    ///
    /// This is the one updated table in the phase, and an update overwrites a state without moving
    /// the stamp that says when it was observed. Three stamps and three bounds, so a replay standing
    /// between the open and either later event is answered with the state that existed then.
    /// </summary>
    [Fact]
    public void A_close_and_a_trim_after_the_as_of_both_read_as_not_having_happened()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        Minute("TSLA", NextSession, new TimeOnly(9, 30), 90m, 90m, 84m, 84.5m);
        Minute("TSLA", NextSession, new TimeOnly(15, 0), 100m, 106m, 100m, 105m);
        Quotes("TSLA", NextSession);
        Stage(NextSession).Manage(NextSession);

        StoredPosition afterwards = Positions(Session, asOf: NextSession).Single();
        Assert.Equal(PositionStatus.Closed, afterwards.Status);
        Assert.Equal(22, afterwards.TrimmedShares);

        StoredPosition asOfTheOpeningDay = Positions(Session, asOf: Session).Single();
        Assert.Equal(PositionStatus.Open, asOfTheOpeningDay.Status);
        Assert.Null(asOfTheOpeningDay.ExitPrice);
        Assert.Null(asOfTheOpeningDay.RealisedR);
        Assert.Null(asOfTheOpeningDay.TrimmedShares);
        Assert.Null(asOfTheOpeningDay.TrimPrice);
        Assert.Equal(150, asOfTheOpeningDay.SharesRemaining);
    }

    /// <summary>
    /// An arming reads as unmade at an as-of before the session that made it, on the same footing.
    /// </summary>
    [Fact]
    public void An_arming_reads_as_unmade_before_the_session_that_made_it()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m, evening: Evening.AddDays(-1), liveSession: Evening);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150, session: Evening, evening: Evening.AddDays(-1));
        Minute("AAPL", Evening, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Evening);
        Broker(Evening).Fill(Evening);

        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 100m, 98m, 99m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Stage().Manage(Session);

        Assert.Equal(ExitReason.Trail, Positions(Evening, asOf: Session).Single().ExitArmedReason);
        Assert.Null(Positions(Evening, asOf: Evening).Single().ExitArmedReason);
    }

    /// <summary>A rerun over a managed session writes nothing, on the guards each update carries.</summary>
    [Fact]
    public void A_rerun_over_a_managed_session_writes_nothing()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Minute("TSLA", Session, new TimeOnly(11, 0), 90m, 90m, 84m, 84.5m);
        Minute("TSLA", Session, new TimeOnly(15, 0), 100m, 106m, 100m, 105m);
        Quotes("TSLA", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        ManageRunResult again = Stage().Manage(Session);

        Assert.Equal(0, again.OpenAtStart);
        Assert.Equal(PositionManager.NothingToManage, again.StoppedBecause);
        Assert.Equal(2, Fills(Session).Count(f => f.Leg != "entry"));
    }

    // ---- the night's own record ----------------------------------------------------------------

    /// <summary>
    /// The run row counts each exit under the rule that produced it, and carries the size of the
    /// approximation the caps make.
    ///
    /// A night of trail exits is a different night from a night of stop-outs, and a single total
    /// lets the one that is a finding hide inside the one that is ordinary. The last figure is what
    /// RiskGate could not see: it ran at 21:10 and read the book coming into the session, so a
    /// position opened and closed inside it still occupied a slot.
    /// see: RiskGate reads the book as it stood coming into the session, and what that costs is counted
    /// </summary>
    [Fact]
    public void The_run_row_counts_each_exit_by_its_rule_and_what_the_caps_could_not_see()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Minute("AAPL", Session, new TimeOnly(11, 0), 99m, 99m, 94m, 95m);
        Quotes("AAPL", Session);
        Broker().Fill(Session);
        Stage().Manage(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredManageRun run = PositionReader.ManageRunsFor(connection, Session).First();

        Assert.Equal(1, run.OpenAtStart);
        Assert.Equal(1, run.LongsManaged);
        Assert.Equal(0, run.ShortsManaged);
        Assert.Equal(1, run.ClosedGiveUp);
        Assert.Equal(0, run.ClosedTrail);
        Assert.Equal(0, run.ClosedReclaim);
        Assert.Equal(0, run.ClosedHoldLimit);
        Assert.Equal(0, run.Trimmed);
        Assert.Equal(1, run.ClosedInTheirOwnSession);
        Assert.Equal(0, run.OpenAtEnd);
        Assert.Equal("clean", run.Outcome);
    }

    // ---- scaffolding ---------------------------------------------------------------------------

    /// <summary>One more session held: a minute to walk, both passes quoted, and the day's bar.</summary>
    private void HeldAnotherSession(string ticker, DateOnly session)
    {
        Minute(ticker, session, new TimeOnly(10, 0), 99m, 100m, 98m, 99m);
        Quotes(ticker, session);
        DailyBar(ticker, session, close: 99m);
    }

    private PaperBroker Broker(DateOnly? on = null)
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        var clock = new FixedClock(SessionBoundaries.At(
            on ?? Session, new TimeOnly(21, 15), SessionBoundaries.UsEquities));

        return new PaperBroker(_connections, new RunLogger(clock, options), clock, options);
    }

    private PositionManager Stage(DateOnly? on = null)
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        var clock = new FixedClock(SessionBoundaries.At(
            on ?? Session, new TimeOnly(21, 20), SessionBoundaries.UsEquities));

        return new PositionManager(_connections, new RunLogger(clock, options), clock, options);
    }

    private IReadOnlyList<StoredPosition> Positions(DateOnly openedSession, DateOnly? asOf = null)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.ForOpenedSession(connection, openedSession, asOf ?? FourthSession, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredFill> Fills(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.FillsOf(connection, session, FourthSession, SessionBoundaries.UsEquities);
    }

    private static string SetupIdOf(string ticker, string direction, DateOnly evening) =>
        $"{evening:yyyy-MM-dd}-{ticker}-{direction}";

    private void Plan(
        string ticker,
        string direction,
        decimal trigger,
        decimal giveUp,
        DateOnly? evening = null,
        DateOnly? liveSession = null)
    {
        DateOnly asOf = evening ?? Evening;
        DateOnly live = liveSession ?? Session;
        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);

        using SqliteConnection connection = _connections.OpenWrite();
        string setupId = SetupIdOf(ticker, direction, asOf);

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(asOf.AddDays(-40)));
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
            setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
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
                plan_id, variant_id, setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @plan_id, @variant_id, @setup_id, @as_of, @live_session, @ticker, @direction,
                @trigger, @give_up, @distance, @shares,
                @equity, @fraction, @budget, @at_stake, @observed_at);
            """;
        plan.Parameters.AddWithValue("@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection)));
        plan.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
        plan.Parameters.AddWithValue("@setup_id", setupId);
        plan.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        plan.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(live));
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
                SessionBoundaries.At(asOf, new TimeOnly(18, 30), SessionBoundaries.UsEquities)));
        plan.ExecuteNonQuery();
    }

    /// <summary>The resolution and the order a triggered plan produces, written as the two stages would.</summary>
    private void Order(
        string ticker,
        string direction,
        TimeOnly at,
        int shares,
        DateOnly? session = null,
        DateOnly? evening = null)
    {
        DateOnly live = session ?? Session;
        DateOnly asOf = evening ?? Evening;
        string setupId = SetupIdOf(ticker, direction, asOf);
        DateTimeOffset touchedAt = SessionBoundaries.At(live, at, SessionBoundaries.UsEquities);
        DateTimeOffset observedAt = SessionBoundaries.At(
            live, new TimeOnly(21, 5), SessionBoundaries.UsEquities);

        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand resolution = connection.CreateCommand())
        {
            resolution.CommandText = """
                INSERT INTO trigger_resolution (
                    plan_id, variant_id, setup_id, live_session, ticker, direction, outcome, touched_at,
                    minutes_walked, observed_at)
                VALUES (@plan_id, @variant_id, @setup_id, @live_session, @ticker, @direction, 'touched', @touched_at, 1, @observed_at);
                """;
        resolution.Parameters.AddWithValue("@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection)));
        resolution.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
            resolution.Parameters.AddWithValue("@setup_id", setupId);
            resolution.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(live));
            resolution.Parameters.AddWithValue("@ticker", ticker);
            resolution.Parameters.AddWithValue("@direction", direction);
            resolution.Parameters.AddWithValue("@touched_at", StoreText.TimestampToStorageText(touchedAt));
            resolution.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
            resolution.ExecuteNonQuery();
        }

        using SqliteCommand order = connection.CreateCommand();
        order.CommandText = """
            INSERT INTO trade_order (
                order_id, plan_id, setup_id, variant_id, live_session, ticker, direction,
                triggered_at, status, planned_shares, shares, risk_at_stake, observed_at)
            VALUES (@plan_id, @plan_id, @id, @variant_id, @live_session, @ticker, @direction,
                    @triggered_at, 'placed', @shares, @shares, @risk, @observed_at);
            """;
        order.Parameters.AddWithValue(
            "@plan_id", PlanIdentity.For(setupId, TestVersions.SeedBaseline(connection)));
        order.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
        order.Parameters.AddWithValue("@id", setupId);
        order.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(live));
        order.Parameters.AddWithValue("@ticker", ticker);
        order.Parameters.AddWithValue("@direction", direction);
        order.Parameters.AddWithValue("@triggered_at", StoreText.TimestampToStorageText(touchedAt));
        order.Parameters.AddWithValue("@shares", shares);
        order.Parameters.AddWithValue("@risk", StoreText.PriceToStorageText(shares * 5m));
        order.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(live, new TimeOnly(21, 10), SessionBoundaries.UsEquities)));
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

    /// <summary>A daily bar with no split behind it, so the printed close is the adjusted one.</summary>
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

    /// <summary>Both passes ran and quoted this name at ten basis points, with the AAPL straddle.</summary>
    private void Quotes(string ticker, DateOnly session)
    {
        Pass(session, "after_open");
        Pass(session, "before_close");
        Snapshot(ticker, session, "after_open", TenBasisPoints, lag: 900, straddleSeconds: 32);
        Snapshot(ticker, session, "before_close", 6d, lag: 880, straddleSeconds: 5);
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

    private void Snapshot(
        string ticker, DateOnly session, string pass, double? basisPoints, int? lag, int? straddleSeconds)
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
                    @bid_ts, @ask_ts, @spread_bps, @lag, @absent, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(session.AddDays(-1)));
        command.Parameters.AddWithValue("@pass", pass);
        command.Parameters.AddWithValue("@snapshot_ts", StoreText.TimestampToStorageText(snapshotAt));
        command.Parameters.AddWithValue(
            "@bid", basisPoints is null ? DBNull.Value : StoreText.PriceToStorageText(99.9m));
        command.Parameters.AddWithValue(
            "@ask", basisPoints is null ? DBNull.Value : StoreText.PriceToStorageText(100.1m));
        command.Parameters.AddWithValue(
            "@bid_ts",
            straddleSeconds is null
                ? DBNull.Value
                : StoreText.TimestampToStorageText(snapshotAt.AddSeconds(-straddleSeconds.Value)));
        command.Parameters.AddWithValue(
            "@ask_ts", straddleSeconds is null ? DBNull.Value : StoreText.TimestampToStorageText(snapshotAt));
        command.Parameters.AddWithValue("@spread_bps", (object?)basisPoints ?? DBNull.Value);
        command.Parameters.AddWithValue("@lag", (object?)lag ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@absent", basisPoints is null ? "the vendor answered with one side of the book" : DBNull.Value);
        command.Parameters.AddWithValue(
            "@observed_at", StoreText.TimestampToStorageText(snapshotAt));
        command.ExecuteNonQuery();
    }
}
