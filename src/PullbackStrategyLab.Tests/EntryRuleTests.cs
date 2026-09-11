using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Generation 1's entry rule at the minute of entry, from 7.8: the watch over one session, the stop it
/// resolves to, and the three stages that carry it from a plan to an order.
///
/// <b>Authored and derived by hand</b>, the zone being one price because every prior close is. The long
/// and short sessions are <see cref="EntryRuleCases"/>'s, so the replay records what these assert.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class EntryRuleTests
{
    private static readonly DateOnly Session = EntryRuleCases.Session;

    private static EntryWatch Watch(string direction, decimal zone) =>
        new(direction, [.. Enumerable.Repeat(zone, EntryRule.WarmupHourlyBars)], Session, SessionBoundaries.UsEquities);

    private static EntryPoint? Feed(EntryWatch watch, params (int T, decimal High, decimal Low, decimal Close)[] minutes)
    {
        foreach ((int t, decimal high, decimal low, decimal close) in minutes)
        {
            watch.Observe(EntryRuleCases.Minute(t), high, low, close);
        }

        return watch.Entry;
    }

    // ---- the watch --------------------------------------------------------------------------

    [Fact]
    public void A_long_arms_on_the_flush_into_the_zone_and_enters_on_the_first_break_of_the_previous_high()
    {
        EntryPoint? entry = Feed(Watch(SetupDirection.Long, 100m),
            (0, 101.50m, 100.80m, 101.20m),
            (1, 101.30m, 100.60m, 100.70m),
            (2, 100.70m, 99.80m, 99.90m),
            (3, 100.20m, 99.70m, 100.10m),
            (4, 100.90m, 100.00m, 100.80m));

        Assert.NotNull(entry);
        Assert.Equal(EntryRuleCases.Minute(4), entry.Minute);
        Assert.Equal(100.20m, entry.Price);
        Assert.Equal(1, entry.CandleMinutes);
        Assert.Equal(EntryRuleCases.Minute(2), entry.ArmedAt);
        Assert.Equal(99.70m, entry.SessionExtreme);
        Assert.Equal(100.00m, entry.CandleExtreme);
    }

    /// <summary>
    /// A break of a candle the flush had not reached yet is not the reclaim: the flush minute itself
    /// cannot enter on the minute before it.
    /// </summary>
    [Fact]
    public void The_flush_minute_cannot_enter_on_the_candle_before_the_flush()
    {
        EntryPoint? entry = Feed(Watch(SetupDirection.Long, 100m),
            (0, 100.50m, 100.20m, 100.40m),
            (1, 100.60m, 99.90m, 100.00m));

        Assert.Null(entry);
    }

    /// <summary>
    /// The short is the long turned over: armed on the flush up into the zone, in on the first break of
    /// the previous candle's low.
    /// </summary>
    [Fact]
    public void A_short_arms_on_the_flush_up_and_enters_on_the_first_break_of_the_previous_low()
    {
        EntryPoint? entry = Feed(Watch(SetupDirection.Short, 50m),
            (0, 49.20m, 48.90m, 49.00m),
            (1, 49.50m, 49.10m, 49.40m),
            (2, 50.20m, 49.60m, 50.10m),
            (3, 50.30m, 49.90m, 50.00m),
            (4, 50.20m, 50.00m, 50.10m),
            (5, 50.10m, 49.70m, 49.80m));

        Assert.NotNull(entry);
        Assert.Equal(EntryRuleCases.Minute(5), entry.Minute);
        Assert.Equal(50.00m, entry.Price);
        Assert.Equal(50.30m, entry.SessionExtreme);
        Assert.Equal(50.10m, entry.CandleExtreme);
    }

    /// <summary>
    /// After fifteen minutes the candle is five minutes wide, and the previous candle is the whole five
    /// minutes before the one being traded: 09:50 enters on the high of 09:45 to 09:49, being 100.60,
    /// and no minute of 09:46 to 09:49 can enter on the candle the flush was in.
    /// </summary>
    [Fact]
    public void After_fifteen_minutes_the_previous_candle_is_five_minutes_wide()
    {
        EntryWatch watch = Watch(SetupDirection.Long, 100m);

        Assert.Null(Feed(watch,
            (16, 100.40m, 99.90m, 100.00m),
            (17, 100.30m, 99.95m, 100.10m),
            (19, 100.60m, 100.00m, 100.20m)));

        EntryPoint? entry = Feed(watch, (20, 100.61m, 100.10m, 100.30m));

        Assert.NotNull(entry);
        Assert.Equal(EntryRuleCases.Minute(20), entry.Minute);
        Assert.Equal(5, entry.CandleMinutes);
        Assert.Equal(100.60m, entry.Price);
    }

    [Fact]
    public void The_candle_widens_at_fifteen_and_sixty_minutes()
    {
        Assert.Equal(1, EntryRule.CandleMinutesAt(0));
        Assert.Equal(1, EntryRule.CandleMinutesAt(14));
        Assert.Equal(5, EntryRule.CandleMinutesAt(15));
        Assert.Equal(5, EntryRule.CandleMinutesAt(59));
        Assert.Equal(15, EntryRule.CandleMinutesAt(60));
    }

    /// <summary>A name with fewer hourly closes than the warm-up has no level, so nothing arms at all.</summary>
    [Fact]
    public void Too_few_hourly_closes_leave_no_level_and_nothing_arms()
    {
        var watch = new EntryWatch(
            SetupDirection.Long, [.. Enumerable.Repeat(100m, EntryRule.WarmupHourlyBars - 1)], Session, SessionBoundaries.UsEquities);

        Feed(watch, (0, 100.50m, 99.00m, 99.50m), (1, 101.00m, 99.40m, 100.90m));

        Assert.False(watch.HadLevels);
        Assert.False(watch.Armed);
        Assert.Null(watch.Entry);
    }

    [Fact]
    public void The_hourly_closes_count_the_stub_as_a_value()
    {
        DateTimeOffset open = SessionBoundaries.At(Session, SessionBoundaries.RegularSessionOpen, SessionBoundaries.UsEquities);

        IReadOnlyList<decimal> closes = EntryRule.HourlyCloses(
            [(open, 1m), (open.AddMinutes(59), 2m), (open.AddMinutes(60), 3m), (open.AddMinutes(361), 7m), (open.AddMinutes(389), 8m)],
            Session,
            SessionBoundaries.UsEquities);

        Assert.Equal([2m, 3m, 8m], closes);
    }

    // ---- the stop ---------------------------------------------------------------------------

    private static EntryPoint Point(decimal price, decimal sessionExtreme, decimal candleExtreme) =>
        new(EntryRuleCases.Minute(4), price, 1, "hourly-ema-9", 100m, EntryRuleCases.Minute(2), sessionExtreme, candleExtreme);

    [Fact]
    public void The_stop_is_the_session_extreme_while_it_is_no_more_than_two_percent_away()
    {
        EntryStop.Resolution stop = EntryStop.Resolve(SetupDirection.Long, Point(100m, 98m, 99.5m), ceiling: 0.03m);

        Assert.Null(stop.RefusedBecause);
        Assert.Equal(EntryStop.SessionExtremeBasis, stop.Basis);
        Assert.Equal(98m, stop.Stop);
        Assert.Equal(0.02m, stop.Fraction);
    }

    /// <summary>More than 2% from the session's low, and the stop moves to the entry candle's low.</summary>
    [Fact]
    public void Past_two_percent_the_stop_moves_to_the_entry_candle()
    {
        EntryStop.Resolution stop = EntryStop.Resolve(SetupDirection.Long, Point(100m, 97.5m, 99m), ceiling: 0.03m);

        Assert.Equal(EntryStop.EntryCandleBasis, stop.Basis);
        Assert.Equal(99m, stop.Stop);
        Assert.Equal(1m, stop.Distance);

        EntryStop.Resolution shortStop = EntryStop.Resolve(SetupDirection.Short, Point(50m, 51.5m, 50.4m), ceiling: 0.03m);
        Assert.Equal(EntryStop.EntryCandleBasis, shortStop.Basis);
        Assert.Equal(50.4m, shortStop.Stop);
    }

    [Fact]
    public void A_stop_wider_than_the_ceiling_refuses_the_entry_and_keeps_the_stop_it_refused()
    {
        EntryStop.Resolution stop = EntryStop.Resolve(SetupDirection.Long, Point(100m, 98.5m, 98.5m), ceiling: 0.01m);

        Assert.NotNull(stop.RefusedBecause);
        Assert.Equal(98.5m, stop.Stop);
    }

    [Fact]
    public void An_entry_more_than_three_percent_off_the_session_extreme_is_not_chased()
    {
        EntryStop.Resolution stop = EntryStop.Resolve(SetupDirection.Long, Point(103.2m, 100m, 103m), ceiling: 0.05m);

        Assert.NotNull(stop.RefusedBecause);
        Assert.Contains("chase", stop.RefusedBecause, StringComparison.Ordinal);
        Assert.Null(stop.Stop);
    }

    [Fact]
    public void The_ceiling_is_the_tighter_of_half_the_range_and_five_percent()
    {
        Assert.Equal(0.025m, EntryRule.CeilingFor(0.05m));
        Assert.Equal(0.05m, EntryRule.CeilingFor(0.10m));
        Assert.Equal(0.05m, EntryRule.CeilingFor(0.20m));
    }

    // ---- the stages -------------------------------------------------------------------------

    /// <summary>
    /// The long and the short each resolve the entry minute, the price, the stop and the size, and the
    /// gate places them in the order they triggered, capped by position size.
    /// </summary>
    [Fact]
    public void A_long_and_a_short_session_resolve_the_entry_the_stop_the_size_and_the_cap_order()
    {
        using var cases = new EntryRuleCases();
        (TriggerRunResult triggers, EntrySizeResult sizes, OrderRunResult orders) = cases.Run();

        Assert.Equal(2, triggers.Touched);
        Assert.Equal(1, triggers.NotTouched);
        Assert.Equal(2, sizes.Sized);
        Assert.Equal(2, orders.Placed);

        StoredEntryResolution lng = cases.Resolutions().Single(r => r.Ticker == EntryRuleCases.Long);
        Assert.Equal(EntryRuleCases.Minute(4), lng.EntryMinute);
        Assert.Equal(100.20m, lng.EntryPrice);
        Assert.Equal(99.70m, lng.StopPrice);
        Assert.Equal(EntryStop.SessionExtremeBasis, lng.StopBasis);
        Assert.Equal(1500, lng.Shares);

        StoredEntryResolution sht = cases.Resolutions().Single(r => r.Ticker == EntryRuleCases.Short);
        Assert.Equal(EntryRuleCases.Minute(5), sht.EntryMinute);
        Assert.Equal(50.00m, sht.EntryPrice);
        Assert.Equal(50.30m, sht.StopPrice);
        Assert.Equal(2500, sht.Shares);

        IReadOnlyList<StoredTradeOrder> placed = cases.Orders();
        Assert.Equal([EntryRuleCases.Long, EntryRuleCases.Short], placed.OrderBy(o => o.TriggeredAt).Select(o => o.Ticker));
        Assert.Equal(349, placed.Single(o => o.Ticker == EntryRuleCases.Long).Shares);
        Assert.Equal(700, placed.Single(o => o.Ticker == EntryRuleCases.Short).Shares);
        Assert.All(placed, o => Assert.Equal(RiskLimits.PositionSize, o.BoundBy));
    }

    /// <summary>A name that flushed and never reclaimed is not touched and writes no order.</summary>
    [Fact]
    public void A_name_whose_reclaim_never_occurs_writes_no_order()
    {
        using var cases = new EntryRuleCases();
        cases.Run();

        Assert.Equal("not_touched", cases.Triggers().Single(t => t.Ticker == EntryRuleCases.NoReclaim).Outcome);
        Assert.DoesNotContain(cases.Resolutions(), r => r.Ticker == EntryRuleCases.NoReclaim);
        Assert.DoesNotContain(cases.Orders(), o => o.Ticker == EntryRuleCases.NoReclaim);
    }

    /// <summary>
    /// Both sessions set a new extreme after the entry, 98.00 under the long and 52.00 over the short,
    /// and neither stop uses it: the stop is the extreme as of the entry minute.
    /// </summary>
    [Fact]
    public void A_session_extreme_set_after_the_entry_minute_does_not_set_the_stop_on_either_side()
    {
        using var cases = new EntryRuleCases();
        cases.Run();

        StoredEntryResolution lng = cases.Resolutions().Single(r => r.Ticker == EntryRuleCases.Long);
        StoredEntryResolution sht = cases.Resolutions().Single(r => r.Ticker == EntryRuleCases.Short);

        Assert.Equal(99.70m, lng.SessionExtreme);
        Assert.NotEqual(98.00m, lng.StopPrice);
        Assert.Equal(50.30m, sht.SessionExtreme);
        Assert.NotEqual(52.00m, sht.StopPrice);
    }

    /// <summary>A rerun of the sizer writes nothing, and the gate's second pass places nothing twice.</summary>
    [Fact]
    public void A_rerun_resolves_and_places_nothing_twice()
    {
        using var cases = new EntryRuleCases();
        cases.Run();
        (_, EntrySizeResult sizes, _) = cases.Run();

        Assert.Equal(0, sizes.Sized);
        Assert.Equal(2, sizes.AlreadyResolved);
        Assert.Equal(2, cases.Resolutions().Count);
        Assert.Equal(2, cases.Orders().Count);
    }
}
