using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Core.Research;
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

    private PlanBuilder Stage()
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new PlanBuilder(_connections, new RunLogger(_clock, options), _clock, options);
    }

    // ---- the rule, which is what a plan carries from 7.8 ---------------------------------------

    /// <summary>
    /// The plan carries the rule, the risk budget and the ceiling the stop may not exceed, and no price
    /// and no size, because those resolve at the entry minute.
    ///
    /// The ceiling is the tighter of half the daily range and 5%: a 5% range gives 2.5%.
    /// see: Order prices and the share count resolve at the entry minute
    /// </summary>
    [Fact]
    public void The_plan_carries_the_rule_and_the_ceiling_and_no_price()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Planned);

        CommittedTradePlan plan = Plans().Single();

        Assert.Equal(EntryRule.FlushReclaim, plan.EntryRule);
        Assert.True(plan.CarriesTheRule);
        Assert.Equal(0.025m, plan.StopCeiling);
        Assert.Null(plan.TriggerPrice);
        Assert.Null(plan.GiveUpPrice);
        Assert.Null(plan.GiveUpDistance);
        Assert.Null(plan.Shares);
        Assert.Null(plan.RiskAtStake);
        Assert.Equal(750m, plan.RiskBudget);
        Assert.Equal(PositionSizing.NotionalEquity, plan.Equity);
        Assert.Equal(PositionSizing.RiskPerTrade, plan.RiskFraction);
    }

    /// <summary>
    /// On a range wider than 10% the absolute 5% is the tighter, which is the only place it binds.
    /// </summary>
    [Fact]
    public void The_ceiling_is_five_percent_where_half_the_range_is_wider()
    {
        Candidate("TSLA", "long", trigger: 102.50m, giveUp: 100.00m, averageDailyRange: 0.14m);

        Stage().Build(Evening);

        Assert.Equal(0.05m, Plans().Single().StopCeiling);
    }

    /// <summary>
    /// Only the sizer turns a distance into a share count, asserted over the shipped source.
    ///
    /// <b>A scan, and it is named as one rather than passed off as the property.</b> It cannot see a
    /// component that reimplements the division, so it is evidence that no second caller exists. From
    /// 7.8 the one caller is the stage that resolves the entry, and the plan stage is not it; the
    /// behavioural half is EntrySizer's own tests, which size an entry and read the row back.
    /// </summary>
    [Fact]
    public void Only_the_sizer_turns_a_distance_into_a_share_count()
    {
        IReadOnlyList<string> callers =
        [
            .. RepositoryLayout.ProductionSourceFiles
                .Where(f => RepositoryLayout.Read(f).Contains("PositionSizing.SharesFor", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .Select(n => n!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["EntrySizer.cs"], callers);
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
    /// A candidate whose daily range the store does not hold gets no plan and is counted as an absent
    /// geometry, rather than a plan with a ceiling taken from a stand-in.
    /// </summary>
    [Fact]
    public void A_candidate_with_no_daily_range_gets_no_plan_and_is_counted_as_absent()
    {
        Candidate("AAPL", "long", trigger: 100m, giveUp: 95m, withSession: false);

        PlanRunResult result = Stage().Build(Evening);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Planned);
        Assert.Equal(1, result.RefusedAbsentGeometry);
        Assert.Equal(0, result.RefusedBelowOneShare);
        Assert.Empty(Plans());
    }

    // ---- immutability and the key --------------------------------------------------------

    /// <summary>A rerun of the same evening writes no row.</summary>
    [Fact]
    public void A_rerun_writes_nothing()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        Stage().Build(Evening);
        CommittedTradePlan first = Plans().Single();

        PlanRunResult again = Stage().Build(Evening);

        Assert.Equal(1, again.Candidates);
        Assert.Equal(1, again.Planned);

        CommittedTradePlan after = Plans().Single();

        Assert.Equal(first, after);
        Assert.Single(Plans());
    }

    /// <summary>
    /// A second plan for one candidate is refused by the store's own key, not by the stage
    /// remembering to check.
    /// </summary>
    [Fact]
    public void A_second_plan_for_one_candidate_under_one_version_is_refused_by_the_key()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);
        Stage().Build(Evening);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_plan (
                plan_id, variant_id, setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @plan_id, @variant_id, @id, '2026-08-25', '2026-08-26', 'AAPL', 'long',
                '99.0000', '98.0000', '1.0000', 750,
                '100000.0000', '0.007500', '750.0000', '750.0000', '2026-08-25T22:30:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue(
            "@plan_id",
            PlanIdentity.For(Plans().Single().SetupId, TestVersions.SeedBaseline(connection)));
        command.Parameters.AddWithValue("@variant_id", TestVersions.Baseline);
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

        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening, SessionBoundaries.UsEquities));
        Assert.Single(TradePlanReader.CommittedForLiveSession(connection, new DateOnly(2026, 8, 26), new DateOnly(2026, 8, 26), SessionBoundaries.UsEquities));

        // A plan carrying the rule has no executed shape until its entry is resolved, so the priced
        // read of the session it rests in has nothing to return yet.
        Assert.Empty(TradePlanReader.ForLiveSession(connection, new DateOnly(2026, 8, 26), new DateOnly(2026, 8, 26), SessionBoundaries.UsEquities));

        // The evening is not the live session, so asking the wrong question returns nothing rather
        // than the same row twice.
        Assert.Empty(TradePlanReader.CommittedForLiveSession(connection, Evening, Evening, SessionBoundaries.UsEquities));
        Assert.Empty(TradePlanReader.WrittenOn(connection, new DateOnly(2026, 8, 26), new DateOnly(2026, 8, 26), SessionBoundaries.UsEquities));
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

        Assert.Empty(TradePlanReader.WrittenOn(connection, Evening, Evening.AddDays(-1), SessionBoundaries.UsEquities));
        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening, SessionBoundaries.UsEquities));
        Assert.Single(TradePlanReader.WrittenOn(connection, Evening, Evening.AddDays(1), SessionBoundaries.UsEquities));
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
    /// A night the cap ran and kept nobody is told apart from a night nobody capped, by both
    /// readers of the cap, from the cap's own run row.
    ///
    /// <b>The row raised at 5.0.</b> The cap writes its decision on candidate rows only, so a night
    /// with no candidate carries no decision anywhere, and both readers said the night was never
    /// capped, which their own comments reserve for a stage that did not run. Every recorded night
    /// has passed nought candidates, so every reading said it. The same store answers both ways
    /// here: with no run row the night reads as never capped, with the cap's run row it reads as the
    /// cap having kept nobody, and the rows are identical between the two.
    /// </summary>
    [Fact]
    public void A_night_the_cap_ran_and_kept_nobody_is_told_apart_from_one_nobody_capped_by_both_readers()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m, cappedOut: null);

        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });
        var publisher = new WatchlistPublisher(_connections, new RunLogger(_clock, options), _clock, options);

        Assert.Equal(PlanBuilder.NeverCapped, Stage().Build(Evening).StoppedBecause);
        Assert.Equal(WatchlistPublisher.NeverCapped, publisher.Publish(Evening).NothingBecause);

        // The cap runs, has no candidate to decide over, and leaves only its run row.
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using RunScope cap = new RunLogger(_clock, options).Begin(connection, SetupCapper.Name, "setup");
            cap.Complete(RunOutcome.Clean);
        }

        Assert.Equal(PlanBuilder.CapKeptNobody, Stage().Build(Evening).StoppedBecause);
        Assert.Equal(WatchlistPublisher.CapKeptNobody, publisher.Publish(Evening).NothingBecause);
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

    /// <summary>
    /// Two live versions selecting one candidate are two plans, and the key admits both.
    ///
    /// <b>The behavioural half of the fan-out.</b> Reading the migration says the constraint moved
    /// from the setup to the plan; this says a night actually writes both rows, with one geometry
    /// and two identities. Before 5.1 the second was refused by `trade_plan`'s own key and the
    /// night would have lost it.
    /// </summary>
    [Fact]
    public void Two_live_versions_selecting_one_candidate_are_two_plans()
    {
        Candidate("AAPL", "long", trigger: 102.50m, giveUp: 100.00m);

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand second = connection.CreateCommand();
            second.CommandText = """
                INSERT INTO variant (
                    variant_id, generation, family, definition, target,
                    minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                    direction, gate, threshold_name, threshold_from, threshold_to)
                VALUES ('F1a', 0, 'selection', 'a widened gate', 'two points of forward return', 1802,
                        'effective_paired_setup_observations', 'open', NULL, '2000-01-01T00:00:00.000Z',
                        'long', 'dip-shape', 'maximum-retrace', '0.40', '0.50');
                """;
            second.ExecuteNonQuery();
        }

        PlanRunResult run = Stage().Build(Evening);

        Assert.Equal(2, run.LiveVersions);
        Assert.Equal(2, run.Planned);
        Assert.Equal(1, run.CandidatesPlanned);

        IReadOnlyList<CommittedTradePlan> plans = Plans();
        Assert.Equal(2, plans.Count);
        Assert.Single(plans.Select(p => p.SetupId).Distinct());
        Assert.Equal(2, plans.Select(p => p.PlanId).Distinct().Count());
        Assert.Equal(["F1a", TestVersions.Baseline], plans.Select(p => p.VariantId).Order().ToArray());
    }

    private IReadOnlyList<CommittedTradePlan> Plans()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradePlanReader.WrittenOn(connection, Evening, Evening, SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredPlanRun> Runs()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TradePlanReader.RunsFor(connection, Evening);
    }

    /// <summary>The fraction of price the authored session's average daily range is, which sets the ceiling.</summary>
    private const decimal AverageDailyRange = 0.05m;

    /// <summary>
    /// One capped candidate, with the final pullback session's bar and figures shaped so that the
    /// derived order prices are exactly <paramref name="trigger"/> and <paramref name="giveUp"/>.
    ///
    /// <b>The setup's own pair is written as something else on purpose.</b> Until 4.18 the stage
    /// copied it into the plan, so a helper that wrote the same numbers into both would let that
    /// regression pass every arithmetic test here. The setup carries the screening pair the
    /// detector would have computed, a whole dip wide, and the plan is asserted against the
    /// session's extremes: for a long the session's high is the trigger and its low sits one offset
    /// above the give-up point, and the mirror for a short.
    /// </summary>
    private void Candidate(
        string ticker,
        string direction,
        decimal? trigger,
        decimal? giveUp,
        bool passedAll = true,
        bool? cappedOut = false,
        bool withSession = true,
        decimal averageDailyRange = AverageDailyRange)
    {
        if (trigger is decimal t && giveUp is decimal g && t != g && withSession)
        {
            Session(ticker, high: Math.Max(t, g), low: Math.Min(t, g), close: t, averageDailyRange);
        }

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
            "@trigger", trigger is null ? DBNull.Value : StoreText.PriceToStorageText(trigger.Value + 1m));
        setup.Parameters.AddWithValue(
            "@stop", giveUp is null ? DBNull.Value : StoreText.PriceToStorageText(giveUp.Value + (trigger == giveUp ? 1m : -3m)));
        setup.Parameters.AddWithValue(
            "@ranges", trigger is null ? DBNull.Value : StoreText.RatioToStorageText(0.30m));
        setup.ExecuteNonQuery();
    }

    /// <summary>The final pullback session's daily bar and the figures beside it, as the stage reads them.</summary>
    private void Session(string ticker, decimal high, decimal low, decimal close, decimal averageDailyRange = AverageDailyRange)
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

        using (SqliteCommand bar = connection.CreateCommand())
        {
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@ticker, @bar_date, @close, @high, @low, @close, @close, 1000000, @observed_at)
                ON CONFLICT (ticker, bar_date, observed_at) DO NOTHING;
                """;
            bar.Parameters.AddWithValue("@ticker", ticker);
            bar.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(Evening));
            bar.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(high));
            bar.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(low));
            bar.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
            bar.Parameters.AddWithValue(
                "@observed_at",
                StoreText.TimestampToStorageText(
                    SessionBoundaries.At(Evening, new TimeOnly(17, 30), SessionBoundaries.UsEquities)));
            bar.ExecuteNonQuery();
        }

        using SqliteCommand figures = connection.CreateCommand();
        figures.CommandText = """
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                 dollar_volume_median_20, range_avg_20)
            VALUES (@ticker, @as_of, @computed_at, @close, @close, @close, @atr, @adr, @dollars, @range)
            ON CONFLICT (ticker, as_of, computed_at) DO NOTHING;
            """;
        figures.Parameters.AddWithValue("@ticker", ticker);
        figures.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Evening));
        figures.Parameters.AddWithValue(
            "@computed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Evening, new TimeOnly(18, 0), SessionBoundaries.UsEquities)));
        figures.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
        figures.Parameters.AddWithValue("@atr", StoreText.PriceToStorageText(high - low));
        figures.Parameters.AddWithValue("@adr", StoreText.RatioToStorageText(averageDailyRange));
        figures.Parameters.AddWithValue("@dollars", StoreText.PriceToStorageText(50_000_000m));
        figures.Parameters.AddWithValue("@range", StoreText.PriceToStorageText(high - low));
        figures.ExecuteNonQuery();
    }
}
