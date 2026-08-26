using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The one candlestick component, as geometry rather than as markup.
///
/// Built here so no later checkpoint invents its own. The setup gallery at 2.9 pages through
/// hundreds of these and the chart page at 1.10 draws one large; two implementations of the
/// same drawing would disagree about which basis the prices are on, and that disagreement is
/// invisible in a picture.
///
/// The arithmetic is separated from the drawing on purpose. A view that computed its own
/// coordinates could only be checked by looking at it, and looking at a chart is exactly the
/// verification the phase report exists to replace.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public static class CandlestickChart
{
    /// <summary>Room for the price axis on the right, in the same units as the box.</summary>
    public const int PriceGutter = 56;

    /// <summary>Room for the date axis along the bottom.</summary>
    public const int DateGutter = 22;

    /// <summary>
    /// Lays a series out inside a box.
    ///
    /// The scale spans the lowest low to the highest high across the candles <b>and</b> every
    /// average drawn beside them, because an average that leaves the box is a line the reader
    /// silently loses. A flat series, where every price is the same, gets a scale of its own
    /// rather than a division by zero.
    /// </summary>
    public static CandlestickGeometry Lay(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<AverageLine> averages,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(averages);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, PriceGutter + 10);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, DateGutter + 10);

        if (candles.Count == 0)
        {
            return CandlestickGeometry.Empty(width, height);
        }

        decimal low = candles.Min(c => c.Low);
        decimal high = candles.Max(c => c.High);

        foreach (AverageLine average in averages)
        {
            foreach (decimal? value in average.Values.Where(v => v is not null))
            {
                low = Math.Min(low, value!.Value);
                high = Math.Max(high, value.Value);
            }
        }

        // A flat series has no range to scale against. Half a unit either side gives the box
        // something to draw in and puts the line through the middle, which is what a flat
        // series looks like.
        if (high == low)
        {
            low -= 0.5m;
            high += 0.5m;
        }

        double plotWidth = width - PriceGutter;
        double plotHeight = height - DateGutter;
        double step = plotWidth / candles.Count;

        // Bodies sit inside their slot with a gap either side, and never thinner than a hair,
        // so a year of sessions in a thumbnail still reads as candles rather than as a smear.
        double bodyWidth = Math.Max(1.0, step * 0.62);

        var bars = new List<CandleGeometry>(candles.Count);
        for (int i = 0; i < candles.Count; i++)
        {
            Candle candle = candles[i];
            double centre = (i * step) + (step / 2);

            double top = Y(Math.Max(candle.Open, candle.Close), low, high, plotHeight);
            double bottom = Y(Math.Min(candle.Open, candle.Close), low, high, plotHeight);

            bars.Add(new CandleGeometry(
                candle.Date,
                centre,
                Y(candle.High, low, high, plotHeight),
                Y(candle.Low, low, high, plotHeight),
                top,
                // A session that opened and closed at the same price has no body at all, and a
                // rectangle of zero height draws nothing. It gets a hairline instead, which is
                // what a doji is.
                Math.Max(1.0, bottom - top),
                candle.Close >= candle.Open,
                bodyWidth));
        }

        var lines = new List<AverageGeometry>(averages.Count);
        foreach (AverageLine average in averages)
        {
            var points = new List<string>();
            for (int i = 0; i < average.Values.Count && i < candles.Count; i++)
            {
                if (average.Values[i] is not decimal value)
                {
                    // A session before the average had converged. The line starts where the
                    // number starts rather than being drawn from a seed.
                    continue;
                }

                double x = (i * step) + (step / 2);
                double y = Y(value, low, high, plotHeight);
                points.Add(string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}"));
            }

            lines.Add(new AverageGeometry(average.Name, string.Join(' ', points), points.Count));
        }

        return new CandlestickGeometry(
            width,
            height,
            plotWidth,
            plotHeight,
            low,
            high,
            bars,
            lines,
            [.. PriceTicks(low, high, plotHeight)],
            IsEmpty: false);
    }

    /// <summary>
    /// A price as a distance down from the top of the plot.
    ///
    /// The one place a price stops being a decimal. It is a screen coordinate from here on,
    /// which is a length rather than a money value, and the crossing is named rather than
    /// implicit for the same reason every other crossing in this codebase is.
    /// </summary>
    private static double Y(decimal price, decimal low, decimal high, double plotHeight) =>
        (double)((high - price) / (high - low)) * plotHeight;

    /// <summary>
    /// Five labels up the right-hand side, on round numbers rather than on the extremes, so two
    /// charts of the same stock over different windows can be read against each other.
    /// </summary>
    private static IEnumerable<PriceTick> PriceTicks(decimal low, decimal high, double plotHeight)
    {
        const int Wanted = 5;

        decimal span = high - low;
        decimal rough = span / Wanted;
        decimal magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)rough)));

        // The parentheses are load-bearing. Without them C# reads this as rough divided by a
        // switch over magnitude, which is a different number that happens to look plausible:
        // the labels land on 100.36, 104.22, 108.08 and read as a chart with an odd scale
        // rather than as a defect.
        decimal stepSize = magnitude * ((rough / magnitude) switch
        {
            <= 1m => 1m,
            <= 2m => 2m,
            <= 5m => 5m,
            _ => 10m,
        });

        for (decimal price = Math.Ceiling(low / stepSize) * stepSize; price <= high; price += stepSize)
        {
            yield return new PriceTick(price, Y(price, low, high, plotHeight));
        }
    }
}

/// <summary>One session, on whichever basis the caller chose. The component does not adjust prices.</summary>
public sealed record Candle(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>
/// One average drawn over the candles, one value per session, null where it had not converged.
/// Null rather than zero: a zero would be drawn at the bottom of the box and read as a price.
/// </summary>
public sealed record AverageLine(string Name, IReadOnlyList<decimal?> Values);

public sealed record CandleGeometry(
    DateOnly Date,
    double Centre,
    double HighY,
    double LowY,
    double BodyTop,
    double BodyHeight,
    bool Up,
    double BodyWidth);

public sealed record AverageGeometry(string Name, string Points, int Drawn);

public sealed record PriceTick(decimal Price, double Y);

public sealed record CandlestickGeometry(
    int Width,
    int Height,
    double PlotWidth,
    double PlotHeight,
    decimal Low,
    decimal High,
    IReadOnlyList<CandleGeometry> Candles,
    IReadOnlyList<AverageGeometry> Averages,
    IReadOnlyList<PriceTick> PriceTicks,
    bool IsEmpty)
{
    /// <summary>
    /// A chart with nothing in it, which draws a message rather than an empty box. There is no
    /// store behind this component until 1.10, and a blank rectangle would read as a stock that
    /// did not move.
    /// </summary>
    public static CandlestickGeometry Empty(int width, int height) =>
        new(width, height, width - CandlestickChart.PriceGutter, height - CandlestickChart.DateGutter,
            0m, 0m, [], [], [], IsEmpty: true);
}
