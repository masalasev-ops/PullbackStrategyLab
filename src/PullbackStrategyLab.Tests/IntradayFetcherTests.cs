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
/// The minute bars, and the offset that decides which session's they are.
///
/// <b>The property these tests carry is the pairing.</b> Everything else here is ordinary ingestion
/// and would be caught by any careless change; the offset would not. A fetch aligned to the setup's
/// own session returns real bars of a real day for a real name, stores cleanly, and produces a
/// resolver that answers a plan from the prices the plan was computed from. Nothing downstream could
/// tell, which is why the pairing refuses rather than returning nothing.
/// see: Minute bars are fetched for the session a plan was live in, never the session it was written on
/// </summary>
public sealed class IntradayFetcherTests : IDisposable
{
    /// <summary>The session whose bars are fetched, and the evening before it, which flagged them.</summary>
    private static readonly DateOnly Session = new(2026, 8, 25);

    private static readonly DateOnly PriorSession = new(2026, 8, 24);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 26, 0, 30, 0, TimeSpan.Zero));

    public IntradayFetcherTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private PullbackStrategyLabOptions Options_(int ceiling) => new()
    {
        DataRoot = _root.Path,
        DailyCallCeiling = ceiling,
    };

    private IntradayFetcher Fetcher(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        IOptions<PullbackStrategyLabOptions> options = Microsoft.Extensions.Options.Options.Create(Options_(dailyCallCeiling));
        return new IntradayFetcher(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

    /// <summary>A name in the universe, so the bar table's foreign key has something to point at.</summary>
    private void Security(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) "
            + "ON CONFLICT (ticker) DO NOTHING;";
        command.Parameters.AddWithValue("@t", ticker);
        command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(PriorSession));
        command.ExecuteNonQuery();
    }

    /// <summary>A flagged setup on one session. Only the columns the fetcher reads are meaningful.</summary>
    private void Flagged(string ticker, DateOnly asOf, string direction = "long")
    {
        Security(ticker);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@id, @as_of, @ticker, @direction, '[]', 1);
            """;
        command.Parameters.AddWithValue("@id", $"{asOf:yyyy-MM-dd}-{ticker}-{direction}");
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", direction);
        command.ExecuteNonQuery();
    }

    /// <summary>An instant inside the regular session of a date, in the trading zone.</summary>
    private static DateTimeOffset At(DateOnly session, int hour, int minute) =>
        SessionBoundaries.At(session, new TimeOnly(hour, minute), SessionBoundaries.UsEquities);

    [Fact]
    public async Task It_fetches_the_session_that_just_closed_for_the_setups_flagged_the_evening_before()
    {
        Flagged("AAPL", PriorSession);
        Flagged("MSFT", PriorSession);

        var vendor = new FakeMarketDataVendor()
            .Minute("AAPL", At(Session, 9, 30), 100m)
            .Minute("AAPL", At(Session, 9, 31), 101m)
            .Minute("MSFT", At(Session, 9, 30), 400m);

        IntradayFetchResult result = await Fetcher(vendor).FetchAsync(Session);

        Assert.Equal(Session, result.SessionDate);
        Assert.Equal(PriorSession, result.SetupAsOf);
        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(3, result.BarsWritten);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        // Two names at five calls each. The cost is the point: it is what makes this row the second
        // largest consumer in the budget and what the ceiling is counted against.
        Assert.Equal(2 * EodhdClient.IntradayCost, result.CallsUsed);
    }

    [Fact]
    public async Task The_window_asked_for_is_the_session_that_closed_and_not_the_one_that_flagged_it()
    {
        Flagged("AAPL", PriorSession);

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m);
        await Fetcher(vendor).FetchAsync(Session);

        (string ticker, DateTimeOffset from, DateTimeOffset to) = Assert.Single(vendor.IntradayRequested);

        Assert.Equal("AAPL", ticker);

        // The whole of the session date in the trading zone, local midnight to local midnight. The
        // prior session's own midnight is strictly before `from`, which is the assertion that the
        // window did not slide back a day: a fetch aimed at the flagging session would return real
        // bars of a real day and nothing downstream could tell.
        Assert.Equal(SessionBoundaries.At(Session, TimeOnly.MinValue, SessionBoundaries.UsEquities), from);
        Assert.Equal(SessionBoundaries.At(Session.AddDays(1), TimeOnly.MinValue, SessionBoundaries.UsEquities), to);
        Assert.True(SessionBoundaries.At(PriorSession, TimeOnly.MinValue, SessionBoundaries.UsEquities) < from);
    }

    /// <summary>
    /// The fail-closed half, and the reason it is a refusal rather than an empty answer.
    ///
    /// Three cases, because there are three ways to get it wrong and only one of them looks wrong:
    /// the setups' own session, a session after the bars', and the boundary itself. All three refuse.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void A_fetch_paired_with_its_own_session_or_a_later_one_refuses(int daysAfter)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => IntradayFetcher.Pairing.Of(Session, Session.AddDays(daysAfter)));

        Assert.Contains("cannot resolve setups flagged", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fetch_paired_with_an_earlier_session_is_the_one_shape_that_is_formed()
    {
        IntradayFetcher.Pairing pairing = IntradayFetcher.Pairing.Of(Session, PriorSession);

        Assert.Equal(Session, pairing.SessionDate);
        Assert.Equal(PriorSession, pairing.SetupAsOf);
    }

    /// <summary>
    /// The same property one level up: the session the stage chooses is never the fetch's own, even
    /// when the store holds setups flagged on it.
    ///
    /// This is the case the fixture cannot exercise and the one that will happen every night from the
    /// second one onwards, because detection runs at 18:20 for the next session and this stage runs
    /// at 20:30 for the session that has just closed. Both sets of rows are in the table when it runs.
    /// </summary>
    [Fact]
    public async Task Setups_flagged_on_the_session_itself_are_not_the_ones_resolved()
    {
        Flagged("AAPL", PriorSession);
        Flagged("TSLA", Session);

        var vendor = new FakeMarketDataVendor()
            .Minute("AAPL", At(Session, 9, 30), 100m)
            .Minute("TSLA", At(Session, 9, 30), 200m);

        IntradayFetchResult result = await Fetcher(vendor).FetchAsync(Session);

        Assert.Equal(PriorSession, result.SetupAsOf);
        Assert.Equal(1, result.Requested);
        Assert.Equal("AAPL", Assert.Single(vendor.IntradayRequested).Ticker);
    }

    [Fact]
    public async Task The_first_night_asks_for_nothing_and_records_that_it_asked_for_nothing()
    {
        // Nothing flagged before this session at all, which is the state the golden fixture is in.
        Flagged("AAPL", Session);

        IntradayFetchResult result = await Fetcher(new FakeMarketDataVendor()).FetchAsync(Session);

        Assert.Null(result.SetupAsOf);
        Assert.Equal(0, result.Requested);
        Assert.Equal(0, result.CallsUsed);
        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(IntradayFetcher.NoPriorSession, result.StoppedBecause);

        // A row either way. A night with no row is a night nobody ran, and that is a different fact
        // from a night that ran and had nothing to ask for.
        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredIntradayFetch fetch = Assert.IsType<StoredIntradayFetch>(
            IntradayBarReader.LatestFetch(connection, Session, Session, SessionBoundaries.UsEquities));

        Assert.Equal(0, fetch.Requested);
        Assert.Equal(IntradayFetcher.NoPriorSession, fetch.StoppedBecause);
    }

    [Fact]
    public async Task A_name_flagged_both_ways_is_one_name_to_buy_minutes_for()
    {
        Flagged("AAPL", PriorSession, "long");
        Flagged("AAPL", PriorSession, "short");

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 9, 30), 100m);
        IntradayFetchResult result = await Fetcher(vendor).FetchAsync(Session);

        Assert.Equal(1, result.Requested);
        Assert.Equal(EodhdClient.IntradayCost, result.CallsUsed);
    }

    [Fact]
    public async Task Bars_are_labelled_with_the_session_window_they_fell_in()
    {
        Flagged("AAPL", PriorSession);

        var vendor = new FakeMarketDataVendor()
            .Minute("AAPL", At(Session, 7, 0), 99m)      // pre-market
            .Minute("AAPL", At(Session, 9, 30), 100m)    // the first regular minute
            .Minute("AAPL", At(Session, 15, 59), 105m)   // the last regular minute
            .Minute("AAPL", At(Session, 16, 0), 106m)    // the first after it
            .Minute("AAPL", At(Session, 18, 0), 107m);   // after hours

        await Fetcher(vendor).FetchAsync(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // The regular session is bounded on the bar's opening stamp, so 15:59 is inside it and 16:00
        // is not: 390 bars and not 391, which is the arithmetic every hourly grid has to answer for.
        IReadOnlyList<StoredIntradayBar> regular =
            IntradayBarReader.Read(connection, "AAPL", Session, Session, SessionBoundaries.UsEquities);
        Assert.Equal(2, regular.Count);
        Assert.All(regular, b => Assert.Equal(IntradayFetcher.RegularWindow, b.SessionWindow));

        // And nothing is dropped. An extended-hours minute is exactly as unrecoverable as a regular
        // one, so all five are stored and the reader bounds rather than the writer filtering.
        IReadOnlyList<StoredIntradayBar> everything =
            IntradayBarReader.Read(connection, "AAPL", Session, Session, SessionBoundaries.UsEquities, regularOnly: false);
        Assert.Equal(5, everything.Count);
        Assert.Equal(3, everything.Count(b => b.SessionWindow == IntradayFetcher.ExtendedWindow));
    }

    [Fact]
    public async Task Every_stored_bar_says_what_it_spans_and_what_basis_it_is_on()
    {
        Flagged("AAPL", PriorSession);

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m, 101m, 99m, 100.5m, 12_345);
        await Fetcher(vendor).FetchAsync(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredIntradayBar bar = Assert.Single(IntradayBarReader.Read(connection, "AAPL", Session, Session, SessionBoundaries.UsEquities));

        Assert.Equal(IntradayFetcher.MinuteInterval, bar.IntervalCode);
        Assert.Equal(IntradayFetcher.RawBasis, bar.PriceBasis);
        Assert.Equal(Session, bar.SessionDate);
        Assert.Equal(100m, bar.Open);
        Assert.Equal(101m, bar.High);
        Assert.Equal(99m, bar.Low);
        Assert.Equal(100.5m, bar.Close);
        Assert.Equal(12_345, bar.Volume);
    }

    [Fact]
    public async Task A_rerun_writes_nothing_where_the_vendor_has_not_moved()
    {
        Flagged("AAPL", PriorSession);

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m);

        IntradayFetchResult first = await Fetcher(vendor).FetchAsync(Session);
        IntradayFetchResult again = await Fetcher(vendor).FetchAsync(Session);

        Assert.Equal(1, first.BarsWritten);
        Assert.Equal(0, again.BarsWritten);
        Assert.Equal(1, again.Unchanged);
    }

    /// <summary>
    /// A vendor correction is a new row and the original stays, which is what append-only means for
    /// this table and is the same property `DailyBarIngestorTests` holds for the daily bars.
    /// </summary>
    [Fact]
    public async Task A_corrected_minute_arrives_as_a_new_row_and_the_original_stays()
    {
        Flagged("AAPL", PriorSession);

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m);
        await Fetcher(vendor).FetchAsync(Session);

        var corrected = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 101m);
        var later = new FixedClock(_clock.UtcNow.AddHours(2));
        IOptions<PullbackStrategyLabOptions> options = Microsoft.Extensions.Options.Options.Create(Options_(5000));

        IntradayFetchResult second = await new IntradayFetcher(
            corrected, _connections, new RunLogger(later, options), later, options).FetchAsync(Session);

        Assert.Equal(1, second.BarsWritten);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM intraday_bar WHERE ticker = 'AAPL'";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);

        // And the reader answers with the later observation, not with both.
        StoredIntradayBar latest = Assert.Single(IntradayBarReader.Read(connection, "AAPL", Session, Session, SessionBoundaries.UsEquities));
        Assert.Equal(101m, latest.Close);
    }

    [Fact]
    public async Task At_the_ceiling_it_stops_short_and_the_row_says_how_far_it_got()
    {
        foreach (string ticker in new[] { "AAA", "BBB", "CCC" })
        {
            Flagged(ticker, PriorSession);
        }

        var vendor = new FakeMarketDataVendor()
            .Minute("AAA", At(Session, 10, 0), 10m)
            .Minute("BBB", At(Session, 10, 0), 20m)
            .Minute("CCC", At(Session, 10, 0), 30m);

        // Two names' worth of allowance for three names.
        IntradayFetchResult result = await Fetcher(vendor, dailyCallCeiling: 2 * EodhdClient.IntradayCost)
            .FetchAsync(Session);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(3, result.Requested);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(IntradayFetcher.CeilingReached, result.StoppedBecause);

        // The shortfall is readable as requested against fetched, which is why both are on the row.
        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredIntradayFetch fetch = Assert.IsType<StoredIntradayFetch>(
            IntradayBarReader.LatestFetch(connection, Session, Session, SessionBoundaries.UsEquities));

        Assert.Equal("partial", fetch.Outcome);
        Assert.Equal(3, fetch.Requested);
        Assert.Equal(2, fetch.Fetched);
    }

    [Fact]
    public async Task A_name_the_vendor_holds_nothing_for_is_counted_rather_than_failing_the_night()
    {
        Flagged("AAPL", PriorSession);
        Flagged("HALT", PriorSession);

        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m);

        IntradayFetchResult result = await Fetcher(vendor).FetchAsync(Session);

        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(2, result.Fetched);
        Assert.Equal(1, result.Empty);
        Assert.Equal(1, result.BarsWritten);
    }

    /// <summary>
    /// The store's own bound, on the same terms as every other reader: a bar observed after the
    /// as-of is invisible until the as-of moves past it.
    /// </summary>
    [Fact]
    public async Task A_bar_observed_tomorrow_is_invisible_as_of_today_and_visible_as_of_tomorrow()
    {
        Flagged("AAPL", PriorSession);

        var tomorrow = new FixedClock(new DateTimeOffset(2026, 8, 27, 0, 30, 0, TimeSpan.Zero));
        IOptions<PullbackStrategyLabOptions> options = Microsoft.Extensions.Options.Options.Create(Options_(5000));
        var vendor = new FakeMarketDataVendor().Minute("AAPL", At(Session, 10, 0), 100m);

        await new IntradayFetcher(vendor, _connections, new RunLogger(tomorrow, options), tomorrow, options)
            .FetchAsync(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(IntradayBarReader.Read(connection, "AAPL", Session, new DateOnly(2026, 8, 25), SessionBoundaries.UsEquities));
        Assert.Single(IntradayBarReader.Read(connection, "AAPL", Session, new DateOnly(2026, 8, 27), SessionBoundaries.UsEquities));
    }
}
