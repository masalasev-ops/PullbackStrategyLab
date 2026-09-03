using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The three market trackers, on the same terms as the daily bars and for a third of the price
/// of one bulk request.
/// </summary>
public sealed class IndexIngestorTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero));

    public IndexIngestorTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IndexIngestor Ingestor(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
        });

        return new IndexIngestor(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    private static FakeMarketDataVendor Trackers(int sessions = 5)
    {
        var vendor = new FakeMarketDataVendor();
        foreach (string symbol in new[] { "SPY", "QQQ", "IWM" })
        {
            vendor.Trading(symbol, AsOf, sessions, close: 400m, volume: 50_000_000);
        }

        return vendor;
    }

    [Fact]
    public async Task The_three_trackers_are_stored_and_cost_one_call_each()
    {
        IndexIngestResult result = await Ingestor(Trackers()).IngestAsync(AsOf);

        // Three calls, not a hundred. Asking the bulk endpoint for three symbols would be a
        // hundred calls to learn three numbers, and the whole endpoint split exists to notice
        // that sort of thing.
        Assert.Equal(3, result.CallsUsed);
        Assert.Equal(3, result.Symbols);
        Assert.Equal(["IWM", "QQQ", "SPY"], StoredSymbols());
    }

    [Fact]
    public async Task Re_running_the_same_window_changes_no_row()
    {
        FakeMarketDataVendor vendor = Trackers();
        IndexIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(AsOf);

        _clock.Advance(TimeSpan.FromHours(1));
        IndexIngestResult second = await ingestor.IngestAsync(AsOf);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.RowsWritten);
        Assert.Equal(15, second.Unchanged);
    }

    [Fact]
    public async Task A_correction_arrives_as_a_new_row_and_a_read_as_of_the_night_still_sees_the_original()
    {
        FakeMarketDataVendor vendor = Trackers(sessions: 1);
        await Ingestor(vendor).IngestAsync(AsOf);

        _clock.Advance(TimeSpan.FromDays(2));
        vendor.Bar(AsOf, "SPY", 401m, 402m, 400m, 401m, 401m, 50_000_000);
        IndexIngestResult corrected = await Ingestor(vendor).IngestAsync(AsOf);

        Assert.Equal(1, corrected.Inserted);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // The same property the daily bars hold, and the reason is the same: a replay of the
        // night the lab acted has to see what the lab saw.
        Assert.Equal(400m, IndexBarReader.Read(connection, "SPY", AsOf, 5, SessionBoundaries.UsEquities).Single().Close);
        Assert.Equal(401m, IndexBarReader.Read(connection, "SPY", AsOf.AddDays(2), 5, SessionBoundaries.UsEquities).Single().Close);
    }

    [Fact]
    public async Task A_run_that_reaches_the_ceiling_part_way_keeps_what_it_fetched_and_reports_partial()
    {
        // Two symbols' worth of budget for three symbols. A backfill is per symbol, so stopping
        // between them leaves no symbol half done and the rerun picks up the rest.
        IndexIngestResult result = await Ingestor(Trackers(), dailyCallCeiling: 2).IngestAsync(AsOf);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Symbols);
        Assert.Equal(2, StoredSymbols().Count);
    }

    private IReadOnlyList<string> StoredSymbols()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT symbol FROM index_bar ORDER BY symbol;";

        var symbols = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            symbols.Add(reader.GetString(0));
        }

        return symbols;
    }
}
