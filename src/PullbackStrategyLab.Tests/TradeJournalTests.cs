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
/// The trade a closed position becomes, and the plan held against what happened to it.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a trade. The
/// positions below are opened and closed by the two stages that own those operations rather than
/// inserted, which is what makes the arithmetic a property of the pipeline rather than of a fixture.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class TradeJournalTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);
    private static readonly DateOnly NextSession = new(2026, 8, 27);
    private static readonly DateOnly ThirdSession = new(2026, 8, 28);

    private const double TenBasisPoints = 10d;

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public TradeJournalTests()
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

    // ---- the trade -----------------------------------------------------------------------------

    /// <summary>
    /// A long that stopped out becomes a trade whose result is the position's, because a long is
    /// charged no borrow.
    /// </summary>
    [Fact]
    public void A_closed_long_becomes_a_trade_and_pays_no_borrow()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);

        TradeRunResult result = Journal().Close(Session);

        Assert.Equal(1, result.ClosedInSession);
        Assert.Equal(1, result.Journalled);
        Assert.Equal(1, result.Longs);
        Assert.Equal(0, result.Shorts);
        Assert.Equal(0, result.ShortsCharged);

        StoredTrade trade = Trades(Session).Single();
        Assert.Null(trade.BorrowCost);
        Assert.Null(trade.BorrowRateAssumed);
        Assert.Equal(trade.GrossPnl, trade.NetPnl);
        Assert.Equal(ExitReason.GaveUp, trade.ExitReason);

        // Before borrow and after borrow are the same number on a long, and the position's own
        // figure is the one this equals.
        StoredPosition position = Positions(Session).Single();
        Assert.Equal(position.RealisedR!.Value, trade.ResultR, 10);
    }

    /// <summary>
    /// A short held overnight is charged borrow for the calendar days it was held, at the rate its
    /// own position stamped on itself.
    ///
    /// The position was worth 150 shares at 99.90, so 14,985 at 1.0% a year over one calendar day is
    /// about 41 cents. It is small on purpose: the rate is set several times higher than a
    /// general-collateral borrow costs and it still rounds to nothing against a stop, which is why
    /// availability rather than cost is what the short side turns on.
    /// </summary>
    [Fact]
    public void A_short_held_overnight_is_charged_borrow_at_its_own_positions_rate()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Quotes("TSLA", Session);
        DailyBar("TSLA", Session, close: 99m);
        Broker().Fill(Session);
        Manager().Manage(Session);

        Minute("TSLA", NextSession, new TimeOnly(9, 30), 100m, 106m, 100m, 105m);
        Quotes("TSLA", NextSession);
        DailyBar("TSLA", NextSession, close: 105m);
        Manager(NextSession).Manage(NextSession);

        TradeRunResult result = Journal(NextSession).Close(NextSession);

        Assert.Equal(1, result.Shorts);
        Assert.Equal(1, result.ShortsCharged);

        StoredTrade trade = Trades(NextSession).Single();
        Assert.Equal(1, trade.HeldCalendarDays);
        Assert.Equal(2, trade.HeldSessions);
        Assert.Equal(BorrowAssumption.AnnualisedRate, trade.BorrowRateAssumed);

        decimal expected = BorrowCost.Charged(trade.ValueAtEntry, BorrowAssumption.AnnualisedRate, 1);
        Assert.Equal(expected, trade.BorrowCost);
        Assert.Equal(trade.GrossPnl - expected, trade.NetPnl);

        // After borrow is worse than before it, and the position's own figure is the one before.
        StoredPosition position = Positions(Session).Single();
        Assert.True(trade.ResultR < position.RealisedR!.Value,
            $"A short charged borrow reported {trade.ResultR} R against a gross {position.RealisedR}.");
    }

    /// <summary>A short closed in the session it opened in was never held overnight and pays nothing.</summary>
    [Fact]
    public void A_short_closed_in_its_own_session_is_charged_no_borrow()
    {
        OpenAndStopOut("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);

        TradeRunResult result = Journal().Close(Session);

        Assert.Equal(1, result.Shorts);
        Assert.Equal(0, result.ShortsCharged);

        StoredTrade trade = Trades(Session).Single();
        Assert.Equal(0, trade.HeldCalendarDays);
        Assert.Equal(0m, trade.BorrowCost);
        Assert.Equal(trade.GrossPnl, trade.NetPnl);
    }

    /// <summary>
    /// A trimmed short's money is the trim's plus the close's, and its exit covered what the trim
    /// left, which is the obligation 4.8 raised against this checkpoint.
    ///
    /// Reading `shares` as what the exit covered would overstate a trimmed short by the trim, and
    /// nothing outside PositionManager read the distinction until this stage existed.
    /// </summary>
    [Fact]
    public void A_trimmed_shorts_money_is_both_halves_and_its_exit_covered_what_was_left()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 100m, giveUp: 105m);
        Order("TSLA", SetupDirection.Short, at: new TimeOnly(10, 0), shares: 150);
        Minute("TSLA", Session, new TimeOnly(10, 0), 101m, 101m, 99m, 99.5m);
        Minute("TSLA", Session, new TimeOnly(11, 0), 90m, 90m, 84m, 84.5m);
        Minute("TSLA", Session, new TimeOnly(15, 0), 100m, 106m, 100m, 105m);
        Quotes("TSLA", Session);
        DailyBar("TSLA", Session, close: 105m);
        Broker().Fill(Session);
        Manager().Manage(Session);

        TradeRunResult result = Journal().Close(Session);

        Assert.Equal(1, result.Trimmed);

        StoredTrade trade = Trades(Session).Single();
        Assert.Equal(150, trade.Shares);
        Assert.Equal(22, trade.TrimmedShares);

        StoredFill exit = Fills(Session).Single(f => f.Leg == "exit");
        StoredFill trim = Fills(Session).Single(f => f.Leg == "trim");
        Assert.Equal(128, exit.Shares);

        decimal entry = trade.EntryPrice;
        Assert.Equal(((entry - trim.Price) * 22) + ((entry - exit.Price) * 128), trade.GrossPnl);
    }

    /// <summary>
    /// A trade whose exit a rule armed on an earlier session records how many sessions it waited,
    /// which is the second obligation 4.8 raised.
    ///
    /// The rule fills at the next open the store holds minutes for, so a session the lab was blind
    /// on postpones the fill rather than reconsidering it. The figure is on each trade rather than
    /// left as an argument about how often that happens.
    /// </summary>
    [Fact]
    public void An_armed_exit_records_how_many_sessions_it_waited()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);
        Manager().Manage(Session);

        // The store holds a bar for the next session and no minutes, so the armed exit waits.
        DailyBar("AAPL", NextSession, close: 98m);

        Minute("AAPL", ThirdSession, new TimeOnly(9, 30), 97m, 98m, 96m, 97m);
        Quotes("AAPL", ThirdSession);
        DailyBar("AAPL", ThirdSession, close: 97m);
        Manager(ThirdSession).Manage(ThirdSession);

        TradeRunResult result = Journal(ThirdSession).Close(ThirdSession);

        Assert.Equal(1, result.ArmedExits);

        StoredTrade trade = Trades(ThirdSession).Single();
        Assert.Equal(Session, trade.ExitArmedSession);
        Assert.Equal(2, trade.ArmedSessionsWaited);
        Assert.Equal(ExitReason.Trail, trade.ExitReason);
    }

    /// <summary>A night that closed nothing is clean and says so.</summary>
    [Fact]
    public void A_night_that_closed_nothing_is_clean()
    {
        TradeRunResult result = Journal().Close(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(TradeJournal.NothingClosed, result.StoppedBecause);
        Assert.Empty(Trades(Session));
    }

    /// <summary>A rerun over a journalled session writes nothing.</summary>
    [Fact]
    public void A_rerun_over_a_journalled_session_writes_nothing()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);

        TradeRunResult again = Journal().Close(Session);

        Assert.Equal(1, again.ClosedInSession);
        Assert.Equal(0, again.Journalled);
        Assert.Single(Trades(Session));
    }

    /// <summary>A trade written after the as-of is invisible to a read standing before it.</summary>
    [Fact]
    public void A_trade_written_after_the_as_of_is_invisible()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);

        Assert.Single(Trades(Session, asOf: Session));
        Assert.Empty(Trades(Session, asOf: Evening));
    }

    // ---- the audit -----------------------------------------------------------------------------

    /// <summary>
    /// The audit's first pair is execution: what each instruction named against what it got, in
    /// money and in basis points, positive where the trade was worse off.
    ///
    /// A long triggering at 100 with a ten-basis-point spread buys at 100.10 and sells its stop of
    /// 95 at 94.905, so both differences are positive and both are ten basis points of the price
    /// each order named.
    /// </summary>
    [Fact]
    public void The_first_pair_is_what_each_instruction_named_against_what_it_got()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);

        AuditRunResult result = Auditor().Audit(Session);

        Assert.Equal(1, result.TradesRead);
        Assert.Equal(1, result.Audited);
        Assert.Equal(1, result.Longs);

        StoredPlanAudit audit = Audits(Session).Single();
        Assert.Equal(100m, audit.PlannedTrigger);
        Assert.Equal(100.10m, audit.ExecutedEntry);
        Assert.Equal(0.10m, audit.EntryDifference);
        Assert.Equal(TenBasisPoints, audit.EntryDifferenceBasisPoints, 6);
        Assert.Equal(FillModel.Slipped, audit.EntryBasis);

        Assert.Equal(95m, audit.ExitRestingPrice);
        Assert.Equal(94.905m, audit.ExecutedExit);
        Assert.Equal(0.095m, audit.ExitDifference);
        Assert.Equal(TenBasisPoints, audit.ExitDifferenceBasisPoints, 6);
    }

    /// <summary>
    /// The second pair is the plan's stop against where the trade ended, and on a trail exit it is a
    /// different number from the first.
    ///
    /// This is the distinction the row exists for. A trail exit ends nowhere near the give-up point
    /// by design, so reading the two as one would report every winner as an enormous execution
    /// failure.
    /// </summary>
    [Fact]
    public void The_second_pair_is_the_plans_stop_and_is_not_the_first_one_restated()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 99m);
        Indicators("AAPL", Session, ema9: 102m, ema50: 90m);
        Broker().Fill(Session);
        Manager().Manage(Session);

        // The trail fires at the next open, well above the give-up point.
        Minute("AAPL", NextSession, new TimeOnly(9, 30), 120m, 121m, 119m, 120m);
        Quotes("AAPL", NextSession);
        DailyBar("AAPL", NextSession, close: 120m);
        Manager(NextSession).Manage(NextSession);
        Journal(NextSession).Close(NextSession);

        Auditor(NextSession).Audit(NextSession);

        StoredPlanAudit audit = Audits(NextSession).Single();
        Assert.Equal(ExitReason.Trail, audit.ExitReason);

        // The exit did what the rule asked to within a spread.
        Assert.Equal(120m, audit.ExitRestingPrice);
        Assert.Equal(TenBasisPoints, audit.ExitDifferenceBasisPoints, 6);

        // And against the plan's stop it is a completely different quantity, in the other direction.
        Assert.Equal(95m, audit.PlannedGiveUp);
        Assert.True(audit.GiveUpDifference < -24m,
            $"A trail exit 25 points above the stop reported a give-up difference of {audit.GiveUpDifference}.");
    }

    /// <summary>
    /// The third pair is the gate: the size the plan carried against the size that was placed, with
    /// the cap that bound.
    ///
    /// RiskGate may reduce a size and may never recompute one, so what is compared is an intention
    /// against an outcome rather than two runs of one formula.
    /// </summary>
    [Fact]
    public void The_third_pair_is_the_size_the_plan_carried_against_the_size_that_was_placed()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);
        Auditor().Audit(Session);

        StoredPlanAudit audit = Audits(Session).Single();
        Assert.Equal(150, audit.PlannedShares);
        Assert.Equal(150, audit.ExecutedShares);
        Assert.Equal(0, audit.SharesDifference);
        Assert.Null(audit.ReducedBecause);

        // The risk the plan intended is the placed count against the distance it named; the risk
        // realised is the same count against the distance from the price the fill got. They differ
        // by the entry slippage and by nothing else.
        Assert.Equal(150 * 5m, audit.RiskIntended);
        Assert.Equal(150 * 5.10m, audit.RiskRealised);
        Assert.Equal(150 * 0.10m, audit.RiskDifference);
    }

    /// <summary>
    /// A gap is recorded as a gap and never read as slippage, which is why the basis is on the row.
    ///
    /// The model charges nothing on a gap and the price moved anyway, so the difference here is real
    /// and is not what the fill was charged. An audit that copied `fill.slippage` would report
    /// nought and hide the largest execution difference the lab ever sees.
    /// </summary>
    [Fact]
    public void A_gap_is_recorded_as_a_gap_and_is_never_read_as_slippage()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Order("AAPL", SetupDirection.Long, at: new TimeOnly(10, 0), shares: 150);
        Minute("AAPL", Session, new TimeOnly(10, 0), 99m, 101m, 99m, 100.5m);
        Quotes("AAPL", Session);
        DailyBar("AAPL", Session, close: 100m);
        Broker().Fill(Session);
        Manager().Manage(Session);

        Minute("AAPL", NextSession, new TimeOnly(9, 30), 88m, 89m, 87m, 88.5m);
        Quotes("AAPL", NextSession);
        DailyBar("AAPL", NextSession, close: 88m);
        Manager(NextSession).Manage(NextSession);
        Journal(NextSession).Close(NextSession);

        AuditRunResult result = Auditor(NextSession).Audit(NextSession);

        Assert.Equal(1, result.GappedAtAnEnd);

        StoredPlanAudit audit = Audits(NextSession).Single();
        Assert.Equal(FillModel.Gapped, audit.ExitBasis);
        Assert.Equal(FillModel.Slipped, audit.EntryBasis);

        // Seven points worse than the price the order named, where the fill was charged nothing.
        Assert.Equal(7m, audit.ExitDifference);
        Assert.Equal(0m, Fills(NextSession).Single(f => f.Leg == "exit").Slippage);
    }

    /// <summary>A rerun over an audited session writes nothing.</summary>
    [Fact]
    public void A_rerun_over_an_audited_session_writes_nothing()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);
        Auditor().Audit(Session);

        AuditRunResult again = Auditor().Audit(Session);

        Assert.Equal(1, again.TradesRead);
        Assert.Equal(0, again.Audited);
        Assert.Single(Audits(Session));
    }

    /// <summary>A night with no trade in it is clean and says so.</summary>
    [Fact]
    public void A_night_with_no_trade_is_clean()
    {
        AuditRunResult result = Auditor().Audit(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(PlanAudit.NothingToAudit, result.StoppedBecause);
    }

    /// <summary>
    /// The audit runs after the journal and changes no result, which is what the ordering buys.
    ///
    /// Asserted over the row rather than over the sequence: the trade's figures are identical before
    /// and after the audit ran, so a component that adjusted one would be visible here.
    /// </summary>
    [Fact]
    public void The_audit_changes_no_result()
    {
        OpenAndStopOut("AAPL", SetupDirection.Long, trigger: 100m, giveUp: 95m);
        Journal().Close(Session);

        StoredTrade before = Trades(Session).Single();
        Auditor().Audit(Session);
        StoredTrade after = Trades(Session).Single();

        Assert.Equal(before, after);
    }

    // ---- scaffolding ---------------------------------------------------------------------------

    /// <summary>A position opened and stopped out inside one session, which is the shortest trade.</summary>
    private void OpenAndStopOut(string ticker, string direction, decimal trigger, decimal giveUp)
    {
        bool isLong = string.Equals(direction, SetupDirection.Long, StringComparison.Ordinal);

        Plan(ticker, direction, trigger, giveUp);
        Order(ticker, direction, at: new TimeOnly(10, 0), shares: 150);
        Minute(ticker, Session, new TimeOnly(10, 0),
            isLong ? 99m : 101m, 101m, 99m, isLong ? 100.5m : 99.5m);
        Minute(ticker, Session, new TimeOnly(11, 0),
            isLong ? 99m : 101m,
            isLong ? 99m : giveUp + 1m,
            isLong ? giveUp - 1m : 101m,
            isLong ? giveUp : giveUp + 1m);
        Quotes(ticker, Session);
        DailyBar(ticker, Session, close: trigger);
        Broker().Fill(Session);
        Manager().Manage(Session);
    }

    private PaperBroker Broker(DateOnly? on = null)
    {
        (IOptions<PullbackStrategyLabOptions> options, FixedClock clock) = At(on ?? Session, new TimeOnly(21, 15));
        return new PaperBroker(_connections, new RunLogger(clock, options), clock, options);
    }

    private PositionManager Manager(DateOnly? on = null)
    {
        (IOptions<PullbackStrategyLabOptions> options, FixedClock clock) = At(on ?? Session, new TimeOnly(21, 20));
        return new PositionManager(_connections, new RunLogger(clock, options), clock, options);
    }

    private TradeJournal Journal(DateOnly? on = null)
    {
        (IOptions<PullbackStrategyLabOptions> options, FixedClock clock) = At(on ?? Session, new TimeOnly(21, 25));
        return new TradeJournal(_connections, new RunLogger(clock, options), clock, options);
    }

    private PlanAudit Auditor(DateOnly? on = null)
    {
        (IOptions<PullbackStrategyLabOptions> options, FixedClock clock) = At(on ?? Session, new TimeOnly(21, 26));
        return new PlanAudit(_connections, new RunLogger(clock, options), clock, options);
    }

    private (IOptions<PullbackStrategyLabOptions>, FixedClock) At(DateOnly session, TimeOnly time) =>
        (Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path }),
         new FixedClock(SessionBoundaries.At(session, time, SessionBoundaries.UsEquities)));

    private IReadOnlyList<StoredTrade> Trades(DateOnly session, DateOnly? asOf = null)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradeReader.ClosedIn(connection, session, asOf ?? ThirdSession, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredPlanAudit> Audits(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        string[] ids = [.. TradeReader.ClosedIn(connection, session, ThirdSession, SessionBoundaries.UsEquities).Select(t => t.TradeId)];
        return TradeReader.AuditsOf(connection, ids, ThirdSession, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredPosition> Positions(DateOnly openedSession)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.ForOpenedSession(connection, openedSession, ThirdSession, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredFill> Fills(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return PositionReader.FillsOf(connection, session, ThirdSession, SessionBoundaries.UsEquities);
    }

    private static string SetupIdOf(string ticker, string direction, DateOnly evening) =>
        $"{evening:yyyy-MM-dd}-{ticker}-{direction}";

    private void Plan(string ticker, string direction, decimal trigger, decimal giveUp)
    {
        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);

        using SqliteConnection connection = _connections.OpenWrite();
        string setupId = SetupIdOf(ticker, direction, Evening);

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

    private void Order(string ticker, string direction, TimeOnly at, int shares)
    {
        string setupId = SetupIdOf(ticker, direction, Evening);
        DateTimeOffset touchedAt = SessionBoundaries.At(Session, at, SessionBoundaries.UsEquities);
        DateTimeOffset observedAt = SessionBoundaries.At(
            Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities);

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
            resolution.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
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
