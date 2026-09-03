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
/// The session walked one minute at a time, and what it does to each plan resting in it.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The funnel
/// passes a median of nought candidates a night on both sides, so no captured night holds a plan and
/// no captured session has ever been walked against one. The plans and the minutes below are written
/// to sit either side of each property under test, which is the footing every gate boundary in this
/// suite stands on.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class TriggerResolverTests : IDisposable
{
    /// <summary>The evening the plans were written on. A Tuesday.</summary>
    private static readonly DateOnly Evening = new(2026, 8, 25);

    /// <summary>The session they are live in, being the next weekday.</summary>
    private static readonly DateOnly Session = new(2026, 8, 26);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, new TimeOnly(21, 5), SessionBoundaries.UsEquities));

    public TriggerResolverTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private TriggerResolver Stage()
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new TriggerResolver(_connections, new RunLogger(_clock, options), _clock, options);
    }

    // ---- the touch, per direction --------------------------------------------------------

    /// <summary>
    /// A long plan is touched by the first minute whose high reaches the trigger, and the resolution
    /// names that minute.
    /// </summary>
    [Fact]
    public void A_long_plan_is_touched_by_the_first_minute_whose_high_reaches_the_trigger()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m);
        Minute("AAPL", new TimeOnly(9, 31), high: 103m, low: 101m);
        Minute("AAPL", new TimeOnly(9, 32), high: 105m, low: 103m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(1, result.Plans);
        Assert.Equal(1, result.Touched);
        Assert.Equal(0, result.NotTouched);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        StoredTriggerResolution resolution = Resolutions().Single();

        Assert.Equal("touched", resolution.Outcome);
        Assert.Equal(At(new TimeOnly(9, 31)), resolution.TouchedAt);
        Assert.Equal(3, resolution.MinutesWalked);
        Assert.Null(resolution.UnresolvedBecause);
    }

    /// <summary>
    /// A short plan is touched by the first minute whose low reaches the trigger, which is the other
    /// end of the bar. Asserted separately because sharing one comparison across the two sides is the
    /// single easiest way to resolve a strategy nobody trades.
    /// </summary>
    [Fact]
    public void A_short_plan_is_touched_by_the_first_minute_whose_low_reaches_the_trigger()
    {
        Plan("TSLA", SetupDirection.Short, trigger: 97.50m, giveUp: 100m);
        Minute("TSLA", new TimeOnly(9, 30), high: 100m, low: 99m);
        Minute("TSLA", new TimeOnly(9, 31), high: 99m, low: 97m);

        Stage().Resolve(Session);

        StoredTriggerResolution resolution = Resolutions().Single();

        Assert.Equal("touched", resolution.Outcome);
        Assert.Equal(At(new TimeOnly(9, 31)), resolution.TouchedAt);
    }

    /// <summary>
    /// Reaching the price exactly is reaching it, on both sides. No margin is what the decision says
    /// and it is the boundary a later reading would move by a cent without anything failing.
    /// </summary>
    [Fact]
    public void The_trigger_is_reached_exactly_with_no_margin_on_either_side()
    {
        Assert.True(TriggerTouch.Reached(SetupDirection.Long, 102.50m, high: 102.50m, low: 101m));
        Assert.False(TriggerTouch.Reached(SetupDirection.Long, 102.50m, high: 102.49m, low: 101m));

        Assert.True(TriggerTouch.Reached(SetupDirection.Short, 97.50m, high: 99m, low: 97.50m));
        Assert.False(TriggerTouch.Reached(SetupDirection.Short, 97.50m, high: 99m, low: 97.51m));
    }

    /// <summary>
    /// An unknown direction is refused rather than read as one of the two.
    ///
    /// The two sides compare opposite ends of a bar, so a silent default would resolve every short
    /// plan on the long comparison: a fill at a price the plan never named, with nothing downstream
    /// able to tell.
    /// </summary>
    [Fact]
    public void An_unknown_direction_is_refused_rather_than_defaulted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TriggerTouch.Reached("flat", 100m, high: 101m, low: 99m));
    }

    // ---- point in time within the day ----------------------------------------------------

    /// <summary>
    /// A later minute cannot move an earlier decision, which is what point-in-time means inside a
    /// single day.
    ///
    /// <b>Asserted by cutting the session rather than by reading the loop.</b> The trigger is reached
    /// at 09:31 and again at 09:45, and the same session truncated after 09:31 gives the same answer
    /// as the whole one. A resolver that could see forward would have every opportunity to record the
    /// later minute here, or to let the higher print at 09:45 decide, and it would still look like a
    /// resolver that walked a day.
    /// </summary>
    [Fact]
    public void A_later_minute_cannot_move_an_earlier_touch()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m);
        Minute("AAPL", new TimeOnly(9, 31), high: 103m, low: 101m);
        Minute("AAPL", new TimeOnly(9, 45), high: 120m, low: 103m);

        Stage().Resolve(Session);
        DateTimeOffset? overTheWholeDay = Resolutions().Single().TouchedAt;

        // The same session with everything after the touch removed. A different store, so the two
        // runs cannot see each other.
        using var truncated = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(truncated.Path));
        new MigrationRunner(factory).Apply();

        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m, into: factory);
        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m, into: factory);
        Minute("AAPL", new TimeOnly(9, 31), high: 103m, low: 101m, into: factory);

        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = truncated.Path });

        new TriggerResolver(factory, new RunLogger(_clock, options), _clock, options).Resolve(Session);

        using SqliteConnection connection = factory.OpenReadOnly();
        DateTimeOffset? overTheTruncatedDay =
            TriggerResolutionReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities).Single().TouchedAt;

        Assert.Equal(At(new TimeOnly(9, 31)), overTheWholeDay);
        Assert.Equal(overTheWholeDay, overTheTruncatedDay);
    }

    /// <summary>
    /// A minute observed after the as-of is invisible, and becomes visible when the as-of moves past
    /// it.
    ///
    /// The third half of point-in-time, over the minutes rather than over the resolutions. A bar the
    /// vendor restated after the session cannot decide a fill the session itself would not have seen.
    /// </summary>
    [Fact]
    public void A_minute_observed_after_the_as_of_cannot_decide_a_fill()
    {
        Minute("AAPL", new TimeOnly(9, 31), high: 103m, low: 101m,
            observedAt: SessionBoundaries.At(Session.AddDays(2), new TimeOnly(20, 30), SessionBoundaries.UsEquities));

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(IntradayBarReader.ReadSession(connection, ["AAPL"], Session, Session, SessionBoundaries.UsEquities));
        Assert.Single(IntradayBarReader.ReadSession(connection, ["AAPL"], Session, Session.AddDays(2), SessionBoundaries.UsEquities));
    }

    /// <summary>
    /// Extended-hours minutes are not walked, so a pre-market print through the trigger does not fill
    /// a resting order.
    ///
    /// The store holds them deliberately and this stage declines them deliberately, which is a
    /// different thing from not having them.
    /// </summary>
    [Fact]
    public void An_extended_hours_print_through_the_trigger_does_not_fill()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(8, 15), high: 110m, low: 109m, window: "extended");
        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(0, result.Touched);
        Assert.Equal(1, result.NotTouched);
        Assert.Equal(1, result.MinutesWalked);
    }

    // ---- the pairing, fail-closed --------------------------------------------------------

    /// <summary>
    /// A plan is never resolved against a session at or before its own date, and the refusal stops
    /// the run rather than answering it.
    ///
    /// <b>It refuses rather than returning no fill</b>, because no fill and cannot-resolve are the
    /// conflation this stage is arranged around. A plan written on the session it is resting in would
    /// be resolved against the very prices its entry level was derived from, and the resulting fill
    /// would look exactly like an ordinary one.
    /// </summary>
    [Fact]
    public void A_plan_resolved_against_its_own_session_refuses_rather_than_returning_no_fill()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m, asOf: Session);
        Minute("AAPL", new TimeOnly(9, 30), high: 105m, low: 100m);

        InvalidOperationException thrown =
            Assert.Throws<InvalidOperationException>(() => Stage().Resolve(Session));

        Assert.Contains("2026-08-26", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(Resolutions());
    }

    /// <summary>
    /// The refusal is formed for every plan and not only for the first, so one bad row among good
    /// ones stops the night.
    ///
    /// A check of the first plan would pass on this store and resolve the rest, which is the shape a
    /// guard applied to the wrong population takes: every line of it correct, and the rows it governs
    /// down a different branch.
    ///
    /// <b>The bad row is dated on its own session rather than after it, and that is not a weaker
    /// case.</b> A plan dated after the session it is resting in never reaches this guard at all: it
    /// is stamped when it is written, so the reader's own point-in-time bound excludes it from a read
    /// standing at the session. Equal is the shape that gets through the bound and is still a plan
    /// resolved against the prices it was computed from.
    /// </summary>
    [Fact]
    public void One_misdated_plan_among_good_ones_stops_the_run()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Plan("MSFT", SetupDirection.Long, trigger: 202.50m, giveUp: 200m, asOf: Session);
        Minute("AAPL", new TimeOnly(9, 30), high: 105m, low: 100m);

        Assert.Throws<InvalidOperationException>(() => Stage().Resolve(Session));
        Assert.Empty(Resolutions());
    }

    // ---- no fill against cannot resolve --------------------------------------------------

    /// <summary>
    /// A name that traded all day and never reached its trigger did not fire, and that is an ordinary
    /// result rather than a fault.
    /// </summary>
    [Fact]
    public void A_name_that_traded_and_never_reached_its_trigger_did_not_fire()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 110m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m);
        Minute("AAPL", new TimeOnly(9, 31), high: 102m, low: 101m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(1, result.NotTouched);
        Assert.Equal(0, result.Unresolvable);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        StoredTriggerResolution resolution = Resolutions().Single();

        Assert.Equal("not_touched", resolution.Outcome);
        Assert.Null(resolution.TouchedAt);
        Assert.Equal(2, resolution.MinutesWalked);
    }

    /// <summary>
    /// A session the store holds no minute for is a night the lab was blind on, and it is recorded as
    /// unresolvable and reported partial rather than as a night on which nothing triggered.
    ///
    /// <b>This is what a market holiday looks like from inside the lab.</b> `live_session` is the next
    /// weekday and nothing here knows whether that weekday trades, so about nine evenings a year a
    /// plan rests in a day that never opened. The lab does not author a calendar to avoid it; it
    /// records that it could not ask, which is the answer it actually has.
    /// see: A session is a date the store holds minutes for, and no calendar is authored here
    /// </summary>
    [Fact]
    public void A_session_with_no_stored_minute_is_unresolvable_and_the_run_is_partial()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(1, result.Plans);
        Assert.Equal(0, result.NotTouched);
        Assert.Equal(1, result.Unresolvable);
        Assert.Equal(0, result.MinutesWalked);
        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(TriggerResolver.SessionHeldNoMinutes, result.StoppedBecause);

        StoredTriggerResolution resolution = Resolutions().Single();

        Assert.Equal("unresolvable", resolution.Outcome);
        Assert.Equal(TriggerResolver.SessionHeldNoMinutes, resolution.UnresolvedBecause);
        Assert.Equal(TriggerResolver.SessionHeldNoMinutes, Runs().Single().StoppedBecause);
    }

    /// <summary>
    /// A name missing from a session that otherwise traded is the same fault one name wide, and it
    /// carries its own reason rather than the session's.
    /// </summary>
    [Fact]
    public void A_name_with_no_stored_minute_in_a_session_that_traded_says_so_separately()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Plan("MSFT", SetupDirection.Long, trigger: 202.50m, giveUp: 200m);
        Minute("AAPL", new TimeOnly(9, 30), high: 105m, low: 100m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(2, result.Plans);
        Assert.Equal(1, result.Touched);
        Assert.Equal(1, result.Unresolvable);
        Assert.Equal(1, result.NamesWalked);
        Assert.Equal(RunOutcome.Partial, result.Outcome);

        StoredTriggerResolution missing = Resolutions().Single(r => r.Ticker == "MSFT");

        Assert.Equal("unresolvable", missing.Outcome);
        Assert.Equal(TriggerResolver.NameHeldNoMinutes, missing.UnresolvedBecause);
        Assert.Equal(0, missing.MinutesWalked);
    }

    /// <summary>A session with no plan resting in it is clean, and says which nothing it was.</summary>
    [Fact]
    public void A_session_with_no_plan_resting_in_it_says_so()
    {
        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(0, result.Plans);
        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(TriggerResolver.NoPlansResting, result.StoppedBecause);
        Assert.Null(Runs().Single().SetupAsOf);
        Assert.Empty(Resolutions());
    }

    // ---- one clock for the session -------------------------------------------------------

    /// <summary>
    /// Two names are walked by one clock, so the earliest trigger of the session is a fact the store
    /// carries rather than one a later component reconstructs.
    ///
    /// The reader returns them in the order the contention rule fills in, which is what 4.6 reads.
    /// </summary>
    [Fact]
    public void Two_names_are_walked_by_one_clock_and_the_earliest_trigger_is_first()
    {
        Plan("LATE", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Plan("EARLY", SetupDirection.Long, trigger: 52.50m, giveUp: 50m);

        Minute("LATE", new TimeOnly(9, 30), high: 101m, low: 100m);
        Minute("EARLY", new TimeOnly(9, 30), high: 53m, low: 50m);
        Minute("LATE", new TimeOnly(10, 15), high: 103m, low: 101m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(2, result.Touched);
        Assert.Equal(2, result.NamesWalked);

        // Two names, three bars, two distinct minutes. The clock walks minutes of the session rather
        // than minutes of each name's day.
        Assert.Equal(2, result.MinutesWalked);

        StoredTriggerResolution[] resolutions = [.. Resolutions()];

        Assert.Equal("EARLY", resolutions[0].Ticker);
        Assert.Equal(At(new TimeOnly(9, 30)), resolutions[0].TouchedAt);
        Assert.Equal("LATE", resolutions[1].Ticker);
        Assert.Equal(At(new TimeOnly(10, 15)), resolutions[1].TouchedAt);
    }

    /// <summary>
    /// Two plans on one name walk the same minutes, and the count on each row is the name's rather
    /// than twice it.
    ///
    /// It cannot arise from one baseline, where a plan is keyed on a setup and a setup is one name in
    /// one direction on one night. It arises at 5.1, when versions fan plans out per variant, and the
    /// figure would then be silently double on exactly the nights two versions selected one name.
    /// </summary>
    [Fact]
    public void Two_plans_on_one_name_each_record_the_names_own_minute_count()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Plan("AAPL", SetupDirection.Short, trigger: 99m, giveUp: 101m);

        Minute("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m);
        Minute("AAPL", new TimeOnly(9, 31), high: 103m, low: 98m);

        TriggerRunResult result = Stage().Resolve(Session);

        Assert.Equal(2, result.Plans);
        Assert.Equal(2, result.Touched);
        Assert.Equal(1, result.NamesWalked);
        Assert.Equal(2, result.MinutesWalked);
        Assert.All(Resolutions(), r => Assert.Equal(2, r.MinutesWalked));
    }

    // ---- the store ------------------------------------------------------------------------

    /// <summary>
    /// A session that has closed does not change, so a rerun of the same evening writes nothing.
    /// </summary>
    [Fact]
    public void A_rerun_of_the_same_session_writes_nothing()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(9, 30), high: 105m, low: 100m);

        Stage().Resolve(Session);
        _clock.Advance(TimeSpan.FromMinutes(30));
        TriggerRunResult again = Stage().Resolve(Session);

        Assert.Equal(1, again.Touched);
        Assert.Single(Resolutions());
    }

    /// <summary>
    /// A resolution is invisible to a read standing before it was written, and becomes visible when
    /// the as-of moves past it.
    /// </summary>
    [Fact]
    public void A_resolution_observed_after_the_as_of_is_invisible_until_the_as_of_moves_past_it()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);
        Minute("AAPL", new TimeOnly(9, 30), high: 105m, low: 100m);
        Stage().Resolve(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(TriggerResolutionReader.ForLiveSession(connection, Session, Session.AddDays(-1), SessionBoundaries.UsEquities));
        Assert.Single(TriggerResolutionReader.ForLiveSession(connection, Session, Session, SessionBoundaries.UsEquities));
        Assert.Single(TriggerResolutionReader.ForLiveSession(connection, Session, Session.AddDays(1), SessionBoundaries.UsEquities));
    }

    /// <summary>
    /// The store refuses a touch with no minute and a minute on anything but a touch, so an outcome
    /// and the evidence for it cannot disagree.
    /// </summary>
    [Fact]
    public void A_touch_with_no_minute_is_refused_by_the_store()
    {
        Plan("AAPL", SetupDirection.Long, trigger: 102.50m, giveUp: 100m);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trigger_resolution
                (setup_id, live_session, ticker, direction, outcome, touched_at,
                 minutes_walked, unresolved_because, observed_at)
            VALUES (@id, @session, 'AAPL', 'long', 'touched', NULL, 5, NULL, @observed);
            """;
        command.Parameters.AddWithValue("@id", SetupId("AAPL", SetupDirection.Long));
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(Session));
        command.Parameters.AddWithValue("@observed", StoreText.TimestampToStorageText(_clock.UtcNow));

        SqliteException thrown = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("CHECK", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static DateTimeOffset At(TimeOnly local) =>
        SessionBoundaries.At(Session, local, SessionBoundaries.UsEquities);

    private static string SetupId(string ticker, string direction) =>
        $"{Evening:yyyy-MM-dd}-{ticker}-{direction}";

    private IReadOnlyList<StoredTriggerResolution> Resolutions()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TriggerResolutionReader.ForLiveSession(connection, Session, Session.AddDays(1), SessionBoundaries.UsEquities);
    }

    private IReadOnlyList<StoredTriggerRun> Runs()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TriggerResolutionReader.RunsFor(connection, Session);
    }

    /// <summary>
    /// A setup, its plan, and the security both hang off. The plan is written directly rather than by
    /// running PlanBuilder, because what is under test is the resolver and a plan built by the stage
    /// would tie these cases to the sizing arithmetic as well.
    /// </summary>
    private void Plan(
        string ticker,
        string direction,
        decimal trigger,
        decimal giveUp,
        DateOnly? asOf = null,
        StoreConnectionFactory? into = null)
    {
        DateOnly written = asOf ?? Evening;
        StoreConnectionFactory factory = into ?? _connections;

        using SqliteConnection connection = factory.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(written.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        string setupId = $"{written:yyyy-MM-dd}-{ticker}-{direction}";

        using (SqliteCommand setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO setup
                    (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                     trigger_price, stop_price, stop_distance_ranges)
                VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @trigger, @stop, @ranges);
                """;
            setup.Parameters.AddWithValue("@id", setupId);
            setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(written));
            setup.Parameters.AddWithValue("@ticker", ticker);
            setup.Parameters.AddWithValue("@direction", direction);
            setup.Parameters.AddWithValue("@trigger", StoreText.PriceToStorageText(trigger));
            setup.Parameters.AddWithValue("@stop", StoreText.PriceToStorageText(giveUp));
            setup.Parameters.AddWithValue("@ranges", StoreText.RatioToStorageText(0.30m));
            setup.ExecuteNonQuery();
        }

        decimal distance = Math.Abs(trigger - giveUp);
        int shares = PositionSizing.SharesFor(distance);

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
        plan.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(written));
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
                SessionBoundaries.At(written, new TimeOnly(18, 30), SessionBoundaries.UsEquities)));
        plan.ExecuteNonQuery();
    }

    private void Minute(
        string ticker,
        TimeOnly local,
        decimal high,
        decimal low,
        string window = "regular",
        DateTimeOffset? observedAt = null,
        StoreConnectionFactory? into = null)
    {
        StoreConnectionFactory factory = into ?? _connections;

        using SqliteConnection connection = factory.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Evening.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intraday_bar
                (ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                 open, high, low, close, volume, vwap_session, observed_at)
            VALUES (@ticker, @bar_ts, @session_date, '1m', @window, 'raw',
                    @open, @high, @low, @close, @volume, NULL, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue(
            "@bar_ts",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(Session, local, SessionBoundaries.UsEquities)));
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(Session));
        command.Parameters.AddWithValue("@window", window);
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(low));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(high));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(high));
        command.Parameters.AddWithValue("@volume", 1_000);
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                observedAt
                ?? SessionBoundaries.At(Session, new TimeOnly(20, 30), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }
}
