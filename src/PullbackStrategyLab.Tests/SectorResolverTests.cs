using System.Text.Json;
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
/// The lazy sector lookup, and what one bad name costs.
///
/// Written after the walk died on its 149th name on 2026-08-27 and took the other 86 with it. The
/// cost was not the calls. `clusters` runs three minutes later over whatever `security` holds, so
/// fifteen of that night's forty-four setups recorded a cluster verdict of failed with no value,
/// and a setup row cannot be improved once its outcome is visible.
/// </summary>
public sealed class SectorResolverTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 27);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 22, 12, 0, TimeSpan.Zero));

    public SectorResolverTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private SectorResolver Resolver(FakeMarketDataVendor vendor)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = 5000,
        });

        return new SectorResolver(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    /// <summary>Names on a scan tonight, none of them resolved, in the order the walk will take.</summary>
    private void Scanned(params string[] tickers)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        foreach (string ticker in tickers)
        {
            using SqliteCommand security = connection.CreateCommand();
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@t, @t, 'US', 'Common Stock', @d)
                """;
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            security.ExecuteNonQuery();

            using SqliteCommand hit = connection.CreateCommand();
            hit.CommandText =
                "INSERT INTO scan_hit (as_of, ticker, scan, magnitude, rank) VALUES (@d, @t, 'gainer', '1.0', 1)";
            hit.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            hit.Parameters.AddWithValue("@t", ticker);
            hit.ExecuteNonQuery();
        }
    }

    private (string? Sector, bool Stamped) Stored(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sector, sector_resolved_at FROM security WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), !reader.IsDBNull(1));
    }

    private (string Outcome, int? Skipped, int Calls) LastRun()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT outcome, skipped, calls_used FROM run_log WHERE stage = 'sectors' ORDER BY started_at DESC LIMIT 1";

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>
    /// The defect itself, in the shape it happened. The walk is alphabetical, so a name that throws
    /// in the middle of it is a name every later ticker sits behind.
    /// </summary>
    [Fact]
    public async Task A_name_that_throws_costs_that_name_and_no_other()
    {
        Scanned("AAA", "BBB", "CCC");

        var vendor = new FakeMarketDataVendor();
        vendor.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);
        vendor.Fundamentals["CCC"] = new VendorFundamentals("CCC", "Energy", "Oil & Gas", 2_000m);
        vendor.FundamentalsThrows["BBB"] = new JsonException("the vendor answered NA where a number was expected");

        SectorResult result = await Resolver(vendor).ResolveAsync(AsOf, limit: 200);

        // Every name asked, not only the ones before the failure.
        Assert.Equal(["AAA", "BBB", "CCC"], vendor.FundamentalsRequested);

        Assert.Equal("Industrials", Stored("AAA").Sector);
        Assert.Equal("Energy", Stored("CCC").Sector);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, result.Resolved);
    }

    /// <summary>
    /// A skipped name keeps its null stamp, so tomorrow asks again. A refusal that happens once
    /// must not permanently record a good name as one the vendor has nothing on, which is what
    /// stamping it would do: the walk is keyed on the stamp rather than on the sector, precisely so
    /// a name already asked about is never asked again.
    /// </summary>
    [Fact]
    public async Task A_skipped_name_is_left_unstamped_so_it_is_asked_again()
    {
        Scanned("AAA", "BBB");

        var vendor = new FakeMarketDataVendor();
        vendor.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);
        vendor.FundamentalsThrows["BBB"] = new VendorException("fundamentals/BBB.US returned 500 InternalServerError.");

        await Resolver(vendor).ResolveAsync(AsOf, limit: 200);

        Assert.True(Stored("AAA").Stamped);
        Assert.False(Stored("BBB").Stamped);

        // And the second run asks about it and no longer asks about the one that answered.
        var second = new FakeMarketDataVendor();
        second.Fundamentals["BBB"] = new VendorFundamentals("BBB", "Utilities", "Water", 3_000m);
        await Resolver(second).ResolveAsync(AsOf, limit: 200);

        Assert.Equal(["BBB"], second.FundamentalsRequested);
        Assert.Equal("Utilities", Stored("BBB").Sector);
    }

    /// <summary>
    /// The run says it did not finish, and says how many it passed over.
    ///
    /// rows_written cannot carry either fact. RunScope measures it as a row-count delta and this
    /// stage only issues UPDATE, so a clean run and the run that died after 149 calls both recorded
    /// 0 rows, which is why the count needed a column of its own.
    /// </summary>
    [Fact]
    public async Task A_run_that_skipped_a_name_is_partial_and_says_how_many()
    {
        Scanned("AAA", "BBB");

        var vendor = new FakeMarketDataVendor();
        vendor.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);
        vendor.FundamentalsThrows["BBB"] = new JsonException("unreadable");

        await Resolver(vendor).ResolveAsync(AsOf, limit: 200);

        (string outcome, int? skipped, int calls) = LastRun();

        Assert.Equal("partial", outcome);
        Assert.Equal(1, skipped);

        // Both names cost a call. The real client counts before it issues the request, so a name
        // that threw is spend, and a run log showing only the resolved ones would under-report it.
        Assert.Equal(2, calls);
    }

    /// <summary>A run that finished its list is clean and carries no skipped count at all.</summary>
    [Fact]
    public async Task A_run_that_skipped_nothing_is_clean_and_records_no_count()
    {
        Scanned("AAA");

        var vendor = new FakeMarketDataVendor();
        vendor.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);

        await Resolver(vendor).ResolveAsync(AsOf, limit: 200);

        (string outcome, int? skipped, _) = LastRun();

        // Null rather than 0, so a stage that walks no list is distinguishable from one that
        // walked a list cleanly.
        Assert.Equal("clean", outcome);
        Assert.Null(skipped);
    }

    /// <summary>
    /// The one thing that must still take the stage down. A store that will not accept a write is
    /// not a condition the next ticker would survive either, so the catch names what the vendor can
    /// do and nothing else.
    /// </summary>
    [Fact]
    public async Task A_failure_that_is_not_the_vendor_still_stops_the_run()
    {
        Scanned("AAA");

        var vendor = new FakeMarketDataVendor();
        vendor.FundamentalsThrows["AAA"] = new InvalidOperationException("the store is gone");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Resolver(vendor).ResolveAsync(AsOf, limit: 200));

        // And the scope that was never completed says failed rather than leaving a run that starts
        // and never ends, which reads as a job still running.
        Assert.Equal("failed", LastRun().Outcome);
    }

    /// <summary>
    /// The three counts partition the pool, so a reader can say how many names carry a sector.
    ///
    /// The line these replaced said "86 asked, 85 resolved, 1 the vendor had nothing on", and a
    /// reader concluded that all 234 of the night's names carry a sector. At most 233 can: a name
    /// the vendor holds nothing on is stamped and has none, which is a third state neither of the
    /// first two figures reports. Two numbers cannot describe three states.
    /// </summary>
    [Fact]
    public async Task The_three_counts_sum_to_the_pool()
    {
        Scanned("AAA", "BBB", "CCC", "DDD");

        var vendor = new FakeMarketDataVendor();
        vendor.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);
        vendor.Fundamentals["BBB"] = new VendorFundamentals("BBB", "Energy", "Oil & Gas", 2_000m);

        // CCC answers nothing, which is a real answer and stamps the row.
        vendor.FundamentalsThrows["DDD"] = new JsonException("unreadable");

        SectorResult result = await Resolver(vendor).ResolveAsync(AsOf, limit: 200);

        Assert.Equal(2, result.Resolved);
        Assert.Equal(1, result.VendorHadNothing);
        Assert.Equal(1, result.Skipped);

        int unstamped = result.Unresolved - result.Resolved - result.VendorHadNothing;

        Assert.Equal(1, unstamped);
        Assert.Equal(result.Unresolved, result.Resolved + result.VendorHadNothing + unstamped);

        // Both units. Four names asked, four requests, four calls at one apiece; the same four
        // against a bulk endpoint would be four hundred, and reporting one figure makes the other
        // unrecoverable.
        Assert.Equal(4, result.Requests);
        Assert.Equal(4 * EodhdClient.FundamentalsCost, result.CallsUsed);
    }

    /// <summary>
    /// The second pass counts itself, so a night where the first died early and the second asked
    /// for everything is visible rather than averaged into one total.
    /// </summary>
    [Fact]
    public async Task Each_pass_of_the_night_says_which_pass_it_is()
    {
        Scanned("AAA", "BBB");

        var first = new FakeMarketDataVendor();
        first.Fundamentals["AAA"] = new VendorFundamentals("AAA", "Industrials", "Engineering", 1_000m);
        first.FundamentalsThrows["BBB"] = new JsonException("unreadable");

        Assert.Equal(1, (await Resolver(first).ResolveAsync(AsOf, limit: 200)).Pass);

        var second = new FakeMarketDataVendor();
        second.Fundamentals["BBB"] = new VendorFundamentals("BBB", "Utilities", "Water", 3_000m);

        SectorResult retry = await Resolver(second).ResolveAsync(AsOf, limit: 200);

        Assert.Equal(2, retry.Pass);
        Assert.Equal(1, retry.Asked);
    }
}
