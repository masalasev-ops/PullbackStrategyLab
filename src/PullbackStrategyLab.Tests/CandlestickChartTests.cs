using System.Globalization;
using PullbackStrategyLab.Web.Shell;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The shared chart's arithmetic.
///
/// Separated from the drawing so it can be asserted at all. A view that computed its own
/// coordinates could only be checked by looking at it, and a chart is the one thing where
/// looking is least reliable: a scale that clips an average, a body drawn upside down and a
/// series a hair out of place all look like a chart.
/// </summary>
public sealed class CandlestickChartTests
{
    private const int Width = 400;
    private const int Height = 200;

    private static readonly DateOnly Start = new(2026, 8, 3);

    private static Candle At(int session, decimal open, decimal high, decimal low, decimal close) =>
        new(Start.AddDays(session), open, high, low, close);

    [Fact]
    public void An_empty_series_says_so_rather_than_drawing_an_empty_box()
    {
        CandlestickGeometry chart = CandlestickChart.Lay([], [], Width, Height);

        Assert.True(chart.IsEmpty);
        Assert.Empty(chart.Candles);
        Assert.Empty(chart.PriceTicks);
    }

    [Fact]
    public void The_scale_spans_the_lowest_low_and_the_highest_high()
    {
        Candle[] candles =
        [
            At(0, 10m, 12m, 9m, 11m),
            At(1, 11m, 15m, 8m, 14m),
        ];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);

        Assert.Equal(8m, chart.Low);
        Assert.Equal(15m, chart.High);

        // The extremes sit on the edges of the plot, so nothing is drawn outside it.
        Assert.Equal(0d, chart.Candles[1].HighY, 6);
        Assert.Equal(chart.PlotHeight, chart.Candles[1].LowY, 6);
    }

    [Fact]
    public void An_average_outside_the_candles_widens_the_scale_rather_than_leaving_the_box()
    {
        Candle[] candles = [At(0, 10m, 12m, 9m, 11m), At(1, 11m, 12m, 10m, 11m)];
        AverageLine[] averages = [new("ema50", [4m, 4.5m])];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, averages, Width, Height);

        // A line that left the box would be silently lost, and a fifty-day average sitting well
        // below a pullback is the ordinary case rather than a corner one.
        Assert.Equal(4m, chart.Low);
        Assert.Equal(12m, chart.High);
    }

    [Fact]
    public void A_session_that_closed_up_is_marked_up_and_one_that_closed_down_is_not()
    {
        Candle[] candles = [At(0, 10m, 12m, 9m, 11m), At(1, 11m, 11.5m, 9m, 9.5m)];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);

        Assert.True(chart.Candles[0].Up);
        Assert.False(chart.Candles[1].Up);
    }

    [Fact]
    public void A_body_is_drawn_from_the_higher_of_open_and_close_downwards()
    {
        // Down session: the open is the top of the body. Drawn from the open rather than from
        // whichever of the two the caller listed first, or half the chart is upside down.
        Candle[] candles = [At(0, 12m, 13m, 8m, 9m)];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);
        CandleGeometry body = chart.Candles[0];

        double topOfBody = (double)((13m - 12m) / (13m - 8m)) * chart.PlotHeight;
        double bottomOfBody = (double)((13m - 9m) / (13m - 8m)) * chart.PlotHeight;

        Assert.Equal(topOfBody, body.BodyTop, 6);
        Assert.Equal(bottomOfBody - topOfBody, body.BodyHeight, 6);
    }

    [Fact]
    public void A_session_that_opened_and_closed_at_the_same_price_still_draws()
    {
        Candle[] candles = [At(0, 10m, 12m, 9m, 10m), At(1, 10m, 11m, 9m, 11m)];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);

        // A rectangle of zero height draws nothing at all, and a doji is a session that
        // happened rather than a session that is missing.
        Assert.True(chart.Candles[0].BodyHeight >= 1d);
    }

    [Fact]
    public void A_series_that_never_moved_does_not_divide_by_zero()
    {
        Candle[] candles = [At(0, 7m, 7m, 7m, 7m), At(1, 7m, 7m, 7m, 7m)];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);

        Assert.Equal(6.5m, chart.Low);
        Assert.Equal(7.5m, chart.High);
        Assert.All(chart.Candles, c => Assert.Equal(chart.PlotHeight / 2, c.HighY, 6));
    }

    [Fact]
    public void An_average_starts_where_its_values_start_rather_than_at_zero()
    {
        Candle[] candles = [At(0, 10m, 11m, 9m, 10m), At(1, 10m, 11m, 9m, 10m), At(2, 10m, 11m, 9m, 10m)];
        AverageLine[] averages = [new("ema50", [null, null, 10m])];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, averages, Width, Height);

        // A session before the average converged has no value, and a zero would be drawn at the
        // bottom of the box and read as a price.
        Assert.Equal(1, chart.Averages[0].Drawn);
        Assert.Equal(9m, chart.Low);
    }

    [Fact]
    public void Coordinates_are_written_invariantly()
    {
        Candle[] candles = [At(0, 10m, 11m, 9m, 10.5m), At(1, 10.5m, 11.5m, 10m, 11m)];
        AverageLine[] averages = [new("ema9", [10.2m, 10.6m])];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, averages, Width, Height);
        string points = chart.Averages[0].Points;

        // An SVG coordinate written under a comma-decimal culture is two numbers to the browser
        // and the chart falls apart silently. The pairs are comma separated, so a decimal comma
        // is not merely wrong, it is unparseable.
        Assert.Equal(2, points.Split(' ').Length);
        Assert.All(points.Split(' '), pair =>
        {
            string[] parts = pair.Split(',');
            Assert.Equal(2, parts.Length);
            Assert.All(parts, part => Assert.True(double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)));
        });
    }

    [Fact]
    public void A_thumbnail_of_a_year_still_draws_candles_rather_than_a_smear()
    {
        Candle[] candles = [.. Enumerable.Range(0, 250).Select(i => At(i, 10m, 11m, 9m, 10.5m))];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], 180, 90);

        Assert.Equal(250, chart.Candles.Count);
        Assert.All(chart.Candles, c => Assert.True(c.BodyWidth >= 1d));
    }

    [Fact]
    public void Price_labels_land_on_round_numbers_rather_than_on_the_extremes()
    {
        Candle[] candles = [At(0, 101.3m, 118.7m, 99.4m, 117m)];

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], Width, Height);

        // Two charts of the same stock over different windows have to be readable against each
        // other, which they are not if each labels its own high and low.
        Assert.NotEmpty(chart.PriceTicks);
        Assert.All(chart.PriceTicks, t => Assert.InRange(t.Price, chart.Low, chart.High));
        Assert.All(chart.PriceTicks, t => Assert.Equal(0m, t.Price % 5m));
    }
}
