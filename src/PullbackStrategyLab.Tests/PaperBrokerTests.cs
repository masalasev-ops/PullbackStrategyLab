using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web.Shell;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The session walked and the entries priced.
///
/// <b>Every exit lives in <see cref="PositionManagerTests"/> from 4.8.</b> This stage prices what a
/// resting order got and nothing else, so a test about a give-up point belongs with the component
/// that decides it rather than with the one that used to.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a plan, an order
/// or a fill. The bars, quotes and orders below are written to sit either side of each rule the stage
/// applies, which is the footing every gate boundary in this suite stands on.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class PaperBrokerTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);
    private static readonly DateOnly NextSession = new(2026, 8, 27);

    private const double TenBasisPoints = 10d;

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, new TimeOnly(21, 15), SessionBoundaries.UsEquities));

    public PaperBrokerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    // ---- the ordinary entry -----------------------------------------------------------------

    /// <summary>
    /// An entry fills at the trigger plus the whole captured spread, and the row says what it was
    /// charged and what that charge was computed from.
    /// </summary>
    [Fact]
    public void An_entry_fills_at_the_trigger_plus_the_whole_captured_spread()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 98m, 99m, 97m, 98m);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(1, result.EntriesFilled);
        Assert.Equal(1, result.Slipped);
        Assert.Equal(0, result.Gapped);

        StoredFill entry = Fills(Session).Single();
        Assert.Equal("entry", entry.Leg);
        Assert.Equal(FillModel.Slipped, entry.Basis);
        Assert.Equal(100m, entry.RestingPrice);
        Assert.Equal(100.10m, entry.Price);
        Assert.Equal(0.10m, entry.Slippage);
        Assert.Equal(TenBasisPoints, entry.SpreadBasisPoints);
        Assert.Equal("after_open", entry.SpreadPass);
        Assert.Equal(32, entry.StraddleSeconds);

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Open, position.Status);
        Assert.Equal(150, position.Shares);
        Assert.Equal(100.10m, position.EntryPrice);

        // The intended risk is the plan's distance and the realised risk is the distance from the
        // price the fill actually got, so the entry slippage is on the row rather than in a comment.
        Assert.Equal(150 * 5m, position.RiskIntended);
        Assert.Equal(150 * 5.10m, position.RiskRealised);
        Assert.Equal(150 * 100.10m, position.ValueAtEntry);
    }

    /// <summary>
    /// The widest of the session's two quotes is charged, whatever time the fill happened.
    ///
    /// Pessimism on purpose, and it removes the within-day question: a fill at 10:00 charged the
    /// 15:45 quote is not reading a book the morning had not reached, because the rule does not
    /// depend on when the fill was.
    /// see: A fill is charged the widest usable quote of its session, not the nearest one
    /// </summary>
    [Fact]
    public void The_widest_quote_of_the_session_is_the_one_charged()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);

        Pass(Session, "after_open");
        Pass(Session, "before_close");
        Snapshot("AAPL", Session, "after_open", TenBasisPoints, lag: 900, straddleSeconds: 32);
        Snapshot("AAPL", Session, "before_close", 40d, lag: 880, straddleSeconds: 4);

        Stage().Fill(Session);

        StoredFill entry = Fills(Session).Single();
        Assert.Equal("before_close", entry.SpreadPass);
        Assert.Equal(40d, entry.SpreadBasisPoints);
        Assert.Equal(100.40m, entry.Price);
        Assert.Equal(4, entry.StraddleSeconds);
    }

    /// <summary>
    /// An entry the session opened through fills at that open, unslipped.
    ///
    /// The gap decision was written about an exit and its argument is symmetric. This direction is
    /// the one that matters more: filling a long at a trigger of 100 in a session that opened at 105
    /// would hand the lab five points it never had, which is the only kind of error this model
    /// cannot afford.
    /// </summary>
    [Fact]
    public void An_entry_the_session_opened_through_fills_at_the_open()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(9, 30), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 105m, 106m, 104m, 105.5m);
        Quotes("AAPL", Session);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(1, result.Gapped);
        Assert.Equal(0, result.Slipped);

        StoredFill entry = Fills(Session).Single();
        Assert.Equal(FillModel.Gapped, entry.Basis);
        Assert.Equal(105m, entry.Price);
        Assert.Equal(0m, entry.Slippage);
    }

    /// <summary>
    /// So does an entry in the middle of the day, which is the half 4.7 left optimistic.
    ///
    /// The gap rule ran only on the session's first regular minute until 4.8, so a minute at noon
    /// that opened past the trigger filled at the trigger: a price that did not trade in that minute
    /// at all, and one that flatters every time. The rule reads the bar rather than the clock.
    /// see: A minute that opens through a resting price fills at that open, whatever time of day it is
    /// </summary>
    [Fact]
    public void An_intraday_minute_that_opens_through_the_trigger_fills_at_that_open()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(12, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(9, 30), 96m, 97m, 95.5m, 96.5m);
        Minute("AAPL", Session, new TimeOnly(12, 0), 105m, 106m, 104m, 105.5m);
        Quotes("AAPL", Session);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(1, result.Gapped);
        Assert.Equal(0, result.Slipped);

        StoredFill entry = Fills(Session).Single();
        Assert.Equal(FillModel.Gapped, entry.Basis);
        Assert.Equal(105m, entry.Price);
    }

    // ---- what cannot be priced ---------------------------------------------------------------

    /// <summary>
    /// A name the session quoted no usable book for is not filled, and the order becomes a row
    /// rather than an absence.
    ///
    /// Charging nought would be a free entry that clears every threshold written as a maximum, and
    /// charging a figure from other names would be a spread nobody measured wearing the authority of
    /// one that was.
    /// see: A fill with no usable quote for its name is refused and recorded, never charged nought
    /// </summary>
    [Fact]
    public void An_order_the_session_quoted_no_book_for_is_recorded_unfilled()
    {
        Plan("MUZ", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("MUZ", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("MUZ", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);

        // The passes ran and the vendor answered with one side, which is what it did for MUZ on the
        // capture of 2026-09-01. A name it never mentioned and a name it quoted with one side are
        // different facts, and neither is a spread.
        Pass(Session, "after_open");
        Pass(Session, "before_close");
        Snapshot("MUZ", Session, "after_open", basisPoints: null, lag: null, straddleSeconds: null);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(0, result.EntriesFilled);
        Assert.Equal(1, result.EntriesUnfilled);
        Assert.Empty(Fills(Session));

        StoredPosition position = Positions(Session).Single();
        Assert.Equal(PositionStatus.Unfilled, position.Status);
        Assert.Equal(0, position.Shares);
        Assert.Equal(PaperBroker.NoUsableQuote, position.UnfilledBecause);
        Assert.Null(position.EntryPrice);
    }

    /// <summary>
    /// A session nobody sampled prices nothing and says so, rather than charging no slippage.
    ///
    /// A quote is not purchasable after its instant has passed, so a session sampled nought times is
    /// a hole in the evidence rather than a session whose spreads were nought. Partial and not
    /// failed: the stage did its whole job over a session whose evidence is missing.
    /// </summary>
    [Fact]
    public void A_session_nobody_sampled_prices_nothing_and_is_recorded_partial()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(PaperBroker.SessionWasNeverSampled, result.StoppedBecause);
        Assert.Equal(1, result.EntriesUnfilled);
        Assert.Empty(Fills(Session));

        Assert.Equal(
            PaperBroker.SessionWasNeverSampled, Positions(Session).Single().UnfilledBecause);
    }

    /// <summary>
    /// A session with orders resting in it and no stored minute is partial, on the terms the
    /// resolver reports one.
    ///
    /// A blind night reported as a night on which nothing filled is the shape that cost this lab an
    /// evening of evidence.
    /// </summary>
    [Fact]
    public void A_session_with_orders_and_no_stored_minute_is_recorded_partial()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Quotes("AAPL", Session);

        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(PaperBroker.SessionHeldNoMinutes, result.StoppedBecause);
        Assert.Equal(0, result.MinutesWalked);
        Assert.Equal(1, result.EntriesUnfilled);
        Assert.Equal(
            PaperBroker.TriggerMinuteNotStored, Positions(Session).Single().UnfilledBecause);
    }

    /// <summary>A night with nothing to price is clean and says so.</summary>
    [Fact]
    public void A_night_with_no_order_is_clean()
    {
        FillRunResult result = Stage().Fill(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(PaperBroker.NothingToFill, result.StoppedBecause);
        Assert.Empty(Positions(Session));
    }

    // ---- the two short assumptions -----------------------------------------------------------

    /// <summary>
    /// A short position carries the assumed borrow rate and the note that availability is not
    /// modelled, and a long carries neither.
    ///
    /// ARCHITECTURE has said since the failure table was written that both are recorded on every
    /// short trade from this checkpoint. A claim that something is recorded on every row is a claim
    /// about a surface, which is the sixth failure shape this corpus catalogues, so it is asserted
    /// over the row rather than over the constant.
    /// </summary>
    [Fact]
    public void A_short_position_carries_the_two_unmodelled_assumptions_and_a_long_carries_neither()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);

        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);

        Quotes("AAPL", Session);
        Quotes("TSLA", Session);

        Stage().Fill(Session);

        StoredPosition longSide = Positions(Session).Single(p => p.Ticker == "AAPL");
        StoredPosition shortSide = Positions(Session).Single(p => p.Ticker == "TSLA");

        Assert.Null(longSide.BorrowRateAssumed);
        Assert.Null(longSide.BorrowAvailability);

        Assert.Equal(BorrowAssumption.AnnualisedRate, shortSide.BorrowRateAssumed);
        Assert.Equal(BorrowAssumption.AvailabilityIsNotModelled, shortSide.BorrowAvailability);

        // A short sells to enter, so it is charged the spread downward.
        Assert.Equal(99.90m, shortSide.EntryPrice);
    }

    /// <summary>A rerun over a session already priced writes nothing, on the store's own keys.</summary>
    [Fact]
    public void A_rerun_over_a_priced_session_writes_nothing()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);

        Stage().Fill(Session);

        FillRunResult again = Stage().Fill(Session);

        Assert.Equal(1, again.EntriesFilled);
        Assert.Single(Positions(Session));
        Assert.Single(Fills(Session));
        Assert.Equal(PositionStatus.Open, Positions(Session).Single().Status);
    }

    // ---- the book the caps read --------------------------------------------------------------

    /// <summary>
    /// RiskGate counts the positions carried in rather than opening on an empty book, which is what
    /// 4.7 changed.
    ///
    /// Four positions held overnight is a full book, so the fifth trigger of the next morning is
    /// refused. Before this checkpoint a position held overnight occupied no slot the next morning
    /// and the caps were looser than the design rather than tighter.
    /// </summary>
    [Fact]
    public void The_caps_count_the_positions_the_lab_is_holding()
    {
        string[] held = ["AAA", "BBB", "CCC", "DDD"];

        foreach (string ticker in held)
        {
            Plan(ticker, SetupDirection.Long, trigger: 100m, giveUp: 95m);
            Order(ticker, SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
            Minute(ticker, Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
            Quotes(ticker, Session);
        }

        Stage().Fill(Session);

        Assert.Equal(4, PositionsOpenComingInto(NextSession));

        // A fifth plan, live in the next session, triggering into a book of four.
        Plan("EEE", SetupDirection.Long, trigger: 100m, giveUp: 95m, evening: Session, liveSession: NextSession);
        Order("EEE", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150, session: NextSession);

        OrderRunResult gate = Gate(NextSession).Apply(NextSession);

        Assert.Equal(0, gate.Placed);
        Assert.Equal(1, gate.BlockedOpenPositions);
    }

    // ---- the night's own record --------------------------------------------------------------

    /// <summary>
    /// The run row carries the book it was handed and what it priced, and no longer a book at the
    /// end of the night.
    ///
    /// <c>exits_filled</c> and <c>open_at_end</c> were dropped by migration 045 rather than kept
    /// reading nought for ever: exits moved to PositionManager at 4.8, and this stage cannot know
    /// what the night ended holding. <c>manage_run</c> is what carries that now.
    /// </summary>
    [Fact]
    public void The_run_row_carries_the_book_it_was_handed_and_what_it_priced()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);

        Stage().Fill(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredFillRun run = PositionReader.RunsFor(connection, Session).First();

        Assert.Equal(0, run.OpenAtStart);
        Assert.Equal(1, run.OrdersPlaced);
        Assert.Equal(1, run.EntriesFilled);
        Assert.Equal(0, run.EntriesUnfilled);
        Assert.Equal(1, run.NamesWalked);
        Assert.Equal(1, run.MinutesWalked);
        Assert.Equal("clean", run.Outcome);
    }

    // ---- the surface the figures are read on -------------------------------------------------

    /// <summary>
    /// The status band reads the positions the lab is holding, and a nought is told apart from an
    /// absence.
    ///
    /// <b>The sixth failure shape, asserted on the surface it is about.</b> The figures are right
    /// in the store the moment this stage writes them; what has to hold is that the band carries
    /// them, which is the only place that claim was ever about. The three fields read "not until
    /// 4.7" from 4.1, honestly, and the band's own guard is what caught them on this checkpoint's
    /// first CI run, which is what that guard was written for.
    /// </summary>
    [Fact]
    public void The_status_band_reads_the_positions_the_lab_is_holding()
    {
        CurrentTo(Session);

        StatusResponse flat = Status();
        Assert.Equal(0, flat.PositionsOpen);
        Assert.Equal(0, flat.ShortPositionsOpen);
        Assert.Equal(0m, flat.RiskAtStake);

        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);

        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);

        Quotes("AAPL", Session);
        Quotes("TSLA", Session);

        Stage().Fill(Session);

        StatusResponse held = Status();
        Assert.Equal(2, held.PositionsOpen);
        Assert.Equal(1, held.ShortPositionsOpen);

        // Each position risks 150 shares against 5.10, being the plan's five points and the dime
        // the entry crossing cost, so 1,530 against a fixed notional of 100,000. The band shows it
        // as a percentage because the cap it is read against is stated as one.
        Assert.Equal(1.530m, held.RiskAtStake);

        // And the view renders those rather than a checkpoint, which is the half a store read
        // cannot see.
        Assert.Equal("2", View(held).PositionsText);
        Assert.Equal("1", View(held).ShortPositionsText);
        Assert.Equal("1.53%", View(held).RiskText);
    }

    /// <summary>A store with no session in it answers the three fields with nothing, never a nought.</summary>
    [Fact]
    public void A_store_with_no_session_answers_the_position_fields_with_nothing()
    {
        StatusResponse empty = Status();

        Assert.Null(empty.PositionsOpen);
        Assert.Null(empty.ShortPositionsOpen);
        Assert.Null(empty.RiskAtStake);

        Assert.Equal(LabStatusView.Unanswered, View(empty).PositionsText);
    }

    // ---- scaffolding --------------------------------------------------------------------------

    private StatusResponse Status() => LabStatus.Read(
        _connections, _clock, dailyCallCeiling: 5000, SessionBoundaries.UsEquities);

    /// <summary>The view the band renders, built from the payload the read surface answered with.</summary>
    private static LabStatusView View(StatusResponse status) => new(
        true, null, status.Store, status.SchemaVersion, status.SchemaVersionExpected,
        status.Session, null, null, status.UniverseMembers, status.BarsStored,
        status.CallsUsed, status.DailyCallCeiling, status.MarketMood,
        status.PositionsOpen, status.ShortPositionsOpen, status.RiskAtStake);

    /// <summary>The session the store is current to, which is what the band's reads are bounded on.</summary>
    private void CurrentTo(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('MARK', 'MARK', 'NASDAQ', 'Common Stock', @as_of)
            ON CONFLICT (ticker) DO NOTHING;

            INSERT INTO universe_snapshot (as_of, ticker) VALUES (@as_of, 'MARK');
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.ExecuteNonQuery();
    }

    private PaperBroker Stage(DateOnly? on = null)
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        FixedClock clock = on is null
            ? _clock
            : new FixedClock(SessionBoundaries.At(on.Value, new TimeOnly(21, 15), SessionBoundaries.UsEquities));

        return new PaperBroker(_connections, new RunLogger(clock, options), clock, options);
    }

    private RiskGate Gate(DateOnly on)
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        var clock = new FixedClock(
            SessionBoundaries.At(on, new TimeOnly(21, 10), SessionBoundaries.UsEquities));

        return new RiskGate(_connections, new RunLogger(clock, options), clock, options);
    }

    private IReadOnlyList<StoredPosition> Positions(DateOnly openedSession, DateOnly? asOf = null)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.ForOpenedSession(connection, openedSession, asOf ?? NextSession.AddDays(1));
    }

    private int PositionsOpenComingInto(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.OpenComingInto(connection, session, session).Count;
    }

    private IReadOnlyList<StoredFill> Fills(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.FillsOf(connection, session, NextSession.AddDays(1));
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
                setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @setup_id, @as_of, @live_session, @ticker, @direction,
                @trigger, @give_up, @distance, @shares,
                @equity, @fraction, @budget, @at_stake, @observed_at);
            """;
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
        string ticker, string direction, TimeOnly at, int shares, DateOnly? session = null)
    {
        DateOnly live = session ?? Session;
        DateOnly evening = live == Session ? Evening : Session;
        string setupId = SetupIdOf(ticker, direction, evening);
        DateTimeOffset touchedAt = SessionBoundaries.At(live, at, SessionBoundaries.UsEquities);
        DateTimeOffset observedAt = SessionBoundaries.At(
            live, new TimeOnly(21, 5), SessionBoundaries.UsEquities);

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
                order_id, setup_id, live_session, ticker, direction, triggered_at, status,
                planned_shares, shares, risk_at_stake, observed_at)
            VALUES (@id, @id, @live_session, @ticker, @direction, @triggered_at, 'placed',
                    @shares, @shares, @risk, @observed_at);
            """;
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
