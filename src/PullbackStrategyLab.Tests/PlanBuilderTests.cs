using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The committed instruction, and the size that belongs to it rather than to RiskGate.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel
/// passes a median of nought candidates a night on both sides, so no captured night has a plan in
/// it and none ever will until the thresholds move. The setups below are authored to sit either
/// side of the properties under test, which is the same footing every gate boundary in this suite
/// stands on.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class PlanBuilderTests : IDisposable
{
    /// <summary>The evening plans are written on. A Tuesday, so the next weekday is the next day.</summary>
    private static readonly DateOnly Evening = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Evening, new TimeOnly(18, 30), SessionBoundaries.UsEquities));

    public PlanBuilderTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private PlanBuilder Stage()
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new PlanBuilder(_connections, new RunLogger(_clock, options), _clock, options);
    }

    // ---- the size, and who owns it -------------------------------------------------------

    /// <summary>
    /// The plan carries a share count, sized from the risk budget and the give-up distance.
    ///
    /// $100,000 at 0.75% is a $750 budget, and a give-up distance of $2.50 buys 300 shares exactly.
    /// The figures are asserted against the arithmetic rather than against a remembered number.
    /// </summary>
    [Fact]
    public void The_plan_carries_a_share_count_sized_from_the_risk_budget()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Planned);

        StoredTradePlan plan = Plans().Single();

        Assert.Equal(2.50m, plan.GiveUpDistance);
        Assert.Equal(300, plan.Shares);
        Assert.Equal(750m, plan.RiskBudget);
        Assert.Equal(750m, plan.RiskAtStake);
        Assert.Equal(PositionSizing.NotionalEquity, plan.Equity);
        Assert.Equal(PositionSizing.RiskPerTrade, plan.RiskFraction);
    }

    /// <summary>
    /// The rounding is down and it is visible, which is why the plan stores both figures.
    ///
    /// A $7 distance divides into $750 a hundred and seven times with $1 left over. Rounding up
    /// would put $756 at stake on a trade whose whole purpose is to risk $750, and storing only the
    /// budget would state a number this trade will never lose.
    /// </summary>
    [Fact]
    public void The_share_count_rounds_down_and_the_plan_records_both_figures()
    {
        Candidate("MSFT", "long", trigger: 207m, giveUp: 200m);

        Stage().Build(Evening);

        StoredTradePlan plan = Plans().Single();

        Assert.Equal(107, plan.Shares);
        Assert.Equal(750m, plan.RiskBudget);
        Assert.Equal(749m, plan.RiskAtStake);
        Assert.True(plan.RiskAtStake <= plan.RiskBudget);
    }

    /// <summary>
    /// Nothing recomputes the size at trigger, asserted over the shipped source rather than over an
    /// intention.
    ///
    /// <b>This is the half of the decision a behavioural test cannot reach yet</b>, because RiskGate
    /// arrives at 4.6 and there is no trigger path to run. What can be asserted now is that the one
    /// function that turns a distance into a share count is called from exactly one place, so a
    /// second sizing cannot appear without this failing. When RiskGate lands it may call
    /// <see cref="PositionSizing.RiskAtStake"/> and the caps, and it may not call
    /// <see cref="PositionSizing.SharesFor"/>.
    ///
    /// <b>A scan, and it is named as one rather than passed off as the property.</b> It cannot see a
    /// component that reimplements the division instead of calling the function, so it is evidence
    /// that no second caller exists and not evidence that no second sizing exists. The behavioural
    /// half arrives at 4.6 with the component that could break it, and 4.6's row carries it.
    /// </summary>
    [Fact]
    public void Only_the_plan_stage_turns_a_distance_into_a_share_count()
    {
        IReadOnlyList<string> callers =
        [
            .. RepositoryLayout.ProductionSourceFiles
                .Where(f => RepositoryLayout.Read(f).Contains("PositionSizing.SharesFor", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .Select(n => n!)
                .Order(StringComparer.Ordinal),
        ];

        // One caller, and the declaring file is not one of them: the scan looks for the qualified
        // call, which PositionSizing.cs does not write about itself.
        Assert.Equal(["PlanBuilder.cs"], callers);
    }

    // ---- the refusals --------------------------------------------------------------------

    /// <summary>
    /// A setup whose thrust has not pulled back yet carries a trigger and a give-up point at the
    /// same price, and gets no plan.
    ///
    /// This is the obligation raised at 3.15 and due here. The row is not absent geometry: two of
    /// its four columns state a number, and a distance of nought clears every threshold written as
    /// a maximum. Sizing it would divide $750 by nought.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void An_equal_trigger_and_give_up_point_gets_no_plan_and_the_run_says_why()
    {
        Candidate("NVDA", "long", trigger: 85.14m, giveUp: 85.14m);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Planned);
        Assert.Equal(1, result.RefusedEqualPrices);
        Assert.Equal(0, result.RefusedAbsentGeometry);
        Assert.Empty(Plans());

        // And the reason is in the store rather than only in the stage's output, counted apart from
        // the other two so the defect cannot hide inside ordinary arithmetic.
        StoredPlanRun run = Runs().Single();

        Assert.Equal(1, run.RefusedEqualPrices);
        Assert.Equal(0, run.RefusedAbsentGeometry);
        Assert.Equal(0, run.RefusedBelowOneShare);
        Assert.Equal(0, run.Planned);
    }

    /// <summary>
    /// A setup whose geometry the detector could not compute at all gets no plan either, and is
    /// counted as the other thing.
    /// </summary>
    [Fact]
    public void An_absent_geometry_gets_no_plan_and_is_counted_apart_from_the_equal_pair()
    {
        Candidate("INTC", "short", trigger: null, giveUp: null);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Planned);
        Assert.Equal(1, result.RefusedAbsentGeometry);
        Assert.Equal(0, result.RefusedEqualPrices);
        Assert.Empty(Plans());
    }

    /// <summary>
    /// A give-up distance wider than the whole risk budget buys under one share, so no plan is
    /// written rather than a plan for nought shares.
    ///
    /// The third reason, and the only one of the three that is arithmetic rather than a defect in
    /// the row: it depends on the budget as well as on the setup, which is why the count is stored
    /// rather than derived from `setup` later.
    /// </summary>
    [Fact]
    public void A_distance_wider_than_the_budget_gets_no_plan()
    {
        Candidate("BRKA", "long", trigger: 701_000m, giveUp: 700_000m);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Planned);
        Assert.Equal(1, result.RefusedBelowOneShare);
        Assert.Empty(Plans());
    }

    // ---- immutability and the key --------------------------------------------------------

    /// <summary>A rerun of the same evening writes no row.</summary>
    [Fact]
    public void A_rerun_writes_nothing()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        Stage().Build(Evening);
        StoredTradePlan first = Plans().Single();

        PlanRunResult again = Stage().Build(Evening);

        Assert.Equal(1, again.Candidates);
        Assert.Equal(1, again.Planned);

        StoredTradePlan after = Plans().Single();

        Assert.Equal(first, after);
        Assert.Single(Plans());
    }

    /// <summary>
    /// A second plan for one candidate is refused by the store's own key, not by the stage
    /// remembering to check.
    /// </summary>
    [Fact]
    public void A_second_plan_for_one_candidate_is_refused_by_the_key()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);
        Stage().Build(Evening);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_plan (
                setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @id, '2026-08-25', '2026-08-26', 'AAPL', 'long',
                '99.0000', '98.0000', '1.0000', 750,
                '100000.0000', '0.007500', '750.0000', '750.0000', '2026-08-25T22:30:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("@id", Plans().Single().SetupId);

        SqliteException thrown = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        Assert.Contains("UNIQUE", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the live session ----------------------------------------------------------------

    /// <summary>
    /// The session a plan is live in is a stored column, and it is the next weekday rather than the
    /// next day.
    /// </summary>
    [Fact]
    public void The_live_session_is_stored_and_skips_the_weekend()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(new DateOnly(2026, 8, 26), result.LiveSession);
        Assert.Equal(new DateOnly(2026, 8, 26), Plans().Single().LiveSession);

        // A Friday evening plans for the Monday, which is the case a calendar step gets wrong and
        // the reason this is a column rather than an inference at the reader.
        Assert.Equal(new DateOnly(2026, 8, 31), PlanBuilder.NextWeekday(new DateOnly(2026, 8, 28)));
        Assert.Equal(new DateOnly(2026, 8, 31), PlanBuilder.NextWeekday(new DateOnly(2026, 8, 29)));
    }

    /// <summary>
    /// The reader answers the two dates separately, and neither derives the other.
    /// </summary>
    [Fact]
    public void The_reader_answers_the_evening_and_the_live_session_separately()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);
        Stage().Build(Evening);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening));
        Assert.Single(TradePlanReader.ForLiveSession(connection, new DateOnly(2026, 8, 26), new DateOnly(2026, 8, 26)));

        // The evening is not the live session, so asking the wrong question returns nothing rather
        // than the same row twice.
        Assert.Empty(TradePlanReader.ForLiveSession(connection, Evening, Evening));
        Assert.Empty(TradePlanReader.WrittenOn(connection, new DateOnly(2026, 8, 26), new DateOnly(2026, 8, 26)));
    }

    /// <summary>
    /// A plan is invisible to a read standing before it was written, and becomes visible when the
    /// as-of moves past it.
    ///
    /// The third half of point-in-time, exercised rather than declared. The stage stamps its rows
    /// with the run's own instant, so a replay of the evening before cannot see a plan written on
    /// this one. It matters little today, because the key refuses a second write and there is no
    /// backfill; it stops being harmless the moment either changes, which is why the read bounds
    /// rather than trusting the writer.
    /// </summary>
    [Fact]
    public void A_plan_observed_after_the_as_of_is_invisible_until_the_as_of_moves_past_it()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);
        Stage().Build(Evening);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(TradePlanReader.WrittenOn(connection, Evening, Evening.AddDays(-1)));
        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening));
        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening.AddDays(1)));
    }

    // ---- the population ------------------------------------------------------------------

    /// <summary>
    /// The population is `capped_out = 0` and nothing else, so this stage does not re-derive the
    /// gate list.
    ///
    /// <b>The row below cannot occur in a live store and that is the point.</b> SetupCapper writes
    /// `capped_out` only over rows that passed every gating check, so a kept row is a passing row by
    /// construction. Authoring the impossible combination is what proves PlanBuilder reads the cap's
    /// decision rather than re-evaluating `passed_all` beside it: a second implementation of the gate
    /// list would disagree with the first here, and on a live night nothing would read both.
    /// </summary>
    [Fact]
    public void The_population_is_what_the_cap_kept_rather_than_a_second_reading_of_the_gates()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m, passedAll: false);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Planned);
    }

    /// <summary>A setup the cap truncated gets no plan, because it is not a candidate.</summary>
    [Fact]
    public void A_setup_the_cap_truncated_gets_no_plan()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m, cappedOut: true);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, result.Planned);
        Assert.Empty(Plans());
    }

    /// <summary>
    /// A night nobody capped is different from a night whose cap kept nothing, and the run row says
    /// which. The same distinction WatchlistPublisher draws over the same population.
    /// </summary>
    [Fact]
    public void A_night_that_was_never_capped_says_so_rather_than_reporting_an_empty_list()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m, cappedOut: null);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(PlanBuilder.NeverCapped, result.StoppedBecause);
        Assert.Equal(PlanBuilder.NeverCapped, Runs().Single().StoppedBecause);
    }

    /// <summary>
    /// A cap that ran and kept nobody is the third shape of nothing, and it is most nights.
    ///
    /// Separated from the one above because only one of the three is worth waking anybody for. A
    /// night where every flagged setup was capped out is an ordinary outcome of the gates; a night
    /// nothing carries a cap decision is a stage that did not run.
    /// </summary>
    [Fact]
    public void A_night_the_cap_kept_nobody_is_a_different_nothing_from_a_night_it_never_ran()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m, cappedOut: true);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(PlanBuilder.AllCappedOut, result.StoppedBecause);
    }

    /// <summary>An evening that flagged nothing at all says so, which is the first of the three.</summary>
    [Fact]
    public void An_evening_that_flagged_nothing_says_so()
    {
        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(PlanBuilder.NothingFlagged, result.StoppedBecause);
    }

    // ---- helpers -------------------------------------------------------------------------

    private IReadOnlyList<StoredTradePlan> Plans()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradePlanReader.WrittenOn(connection, Evening, Evening);
    }

    private IReadOnlyList<StoredPlanRun> Runs()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradePlanReader.RunsFor(connection, Evening);
    }

    private void Candidate(
        string ticker,
        string direction,
        decimal? trigger,
        decimal? giveUp,
        bool passedAll = true,
        bool? cappedOut = false)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Evening.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @as_of, @ticker, @direction, '[]', @passed, @capped,
                 @trigger, @stop, @ranges);
            """;
        setup.Parameters.AddWithValue("@id", $"{Evening:yyyy-MM-dd}-{ticker}-{direction}");
        setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
        setup.Parameters.AddWithValue("@ticker", ticker);
        setup.Parameters.AddWithValue("@direction", direction);
        setup.Parameters.AddWithValue("@passed", passedAll ? 1 : 0);
        setup.Parameters.AddWithValue("@capped", cappedOut is null ? DBNull.Value : cappedOut.Value ? 1 : 0);
        setup.Parameters.AddWithValue(
            "@trigger", trigger is null ? DBNull.Value : StoreText.PriceToStorageText(trigger.Value));
        setup.Parameters.AddWithValue(
            "@stop", giveUp is null ? DBNull.Value : StoreText.PriceToStorageText(giveUp.Value));
        setup.Parameters.AddWithValue(
            "@ranges", trigger is null ? DBNull.Value : StoreText.RatioToStorageText(0.30m));
        setup.ExecuteNonQuery();
    }
}
