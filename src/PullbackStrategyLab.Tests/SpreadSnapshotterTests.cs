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
/// The spread capture, and the four properties it exists to hold.
///
/// <b>The offset</b>, which is the minute bars' pairing arrived at from the other side: this stage
/// runs inside session N and samples the names capped on the evening of N-1, whose plans are live in
/// the session it is running in.
/// see: Minute bars are fetched for the session a plan was live in, never the session it was written on
///
/// <b>The population</b>, which is the capped set rather than every flagged name, and is the one
/// place the two intraday captures differ.
///
/// <b>The three shortfalls</b>, which are a session sampled once, a session sampled not at all, and
/// a pass that reached some of its names. They are different facts with different homes, and the
/// third is per name where the first two are per session.
///
/// <b>The absence of a figure that is not there.</b> A name the vendor answered with one side is
/// stored with a null spread and a reason, never with a zero: a spread of nought is a free entry and
/// it clears every threshold written as a maximum.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
/// </summary>
public sealed class SpreadSnapshotterTests : IDisposable
{
    /// <summary>The session being traded, and the evening before it, which capped the names.</summary>
    private static readonly DateOnly Session = new(2026, 8, 25);

    private static readonly DateOnly PriorSession = new(2026, 8, 24);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    /// <summary>Inside the session, which is when this stage runs, unlike every other vendor stage.</summary>
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, SpreadSnapshotter.AfterOpenSample, SessionBoundaries.UsEquities));

    public SpreadSnapshotterTests()
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

    private SpreadSnapshotter Snapshotter(FakeMarketDataVendor vendor, int dailyCallCeiling = 5000)
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(Options_(dailyCallCeiling));
        return new SpreadSnapshotter(vendor, _connections, new RunLogger(_clock, options), _clock, options);
    }

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

    /// <summary>
    /// A setup on one evening. <paramref name="cappedOut"/> is the column that decides whether this
    /// stage sees it at all, and it is stated on every row rather than defaulted, because the
    /// difference between the two populations is the thing under test.
    /// </summary>
    private void Setup(string ticker, DateOnly asOf, int cappedOut, string direction = "long")
    {
        Security(ticker);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out)
            VALUES (@id, @as_of, @ticker, @direction, '[]', 1, @capped_out);
            """;
        command.Parameters.AddWithValue("@id", $"{asOf:yyyy-MM-dd}-{ticker}-{direction}");
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@capped_out", cappedOut);
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset At(DateOnly session, TimeOnly local) =>
        SessionBoundaries.At(session, local, SessionBoundaries.UsEquities);

    // ---- the offset ----------------------------------------------------------------------

    [Fact]
    public async Task It_snapshots_the_names_capped_on_the_evening_before_the_session_it_runs_in()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);
        Setup("MSFT", PriorSession, cappedOut: 0);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 316.59m, 316.69m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 56)))
            .Quote("MSFT", 507.30m, 507.87m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        SpreadPassResult result = await Snapshotter(vendor)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(Session, result.SessionDate);
        Assert.Equal(PriorSession, result.SetupAsOf);
        Assert.Equal(2, result.Requested);
        Assert.Equal(2, result.Answered);
        Assert.Equal(2, result.Quoted);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        // Two names at one call each, which is the figure the 120 a session is derived from and the
        // one place a batch endpoint could have been mistaken for a batch price.
        Assert.Equal(2, result.CallsUsed);
    }

    [Fact]
    public void A_session_cannot_be_paired_with_the_setups_flagged_inside_it()
    {
        // The same refusal the minute bars carry, cited rather than reimplemented: a pass sampling
        // the names flagged on its own evening would be describing an entry cost for plans that do
        // not exist yet.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => IntradayFetcher.Pairing.Of(Session, Session));

        Assert.Contains("strictly before", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_before_which_nothing_was_flagged_records_a_pass_of_nothing()
    {
        SpreadPassResult result = await Snapshotter(new FakeMarketDataVendor())
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Null(result.SetupAsOf);
        Assert.Equal(0, result.Requested);
        Assert.Equal(RunOutcome.Clean, result.Outcome);
        Assert.Equal(SpreadSnapshotter.NoPriorSession, result.StoppedBecause);

        // The row is written anyway, which is what keeps a session that ran and asked for nothing
        // distinguishable from a session nobody sampled.
        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Single(SpreadSnapshotReader.PassesOf(connection, Session, Session));
    }

    // ---- the population ------------------------------------------------------------------

    [Fact]
    public async Task It_reads_the_capped_names_and_not_every_flagged_one()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);
        Setup("ZZZZ", PriorSession, cappedOut: 1);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 100m, 100.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)))
            .Quote("ZZZZ", 10m, 10.5m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        SpreadPassResult result = await Snapshotter(vendor)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(1, result.Requested);
        Assert.Equal(["AAPL"], vendor.QuoteBatchesRequested.Single());
    }

    [Fact]
    public async Task A_name_capped_on_both_sides_is_one_request()
    {
        Setup("AAPL", PriorSession, cappedOut: 0, direction: "long");
        Setup("AAPL", PriorSession, cappedOut: 0, direction: "short");

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 100m, 100.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        SpreadPassResult result = await Snapshotter(vendor)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(1, result.Requested);
        Assert.Equal(1, result.CallsUsed);
    }

    // ---- the three shortfalls ------------------------------------------------------------

    [Fact]
    public async Task A_session_sampled_once_is_degraded_and_says_which_pass_it_has()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 100m, 100.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        using SqliteConnection connection = _connections.OpenReadOnly();
        SessionSampling sampling = SpreadSnapshotReader.SamplingOf(connection, Session, Session);

        Assert.True(sampling.IsDegraded);
        Assert.False(sampling.IsComplete);
        Assert.False(sampling.IsUnsampled);
        Assert.Equal([SpreadSnapshotter.AfterOpenPass], sampling.Passes);

        // Degraded is not an error. One sample is a real answer and the reader returns it.
        SessionSpread spread = SpreadSnapshotReader.Read(connection, "AAPL", Session, Session);
        Assert.Single(spread.Usable);
    }

    [Fact]
    public async Task Both_passes_make_a_session_complete()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 100m, 100.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        _clock.Advance(SpreadSnapshotter.BeforeCloseSample.ToTimeSpan() - SpreadSnapshotter.AfterOpenSample.ToTimeSpan());
        vendor.Quote("AAPL", 100m, 100.4m, At(Session, new TimeOnly(15, 25)), At(Session, new TimeOnly(15, 25)));
        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.BeforeClosePass);

        using SqliteConnection connection = _connections.OpenReadOnly();
        SessionSpread spread = SpreadSnapshotReader.Read(connection, "AAPL", Session, Session);

        Assert.True(spread.Sampling.IsComplete);
        Assert.Equal(2, spread.Usable.Count);

        // The two samples disagree, which is the reason there are two: the morning reading alone
        // would have described this name's entry cost at a quarter of what it became.
        Assert.True(spread.Usable[0].SpreadBasisPoints < spread.Usable[1].SpreadBasisPoints);
    }

    [Fact]
    public void A_session_nobody_sampled_refuses_rather_than_answering_with_nothing()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => SpreadSnapshotReader.Read(connection, "AAPL", Session, Session));

        Assert.Contains("hole in the evidence", refused.Message, StringComparison.Ordinal);
        Assert.True(SpreadSnapshotReader.SamplingOf(connection, Session, Session).IsUnsampled);
    }

    [Fact]
    public async Task A_pass_stopped_by_the_ceiling_is_partial_and_says_how_far_it_got()
    {
        for (int at = 0; at < 3; at++)
        {
            Setup($"NAME{at}", PriorSession, cappedOut: 0);
        }

        var vendor = new FakeMarketDataVendor();
        for (int at = 0; at < 3; at++)
        {
            vendor.Quote($"NAME{at}", 10m, 10.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));
        }

        // A ceiling that pays for two of the three names.
        SpreadPassResult result = await Snapshotter(vendor, dailyCallCeiling: 2)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(SpreadSnapshotter.CeilingReached, result.StoppedBecause);
        Assert.Equal(3, result.Requested);
        Assert.Equal(2, result.Answered);

        // The names not reached are the arithmetic, which is why `requested` is what was asked for
        // rather than what came back.
        Assert.Equal(1, result.Requested - result.Answered);
    }

    [Fact]
    public async Task A_ceiling_that_cannot_pay_for_a_whole_batch_still_buys_the_names_it_can()
    {
        // The defect this asserts against: a batch is charged whole, so a fixed twenty asked against
        // a remainder of two is refused entire and two buyable spreads are lost with the budget to
        // buy them unspent. Recoverable inputs can afford that and this one cannot.
        for (int at = 0; at < EodhdClient.UsQuoteBatchSize + 5; at++)
        {
            Setup($"N{at:D3}", PriorSession, cappedOut: 0);
        }

        var vendor = new FakeMarketDataVendor();
        for (int at = 0; at < EodhdClient.UsQuoteBatchSize + 5; at++)
        {
            vendor.Quote($"N{at:D3}", 10m, 10.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));
        }

        int ceiling = EodhdClient.UsQuoteBatchSize - 3;
        SpreadPassResult result = await Snapshotter(vendor, ceiling)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        // One request, trimmed to the ceiling rather than refused for exceeding it, and every call
        // the day had left was spent on a name.
        Assert.Equal(ceiling, Assert.Single(vendor.QuoteBatchesRequested).Length);
        Assert.Equal(ceiling, result.Answered);
        Assert.Equal(RunOutcome.Partial, result.Outcome);
        Assert.Equal(SpreadSnapshotter.CeilingReached, result.StoppedBecause);
    }

    [Fact]
    public async Task It_batches_the_request_and_still_pays_per_name()
    {
        for (int at = 0; at < EodhdClient.UsQuoteBatchSize + 3; at++)
        {
            Setup($"N{at:D3}", PriorSession, cappedOut: 0);
        }

        var vendor = new FakeMarketDataVendor();
        for (int at = 0; at < EodhdClient.UsQuoteBatchSize + 3; at++)
        {
            vendor.Quote($"N{at:D3}", 10m, 10.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));
        }

        SpreadPassResult result = await Snapshotter(vendor)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(2, vendor.QuoteBatchesRequested.Count);
        Assert.Equal(EodhdClient.UsQuoteBatchSize, vendor.QuoteBatchesRequested[0].Length);
        Assert.Equal(3, vendor.QuoteBatchesRequested[1].Length);

        // Two requests, twenty-three calls. The saving is in round trips and not in the budget.
        Assert.Equal(EodhdClient.UsQuoteBatchSize + 3, result.CallsUsed);
    }

    // ---- what is stored, and what is deliberately not ------------------------------------

    [Fact]
    public async Task A_name_the_vendor_answers_with_one_side_is_stored_with_no_spread_and_a_reason()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", bid: 100m, ask: null, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        SpreadPassResult result = await Snapshotter(vendor)
            .SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        Assert.Equal(1, result.Answered);
        Assert.Equal(0, result.Quoted);
        Assert.Equal(1, result.Unquoted);

        using SqliteConnection connection = _connections.OpenReadOnly();
        SessionSpread spread = SpreadSnapshotReader.Read(connection, "AAPL", Session, Session);

        SpreadSample sample = Assert.Single(spread.Samples);
        Assert.Null(sample.SpreadBasisPoints);
        Assert.Equal(SpreadSnapshotter.NoBook, sample.AbsentBecause);
        Assert.Empty(spread.Usable);
    }

    [Fact]
    public async Task A_crossed_book_is_not_a_spread_of_nought()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        // The bid above the ask. A real state of a real feed, and not a free entry.
        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", bid: 100.20m, ask: 100.10m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));

        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        using SqliteConnection connection = _connections.OpenReadOnly();
        SessionSpread spread = SpreadSnapshotReader.Read(connection, "AAPL", Session, Session);

        Assert.Null(Assert.Single(spread.Samples).SpreadBasisPoints);
    }

    [Fact]
    public void The_spread_is_basis_points_of_the_mid()
    {
        var quote = new VendorQuote("AAPL", 316.59m, 316.69m, 1, 4, null, null, null, null);

        // 0.10 over a mid of 316.64, which is 3.158 basis points. The figure is checked against a
        // real quote rather than a round one: the capture of 2026-09-01 is where it comes from.
        Assert.Equal(3.158d, SpreadSnapshotter.SpreadBasisPoints(quote)!.Value, 3);
    }

    [Fact]
    public void The_lag_is_measured_from_the_older_side_of_the_book()
    {
        DateTimeOffset taken = At(Session, new TimeOnly(10, 15));
        var quote = new VendorQuote(
            "AAPL", 100m, 100.1m, 1, 1,
            BidAt: taken.AddMinutes(-20),
            AskAt: taken.AddMinutes(-1),
            LastTrade: null,
            LastTradeAt: null);

        // Twenty minutes, not one. A spread is only as fresh as its stalest half, and an ask stamped
        // a second ago against a four-minute-old bid is a four-minute-old spread.
        Assert.Equal(1200, SpreadSnapshotter.QuoteLagSeconds(quote, taken));
    }

    [Fact]
    public void A_quote_with_one_stamp_missing_has_no_lag_rather_than_a_lag_of_nought()
    {
        DateTimeOffset taken = At(Session, new TimeOnly(10, 15));
        var quote = new VendorQuote("AAPL", 100m, 100.1m, 1, 1, taken.AddMinutes(-5), null, null, null);

        Assert.Null(SpreadSnapshotter.QuoteLagSeconds(quote, taken));
    }

    [Fact]
    public async Task The_two_passes_are_different_rows_of_one_session()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        var vendor = new FakeMarketDataVendor()
            .Quote("AAPL", 100m, 100.1m, At(Session, new TimeOnly(9, 55)), At(Session, new TimeOnly(9, 55)));
        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.AfterOpenPass);

        _clock.Advance(TimeSpan.FromHours(5));
        await Snapshotter(vendor).SnapshotAsync(Session, SpreadSnapshotter.BeforeClosePass);

        using SqliteConnection connection = _connections.OpenReadOnly();
        SessionSpread spread = SpreadSnapshotReader.Read(connection, "AAPL", Session, Session);

        Assert.Equal(
            [SpreadSnapshotter.AfterOpenPass, SpreadSnapshotter.BeforeClosePass],
            spread.Samples.Select(s => s.Pass).ToArray());
    }

    [Fact]
    public async Task A_pass_named_as_anything_else_is_refused()
    {
        Setup("AAPL", PriorSession, cappedOut: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Snapshotter(new FakeMarketDataVendor()).SnapshotAsync(Session, "midday"));
    }

    [Fact]
    public void The_two_sample_times_bracket_the_session_and_avoid_both_auctions()
    {
        // The design, asserted rather than described. A sample inside the first fifteen minutes or
        // the last fifteen is measuring an auction rather than the name.
        Assert.True(SpreadSnapshotter.AfterOpenSample > SessionBoundaries.RegularSessionOpen.Add(TimeSpan.FromMinutes(15)));
        Assert.True(SpreadSnapshotter.BeforeCloseSample < SessionBoundaries.RegularSessionClose.Add(TimeSpan.FromMinutes(-10)));
        Assert.True(SpreadSnapshotter.AfterOpenSample < SpreadSnapshotter.BeforeCloseSample);
        Assert.Equal(2, SpreadSnapshotter.Samples.Count);
    }

    [Fact]
    public void The_budget_row_is_the_capped_set_times_the_two_samples()
    {
        // The 120 a session, derived here rather than stated. Sixty capped names, one call each,
        // twice: the endpoint takes a batch and prices it per name, so the figure does not move with
        // how the request is split.
        const int CappedNames = 60;
        Assert.Equal(120, CappedNames * SpreadSnapshotter.Samples.Count * EodhdClient.UsQuoteCost);
    }
}
