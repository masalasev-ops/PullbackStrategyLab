using Microsoft.Extensions.Options;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the chart reads, and the one property that makes it worth reading: the last point of
/// every line the page draws is the number the engine stored.
///
/// It held on the first live run for the nine and twenty-one day lines and failed for the fifty,
/// 343.2979 drawn against 343.3746 stored, because the chart read a longer window than the
/// engine and a longer window seeds the average in a different place. Both looked like a moving
/// average. This is where that cannot happen quietly again.
/// see: The averages are one implementation, computed nightly and drawn on demand
/// </summary>
public sealed class LabChartTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 25);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero));

    public LabChartTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    /// <summary>
    /// A store holding one name with enough history to converge, priced so no two sessions are
    /// alike. A flat series would agree under any seed at all, which is the one shape that
    /// cannot show the defect this class exists for.
    /// </summary>
    private void Seed(string ticker = "TEST", int sessions = 260)
    {
        var vendor = new FakeMarketDataVendor();
        DateOnly date = AsOf;
        int written = 0;
        int step = 0;

        while (written < sessions)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                // A slow drift with a wobble on it, so the fifty-day average is still moving
                // where the nine-day one has settled.
                decimal close = 100m + (step * 0.35m) + (step % 7 * 1.4m);
                vendor.Bar(date, ticker, close - 0.4m, close + 1.1m, close - 1.3m, close, close, 4_000_000);
                written++;
                step++;
            }

            date = date.AddDays(-1);
        }

        vendor.Listing(ticker);

        new UniverseBuilder(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options())
            .BuildAsync(AsOf).GetAwaiter().GetResult();

        new DailyBarIngestor(vendor, _connections, new RunLogger(_clock, Options()), _clock, Options())
            .BackfillAsync(BackfillSelection.Named, [ticker], AsOf).GetAwaiter().GetResult();
    }

    private ChartResponse Read(string ticker, int sessions = 60) =>
        LabChart.Read(_connections, ticker, AsOf, sessions, _clock.UtcNow);

    [Fact]
    public void Every_line_the_page_draws_ends_on_the_number_the_engine_stored()
    {
        Seed();
        IndicatorResult computed = new IndicatorEngine(_connections, new RunLogger(_clock, Options()), _clock, Options())
            .Compute(AsOf);

        Assert.Equal(1, computed.Computed);

        ChartResponse chart = Read("TEST");

        Assert.NotNull(chart.Readout);
        Assert.Equal(60, chart.Drawn);

        decimal? Drawn(string name) => chart.Averages.Single(a => a.Name == name).Values[^1];

        // The property. Not "close enough": the same arithmetic over the same window is the
        // same decimal, and anything less means the page is drawn from numbers the lab did not
        // act on.
        Assert.Equal(chart.Readout!.Ema9, Drawn("ema9"));
        Assert.Equal(chart.Readout.Ema21, Drawn("ema21"));
        Assert.Equal(chart.Readout.Ema50, Drawn("ema50"));
    }

    [Fact]
    public void The_window_is_read_with_its_warm_up_behind_it_and_drawn_without_it()
    {
        Seed();
        ChartResponse chart = Read("TEST", sessions: 60);

        Assert.Equal(60, chart.Drawn);
        Assert.Equal(60 + LabChart.WarmupSessions, chart.Read);
        Assert.Equal(60, chart.Bars.Count);

        // Every drawn session carries a value, because the sessions that would not have one are
        // behind the left edge.
        Assert.All(chart.Averages, a => Assert.All(a.Values, v => Assert.NotNull(v)));
    }

    [Fact]
    public void A_ticker_the_store_has_never_held_is_answered_rather_than_refused()
    {
        Seed();
        ChartResponse chart = Read("NOSUCH");

        Assert.NotNull(chart.Nothing);
        Assert.Empty(chart.Bars);
        Assert.Null(chart.Readout);
    }

    [Fact]
    public void A_request_with_no_store_says_so()
    {
        using var empty = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(empty.Path));

        ChartResponse chart = LabChart.Read(connections, "TEST", AsOf, 60, _clock.UtcNow);

        Assert.Equal("there is no store yet", chart.Nothing);
    }

    [Fact]
    public void The_window_is_bounded_rather_than_answered_from_bars_nobody_has()
    {
        Seed();
        ChartResponse chart = Read("TEST", sessions: 10_000);

        // What was served is bounded, and what was asked for is reported as it was asked. This used
        // to assert Requested == MaximumSessions, which pinned in a response that could not say it
        // had truncated: the page offered a 750-session window, the surface drew 500, and the field
        // named Requested reported 500 as though that was the ask.
        Assert.Equal(10_000, chart.Requested);
        Assert.True(chart.Read <= LabChart.MaximumSessions + LabChart.WarmupSessions);
        Assert.True(chart.Requested > chart.Read, "the response cannot say the window was cut.");
    }

    [Fact]
    public void Prices_are_drawn_on_the_adjusted_basis()
    {
        Seed();

        // Every bar's high and low are put on the adjusted basis through that bar's own factor,
        // so the three prices belong to one scale. A chart mixing a raw high with an adjusted
        // close draws a high below its own close on a split name, and looks like a chart.
        ChartResponse chart = Read("TEST");

        Assert.All(chart.Bars, bar =>
        {
            Assert.True(bar.High >= bar.Close);
            Assert.True(bar.Low <= bar.Close);
        });
    }

    [Fact]
    public void The_series_and_the_single_value_are_the_same_computation()
    {
        // The property stated on the arithmetic itself rather than through the store, so a
        // failure points at the formula rather than at the reader.
        decimal[] values = [.. Enumerable.Range(0, 200).Select(i => 50m + (i * 0.3m) + (i % 5 * 0.7m))];

        IReadOnlyList<decimal?> series = Averages.ExponentialSeries(values, 50, window: 150);

        Assert.Equal(Averages.Exponential(values[^150..], 50), series[^1]);
        Assert.Equal(Averages.Exponential(values[^151..^1], 50), series[^2]);

        // Nothing before the window is full, because a value there is the seed rather than the
        // average.
        Assert.All(series.Take(149), v => Assert.Null(v));
        Assert.NotNull(series[149]);
    }
}
