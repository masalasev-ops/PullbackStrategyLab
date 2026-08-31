using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The bar store, and the two properties that make a replay mean anything: bars are
/// append-only, and a read sees only what had been observed by its as-of date.
/// </summary>
public sealed class DailyBarIngestorTests : IDisposable
{
    private static readonly DateOnly BarDate = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 21, 30, 0, TimeSpan.Zero));

    public DailyBarIngestorTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
        Universe("AAA", "BBB");
    }

    public void Dispose() => _root.Dispose();

    private DailyBarIngestor Ingestor(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
        });

        return new DailyBarIngestor(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    [Fact]
    public async Task Bars_are_stored_for_the_names_in_the_universe_and_no_others()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Bar(BarDate, "AAA", close: 100m, volume: 1_000);
        vendor.Bar(BarDate, "BBB", close: 200m, volume: 2_000);
        vendor.Bar(BarDate, "ZZZ", close: 300m, volume: 3_000);

        DailyBarIngestResult result = await Ingestor(vendor).IngestAsync(BarDate);

        Assert.Equal(3, result.Published);
        Assert.Equal(2, result.InUniverse);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(["AAA", "BBB"], StoredTickers());
    }

    [Fact]
    public async Task Re_running_the_same_date_changes_no_row()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Bar(BarDate, "AAA", close: 100m, volume: 1_000);

        DailyBarIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(BarDate);

        // The clock has moved on, so a naive append would write a second row under a later
        // observed_at and call it a correction.
        _clock.Advance(TimeSpan.FromHours(1));
        DailyBarIngestResult second = await ingestor.IngestAsync(BarDate);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.RowsWritten);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task Re_running_a_backfilled_date_changes_no_row_either()
    {
        // The case a same-day test cannot reach. A bar dated a fortnight ago is observed today,
        // so an ingestor comparing against observations made by the bar date finds nothing and
        // rewrites the same figures under a new observation on every run. It looks idempotent
        // for tonight's date and is not idempotent at all for any other.
        DateOnly backfilled = BarDate.AddDays(-14);

        var vendor = new FakeMarketDataVendor();
        vendor.Bar(backfilled, "AAA", close: 100m, volume: 1_000);

        DailyBarIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(backfilled);

        _clock.Advance(TimeSpan.FromMinutes(5));
        DailyBarIngestResult second = await ingestor.IngestAsync(backfilled);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(1, RowCount());
    }

    [Fact]
    public async Task A_vendor_correction_arrives_as_a_new_row_and_the_original_stays()
    {
        var first = new FakeMarketDataVendor();
        first.Bar(BarDate, "AAA", close: 100m, volume: 1_000);
        await Ingestor(first).IngestAsync(BarDate);

        _clock.Advance(TimeSpan.FromDays(1));

        var corrected = new FakeMarketDataVendor();
        corrected.Bar(BarDate, "AAA", close: 101m, volume: 1_000);
        DailyBarIngestResult result = await Ingestor(corrected).IngestAsync(BarDate);

        Assert.Equal(1, result.Corrections);

        // Two rows for one bar. The wrong figure is still there, because a replay of the night
        // the lab acted on it has to see what the lab saw.
        Assert.Equal(2, RowCount());
    }

    [Fact]
    public async Task A_read_sees_the_figure_that_had_been_observed_by_its_as_of_date_and_not_the_correction()
    {
        var first = new FakeMarketDataVendor();
        first.Bar(BarDate, "AAA", close: 100m, volume: 1_000);
        await Ingestor(first).IngestAsync(BarDate);

        _clock.Advance(TimeSpan.FromDays(2));

        var corrected = new FakeMarketDataVendor();
        corrected.Bar(BarDate, "AAA", close: 101m, volume: 1_000);
        await Ingestor(corrected).IngestAsync(BarDate);

        var reader = new DailyBarReader(_connections);

        // The single most important property in the system. A read as of the night itself sees
        // 100, because that is what the lab had; a read as of today sees the correction.
        Assert.Equal(100m, reader.Read("AAA", BarDate, sessions: 5).Single().Close);
        Assert.Equal(101m, reader.Read("AAA", BarDate.AddDays(2), sessions: 5).Single().Close);
    }

    [Fact]
    public async Task A_bar_dated_after_the_as_of_date_is_invisible_to_a_read()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Bar(BarDate, "AAA", close: 100m, volume: 1_000);
        vendor.Bar(BarDate.AddDays(1), "AAA", close: 110m, volume: 1_000);

        DailyBarIngestor ingestor = Ingestor(vendor);
        await ingestor.IngestAsync(BarDate);
        _clock.Advance(TimeSpan.FromDays(1));
        await ingestor.IngestAsync(BarDate.AddDays(1));

        var reader = new DailyBarReader(_connections);

        Assert.Single(reader.Read("AAA", BarDate, sessions: 10));
        Assert.Equal(2, reader.Read("AAA", BarDate.AddDays(1), sessions: 10).Count);
    }

    [Fact]
    public async Task Bars_come_back_oldest_first_and_the_window_takes_the_most_recent()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Trading("AAA", BarDate, 10, close: 100m, volume: 1_000);

        DailyBarIngestor ingestor = Ingestor(vendor);
        for (DateOnly d = BarDate.AddDays(-13); d <= BarDate; d = d.AddDays(1))
        {
            await ingestor.IngestAsync(d);
        }

        IReadOnlyList<StoredDailyBar> bars = new DailyBarReader(_connections).Read("AAA", BarDate, sessions: 3);

        Assert.Equal(3, bars.Count);
        Assert.True(bars[0].BarDate < bars[1].BarDate && bars[1].BarDate < bars[2].BarDate);
        Assert.Equal(BarDate, bars[^1].BarDate);
    }

    [Fact]
    public async Task The_nightly_pull_is_one_bulk_request()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Bar(BarDate, "AAA", close: 100m, volume: 1_000);

        DailyBarIngestResult result = await Ingestor(vendor).IngestAsync(BarDate);

        // A hundred, which is the whole market. It replaces about six thousand individual
        // requests and is the single largest line in the nightly budget.
        Assert.Equal(100, result.CallsUsed);
        Assert.Single(vendor.DatesRequested);
    }

    [Fact]
    public async Task A_run_that_reaches_the_ceiling_completes_partial_and_writes_nothing()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Bar(BarDate, "AAA", close: 100m, volume: 1_000);

        DailyBarIngestResult result = await Ingestor(vendor, dailyCallCeiling: 50).IngestAsync(BarDate);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(0, RowCount());
    }

    [Fact]
    public void The_write_connection_reports_wal_and_foreign_keys_on()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        // Both are silently off by default in SQLite, and off-by-default is how they stay
        // wrong. Asserted here as well as at the store tests, because this is the checkpoint
        // whose done condition names them.
        Assert.Equal("wal", StoreConnectionFactory.ReadPragma(connection, "journal_mode"), ignoreCase: true);
        Assert.Equal("1", StoreConnectionFactory.ReadPragma(connection, "foreign_keys"));
    }

    [Fact]
    public void A_bar_for_a_ticker_with_no_security_row_is_refused_by_the_store()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES ('NOPE', '2026-08-25', '1', '1', '1', '1', '1', 1, '2026-08-25T21:30:00.000Z');
            """;

        // Which is what foreign_keys being on actually buys. With it off this row would land
        // and nothing would ever join to it.
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    // ---- the delisted purchase -----------------------------------------------------------
    //
    // The survivorship hole, bought in two verbs because the store's own constraint says so:
    // `daily_bar` references `security`, `security` is written by the universe builder, and
    // `daily_bar` by the ingestor. So `delisted-list` records the names and `backfill --delisted`
    // buys their history, and the second reads what the first wrote.
    //
    // Four properties make it safe to leave running across nights: it buys the names the exchange
    // removed rather than the ones it still lists, it never buys a name the universe has held, it
    // stops on the ceiling instead of spending the evening's budget, and it resumes from the
    // record the fetch itself writes rather than from a copy of it.
    // see: Delisted daily history is bought so a reconstructed walk is not confined to survivors

    [Fact]
    public async Task The_delisted_list_records_securities_and_no_membership()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE").Delisted("LEFT");

        DelistedListResult result = await Builder(vendor).RecordDelistedAsync(BarDate);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(2, result.Listed);
        Assert.Equal(2, result.OfTheType);
        Assert.Equal(1, vendor.DelistedListRequests);
        Assert.Equal(0, vendor.SymbolListRequests);

        // Recorded as instruments that existed, and as nothing that trades. The two tables
        // staying apart is the whole of what stops a delisted name reaching a plan.
        Assert.Equal(["AAA", "BBB", "GONE", "LEFT"], StoredSecurities());
        Assert.Equal(["AAA", "BBB"], StoredMembers());
    }

    [Fact]
    public async Task Only_the_configured_security_type_is_recorded()
    {
        // The list carries funds, warrants and preferred shares, and the reconstructed walk has
        // no use for any of them. Filtered here rather than at the fetch, because a name with no
        // security row cannot have bars stored at all and so can never cost a call.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE").Delisted("FUND", type: "ETF").Delisted("PREF", type: "PREFERRED STOCK");

        DelistedListResult result = await Builder(vendor).RecordDelistedAsync(BarDate);

        Assert.Equal(3, result.Listed);
        Assert.Equal(1, result.OfTheType);
        Assert.Equal(["AAA", "BBB", "GONE"], StoredSecurities());
    }

    [Fact]
    public async Task A_venue_the_purchase_does_not_cover_is_not_recorded()
    {
        // The larger of the two bounds on the purchase, and the one that decides how many nights
        // it takes. The delisted list holds 32,851 common stocks and 15,983 of them are on the two
        // venues the universe is 98% drawn from; the rest are four more nights of the ceiling for
        // venues the universe holds 30 names on out of 2,005.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE").Delisted("PINKY", exchange: "PINK");

        DelistedListResult result = await Builder(vendor).RecordDelistedAsync(BarDate);

        Assert.Equal(2, result.Listed);
        Assert.Equal(1, result.OfTheType);
        Assert.Equal(["AAA", "BBB", "GONE"], StoredSecurities());
    }

    [Fact]
    public async Task The_delisted_run_buys_the_history_of_the_names_the_list_recorded()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE").Delisted("LEFT");
        vendor.Bar(BarDate, "GONE", close: 10m, volume: 1_000);
        vendor.Bar(BarDate, "LEFT", close: 20m, volume: 2_000);
        await Builder(vendor).RecordDelistedAsync(BarDate);

        BackfillResult result = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(2, result.Candidates);
        Assert.Equal(0, result.AlreadyFetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(["GONE", "LEFT"], vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_name_the_universe_once_held_is_not_bought_as_a_delisted_one()
    {
        // What the selection actually rests on, and it is a property of the other stage's
        // writes rather than of this one's. A departed member keeps its membership row with a
        // removal date, so it is excluded here; a name only the delisted list ever saw has no
        // membership row at all. If the universe builder ever wrote a security row for a name
        // it did not offer membership to, this is the test that would fail.
        var vendor = new FakeMarketDataVendor();
        Depart("BBB");

        BackfillResult result = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(0, result.Candidates);
        Assert.Empty(vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_night_where_the_list_did_not_run_buys_nothing_rather_than_failing_on_every_insert()
    {
        // The reason the selection is read from the store rather than from the vendor. Asking
        // the endpoint would name the same tickers, and every bar bought for them would then
        // fail the foreign key to `security`: a night that spent its calls and stored none of
        // what it bought. Reading the store makes the set it can fetch the set it can hold.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE");
        vendor.Bar(BarDate, "GONE", close: 10m, volume: 1_000);

        BackfillResult result = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(0, result.Candidates);
        Assert.Empty(vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_name_an_earlier_night_fetched_is_not_bought_a_second_time()
    {
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE").Delisted("LEFT");
        vendor.Bar(BarDate, "GONE", close: 10m, volume: 1_000);
        vendor.Bar(BarDate, "LEFT", close: 20m, volume: 2_000);
        await Builder(vendor).RecordDelistedAsync(BarDate);

        await Ingestor(vendor).BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);
        vendor.HistoriesRequested.Clear();
        _clock.Advance(TimeSpan.FromDays(1));

        BackfillResult second = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(2, second.Candidates);
        Assert.Equal(2, second.AlreadyFetched);
        Assert.Equal(0, second.Selected);
        Assert.Empty(vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_delisted_name_whose_history_comes_back_empty_is_not_asked_for_again()
    {
        // The case a "which tickers have bars" resume would get wrong every night for ever. A
        // name delisted before the backfill window returns nothing, and nothing is the answer
        // rather than a failure, so the row `history_refetch` carries is what stops the call
        // repeating at one call a night.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("OLD");
        await Builder(vendor).RecordDelistedAsync(BarDate);

        BackfillResult first = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(1, first.Fetched);
        Assert.Equal(0, first.Inserted);
        Assert.Equal(["OLD"], vendor.HistoriesRequested);

        vendor.HistoriesRequested.Clear();
        _clock.Advance(TimeSpan.FromDays(1));
        BackfillResult second = await Ingestor(vendor)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(1, second.AlreadyFetched);
        Assert.Empty(vendor.HistoriesRequested);
    }

    [Fact]
    public async Task The_run_stops_on_the_ceiling_and_the_next_night_carries_on_from_there()
    {
        // What spreads the purchase over nights rather than over the evening's budget. Two
        // calls are left on the first night, so the third name is refused, the run is partial,
        // and the night after asks for exactly that name.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("AAG").Delisted("BBG").Delisted("CCG");
        vendor.Bar(BarDate, "AAG", close: 10m, volume: 1_000);
        vendor.Bar(BarDate, "BBG", close: 20m, volume: 2_000);
        vendor.Bar(BarDate, "CCG", close: 30m, volume: 3_000);
        await Builder(vendor).RecordDelistedAsync(BarDate);
        _clock.Advance(TimeSpan.FromDays(1));

        BackfillResult first = await Ingestor(vendor, dailyCallCeiling: 2)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(RunOutcome.Partial, first.Outcome);
        Assert.Equal(3, first.Selected);
        Assert.Equal(2, first.Fetched);
        Assert.Equal(["AAG", "BBG"], vendor.HistoriesRequested);

        vendor.HistoriesRequested.Clear();
        _clock.Advance(TimeSpan.FromDays(1));

        BackfillResult second = await Ingestor(vendor, dailyCallCeiling: 2)
            .BackfillAsync(BackfillSelection.DelistedNames, [], BarDate);

        Assert.Equal(RunOutcome.Clean, second.Outcome);
        Assert.Equal(2, second.AlreadyFetched);
        Assert.Equal(["CCG"], vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_night_with_nothing_left_records_no_name_and_says_the_run_was_partial()
    {
        // The list is the lister's only request, so an exhausted budget stops it before it knows
        // what it would have recorded. Partial rather than clean: no name was refused, and the
        // run did not cover what it was asked for either.
        var vendor = new FakeMarketDataVendor();
        vendor.Delisted("GONE");

        DelistedListResult result = await Builder(vendor, dailyCallCeiling: 2).RecordDelistedAsync(BarDate);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(0, result.Listed);
        Assert.Equal(["AAA", "BBB"], StoredSecurities());
    }

    private UniverseBuilder Builder(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        var options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
        });

        return new UniverseBuilder(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    private void Depart(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE universe_member SET removed_on = '2026-08-24' WHERE ticker = @t;";
        command.Parameters.AddWithValue("@t", ticker);
        command.ExecuteNonQuery();
    }

    private string[] StoredSecurities() => Column("SELECT ticker FROM security ORDER BY ticker;");

    private string[] StoredMembers() =>
        Column("SELECT ticker FROM universe_member WHERE removed_on IS NULL ORDER BY ticker;");

    private string[] Column(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
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

    private int RowCount()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM daily_bar;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<string> StoredTickers()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT ticker FROM daily_bar ORDER BY ticker;";

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }
}
