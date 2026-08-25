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
/// Corporate actions, and the rebuild a split forces.
///
/// The checkpoint's done condition is the first test: a synthetic split forces a full recompute
/// for that ticker and only that ticker. "Only that ticker" is the half that is easy to get
/// wrong and impossible to notice, because a rebuild that took the whole universe would produce
/// correct numbers at thirty times the cost and nothing on the surface would say so.
/// </summary>
public sealed class ActionIngestorTests : IDisposable
{
    private static readonly DateOnly EffectiveDate = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 21, 30, 0, TimeSpan.Zero));

    public ActionIngestorTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
        Universe("AAA", "BBB", "CCC");
    }

    public void Dispose() => _root.Dispose();

    private ActionIngestor Ingestor(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
        });

        return new ActionIngestor(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    [Fact]
    public async Task An_action_demands_a_rebuild_for_that_ticker_and_only_that_ticker()
    {
        // The done condition. AAA splits four for one, BBB pays a dividend the same evening,
        // CCC does nothing at all. The third is the half that is easy to get wrong and
        // impossible to notice.
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Dividend(EffectiveDate, "BBB", 0.44m);

        ActionIngestResult result = await Ingestor(vendor).IngestAsync(EffectiveDate, withDividends: true);

        Assert.Equal(2, result.Inserted);

        // Both actions move the adjusted close, so both raise a demand. Magnitude does not enter
        // it: a dividend distorts an average less than a split does, and "less wrong" is not a
        // category this design has.
        Assert.Equal(2, result.DemandsRaised);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Equal(["AAA", "BBB"], IndicatorRebuildReader.BlockedTickers(connection, EffectiveDate).Order(StringComparer.Ordinal));

        // The dividend is stored, and it is stored as cash per share rather than as a factor.
        StoredCorporateAction dividend = Assert.Single(CorporateActionReader.Read(connection, "BBB", EffectiveDate));
        Assert.Equal(CorporateActionType.Dividend, dividend.Type);
        Assert.Equal(0.44m, dividend.Ratio);
    }

    [Fact]
    public async Task A_one_for_one_split_rescales_nothing_and_demands_no_rebuild()
    {
        // The vendor publishes these. They are a bookkeeping artefact rather than an event, and
        // rebuilding a whole price history against a factor of one is work for no change.
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 1m);

        ActionIngestResult result = await Ingestor(vendor).IngestAsync(EffectiveDate);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.DemandsRaised);
    }

    [Fact]
    public async Task A_second_split_on_a_later_date_demands_a_second_rebuild_and_no_other_ticker()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Split(EffectiveDate.AddDays(7), "AAA", 2m);

        ActionIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(EffectiveDate);
        _clock.Advance(TimeSpan.FromDays(7));
        await ingestor.IngestAsync(EffectiveDate.AddDays(7));

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<RebuildDemand> open = IndicatorRebuildReader.Open(connection, EffectiveDate.AddDays(7));

        Assert.Equal(2, open.Count);
        Assert.All(open, d => Assert.Equal("AAA", d.Ticker));
    }

    [Fact]
    public async Task Actions_are_stored_for_the_names_in_the_universe_and_no_others()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Split(EffectiveDate, "ZZZ", 4m);

        ActionIngestResult result = await Ingestor(vendor).IngestAsync(EffectiveDate);

        Assert.Equal(2, result.SplitsPublished);
        Assert.Equal(1, result.InUniverse);
        Assert.Equal(1, result.Inserted);
    }

    [Fact]
    public async Task Re_running_the_date_writes_no_row_and_demands_no_second_rebuild()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);

        ActionIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(EffectiveDate);

        _clock.Advance(TimeSpan.FromHours(2));
        ActionIngestResult second = await ingestor.IngestAsync(EffectiveDate);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.RowsWritten);
        Assert.Equal(1, CountRows("corporate_action"));
        Assert.Equal(1, CountRows("indicator_rebuild"));
    }

    [Fact]
    public async Task A_restated_ratio_raises_a_second_demand_and_leaves_the_first_alone()
    {
        // The case 004 could not represent at all. A ticker is rebuilt, the vendor then restates
        // the ratio, and under a key of ticker and date the restatement had nowhere to go: the
        // action could not be stored twice, and the demand it should raise collided with one
        // already satisfied. The stock stayed rebuilt against a factor nobody publishes.
        var first = new FakeMarketDataVendor();
        first.Split(EffectiveDate, "AAA", 4m);
        await Ingestor(first).IngestAsync(EffectiveDate);

        Stamp("AAA", EffectiveDate, CorporateActionType.Split, _clock.UtcNow.AddHours(1));
        _clock.Advance(TimeSpan.FromDays(1));

        var corrected = new FakeMarketDataVendor();
        corrected.Split(EffectiveDate, "AAA", 5m);
        ActionIngestResult result = await Ingestor(corrected).IngestAsync(EffectiveDate);

        Assert.Equal(1, result.Restatements);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.DemandsRaised);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // Two observations of one action, and two demands: the satisfied one from the first
        // night and an open one from the restatement. Nothing was mutated and nothing cleared.
        Assert.Equal(2, CountRows("corporate_action"));
        Assert.Equal(2, CountRows("indicator_rebuild"));
        Assert.Equal(["AAA"], IndicatorRebuildReader.BlockedTickers(connection, EffectiveDate.AddDays(1)));

        // And a read takes the latest observation, so the ratio in force is the restated one.
        Assert.Equal(5m, Assert.Single(CorporateActionReader.Read(connection, "AAA", EffectiveDate.AddDays(1))).Ratio);
    }

    [Fact]
    public async Task A_restatement_does_not_change_what_a_night_before_it_saw()
    {
        var first = new FakeMarketDataVendor();
        first.Split(EffectiveDate, "AAA", 4m);
        await Ingestor(first).IngestAsync(EffectiveDate);

        _clock.Advance(TimeSpan.FromDays(2));

        var corrected = new FakeMarketDataVendor();
        corrected.Split(EffectiveDate, "AAA", 5m);
        await Ingestor(corrected).IngestAsync(EffectiveDate);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // The same property the bar reader holds. A replay of the night the lab acted has to see
        // the factor the lab had, including the one that turned out to be wrong.
        Assert.Equal(4m, Assert.Single(CorporateActionReader.Read(connection, "AAA", EffectiveDate)).Ratio);
        Assert.Equal(5m, Assert.Single(CorporateActionReader.Read(connection, "AAA", EffectiveDate.AddDays(2))).Ratio);
    }

    [Fact]
    public async Task Every_demand_names_an_action_that_is_actually_stored()
    {
        // What the foreign key would have bought. It is not declared, because SQLite rewrites a
        // child's foreign key clause when the parent is renamed and a hand-written table rebuild
        // renames, so each table's rebuild would depend on the order of the other's.
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Dividend(EffectiveDate, "BBB", 0.44m);
        await Ingestor(vendor).IngestAsync(EffectiveDate, withDividends: true);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM indicator_rebuild r
             WHERE NOT EXISTS (
                     SELECT 1 FROM corporate_action a
                      WHERE a.ticker = r.ticker
                        AND a.effective_date = r.effective_date
                        AND a.type = r.type
                        AND a.observed_at = r.observed_at);
            """;

        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Dividends_are_not_requested_unless_they_are_asked_for()
    {
        // A bulk request a night, every night, for data nothing downstream reads yet. Nothing
        // else in the system would notice this starting.
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Dividend(EffectiveDate, "BBB", 0.44m);

        ActionIngestResult result = await Ingestor(vendor).IngestAsync(EffectiveDate);

        (DateOnly Date, CorporateActionType Type) asked = Assert.Single(vendor.ActionsRequested);
        Assert.Equal(EffectiveDate, asked.Date);
        Assert.Equal(CorporateActionType.Split, asked.Type);
        Assert.Equal(0, result.DividendsPublished);
        Assert.Equal(EodhdClient.BulkSplitCost, result.CallsUsed);
    }

    [Fact]
    public async Task Asking_for_dividends_costs_a_second_bulk_request()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);

        ActionIngestResult result = await Ingestor(vendor).IngestAsync(EffectiveDate, withDividends: true);

        Assert.Equal(EodhdClient.BulkSplitCost + EodhdClient.BulkDividendCost, result.CallsUsed);
    }

    [Fact]
    public async Task A_run_that_reaches_the_ceiling_on_the_splits_request_stores_nothing()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);

        ActionIngestResult result = await Ingestor(vendor, dailyCallCeiling: 50).IngestAsync(EffectiveDate);

        // A split half-ingested is worse than one not ingested at all: the second is a gap the
        // rerun closes, and the first is a store that believes it has tonight's splits.
        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(0, CountRows("corporate_action"));
        Assert.Equal(0, CountRows("indicator_rebuild"));
    }

    [Fact]
    public async Task A_ceiling_reached_on_the_dividends_request_keeps_the_splits_and_reports_partial()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        vendor.Dividend(EffectiveDate, "BBB", 0.44m);

        ActionIngestResult result = await Ingestor(vendor, dailyCallCeiling: 150)
            .IngestAsync(EffectiveDate, withDividends: true);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, CountRows("corporate_action"));
    }

    [Fact]
    public async Task A_split_is_invisible_to_a_read_as_of_a_night_before_it_was_observed()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        await Ingestor(vendor).IngestAsync(EffectiveDate);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // The same property the bar reader holds, for the same reason. A replay of the Monday
        // before must not see Tuesday's split, or it answers with knowledge the lab did not have.
        Assert.Empty(CorporateActionReader.Read(connection, "AAA", EffectiveDate.AddDays(-1)));
        Assert.Single(CorporateActionReader.Read(connection, "AAA", EffectiveDate));
    }

    [Fact]
    public async Task A_ticker_stays_blocked_as_of_the_nights_it_was_outstanding_after_the_rebuild_lands()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Split(EffectiveDate, "AAA", 4m);
        await Ingestor(vendor).IngestAsync(EffectiveDate);

        // What IndicatorEngine will do at 1.6, written here by hand because the component that
        // owns the update does not exist yet.
        Stamp("AAA", EffectiveDate, CorporateActionType.Split, _clock.UtcNow.AddDays(2));

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Equal(["AAA"], IndicatorRebuildReader.BlockedTickers(connection, EffectiveDate));
        Assert.Empty(IndicatorRebuildReader.BlockedTickers(connection, EffectiveDate.AddDays(3)));
    }

    [Theory]
    [InlineData("4.000000/1.000000", 4)]
    [InlineData("3/2", 1.5)]
    [InlineData("1.000000/1.000000", 1)]
    public void A_split_ratio_is_read_as_a_factor_in_decimal(string published, double expected)
    {
        // Decimal, not double. Three for two is 1.5 exactly in decimal and is not in binary
        // floating point, and a factor a hair under rescales a whole price history a hair under.
        Assert.Equal((decimal)expected, EodhdClient.ParseSplit(published));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4.000000")]
    [InlineData("4.000000/0")]
    [InlineData("four for one")]
    public void A_split_ratio_the_vendor_did_not_publish_properly_is_no_ratio_at_all(string? published)
    {
        // Nothing, rather than zero. A ratio of zero reads as a stock whose price went to
        // nothing, and it would be applied without complaint.
        Assert.Null(EodhdClient.ParseSplit(published));
    }

    /// <summary>
    /// Writes the rebuilt_at that IndicatorEngine will write at 1.6. By hand and in the test,
    /// because putting it in shipped code before its component exists would give the update to
    /// whichever type happened to hold the statement.
    /// </summary>
    private void Stamp(string ticker, DateOnly effectiveDate, CorporateActionType type, DateTimeOffset at)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE indicator_rebuild
               SET rebuilt_at = @at
             WHERE ticker = @ticker AND effective_date = @effective_date AND type = @type;
            """;
        command.Parameters.AddWithValue("@at", StoreText.TimestampToStorageText(at));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(effectiveDate));
        command.Parameters.AddWithValue("@type", type.ToStorageText());
        command.ExecuteNonQuery();
    }

    private void Universe(params string[] tickers)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        foreach (string ticker in tickers)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2026-08-25');
                INSERT INTO universe_member (ticker, added_on) VALUES (@t, '2026-08-25');
                """;
            command.Parameters.AddWithValue("@t", ticker);
            command.ExecuteNonQuery();
        }
    }

    private int CountRows(string table)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
