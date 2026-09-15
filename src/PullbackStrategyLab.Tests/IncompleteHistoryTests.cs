using System.Globalization;
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
/// A stock's stored history made whole before its averages are computed, from 7.18: which sessions
/// count as missing, what the evening asks the vendor for to complete them, and the averages refused
/// for a window that is still missing one.
///
/// Every store here holds index history, because the index history is what says a day was a session.
/// A store with none places no day and reports nothing missing, which is why the older suites seeding
/// bars alone are untouched by any of this.
/// see: A stock's history is made whole before its averages are computed, and an average across a missing session is refused
/// </summary>
public sealed class IncompleteHistoryTests : IDisposable
{
    /// <summary>The Monday the missing Friday was found, which is the shape every test here repeats.</summary>
    private static readonly DateOnly AsOf = new(2026, 9, 14);

    /// <summary>Well before any night in these tests, so a seeded observation is visible to every read.</summary>
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero));

    public IncompleteHistoryTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    // ---- which sessions are missing ----------------------------------------------------------

    [Fact]
    public void A_day_the_index_history_holds_between_two_of_a_stocks_bars_is_missing_and_a_day_it_does_not_hold_is_not()
    {
        DateOnly[] weekdays = Weekdays(AsOf, 10);
        DateOnly holiday = weekdays[3];
        DateOnly lost = weekdays[6];
        DateOnly[] held = [.. weekdays.Where(d => d != holiday)];

        IndexSessions(held);
        Member("AAA");
        Bars("AAA", held.Where(d => d != lost), LongAgo);

        IReadOnlyDictionary<string, IReadOnlyList<DateOnly>> missing = Missing(LongAgo.AddYears(1));

        // The holiday is absent from the stock and from the trackers alike, so it was never a session.
        Assert.Equal([lost], missing["AAA"]);
    }

    [Fact]
    public void A_day_on_or_before_the_last_refetch_is_not_missing_because_the_vendor_was_already_asked_for_it()
    {
        DateOnly[] held = Weekdays(AsOf, 10);
        DateOnly lost = held[5];

        IndexSessions(held);

        // Both lack the same day. One was refetched through it and the vendor had nothing, which is a
        // halt; the other was last refetched the day before it, which is a day nobody has asked for.
        Member("HALT");
        Bars("HALT", held.Where(d => d != lost), LongAgo);
        Refetch("HALT", through: lost, at: LongAgo);

        Member("LOST");
        Bars("LOST", held.Where(d => d != lost), LongAgo);
        Refetch("LOST", through: held[4], at: LongAgo);

        IReadOnlyDictionary<string, IReadOnlyList<DateOnly>> missing = Missing(LongAgo.AddYears(1));

        Assert.False(missing.ContainsKey("HALT"));
        Assert.Equal([lost], missing["LOST"]);
    }

    [Fact]
    public void Sessions_before_a_stocks_first_bar_are_not_missing()
    {
        DateOnly[] held = Weekdays(AsOf, 10);

        IndexSessions(held);
        Member("NEW");
        Bars("NEW", held.TakeLast(3), LongAgo);

        Assert.Empty(Missing(LongAgo.AddYears(1)));
    }

    [Fact]
    public void A_bar_observed_after_the_bound_does_not_fill_the_day_for_a_read_bounded_before_it()
    {
        DateOnly[] held = Weekdays(AsOf, 10);
        DateOnly lost = held[5];
        DateTimeOffset filledAt = new(2026, 9, 15, 3, 50, 0, TimeSpan.Zero);

        IndexSessions(held);
        Member("AAA");
        Bars("AAA", held.Where(d => d != lost), LongAgo);
        Bars("AAA", [lost], filledAt);

        Assert.Equal([lost], Missing(filledAt.AddTicks(-1))["AAA"]);
        Assert.Empty(Missing(filledAt));
    }

    // ---- what the evening asks for -----------------------------------------------------------

    [Fact]
    public async Task The_incomplete_mode_fetches_a_member_never_fetched_and_a_member_missing_a_session_and_nothing_else()
    {
        DateOnly[] held = Weekdays(AsOf, 10);
        DateOnly lost = held[5];

        IndexSessions(held);

        // Joined the list with the few bars the bulk stored after it, and no history.
        Member("NEW");
        Bars("NEW", held.TakeLast(3), LongAgo);

        // Off the list for a day after its last refetch.
        Member("GAP");
        Bars("GAP", held.Where(d => d != lost), LongAgo);
        Refetch("GAP", through: held[0], at: LongAgo);

        // Missing the same day, and already asked for it.
        Member("HALT");
        Bars("HALT", held.Where(d => d != lost), LongAgo);
        Refetch("HALT", through: AsOf.AddDays(-1), at: LongAgo);

        Member("WHOLE");
        Bars("WHOLE", held, LongAgo);
        Refetch("WHOLE", through: held[0], at: LongAgo);

        var vendor = new FakeMarketDataVendor();
        foreach (DateOnly day in held)
        {
            vendor.Bar(day, "GAP", 10m, 1_000);
            vendor.Bar(day, "NEW", 10m, 1_000);
        }

        int exit = await Ingestor(vendor).RunBackfillAsync([DailyBarIngestor.IncompleteFlag]);

        Assert.Equal(0, exit);
        Assert.Equal(["GAP", "NEW"], vendor.HistoriesRequested);

        // One member misses the day, which is one call per name against a hundred for the market.
        Assert.Empty(vendor.DatesRequested);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Empty(DailyBarIngestor.SelectIncomplete(connection, AsOf, _clock.UtcNow.AddMinutes(1), Options().Value.IndexSymbols).MissingASession);
    }

    [Fact]
    public async Task A_session_more_members_miss_than_a_bulk_request_costs_is_read_whole_for_the_market()
    {
        int members = (EodhdClient.BulkEndOfDayCost / EodhdClient.DailyHistoryCost) + 1;
        (FakeMarketDataVendor vendor, DateOnly lost) = MembersMissingOneDay(members);

        await Ingestor(vendor).RunBackfillAsync([DailyBarIngestor.IncompleteFlag]);

        Assert.Equal([lost], vendor.DatesRequested);

        // The day read whole completed every one of them, so none is then asked for by name.
        Assert.Empty(vendor.HistoriesRequested);
    }

    [Fact]
    public async Task A_session_missed_by_as_many_members_as_a_bulk_request_costs_is_asked_for_name_by_name()
    {
        int members = EodhdClient.BulkEndOfDayCost / EodhdClient.DailyHistoryCost;
        (FakeMarketDataVendor vendor, _) = MembersMissingOneDay(members);

        await Ingestor(vendor).RunBackfillAsync([DailyBarIngestor.IncompleteFlag]);

        Assert.Empty(vendor.DatesRequested);
        Assert.Equal(members, vendor.HistoriesRequested.Count);
    }

    // ---- the averages ------------------------------------------------------------------------

    [Fact]
    public void A_member_missing_a_session_gets_no_averages_and_is_counted_and_the_rest_of_the_night_does()
    {
        DateOnly[] held = Weekdays(AsOf, IndicatorEngine.WarmupSessions + 10);
        DateOnly lost = held[^20];

        IndexSessions(held);

        Member("GAP");
        Bars("GAP", held.Where(d => d != lost), LongAgo);
        Refetch("GAP", through: held[0], at: LongAgo);
        Snapshot("GAP");

        Member("WHOLE");
        Bars("WHOLE", held, LongAgo);
        Refetch("WHOLE", through: held[0], at: LongAgo);
        Snapshot("WHOLE");

        IndicatorResult result = Engine().Compute(AsOf);

        Assert.Equal(1, result.MissingASession);
        Assert.Equal(1, result.Computed);
        Assert.Equal(0, result.ShortOfWarmup);
        Assert.Equal(["WHOLE"], IndicatorTickers());
    }

    [Fact]
    public void A_member_missing_a_session_the_vendor_was_asked_for_and_did_not_have_is_averaged()
    {
        DateOnly[] held = Weekdays(AsOf, IndicatorEngine.WarmupSessions + 10);
        DateOnly lost = held[^20];

        IndexSessions(held);

        Member("HALT");
        Bars("HALT", held.Where(d => d != lost), LongAgo);
        Refetch("HALT", through: held[0], at: LongAgo);
        Snapshot("HALT");

        Assert.Equal(1, Engine().Compute(AsOf).MissingASession);

        // The evening's refetch asked for the whole series and the day did not come back.
        _clock.Advance(TimeSpan.FromMinutes(10));
        Refetch("HALT", through: AsOf, at: _clock.UtcNow);
        _clock.Advance(TimeSpan.FromMinutes(10));

        IndicatorResult after = Engine().Compute(AsOf);

        Assert.Equal(0, after.MissingASession);
        Assert.Equal(1, after.Computed);
    }

    // ---- the order the evening runs in -------------------------------------------------------

    /// <summary>
    /// The index history is ingested before the slot that asks what is missing. On the evening after a
    /// night that never ran, the lost day is in the trackers only once `index-bars` has refetched them,
    /// so with the two the other way round the day would be unknown to the stage that buys it back and
    /// found by the averages instead, which would refuse the whole universe for that night.
    /// </summary>
    [Fact]
    public void The_index_history_is_ingested_before_the_evening_asks_which_sessions_are_missing()
    {
        string[] slots = [.. NightlySchedule.Slots.Select(s => s.Slot)];
        Assert.True(Array.IndexOf(slots, "index") < Array.IndexOf(slots, "rebuild"),
            "the index slot runs after the rebuild slot, so a lost day is unknown to the incomplete mode");

        IReadOnlyList<string> evening = NightlySchedule.Windows.Single(w => w.Window == "evening").Slots;
        Assert.True(evening.ToList().IndexOf("index") < evening.ToList().IndexOf("rebuild"),
            "the evening window runs the rebuild slot before the index slot");

        // And the day is invisible until the trackers hold it, which is why the order matters.
        DateOnly[] held = Weekdays(AsOf, 10);
        DateOnly lost = held[^2];

        IndexSessions(held.Where(d => d != lost));
        Member("AAA");
        Bars("AAA", held.Where(d => d != lost), LongAgo);

        Assert.Empty(Missing(LongAgo.AddYears(1)));

        IndexSessions([lost]);

        Assert.Equal([lost], Missing(LongAgo.AddYears(1))["AAA"]);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private DailyBarIngestor Ingestor(FakeMarketDataVendor vendor) =>
        new(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options());

    private IndicatorEngine Engine() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    private IReadOnlyDictionary<string, IReadOnlyList<DateOnly>> Missing(DateTimeOffset observedBefore)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return HistoryGapReader.Missing(connection, Options().Value.IndexSymbols, AsOf, IndicatorEngine.WarmupSessions, observedBefore);
    }

    /// <summary>
    /// <paramref name="count"/> members with every session but one, each refetched before it, and a
    /// vendor whose bulk answer for that day carries all of them.
    /// </summary>
    private (FakeMarketDataVendor Vendor, DateOnly Lost) MembersMissingOneDay(int count)
    {
        DateOnly[] held = Weekdays(AsOf, 5);
        DateOnly lost = held[2];
        IndexSessions(held);

        var vendor = new FakeMarketDataVendor();

        for (int i = 0; i < count; i++)
        {
            string ticker = "M" + i.ToString("D3", CultureInfo.InvariantCulture);
            Member(ticker);
            Bars(ticker, held.Where(d => d != lost), LongAgo);
            Refetch(ticker, through: held[0], at: LongAgo);
            vendor.Bar(lost, ticker, 10m, 1_000);
        }

        return (vendor, lost);
    }

    /// <summary>The last <paramref name="count"/> weekdays up to and including <paramref name="through"/>, oldest first.</summary>
    private static DateOnly[] Weekdays(DateOnly through, int count)
    {
        var days = new List<DateOnly>();
        for (DateOnly day = through; days.Count < count; day = day.AddDays(-1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(day);
            }
        }

        days.Reverse();
        return [.. days];
    }

    private void IndexSessions(IEnumerable<DateOnly> days)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        foreach (DateOnly day in days)
        {
            foreach (string symbol in Options().Value.IndexSymbols)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO index_bar (symbol, bar_date, open, high, low, close, adj_close, volume, observed_at)
                    VALUES (@symbol, @bar_date, '1', '1', '1', '1', '1', 1, @observed_at);
                    """;
                command.Parameters.AddWithValue("@symbol", symbol);
                command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(day));
                command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(LongAgo));
                command.ExecuteNonQuery();
            }
        }
    }

    private void Member(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01');
            INSERT INTO universe_member (ticker, added_on) VALUES (@t, '2020-01-01');
            """;
        command.Parameters.AddWithValue("@t", ticker);
        command.ExecuteNonQuery();
    }

    private void Snapshot(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO universe_snapshot (as_of, ticker) VALUES (@as_of, @t);";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        command.Parameters.AddWithValue("@t", ticker);
        command.ExecuteNonQuery();
    }

    private void Bars(string ticker, IEnumerable<DateOnly> days, DateTimeOffset observedAt)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        int i = 0;

        foreach (DateOnly day in days)
        {
            decimal close = 100m + (i++ % 7);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@t, @bar_date, @close, @high, @low, @close, @close, 1000000, @observed_at);
                """;
            command.Parameters.AddWithValue("@t", ticker);
            command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(day));
            command.Parameters.AddWithValue("@close", close.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@high", (close + 1m).ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@low", (close - 1m).ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Refetch(string ticker, DateOnly through, DateTimeOffset at)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO history_refetch (ticker, refetched_at, from_date, to_date, bars_written)
            VALUES (@t, @at, '2023-01-01', @to, 0);
            """;
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@at", StoreText.TimestampToStorageText(at));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(through));
        command.ExecuteNonQuery();
    }

    private string[] IndicatorTickers()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT ticker FROM indicator_daily ORDER BY ticker;";

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return [.. tickers];
    }
}
