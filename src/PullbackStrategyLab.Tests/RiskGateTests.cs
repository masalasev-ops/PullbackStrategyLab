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
/// The caps, applied to each trigger in the order it happened.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel passes
/// a median of nought candidates a night on both sides, so no captured night holds a plan, a trigger
/// or an order. The plans and triggers below are written to sit either side of each cap, which is the
/// footing every gate boundary in this suite stands on.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class RiskGateTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 8, 25);
    private static readonly DateOnly Session = new(2026, 8, 26);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, new TimeOnly(21, 10), SessionBoundaries.UsEquities));

    public RiskGateTests()
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

    private RiskGate Stage()
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new RiskGate(_connections, new RunLogger(_clock, options), _clock, options);
    }

    // ---- the arithmetic, over every arrangement rather than over a session -----------------

    /// <summary>
    /// The caps are the six ARCHITECTURE states, and what each one can do is a property of the cap.
    ///
    /// Two count caps that can only block, two proportional caps that reduce, one quantity the plan
    /// was sized from and one gate that runs at detection. 4.6's own row said three count caps, which
    /// is one more than either table holds, and this is the reconciliation as an assertion.
    /// </summary>
    [Fact]
    public void The_six_limits_are_two_counts_two_proportions_a_budget_and_a_detection_gate()
    {
        Assert.Equal(4, RiskCaps.MaxOpenPositions);
        Assert.Equal(2, RiskCaps.MaxOpenShortPositions);
        Assert.Equal(0.35m, RiskCaps.MaxPositionFraction);
        Assert.Equal(0.03m, RiskCaps.MaxTotalRiskFraction);
        Assert.Equal(0.0075m, PositionSizing.RiskPerTrade);
        Assert.Equal(0.5m, RiskCaps.GiveUpDistanceRanges);

        // Four positions each risking the whole per-trade budget is the total-risk cap exactly, so
        // the two proportional caps are consistent with each other rather than merely both stated.
        Assert.Equal(PositionSizing.RiskBudget * RiskCaps.MaxOpenPositions, RiskCaps.MaxTotalRisk);

        // The two the gate applies, the two it does not, and the two that reduce, named as sets so a
        // cap added later has to be classified rather than appearing in one of them by accident.
        Assert.Equal(
            [RiskLimits.OpenPositions, RiskLimits.OpenShorts, RiskLimits.PositionSize, RiskLimits.TotalRisk],
            RiskLimits.All);
    }

    /// <summary>An order that fits every cap is placed at the plan's own size, with nothing bound.</summary>
    [Fact]
    public void An_order_that_fits_every_cap_is_placed_unchanged()
    {
        RiskVerdict verdict = RiskLimits.Apply(
            SetupDirection.Long, plannedShares: 100, triggerPrice: 50m, giveUpDistance: 5m, OpenBook.Empty);

        Assert.True(verdict.IsPlaced);
        Assert.Equal(100, verdict.Shares);
        Assert.Equal(500m, verdict.RiskAtStake);
        Assert.False(verdict.Reduced);
        Assert.Null(verdict.BoundBy);
    }

    /// <summary>
    /// The count caps block and never reduce, because there is no fraction of a slot.
    /// </summary>
    [Fact]
    public void A_count_cap_blocks_rather_than_reducing()
    {
        RiskVerdict fifth = RiskLimits.Apply(
            SetupDirection.Long, 100, 50m, 5m, new OpenBook(RiskCaps.MaxOpenPositions, 0, 0m));

        Assert.False(fifth.IsPlaced);
        Assert.Equal(0, fifth.Shares);
        Assert.Equal(RiskLimits.OpenPositions, fifth.BoundBy);
        Assert.Contains("4", fifth.Because!, StringComparison.Ordinal);

        RiskVerdict third = RiskLimits.Apply(
            SetupDirection.Short, 100, 50m, 5m, new OpenBook(2, RiskCaps.MaxOpenShortPositions, 0m));

        Assert.False(third.IsPlaced);
        Assert.Equal(RiskLimits.OpenShorts, third.BoundBy);
    }

    /// <summary>
    /// The short cap is a bound inside the whole rather than beside it: two shorts and two longs is
    /// four positions, and the fifth is refused by the count cap and not by the short one.
    /// </summary>
    [Fact]
    public void The_short_cap_sits_inside_the_position_cap_rather_than_beside_it()
    {
        RiskVerdict verdict = RiskLimits.Apply(
            SetupDirection.Short, 100, 50m, 5m, new OpenBook(4, 2, 0m));

        Assert.Equal(RiskLimits.OpenPositions, verdict.BoundBy);

        // A third long with two shorts open is fine, because the short cap governs the short side.
        RiskVerdict longSide = RiskLimits.Apply(SetupDirection.Long, 100, 50m, 5m, new OpenBook(2, 2, 0m));

        Assert.True(longSide.IsPlaced);
    }

    /// <summary>
    /// The position-size cap reduces to what 35% of the account buys, and never recomputes a size
    /// from a risk budget.
    ///
    /// $35,000 at $50 is 700 shares. A plan of 1,000 is trimmed to 700 and the cap is named on the
    /// row; the give-up distance is untouched, so what the trade risks falls with the size rather
    /// than the stop moving.
    /// </summary>
    [Fact]
    public void The_position_size_cap_reduces_and_names_itself()
    {
        RiskVerdict verdict = RiskLimits.Apply(
            SetupDirection.Long, plannedShares: 1_000, triggerPrice: 50m, giveUpDistance: 0.5m, OpenBook.Empty);

        Assert.True(verdict.IsPlaced);
        Assert.True(verdict.Reduced);
        Assert.Equal(700, verdict.Shares);
        Assert.Equal(RiskLimits.PositionSize, verdict.BoundBy);
        Assert.Equal(350m, verdict.RiskAtStake);
    }

    /// <summary>
    /// The total-risk cap reduces to the room left in the account, which is the other proportional
    /// cap and the one that depends on what is already open.
    /// </summary>
    [Fact]
    public void The_total_risk_cap_reduces_to_the_room_left()
    {
        // $2,900 of $3,000 already at stake leaves $100, which at a $5 give-up distance is 20 shares.
        RiskVerdict verdict = RiskLimits.Apply(
            SetupDirection.Long, 100, 50m, 5m, new OpenBook(3, 0, 2_900m));

        Assert.True(verdict.IsPlaced);
        Assert.Equal(20, verdict.Shares);
        Assert.Equal(RiskLimits.TotalRisk, verdict.BoundBy);
        Assert.Equal(100m, verdict.RiskAtStake);
    }

    /// <summary>
    /// The tighter of the two proportional caps decides, whichever order they are asked in.
    ///
    /// Each is asked for the largest count it allows and the smallest answer wins, so a size that
    /// satisfies one and not the other cannot survive by being applied in a convenient order.
    /// </summary>
    [Fact]
    public void The_tighter_proportional_cap_decides()
    {
        // Position size allows 700 at $50; the room left allows 30 at a $5 distance.
        RiskVerdict tightRisk = RiskLimits.Apply(
            SetupDirection.Long, 1_000, 50m, 5m, new OpenBook(1, 0, 2_850m));

        Assert.Equal(30, tightRisk.Shares);
        Assert.Equal(RiskLimits.TotalRisk, tightRisk.BoundBy);

        // The whole budget free and a dearer name: the account allows 600 shares of risk at a $5
        // distance and 350 shares of a $100 stock, so position size is now the tighter of the two.
        RiskVerdict tightSize = RiskLimits.Apply(SetupDirection.Long, 1_000, 100m, 5m, OpenBook.Empty);

        Assert.Equal(350, tightSize.Shares);
        Assert.Equal(RiskLimits.PositionSize, tightSize.BoundBy);

        // And with an empty book at $50 the total-risk cap is the tighter one, which is worth
        // asserting beside it: the two swap places on the price of the stock, so a test written at
        // one price would read as though one cap always won.
        RiskVerdict atFifty = RiskLimits.Apply(SetupDirection.Long, 1_000, 50m, 5m, OpenBook.Empty);

        Assert.Equal(600, atFifty.Shares);
        Assert.Equal(RiskLimits.TotalRisk, atFifty.BoundBy);
    }

    /// <summary>
    /// A proportional cap that reduces below one share blocks, and names the cap that took it there.
    ///
    /// The one path where reducing ends in a refusal, and the same floor PlanBuilder refuses on. A
    /// blocked row with no cap named would be a refusal nobody could act on.
    /// </summary>
    [Fact]
    public void A_reduction_below_one_share_blocks_and_names_the_cap()
    {
        RiskVerdict verdict = RiskLimits.Apply(
            SetupDirection.Long, 100, 50m, giveUpDistance: 5m, new OpenBook(3, 0, RiskCaps.MaxTotalRisk));

        Assert.False(verdict.IsPlaced);
        Assert.Equal(0, verdict.Shares);
        Assert.Equal(RiskLimits.TotalRisk, verdict.BoundBy);
        Assert.NotNull(verdict.Because);
    }

    /// <summary>An unknown direction is refused rather than granted one of two different limits.</summary>
    [Fact]
    public void An_unknown_direction_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RiskLimits.Apply("flat", 100, 50m, 5m, OpenBook.Empty));
    }

    /// <summary>
    /// Nothing in the gate turns a risk budget into a share count, asserted over the shipped source.
    ///
    /// <b>This is the behavioural half of the sizing decision that 4.16 could not reach</b>, and it
    /// is the checkpoint that could have broken it. 4.16 held it with a scan naming the one caller of
    /// <see cref="PositionSizing.SharesFor"/>; a scan cannot see a component that reimplements the
    /// division instead of calling the function, so the assertion here is that the gate produces the
    /// plan's own size wherever no cap binds, over a sweep of distances rather than over one.
    /// </summary>
    [Fact]
    public void The_gate_never_recomputes_a_size_from_a_risk_budget()
    {
        IReadOnlyList<string> callers =
        [
            .. RepositoryLayout.ProductionSourceFiles
                .Where(f => RepositoryLayout.Read(f).Contains("PositionSizing.SharesFor", StringComparison.Ordinal))
                .Select(f => Path.GetFileName(f)!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["PlanBuilder.cs"], callers);

        // And behaviourally: whatever the distance, an unbound order carries the plan's count. A gate
        // that recomputed would return the budget over the distance, which differs from the planned
        // count on every one of these except by coincidence.
        foreach (decimal distance in new[] { 0.25m, 1m, 2.5m, 7m, 13.33m })
        {
            RiskVerdict verdict = RiskLimits.Apply(SetupDirection.Long, plannedShares: 11, 10m, distance, OpenBook.Empty);

            Assert.Equal(11, verdict.Shares);
            Assert.Equal(11 * distance, verdict.RiskAtStake);
        }
    }

    // ---- the session, in the order the triggers happened -----------------------------------

    /// <summary>
    /// When more plans trigger than the caps allow, the earliest trigger fills and the later one is
    /// blocked with a reason.
    ///
    /// The done condition's own case, and the reason the resolver walks one clock for the session:
    /// which name fired first is a comparison across names.
    /// see: Plans are resting orders and fills go in time order when the caps bind
    /// </summary>
    [Fact]
    public void When_two_plans_trigger_and_one_slot_remains_the_earlier_fills()
    {
        // Four slots. Five names, the fifth touching last.
        Triggered("AAA", new TimeOnly(9, 30));
        Triggered("BBB", new TimeOnly(9, 31));
        Triggered("CCC", new TimeOnly(9, 32));
        Triggered("EARLY", new TimeOnly(10, 15));
        Triggered("LATE", new TimeOnly(10, 16));

        OrderRunResult result = Stage().Apply(Session);

        Assert.Equal(5, result.Triggers);
        Assert.Equal(4, result.Placed);
        Assert.Equal(1, result.Blocked);
        Assert.Equal(1, result.BlockedOpenPositions);

        IReadOnlyList<StoredTradeOrder> orders = Orders();

        Assert.Equal("placed", orders.Single(o => o.Ticker == "EARLY").Status);

        StoredTradeOrder late = orders.Single(o => o.Ticker == "LATE");

        Assert.Equal("blocked", late.Status);
        Assert.Equal(0, late.Shares);
        Assert.Equal(RiskLimits.OpenPositions, late.BoundBy);
        Assert.NotNull(late.BlockedBecause);

        // The plan's own size survives on the blocked row, because what it planned is what the audit
        // at 4.9 compares against.
        Assert.True(late.PlannedShares > 0);
    }

    /// <summary>
    /// Two plans touching in the same minute are ordered by ticker, and the tie is broken the same
    /// way every time.
    ///
    /// <b>Rank cannot break it and the corpus says so outright</b>: rank governs which setups are
    /// recorded under the nightly cap and how the screen sorts, and it governs no fill. So the tie
    /// falls to the ticker, which is the tiebreak the screen and the cap already use, and it is
    /// deterministic rather than fair. It is decided rather than left because a tie decided by
    /// whatever order a query happened to return would be a fill nobody could reproduce.
    /// </summary>
    [Fact]
    public void Two_plans_touching_in_one_minute_are_ordered_by_ticker()
    {
        Triggered("AAA", new TimeOnly(9, 30));
        Triggered("BBB", new TimeOnly(9, 31));
        Triggered("CCC", new TimeOnly(9, 32));
        Triggered("YYY", new TimeOnly(10, 15));
        Triggered("ZZZ", new TimeOnly(10, 15));

        Stage().Apply(Session);

        IReadOnlyList<StoredTradeOrder> orders = Orders();

        Assert.Equal("placed", orders.Single(o => o.Ticker == "YYY").Status);
        Assert.Equal("blocked", orders.Single(o => o.Ticker == "ZZZ").Status);
    }

    /// <summary>
    /// The short cap binds across the session, so a third short is blocked while a long is not.
    /// </summary>
    [Fact]
    public void A_third_short_is_blocked_and_a_long_in_the_same_book_is_not()
    {
        Triggered("SHA", new TimeOnly(9, 30), SetupDirection.Short);
        Triggered("SHB", new TimeOnly(9, 31), SetupDirection.Short);
        Triggered("SHC", new TimeOnly(9, 32), SetupDirection.Short);
        Triggered("LNG", new TimeOnly(9, 33));

        OrderRunResult result = Stage().Apply(Session);

        Assert.Equal(3, result.Placed);
        Assert.Equal(1, result.Blocked);
        Assert.Equal(1, result.BlockedOpenShorts);

        Assert.Equal("blocked", Orders().Single(o => o.Ticker == "SHC").Status);
        Assert.Equal("placed", Orders().Single(o => o.Ticker == "LNG").Status);
    }

    /// <summary>
    /// A plan whose trigger was never touched gets no order at all, placed or blocked.
    ///
    /// A blocked order is a decision the caps took, and there is no decision to take about a plan
    /// that never fired. Writing one would put a refusal on the record that the caps never made.
    /// </summary>
    [Fact]
    public void A_plan_that_never_triggered_gets_no_order()
    {
        Triggered("AAA", new TimeOnly(9, 30));
        Plan("BBB", SetupDirection.Long);

        OrderRunResult result = Stage().Apply(Session);

        Assert.Equal(1, result.Triggers);
        Assert.Single(Orders());
        Assert.Equal("AAA", Orders()[0].Ticker);
    }

    /// <summary>A session with no trigger is clean and says which nothing it was.</summary>
    [Fact]
    public void A_session_with_no_trigger_says_so()
    {
        OrderRunResult result = Stage().Apply(Session);

        Assert.Equal(0, result.Triggers);
        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(RiskGate.NoTriggers, result.StoppedBecause);
        Assert.Equal(RiskGate.NoTriggers, Runs().Single().StoppedBecause);
        Assert.Empty(Orders());
    }

    /// <summary>
    /// A night of blocked orders is clean, because the caps binding is what they are for.
    ///
    /// Calling it partial would report almost every busy morning as degraded, which is a signal that
    /// means nothing. Partial is reserved for a stage that could not do its work.
    /// </summary>
    [Fact]
    public void A_night_of_blocked_orders_is_clean()
    {
        for (int i = 0; i < 6; i++)
        {
            Triggered($"N{i:00}", new TimeOnly(9, 30 + i));
        }

        OrderRunResult result = Stage().Apply(Session);

        Assert.Equal(2, result.Blocked);
        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Null(result.StoppedBecause);
    }

    /// <summary>
    /// A plan risking more than the budget it names stops the stage rather than being trimmed.
    ///
    /// Risk per trade is what the plan was sized from and not a cap this component applies, so a plan
    /// over its own budget is a defect at 18:30. Gating it would treat a broken plan as an ordinary
    /// large one and carry the defect forward into a position.
    /// </summary>
    [Fact]
    public void A_plan_over_its_own_risk_budget_stops_the_stage()
    {
        Triggered("AAA", new TimeOnly(9, 30));

        using (SqliteConnection write = _connections.OpenWrite())
        {
            using SqliteCommand inflate = write.CreateCommand();
            inflate.CommandText =
                "UPDATE trade_plan SET risk_at_stake = @risk WHERE ticker = 'AAA';";
            inflate.Parameters.AddWithValue(
                "@risk", StoreText.PriceToStorageText(PositionSizing.RiskBudget + 1m));
            inflate.ExecuteNonQuery();
        }

        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => Stage().Apply(Session));

        Assert.Contains(RiskGate.PlanOverBudget, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(Orders());
    }

    /// <summary>A session that has closed does not change, so a rerun writes nothing.</summary>
    [Fact]
    public void A_rerun_of_the_same_session_writes_nothing()
    {
        Triggered("AAA", new TimeOnly(9, 30));

        Stage().Apply(Session);
        _clock.Advance(TimeSpan.FromMinutes(30));
        Stage().Apply(Session);

        Assert.Single(Orders());
    }

    /// <summary>
    /// An order is invisible to a read standing before it was written, and becomes visible when the
    /// as-of moves past it.
    /// </summary>
    [Fact]
    public void An_order_observed_after_the_as_of_is_invisible_until_the_as_of_moves_past_it()
    {
        Triggered("AAA", new TimeOnly(9, 30));
        Stage().Apply(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(TradeOrderReader.ForLiveSession(connection, Session, Session.AddDays(-1), SessionBoundaries.UsEquities));
        Assert.Single(TradeOrderReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities));
    }

    /// <summary>
    /// The store refuses a placed order with no shares and a blocked one with them, so a status
    /// cannot disagree with the only number anybody acts on.
    /// </summary>
    [Fact]
    public void A_placed_order_with_no_shares_is_refused_by_the_store()
    {
        Triggered("AAA", new TimeOnly(9, 30));
        Stage().Apply(Session);

        using SqliteConnection write = _connections.OpenWrite();
        using SqliteCommand command = write.CreateCommand();
        command.CommandText = "UPDATE trade_order SET shares = 0 WHERE status = 'placed';";

        SqliteException thrown = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("CHECK", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers -------------------------------------------------------------------------

    private IReadOnlyList<StoredTradeOrder> Orders()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradeOrderReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredOrderRun> Runs()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradeOrderReader.RunsFor(connection, Session);
    }

    /// <summary>A plan, and the trigger that reached it.</summary>
    private void Triggered(string ticker, TimeOnly touchedAt, string direction = SetupDirection.Long)
    {
        Plan(ticker, direction);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand resolution = connection.CreateCommand();
        resolution.CommandText = """
            INSERT INTO trigger_resolution (
                plan_id, variant_id, setup_id, live_session, ticker, direction, outcome,
                touched_at, minutes_walked, unresolved_because, observed_at)
            VALUES (@plan_id, @variant_id, @setup_id, @live_session, @ticker, @direction, 'touched', @touched_at, 390, NULL, @observed_at);
            """;
        resolution.Parameters.AddWithValue(
            "@plan_id",
            PlanIdentity.For($"{Evening:yyyy-MM-dd}-{ticker}-{direction}", TestVersions.SeedBaseline(connection)));
        resolution.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
        resolution.Parameters.AddWithValue("@setup_id", $"{Evening:yyyy-MM-dd}-{ticker}-{direction}");
        resolution.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(Session));
        resolution.Parameters.AddWithValue("@ticker", ticker);
        resolution.Parameters.AddWithValue("@direction", direction);
        resolution.Parameters.AddWithValue(
            "@touched_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, touchedAt, SessionBoundaries.UsEquities)));
        resolution.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities)));
        resolution.ExecuteNonQuery();
    }

    /// <summary>
    /// A plan resting in the session. $50 trigger, $5 give-up, so 150 shares risking $750, which is
    /// the whole per-trade budget and puts four of them at the account's total-risk cap exactly.
    /// </summary>
    private void Plan(string ticker, string direction)
    {
        const decimal trigger = 50m;
        const decimal giveUp = 45m;
        decimal distance = trigger - giveUp;
        int shares = PositionSizing.SharesFor(distance);

        using SqliteConnection connection = _connections.OpenWrite();
        string setupId = $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";

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
                VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @trigger, @stop, @ranges);
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
}
