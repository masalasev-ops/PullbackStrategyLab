using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The calibration minute backfill, from 7.7: which windows the plan lays out and which rows it leaves
/// short, and what the stage that buys them writes and where.
///
/// <b>The plan's cases are authored and derived by hand before they were run.</b> Sessions are the
/// weekdays, which is what a store with no holiday in the span holds.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class MinuteBackfillTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 11, 22, 0, 0, TimeSpan.Zero));

    public MinuteBackfillTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private static IReadOnlyList<DateOnly> Weekdays(DateOnly from, DateOnly through)
    {
        var days = new List<DateOnly>();
        for (DateOnly d = from; d <= through; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(d);
            }
        }

        return days;
    }

    private static readonly IReadOnlyList<DateOnly> Year = Weekdays(new DateOnly(2025, 6, 2), new DateOnly(2026, 8, 31));

    private static MinuteBackfillPlan.Row Row(string id, string ticker, DateOnly entry) => new(id, ticker, entry);

    // ---- the plan -------------------------------------------------------------------------

    [Fact]
    public void The_warm_up_is_eleven_sessions_and_a_window_is_one_hundred_and_twenty_days()
    {
        Assert.Equal(63, MinuteBackfillPlan.WarmupHourlyBars);
        Assert.Equal(11, MinuteBackfillPlan.WarmupSessions);

        // 2026-06-01 back 119 days is 2026-02-02, so the window holds 120 calendar days.
        Assert.Equal(new DateOnly(2026, 2, 2), MinuteBackfillPlan.WindowStart(new DateOnly(2026, 6, 1)));
    }

    /// <summary>
    /// The latest row ends a window and everything inside it is served by it; a row older than the
    /// window starts the next one. Two windows for three rows.
    /// </summary>
    [Fact]
    public void Rows_inside_a_window_share_it_and_an_older_row_starts_the_next()
    {
        MinuteBackfillPlan.Plan plan = MinuteBackfillPlan.Of(
            [
                Row("a", "AAA", new DateOnly(2026, 6, 1)),
                Row("b", "AAA", new DateOnly(2026, 5, 1)),
                Row("c", "AAA", new DateOnly(2026, 1, 15)),
            ],
            Year);

        Assert.Equal(2, plan.Windows.Count);

        MinuteBackfillPlan.Window older = plan.Windows[0];
        MinuteBackfillPlan.Window newer = plan.Windows[1];

        Assert.Equal(new DateOnly(2026, 1, 15), older.To);
        Assert.Equal(new DateOnly(2025, 9, 18), older.From);
        Assert.Equal(["c"], older.Rows.Select(r => r.SetupId));

        Assert.Equal(new DateOnly(2026, 6, 1), newer.To);
        Assert.Equal(["a", "b"], newer.Rows.Select(r => r.SetupId).Order(StringComparer.Ordinal));
        Assert.Empty(plan.Short);
    }

    /// <summary>
    /// A row landing two sessions after its window's start has two sessions of warm-up and wants
    /// eleven, and no earlier window reaches back behind it. Reported with nine missing.
    /// </summary>
    [Fact]
    public void A_row_landing_at_a_window_start_is_reported_with_its_shortfall()
    {
        MinuteBackfillPlan.Plan plan = MinuteBackfillPlan.Of(
            [
                Row("late", "AAA", new DateOnly(2026, 6, 1)),
                Row("early", "AAA", new DateOnly(2026, 2, 4)),
            ],
            Year);

        MinuteBackfillPlan.Window only = Assert.Single(plan.Windows);
        Assert.Equal(2, only.Rows.Count);

        MinuteBackfillPlan.Shortfall shortfall = Assert.Single(plan.Short);
        Assert.Equal("early", shortfall.Row.SetupId);
        Assert.Equal(new DateOnly(2026, 2, 2), shortfall.WindowFrom);
        Assert.Equal(2, shortfall.WarmupSessionsHeld);
        Assert.Equal(9, shortfall.Missing);
    }

    /// <summary>
    /// The same row, where an earlier window happens to end the day before this one starts: the run of
    /// bought sessions carries on into it, so the row is not short. An average needs an unbroken
    /// series, and that is what two adjacent windows give it.
    /// </summary>
    [Fact]
    public void Warm_up_carries_into_an_adjacent_earlier_window()
    {
        MinuteBackfillPlan.Plan plan = MinuteBackfillPlan.Of(
            [
                Row("late", "AAA", new DateOnly(2026, 6, 1)),
                Row("early", "AAA", new DateOnly(2026, 2, 4)),
                Row("adjacent", "AAA", new DateOnly(2026, 1, 30)),
            ],
            Year);

        Assert.Equal(2, plan.Windows.Count);
        Assert.Empty(plan.Short);
    }

    [Fact]
    public void Two_names_never_share_a_window()
    {
        MinuteBackfillPlan.Plan plan = MinuteBackfillPlan.Of(
            [
                Row("a", "AAA", new DateOnly(2026, 6, 1)),
                Row("b", "BBB", new DateOnly(2026, 6, 1)),
            ],
            Year);

        Assert.Equal(["AAA", "BBB"], plan.Windows.Select(w => w.Ticker));
    }

    // ---- the stage ------------------------------------------------------------------------

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path, DailyCallCeiling = 10 });

    private MinuteBackfiller Backfiller(IMarketDataVendor vendor) =>
        new(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options());

    private void Seed(IReadOnlyList<DateOnly> sessions, params (string Id, string Ticker, DateOnly AsOf)[] rows)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = "INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2025-01-01');";
            security.ExecuteNonQuery();
        }

        foreach (DateOnly date in sessions)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES ('AAA', @d, '100', '100', '100', '100', '100', 1000000, @obs);
                """;
            command.Parameters.AddWithValue("@d", StoreText.DateToStorageText(date));
            command.Parameters.AddWithValue(
                "@obs", StoreText.TimestampToStorageText(SessionBoundaries.At(date, new TimeOnly(18, 0), SessionBoundaries.UsEquities)));
            command.ExecuteNonQuery();
        }

        foreach ((string id, string ticker, DateOnly asOf) in rows)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO calibration_setup (setup_id, as_of, ticker, direction, check_results, passed_all)
                VALUES (@id, @as_of, @ticker, 'long', '[]', 0);
                """;
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@ticker", ticker);
            command.ExecuteNonQuery();
        }
    }

    private long Count(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The minutes land in the research table and never in the live capture table, the entry session is
    /// the next stored session, the calls sit outside the ceiling, and a second run buys nothing.
    /// </summary>
    [Fact]
    public async Task The_minutes_land_in_the_research_table_and_a_second_run_buys_nothing()
    {
        // Sessions through 2026-08-21, and a row flagged on 2026-08-20, so its entry is 2026-08-21.
        Seed(Weekdays(new DateOnly(2026, 3, 2), new DateOnly(2026, 8, 21)), ("r1", "AAA", new DateOnly(2026, 8, 20)));

        var vendor = new FakeMarketDataVendor()
            .Minute("AAA", SessionBoundaries.At(new DateOnly(2026, 8, 20), new TimeOnly(10, 0), SessionBoundaries.UsEquities), 100m)
            .Minute("AAA", SessionBoundaries.At(new DateOnly(2026, 8, 21), new TimeOnly(10, 0), SessionBoundaries.UsEquities), 101m)
            .Minute("AAA", SessionBoundaries.At(new DateOnly(2026, 8, 21), new TimeOnly(17, 0), SessionBoundaries.UsEquities), 102m);

        MinuteBackfillResult first = await Backfiller(vendor).BackfillAsync();

        Assert.Equal(1, first.Windows);
        Assert.Equal(1, first.WindowsBought);
        Assert.Equal(3, first.BarsWritten);
        Assert.Equal(2, first.SessionsAnswered);
        Assert.Equal(EodhdClient.IntradayCost, first.CallsUsed);

        (string _, DateTimeOffset from, DateTimeOffset to) = Assert.Single(vendor.IntradayRequested);
        Assert.Equal(SessionBoundaries.At(new DateOnly(2026, 4, 24), TimeOnly.MinValue, SessionBoundaries.UsEquities), from);
        Assert.Equal(SessionBoundaries.At(new DateOnly(2026, 8, 22), TimeOnly.MinValue, SessionBoundaries.UsEquities), to);

        Assert.Equal(3, Count("SELECT COUNT(*) FROM calibration_minute_bar;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM intraday_bar;"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM calibration_minute_bar WHERE session_window = 'extended';"));
        Assert.Equal(0, Count("SELECT counts_against_ceiling FROM run_log WHERE stage = 'backfill-minutes';"));

        MinuteBackfillResult second = await Backfiller(vendor).BackfillAsync();

        Assert.Equal(0, second.WindowsBought);
        Assert.Equal(1, second.WindowsAlreadyHeld);
        Assert.Equal(0, second.CallsUsed);
        Assert.Single(vendor.IntradayRequested);
    }

    /// <summary>
    /// The last row of a store has no stored session after it, so its entry is the next weekday, which
    /// is the plan's own rule for a live session on an evening that cannot see the next day.
    /// </summary>
    [Fact]
    public async Task A_row_on_the_last_stored_session_enters_on_the_next_weekday()
    {
        Seed(Weekdays(new DateOnly(2026, 3, 2), new DateOnly(2026, 8, 21)), ("r1", "AAA", new DateOnly(2026, 8, 21)));

        var vendor = new FakeMarketDataVendor();
        await Backfiller(vendor).BackfillAsync();

        (string _, DateTimeOffset _, DateTimeOffset to) = Assert.Single(vendor.IntradayRequested);
        Assert.Equal(SessionBoundaries.At(new DateOnly(2026, 8, 25), TimeOnly.MinValue, SessionBoundaries.UsEquities), to);
    }

    [Fact]
    public async Task A_dry_run_lays_the_windows_out_and_spends_nothing()
    {
        Seed(Weekdays(new DateOnly(2026, 3, 2), new DateOnly(2026, 8, 21)),
            ("r1", "AAA", new DateOnly(2026, 8, 20)),
            ("r2", "AAA", new DateOnly(2026, 4, 24)));

        var vendor = new FakeMarketDataVendor();
        MinuteBackfillResult dry = await Backfiller(vendor).BackfillAsync(dryRun: true);

        Assert.Equal(1, dry.Windows);
        Assert.Equal(1, dry.ShortRows);
        Assert.Empty(vendor.IntradayRequested);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM calibration_minute_shortfall;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM calibration_minute_window;"));
    }

    /// <summary>
    /// A row whose entry lands at its window's start is written with its shortfall. r2 is flagged on
    /// 2026-04-24, a Friday, so it enters on 2026-04-27, one session after the window starting on the
    /// Friday: one session held, ten missing.
    /// </summary>
    [Fact]
    public async Task A_short_row_is_written_with_what_it_has_and_what_it_wants()
    {
        Seed(Weekdays(new DateOnly(2026, 3, 2), new DateOnly(2026, 8, 21)),
            ("r1", "AAA", new DateOnly(2026, 8, 20)),
            ("r2", "AAA", new DateOnly(2026, 4, 24)));

        await Backfiller(new FakeMarketDataVendor()).BackfillAsync();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM calibration_minute_shortfall WHERE setup_id = 'r2' AND warmup_sessions = 1 AND warmup_wanted = 11;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM calibration_minute_shortfall WHERE setup_id = 'r1';"));
    }
}
