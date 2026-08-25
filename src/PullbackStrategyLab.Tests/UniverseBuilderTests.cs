using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The universe filter, against a market the test states outright. The floors are the whole
/// content of this stage, and each is asserted by the case that should fail it rather than by
/// a name that clears everything.
/// </summary>
public sealed class UniverseBuilderTests : IDisposable
{
    /// <summary>A Tuesday, so the walk back covers a weekend within the first week.</summary>
    private static readonly DateOnly AsOf = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero));

    public UniverseBuilderTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private UniverseBuilder Builder(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000, int windowSessions = 20)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
            Universe = new UniverseOptions { LiquidityWindowSessions = windowSessions },
        });

        return new UniverseBuilder(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    [Fact]
    public async Task Only_common_stock_survives_whatever_else_the_exchange_lists()
    {
        var vendor = new FakeMarketDataVendor()
            .Listing("AAPL")
            .Listing("SPY", type: "ETF")
            .Listing("PFDX", type: "Preferred Stock");

        vendor.Trading("AAPL", AsOf, 20, close: 200m, volume: 5_000_000);
        vendor.Trading("SPY", AsOf, 20, close: 500m, volume: 80_000_000);
        vendor.Trading("PFDX", AsOf, 20, close: 25m, volume: 4_000_000);

        UniverseBuildResult result = await Builder(vendor).BuildAsync(AsOf);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(["AAPL"], Members());
    }

    [Fact]
    public async Task A_stock_below_the_price_floor_is_excluded_however_much_of_it_trades()
    {
        var vendor = new FakeMarketDataVendor().Listing("PENNY").Listing("REAL");

        // Four dollars, and half a billion dollars a day changing hands. Below the floor the
        // spread widens enough to swallow the stop, so volume does not rescue it.
        vendor.Trading("PENNY", AsOf, 20, close: 4.99m, volume: 100_000_000);
        vendor.Trading("REAL", AsOf, 20, close: 5.01m, volume: 5_000_000);

        await Builder(vendor).BuildAsync(AsOf);

        Assert.Equal(["REAL"], Members());
    }

    [Fact]
    public async Task A_stock_below_the_liquidity_floor_is_excluded_however_expensive_it_is()
    {
        var vendor = new FakeMarketDataVendor().Listing("THIN").Listing("DEEP");

        // Nineteen million a day against a twenty million floor.
        vendor.Trading("THIN", AsOf, 20, close: 950m, volume: 20_000);
        vendor.Trading("DEEP", AsOf, 20, close: 100m, volume: 210_000);

        await Builder(vendor).BuildAsync(AsOf);

        Assert.Equal(["DEEP"], Members());
    }

    [Fact]
    public async Task The_liquidity_floor_is_a_median_so_one_extraordinary_day_does_not_carry_a_name()
    {
        var vendor = new FakeMarketDataVendor().Listing("SPIKE");

        // Nineteen quiet days at ten million, and one earnings day at four hundred million.
        // The mean clears the floor comfortably; the median does not, and the median is what
        // says whether you could actually get out on an ordinary day.
        vendor.Trading("SPIKE", AsOf, 20, close: 100m, volume: 100_000);
        vendor.Bar(AsOf, "SPIKE", close: 100m, volume: 4_000_000);

        await Builder(vendor).BuildAsync(AsOf);

        Assert.Empty(Members());
    }

    [Fact]
    public async Task A_name_that_did_not_trade_through_the_window_fails_rather_than_being_given_a_short_series()
    {
        var vendor = new FakeMarketDataVendor().Listing("NEW").Listing("OLD");

        // Listed three days ago. Three enormous days are not evidence that it trades.
        vendor.Trading("NEW", AsOf, 3, close: 100m, volume: 5_000_000);
        vendor.Trading("OLD", AsOf, 20, close: 100m, volume: 300_000);

        await Builder(vendor).BuildAsync(AsOf);

        Assert.Equal(["OLD"], Members());
    }

    [Fact]
    public async Task The_snapshot_records_who_was_listed_on_the_night()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA").Listing("BBB");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);
        vendor.Trading("BBB", AsOf, 20, close: 100m, volume: 300_000);

        await Builder(vendor).BuildAsync(AsOf);

        // The one record that cannot be reconstructed later, because a delisted name is simply
        // absent from tomorrow's symbol list.
        Assert.Equal(["AAA", "BBB"], Snapshot(AsOf));
    }

    [Fact]
    public async Task Re_running_the_same_date_changes_no_row()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);

        UniverseBuilder builder = Builder(vendor);
        await builder.BuildAsync(AsOf);
        string before = StoreFingerprint();

        UniverseBuildResult second = await builder.BuildAsync(AsOf);

        Assert.Equal(before, StoreFingerprint());
        Assert.Equal(0, second.RowsWritten);
    }

    [Fact]
    public async Task A_name_that_leaves_keeps_its_row_and_gains_a_date_and_coming_back_clears_it()
    {
        var vendor = new FakeMarketDataVendor().Listing("STAY").Listing("GOES");
        vendor.Trading("STAY", AsOf, 20, close: 100m, volume: 300_000);
        vendor.Trading("GOES", AsOf, 20, close: 100m, volume: 300_000);

        await Builder(vendor).BuildAsync(AsOf);

        // The next night, GOES has dried up. The clock moves with it, because the call ceiling
        // is a daily total and three nights of screening on one date would exhaust it.
        _clock.Advance(TimeSpan.FromDays(1));
        DateOnly next = AsOf.AddDays(1);
        var thinner = new FakeMarketDataVendor().Listing("STAY").Listing("GOES");
        thinner.Trading("STAY", next, 20, close: 100m, volume: 300_000);
        thinner.Trading("GOES", next, 20, close: 100m, volume: 1_000);

        await Builder(thinner).BuildAsync(next);

        // Membership is state, not a filter. The row stays so a setup recorded while it was a
        // member still resolves to a security.
        Assert.Equal(["STAY"], Members());
        Assert.Equal("2026-08-26", RemovedOn("GOES"));
        Assert.Equal("2026-08-25", AddedOn("GOES"));

        // And back again on the third night.
        _clock.Advance(TimeSpan.FromDays(1));
        DateOnly third = AsOf.AddDays(2);
        var recovered = new FakeMarketDataVendor().Listing("STAY").Listing("GOES");
        recovered.Trading("STAY", third, 20, close: 100m, volume: 300_000);
        recovered.Trading("GOES", third, 20, close: 100m, volume: 300_000);

        await Builder(recovered).BuildAsync(third);

        Assert.Null(RemovedOn("GOES"));
        Assert.Equal("2026-08-27", AddedOn("GOES"));
    }

    [Fact]
    public async Task A_weekend_is_never_requested_because_a_request_costs_the_same_whether_it_trades_or_not()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);

        await Builder(vendor).BuildAsync(AsOf);

        Assert.DoesNotContain(vendor.DatesRequested, d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(20, vendor.DatesRequested.Count);
    }

    [Fact]
    public async Task The_screen_costs_what_the_call_budget_says_it_costs()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);

        UniverseBuildResult result = await Builder(vendor).BuildAsync(AsOf);

        // The symbol list plus twenty market days of the whole market. RUNBOOK's backfill
        // budgets about 2,000 for the second of those, and this is where that number comes from.
        Assert.Equal(EodhdClient.SymbolListCost + (20 * EodhdClient.BulkEndOfDayCost), result.CallsUsed);
        Assert.Equal(2005, result.CallsUsed);
    }

    [Fact]
    public async Task A_run_that_reaches_the_ceiling_leaves_membership_alone_and_still_writes_the_snapshot()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA").Listing("BBB");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);
        vendor.Trading("BBB", AsOf, 20, close: 100m, volume: 300_000);

        await Builder(vendor).BuildAsync(AsOf);

        // The next night, with enough budget for the symbol list and five market days.
        _clock.Advance(TimeSpan.FromDays(1));
        DateOnly next = AsOf.AddDays(1);
        var thin = new FakeMarketDataVendor().Listing("AAA").Listing("BBB");
        thin.Trading("AAA", next, 20, close: 100m, volume: 300_000);
        thin.Trading("BBB", next, 20, close: 100m, volume: 300_000);

        UniverseBuildResult result = await Builder(thin, dailyCallCeiling: 600).BuildAsync(next);

        Assert.Equal(RunOutcome.Partial, result.Outcome);

        // A median over five sessions is a different floor, so membership is left as the last
        // complete screen set it rather than rebuilt from a window that is not the one the
        // parameters describe.
        Assert.Equal(["AAA", "BBB"], Members());

        // And the night still has a snapshot. It is the one record that cannot be
        // reconstructed later, so a degraded run writes it from the membership that stands.
        Assert.Equal(["AAA", "BBB"], Snapshot(next));
    }

    [Fact]
    public async Task The_first_ever_run_reaching_the_ceiling_leaves_an_empty_night_rather_than_a_false_one()
    {
        var vendor = new FakeMarketDataVendor().Listing("AAA");
        vendor.Trading("AAA", AsOf, 20, close: 100m, volume: 300_000);

        UniverseBuildResult result = await Builder(vendor, dailyCallCeiling: 600).BuildAsync(AsOf);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Empty(Members());
        Assert.Empty(Snapshot(AsOf));
    }

    private IReadOnlyList<string> Members()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Query(connection, "SELECT ticker FROM universe_member WHERE removed_on IS NULL ORDER BY ticker;");
    }

    private IReadOnlyList<string> Snapshot(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Query(connection, $"SELECT ticker FROM universe_snapshot WHERE as_of = '{asOf:yyyy-MM-dd}' ORDER BY ticker;");
    }

    private string? RemovedOn(string ticker) => Single($"SELECT removed_on FROM universe_member WHERE ticker = '{ticker}';");

    private string? AddedOn(string ticker) => Single($"SELECT added_on FROM universe_member WHERE ticker = '{ticker}';");

    /// <summary>Everything in the three stores, in one string, so idempotence is one comparison.</summary>
    private string StoreFingerprint()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return string.Join("|", Query(connection, """
            SELECT ticker || ':' || first_seen FROM security
            UNION ALL SELECT ticker || ':' || added_on || ':' || COALESCE(removed_on, '-') FROM universe_member
            UNION ALL SELECT as_of || ':' || ticker FROM universe_snapshot
            ORDER BY 1;
            """));
    }

    private string? Single(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : value.ToString();
    }

    private static IReadOnlyList<string> Query(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
