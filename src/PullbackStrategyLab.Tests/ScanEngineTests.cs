using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The six mover scans: what they rank on, which way, and on which price basis.
///
/// These are not the checkpoint's verification, which is the fixture diff against an independent
/// ranking. They are the properties a diff cannot state: that the basis is adjusted, that the
/// tiebreak is deterministic, and that a name short of the window is measured on nothing.
/// </summary>
public sealed class ScanEngineTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 26, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly AsOf = new(2026, 8, 26);

    public ScanEngineTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private ScanEngine Stage() => new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the basis --------------------------------------------------------------------------

    [Fact]
    public void A_split_inside_the_month_window_does_not_make_a_riser_the_biggest_laggard()
    {
        // The fixture's own case, written out so it is a property rather than a coincidence of
        // which names were captured. A vendor adjusts the history behind a split and leaves the
        // sessions after it alone, so the one-day magnitude on the split date is the same either
        // way and only the twenty-session magnitude spans the adjustment.
        //
        // RISER doubles in share count halfway through the window and is up 7% on the adjusted
        // basis. Read raw it is down 46%. FALLER is genuinely down 14% on both bases.
        Seed("RISER", split: true);
        Seed("FALLER", split: false);

        Stage().Scan(AsOf);

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<StoredScanHit> laggards = ScanHitReader.Read(connection, AsOf, "laggard", SessionBoundaries.UsEquities);
        IReadOnlyList<StoredScanHit> leaders = ScanHitReader.Read(connection, AsOf, "leader", SessionBoundaries.UsEquities);

        Assert.Equal("FALLER", laggards[0].Ticker);
        Assert.True(laggards[0].Magnitude < 0m);

        StoredScanHit riser = leaders.Single(h => h.Ticker == "RISER");
        Assert.True(riser.Magnitude > 0m,
            $"RISER's month magnitude is {riser.Magnitude}, which is the raw basis rather than the adjusted one");
    }

    [Fact]
    public void The_magnitudes_read_the_adjusted_basis_directly()
    {
        // The arithmetic, stated on its own, because the store test above can only show the
        // consequence. A factor of a half is a two-for-one split.
        Assert.Equal(0m, ScanMagnitudes.DailyChange(100m, 100m));
        Assert.Equal(0.10m, ScanMagnitudes.DailyChange(100m, 110m));

        // The open on the adjusted basis, through its own bar's factor rather than the previous
        // bar's. Using the previous bar's would be wrong on exactly the session a distribution
        // goes ex, which is the session the gap scan exists to notice.
        Assert.Equal(50m, ScanMagnitudes.OnTheAdjustedBasis(price: 100m, close: 200m, adjustedClose: 100m));
        Assert.Equal(100m, ScanMagnitudes.OnTheAdjustedBasis(price: 100m, close: 0m, adjustedClose: 100m));
    }

    // ---- ranking ----------------------------------------------------------------------------

    [Fact]
    public void Ties_break_on_ticker_so_the_boundary_is_deterministic()
    {
        // Two names with the same magnitude is unlikely on a real market day and certain on a
        // fixture. Without a stated second key the boundary of the top fifty would depend on the
        // order the store returned rows, which is a diff that fails on a platform.
        ScanEngine.Candidate[] tied =
        [
            new("ZZZZ", 0.05m, 0m, 0m),
            new("AAAA", 0.05m, 0m, 0m),
            new("MMMM", 0.05m, 0m, 0m),
        ];

        IReadOnlyList<(ScanEngine.Candidate Candidate, int Rank)> top = ScanEngine.Top(tied, "gainer");

        Assert.Equal(["AAAA", "MMMM", "ZZZZ"], top.Select(t => t.Candidate.Ticker));
        Assert.Equal([1, 2, 3], top.Select(t => t.Rank));
    }

    [Fact]
    public void Each_scan_ranks_the_opposite_way_from_its_mirror()
    {
        ScanEngine.Candidate[] candidates =
        [
            new("UP", 0.10m, 0.10m, 0.10m),
            new("DOWN", -0.10m, -0.10m, -0.10m),
        ];

        foreach ((string up, string down) in new[] { ("gainer", "decliner"), ("gapper", "gapdown"), ("leader", "laggard") })
        {
            Assert.Equal("UP", ScanEngine.Top(candidates, up)[0].Candidate.Ticker);
            Assert.Equal("DOWN", ScanEngine.Top(candidates, down)[0].Candidate.Ticker);
        }
    }

    [Fact]
    public void The_breadth_is_a_count_rather_than_a_threshold()
    {
        // Sixty names all moving, and exactly fifty are kept. The point of a rank cut is that the
        // count is the same on a quiet night and a violent one, which is what makes it calibratable
        // against nightly counts with no forward return in the store.
        ScanEngine.Candidate[] many =
            [.. Enumerable.Range(0, 60).Select(i => new ScanEngine.Candidate($"T{i:D3}", 0.01m * i, 0m, 0m))];

        Assert.Equal(ScanEngine.Breadth, ScanEngine.Top(many, "gainer").Count);
        Assert.Equal(ScanEngine.Breadth, ScanEngine.Top(many, "decliner").Count);
    }

    // ---- the window -------------------------------------------------------------------------

    [Fact]
    public void A_name_short_of_the_window_is_measured_on_nothing()
    {
        // Not ranked on a shorter window. A stock with three sessions of history has moved a long
        // way in all of them, so a scan that quietly shortened its window would put every recent
        // listing at the top of the month movers.
        Seed("YOUNG", split: false, sessions: 5);

        ScanResult result = Stage().Scan(AsOf);

        Assert.Equal(1, result.ShortOfHistory);
        Assert.Equal(0, result.Measured);
        Assert.Equal(0, result.Hits);
    }

    [Fact]
    public void A_second_run_over_the_same_night_writes_nothing()
    {
        Seed("RISER", split: false);

        ScanResult first = Stage().Scan(AsOf);
        ScanResult second = Stage().Scan(AsOf);

        Assert.True(first.Inserted > 0);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(first.Hits, second.AlreadyStored);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// A name with enough history to be measured, optionally with a two-for-one split halfway
    /// through the month window.
    ///
    /// The split is written the way a vendor publishes one: the adjusted close behind the ex-date
    /// is halved and the raw close is left alone, and from the ex-date forward the two agree. That
    /// asymmetry is the whole point of the test, so it is written out rather than approximated.
    /// </summary>
    private void Seed(string ticker, bool split, int sessions = ScanEngine.HistorySessions + 2)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
                """;
            security.Parameters.AddWithValue("@ticker", ticker);
            security.ExecuteNonQuery();
        }

        using (SqliteCommand snapshot = connection.CreateCommand())
        {
            snapshot.CommandText = "INSERT INTO universe_snapshot (as_of, ticker) VALUES (@as_of, @ticker)";
            snapshot.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            snapshot.Parameters.AddWithValue("@ticker", ticker);
            snapshot.ExecuteNonQuery();
        }

        // FALLER falls steadily. RISER rises steadily on the adjusted basis, and its raw price
        // doubles at the split so that reading it raw shows a collapse.
        int exDate = sessions - (ScanEngine.MonthWindow / 2);

        for (int i = 0; i < sessions; i++)
        {
            DateOnly date = AsOf.AddDays(i - sessions + 1);
            bool falling = ticker == "FALLER";
            decimal adjusted = falling ? 200m - i * 0.5m : 100m + i * 0.25m;
            decimal raw = split && i < exDate ? adjusted * 2m : adjusted;

            using SqliteCommand bar = connection.CreateCommand();
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@ticker, @bar_date, @open, @high, @low, @close, @adj, 1000000, @observed_at)
                """;
            bar.Parameters.AddWithValue("@ticker", ticker);
            bar.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(date));
            bar.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(raw));
            bar.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(raw + 1m));
            bar.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(raw - 1m));
            bar.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(raw));
            bar.Parameters.AddWithValue("@adj", StoreText.PriceToStorageText(adjusted));
            bar.Parameters.AddWithValue("@observed_at", "2026-08-26T20:00:00.000Z");
            bar.ExecuteNonQuery();
        }
    }
}
