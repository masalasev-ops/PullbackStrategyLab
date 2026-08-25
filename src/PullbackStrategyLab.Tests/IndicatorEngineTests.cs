using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The averages, and the two cases where the engine writes nothing rather than a number.
///
/// The arithmetic tests are stated against figures worked out by hand on series small enough to
/// check on paper. They are not the checkpoint's verification, which is three real tickers
/// against an independent calculation recorded in PROGRESS; they are what makes a failure there
/// point at a formula rather than at the whole component.
/// </summary>
public sealed class IndicatorEngineTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero));

    public IndicatorEngineTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private IndicatorEngine Engine() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    private DailyBarIngestor Ingestor(FakeMarketDataVendor vendor) =>
        new(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options());

    private ActionIngestor Actions(FakeMarketDataVendor vendor) =>
        new(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options());

    // ---- the arithmetic ------------------------------------------------------------------

    [Fact]
    public void The_exponential_average_seeds_on_the_simple_average_of_its_first_period()
    {
        // Five values, period three. The seed is the mean of 1, 2 and 3, which is 2. The
        // multiplier is 2/4, so 2 + (4-2)/2 = 3, then 3 + (5-3)/2 = 4.
        decimal[] values = [1m, 2m, 3m, 4m, 5m];

        Assert.Equal(4m, Averages.Exponential(values, 3));

        // And with no values beyond the seed it is exactly the simple average, which is the
        // property that makes the seed checkable by hand at all.
        Assert.Equal(2m, Averages.Exponential([1m, 2m, 3m], 3));
    }

    [Fact]
    public void The_true_range_takes_the_gap_when_the_gap_is_larger_than_the_days_own_range()
    {
        // Three bars, period two. Bar 1 closes at 10. Bar 2 opens away and ranges 11 to 10.5,
        // so its own range is 0.5 and its gap from 10 is 1.0: the true range is 1.0. Bar 3
        // ranges 10.5 to 10.0 against a previous close of 10.6, so its range is 0.5 and its
        // largest gap is 0.6.
        decimal[] high = [10m, 11m, 10.5m];
        decimal[] low = [10m, 10.5m, 10m];
        decimal[] close = [10m, 10.6m, 10.2m];

        // Seeded on the mean of the two true ranges, 1.0 and 0.6, which is 0.8. Nothing follows
        // the seed here, so the answer is the seed.
        Assert.Equal(0.8m, Averages.Wilder(high, low, close, 2));
    }

    [Fact]
    public void The_true_range_average_uses_wilders_smoothing_and_not_an_exponential_one()
    {
        // Four bars, period two, so one true range follows the seed. True ranges are 1, 1 and 3.
        // The seed is 1, and Wilder's step is (1*(2-1) + 3)/2 = 2. An exponential average with
        // the same period would use a multiplier of 2/3 and give 1 + (3-1)*2/3, which is 2.33.
        decimal[] high = [10m, 11m, 12m, 15m];
        decimal[] low = [10m, 10m, 11m, 12m];
        decimal[] close = [10m, 11m, 12m, 15m];

        Assert.Equal(2m, Averages.Wilder(high, low, close, 2));
    }

    [Fact]
    public void The_daily_range_is_a_fraction_and_a_corporate_action_cannot_move_it()
    {
        // The same series on two bases, one adjusted by a factor of four. Every price differs
        // and the range fraction does not, because the factor cancels top and bottom. It is the
        // one figure here a split cannot corrupt, and it is still withheld from a blocked ticker
        // rather than written beside six numbers that are wrong.
        IReadOnlyList<StoredDailyBar> raw = Window("AAA", 60, i => (100m + i, 102m + i, 99m + i, 100m + i, 1m));
        IReadOnlyList<StoredDailyBar> quartered = Window("AAA", 60, i => (100m + i, 102m + i, 99m + i, 100m + i, 0.25m));

        Assert.Equal(
            IndicatorEngine.Calculate(raw).AverageDailyRange,
            IndicatorEngine.Calculate(quartered).AverageDailyRange);

        // A fraction, not a percentage. Three points of range on a hundred-dollar stock is 0.03.
        Assert.InRange(IndicatorEngine.Calculate(raw).AverageDailyRange, 0.02m, 0.04m);
    }

    [Fact]
    public void The_median_dollar_volume_is_raw_and_takes_the_middle_rather_than_the_mean()
    {
        // One earnings day at twenty times normal volume. The mean would carry a name over a
        // floor it does not otherwise clear; the median does not notice it.
        var bars = new List<StoredDailyBar>();
        for (int i = 0; i < 60; i++)
        {
            long volume = i == 59 ? 20_000_000 : 1_000_000;
            bars.Add(new StoredDailyBar("AAA", AsOf.AddDays(i - 59), 10m, 10m, 10m, 10m, 10m, volume,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }

        Assert.Equal(10_000_000m, IndicatorEngine.Calculate(bars).DollarVolumeMedian);
    }

    // ---- the refusals --------------------------------------------------------------------

    [Fact]
    public async Task A_ticker_short_of_the_warmup_gets_no_row_rather_than_an_unconverged_number()
    {
        await StoreSessions("AAA", IndicatorEngine.WarmupSessions - 1);

        IndicatorResult result = Engine().Compute(AsOf);

        Assert.Equal(0, result.Computed);
        Assert.Equal(1, result.ShortOfWarmup);
        Assert.Equal(0, CountIndicators());
    }

    [Fact]
    public async Task A_ticker_with_the_warmup_behind_it_gets_a_row()
    {
        await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        IndicatorResult result = Engine().Compute(AsOf);

        Assert.Equal(1, result.Computed);
        Assert.Equal(0, result.Blocked);
        Assert.Equal(1, CountIndicators());
    }

    [Fact]
    public async Task A_ticker_with_an_open_demand_is_refused_and_the_others_are_not()
    {
        await StoreSessions("AAA", IndicatorEngine.WarmupSessions);
        await StoreSessions("BBB", IndicatorEngine.WarmupSessions);

        // The action lands after the bars were observed, so the window does not account for it.
        _clock.Advance(TimeSpan.FromHours(1));
        var vendor = new FakeMarketDataVendor();
        vendor.Split(AsOf, "AAA", 4m);
        await Actions(vendor).IngestAsync(AsOf);

        IndicatorResult result = Engine().Compute(AsOf);

        Assert.Equal(1, result.Blocked);
        Assert.Equal(1, result.Computed);
        Assert.Equal(0, result.DemandsSatisfied);
        Assert.Equal(["BBB"], StoredTickers());
    }

    [Fact]
    public async Task The_engine_refuses_until_the_history_has_been_refetched_and_then_satisfies_the_demand()
    {
        // The whole cycle, which is what the rebuild path is for. A split lands, the ticker is
        // refused, the history is refetched on the new basis, and the run that computes it
        // stamps the demand.
        //
        // The recompute is a session later than the refetch, and it has to be. A read as of a
        // night sees only what had been observed by the end of that night, so a refetch done on
        // Wednesday cannot change what Tuesday saw. That is the point-in-time rule doing its job
        // rather than an inconvenience to work around.
        FakeMarketDataVendor vendor = await StoreSessions("AAA", IndicatorEngine.WarmupSessions);
        int seeded = vendor.HistoriesRequested.Count;

        AtEndOf(AsOf);
        vendor.Split(AsOf, "AAA", 4m);
        vendor.Adjust("AAA", 0.25m);
        await Actions(vendor).IngestAsync(AsOf);

        Assert.Equal(1, Engine().Compute(AsOf).Blocked);

        DateOnly rebuiltOn = AsOf.AddDays(1);
        AtEndOf(rebuiltOn);
        BackfillResult backfill = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], rebuiltOn);

        Assert.Equal(1, backfill.Selected);
        Assert.Equal(["AAA"], vendor.HistoriesRequested.Skip(seeded));

        IndicatorResult after = Engine().Compute(rebuiltOn);

        Assert.Equal(0, after.Blocked);
        Assert.Equal(1, after.Computed);
        Assert.Equal(1, after.DemandsSatisfied);

        // Stamped rather than cleared: the row stays and says when it was honoured.
        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Empty(IndicatorRebuildReader.Open(connection, rebuiltOn));
        Assert.Equal(1, CountRows("indicator_rebuild"));
    }

    [Fact]
    public async Task A_restated_ratio_blocks_the_ticker_again_after_it_was_already_rebuilt()
    {
        // The done condition the correction owes. The first demand is satisfied, the vendor then
        // restates the ratio, and the second demand blocks the ticker again rather than
        // colliding with a demand already met and vanishing.
        FakeMarketDataVendor vendor = await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        AtEndOf(AsOf);
        vendor.Split(AsOf, "AAA", 4m);
        vendor.Adjust("AAA", 0.25m);
        await Actions(vendor).IngestAsync(AsOf);

        DateOnly rebuiltOn = AsOf.AddDays(1);
        AtEndOf(rebuiltOn);
        await Ingestor(vendor).BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], rebuiltOn);
        Assert.Equal(1, Engine().Compute(rebuiltOn).DemandsSatisfied);

        // The restatement.
        DateOnly restatedOn = AsOf.AddDays(2);
        AtEndOf(restatedOn);
        var restated = new FakeMarketDataVendor();
        restated.Split(AsOf, "AAA", 5m);
        Assert.Equal(1, (await Actions(restated).IngestAsync(AsOf)).DemandsRaised);

        // The vendor's adjusted series moves with the restated factor, five for one where it had
        // published four. A refetch that returned the figures already stored would be a market
        // in which restating a split changed nothing.
        vendor.Adjust("AAA", 4m / 5m);

        Assert.Equal(1, Engine().Compute(restatedOn).Blocked);
        Assert.Equal(0, Engine().Compute(restatedOn).Computed);

        // And it stays blocked until the history is refetched again.
        DateOnly rebuiltAgainOn = AsOf.AddDays(3);
        AtEndOf(rebuiltAgainOn);
        await Ingestor(vendor).BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], rebuiltAgainOn);

        IndicatorResult third = Engine().Compute(rebuiltAgainOn);
        Assert.Equal(0, third.Blocked);
        Assert.Equal(1, third.Computed);
        Assert.Equal(1, third.DemandsSatisfied);
    }

    [Fact]
    public async Task A_refetch_that_changes_nothing_still_satisfies_the_demand()
    {
        // The case that killed the first satisfaction rule, and it is the ordinary case rather
        // than a corner. A refetch rewrites the bars an action moved and leaves the recent ones
        // alone, because those were already ingested on the post-action basis. Inferring the
        // rebuild from what changed therefore leaves the ticker blocked for ever, which is what
        // the live store did on the first real split it saw.
        //
        // Here nothing at all changed, which is the extreme of the same thing. The demand is
        // satisfied anyway, because the fact that matters is that the series was looked at.
        FakeMarketDataVendor vendor = await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        AtEndOf(AsOf);
        vendor.Split(AsOf, "AAA", 4m);
        await Actions(vendor).IngestAsync(AsOf);

        DateOnly rebuiltOn = AsOf.AddDays(1);
        AtEndOf(rebuiltOn);

        // No Adjust call, so the vendor returns exactly the series already stored.
        BackfillResult backfill = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], rebuiltOn);

        Assert.Equal(0, backfill.Inserted);

        IndicatorResult after = Engine().Compute(rebuiltOn);
        Assert.Equal(0, after.Blocked);
        Assert.Equal(1, after.Computed);
        Assert.Equal(1, after.DemandsSatisfied);
    }

    [Fact]
    public async Task A_nightly_ingest_does_not_satisfy_a_demand_however_many_nights_pass()
    {
        // The other direction the inference failed in, and the worse one, because it produces
        // numbers. The nightly ingest writes a bar for every name every night, so a rule keyed
        // on the latest observation would clear every demand by the following evening with
        // nothing having been refetched.
        FakeMarketDataVendor vendor = await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        AtEndOf(AsOf);
        vendor.Split(AsOf, "AAA", 4m);
        vendor.Adjust("AAA", 0.25m);
        await Actions(vendor).IngestAsync(AsOf);

        for (int day = 1; day <= 3; day++)
        {
            DateOnly session = AsOf.AddDays(day);
            AtEndOf(session);
            vendor.Bar(session, "AAA", 25m, 26m, 24m, 25m, 25m, 1_000_000);
            await Ingestor(vendor).IngestAsync(session);

            IndicatorResult result = Engine().Compute(session);
            Assert.Equal(1, result.Blocked);
            Assert.Equal(0, result.DemandsSatisfied);
        }
    }

    [Fact]
    public async Task A_rerun_that_produces_the_same_figures_writes_no_row()
    {
        await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        AtEndOf(AsOf);
        Assert.Equal(1, Engine().Compute(AsOf).Computed);

        _clock.Advance(TimeSpan.FromHours(1));
        IndicatorResult second = Engine().Compute(AsOf);

        // Append-only is not the same as writing a row every time. A rerun after a failed stage
        // has to cost nothing and change nothing, exactly as it does for a bar.
        Assert.Equal(0, second.Computed);
        Assert.Equal(0, second.Recomputed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(1, CountIndicators());
    }

    [Fact]
    public async Task A_rebuild_reaches_a_session_already_computed_and_leaves_what_it_said_intact()
    {
        // The property the table was rekeyed for. A session is computed, an action lands
        // afterwards, the history is refetched, and the session is computed again on the new
        // basis. A read as of the original night still returns what the lab had that night, and
        // a read as of today returns what it has now.
        FakeMarketDataVendor vendor = await StoreSessions("AAA", IndicatorEngine.WarmupSessions);

        AtEndOf(AsOf);
        Assert.Equal(1, Engine().Compute(AsOf).Computed);

        StoredIndicators original;
        using (SqliteConnection connection = _connections.OpenReadOnly())
        {
            original = Assert.IsType<StoredIndicators>(IndicatorDailyReader.Read(connection, "AAA", AsOf, AsOf));
        }

        // The action, a session later, and the vendor's adjusted series moves with it.
        DateOnly actionOn = AsOf.AddDays(1);
        AtEndOf(actionOn);
        vendor.Split(actionOn, "AAA", 4m);
        vendor.Adjust("AAA", 0.25m);
        await Actions(vendor).IngestAsync(actionOn);

        Assert.Equal(1, Engine().Compute(actionOn).Blocked);

        DateOnly rebuiltOn = AsOf.AddDays(2);
        AtEndOf(rebuiltOn);
        await Ingestor(vendor).BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], rebuiltOn);

        IndicatorResult after = Engine().Compute(rebuiltOn);
        Assert.Equal(1, after.DemandsSatisfied);
        Assert.Equal(1, after.Recomputed);

        using SqliteConnection read = _connections.OpenReadOnly();

        // Two observations of the same session, and neither has replaced the other.
        Assert.Equal(2, CountRowsFor("AAA", AsOf));

        StoredIndicators asItWasThen = Assert.IsType<StoredIndicators>(IndicatorDailyReader.Read(read, "AAA", AsOf, AsOf));
        StoredIndicators asItIsNow = Assert.IsType<StoredIndicators>(IndicatorDailyReader.Read(read, "AAA", AsOf, rebuiltOn));

        Assert.Equal(original.EmaShort, asItWasThen.EmaShort);
        Assert.NotEqual(original.EmaShort, asItIsNow.EmaShort);

        // A quarter of the price, because that is what a four-for-one does to an adjusted series.
        Assert.Equal(original.EmaShort / 4m, asItIsNow.EmaShort);
    }

    /// <summary>
    /// Moves the clock to the evening of a session, which is when every nightly stage runs. The
    /// tests set it explicitly rather than nudging it forward, because the thing under test is
    /// which session an observation belongs to and an hour either side of midnight decides it.
    /// </summary>
    private void AtEndOf(DateOnly session) =>
        _clock.MoveTo(new DateTimeOffset(session.Year, session.Month, session.Day, 22, 0, 0, TimeSpan.Zero));

    // ---- fixtures ------------------------------------------------------------------------

    /// <summary>
    /// A ticker trading every session up to the as-of date, ingested through the real path so
    /// the stored observations are the ones a run would have made.
    /// </summary>
    private async Task<FakeMarketDataVendor> StoreSessions(string ticker, int sessions)
    {
        var vendor = new FakeMarketDataVendor();
        var dates = new List<DateOnly>();

        DateOnly date = AsOf;
        while (dates.Count < sessions)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                dates.Add(date);
            }

            date = date.AddDays(-1);
        }

        dates.Reverse();

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand security = connection.CreateCommand();
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01') ON CONFLICT (ticker) DO NOTHING;
                INSERT INTO universe_member (ticker, added_on) VALUES (@t, '2020-01-01') ON CONFLICT (ticker) DO NOTHING;
                """;
            security.Parameters.AddWithValue("@t", ticker);
            security.ExecuteNonQuery();

            // A snapshot for the as-of date and the week after it. The engine reads the night's
            // snapshot rather than current membership, because that is what keeps a replay free
            // of survivorship bias, so a test date with no snapshot has no universe at all.
            for (int day = 0; day <= 7; day++)
            {
                using SqliteCommand snapshot = connection.CreateCommand();
                snapshot.CommandText = """
                    INSERT INTO universe_snapshot (as_of, ticker) VALUES (@as_of, @t)
                    ON CONFLICT (as_of, ticker) DO NOTHING;
                    """;
                snapshot.Parameters.AddWithValue("@t", ticker);
                snapshot.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf.AddDays(day)));
                snapshot.ExecuteNonQuery();
            }
        }

        for (int i = 0; i < dates.Count; i++)
        {
            decimal close = 100m + (i % 7);
            vendor.Bar(dates[i], ticker, close, close + 1m, close - 1m, close, close, 1_000_000);
        }

        // Seeded through the per-ticker path rather than a bulk request a night. It is one call
        // instead of a hundred and fifty hundreds, and it is the same code the rebuild uses, so
        // the stored observations, and the refetch record beside them, are the ones a real run
        // would have made.
        //
        // Dated the evening before the as-of date, because a fixture that seeded at the same
        // instant an action is later observed would make the two indistinguishable and every
        // test of the blocking rule would pass for the wrong reason.
        AtEndOf(AsOf.AddDays(-1));
        await Ingestor(vendor).BackfillAsync(BackfillSelection.Named, [ticker], AsOf);

        return vendor;
    }

    /// <summary>A window a calculation test can state outright, oldest first.</summary>
    private static IReadOnlyList<StoredDailyBar> Window(
        string ticker,
        int sessions,
        Func<int, (decimal Open, decimal High, decimal Low, decimal Close, decimal Factor)> shape)
    {
        var bars = new List<StoredDailyBar>();
        for (int i = 0; i < sessions; i++)
        {
            (decimal open, decimal high, decimal low, decimal close, decimal factor) = shape(i);
            bars.Add(new StoredDailyBar(
                ticker, new DateOnly(2026, 1, 1).AddDays(i),
                open, high, low, close, close * factor, 1_000_000,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }

        return bars;
    }

    private int CountIndicators() => CountRows("indicator_daily");

    private int CountRowsFor(string ticker, DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM indicator_daily WHERE ticker = @t AND as_of = @s;";
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@s", StoreText.DateToStorageText(session));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private int CountRows(string table)
    {
        SqliteIdentifier.Validate(table);
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<string> StoredTickers()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM indicator_daily ORDER BY ticker;";

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }
}
