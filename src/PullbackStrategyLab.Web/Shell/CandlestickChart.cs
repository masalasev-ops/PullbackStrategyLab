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
    /// <param name="levels">
    /// Prices to draw a horizontal line across the window at, which is empty on every chart of a
    /// stock and carries the trade's four on the strip beside a trade's minutes.
    ///
    /// <b>Added at 5.5 rather than a second component being written.</b> The daily strip a held
    /// position is drawn on needs the same four lines the minute picture has, and the alternative
    /// was passing them as flat averages, which would have made the legend call a stop an average.
    /// They widen the scale on exactly the terms an average does: a stop drawn outside the box is a
    /// line the reader silently loses.
    /// </param>
    public static CandlestickGeometry Lay(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<AverageLine> averages,
        int width,
        int height,
        IReadOnlyList<PriceLevel>? levels = null)
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

        foreach (PriceLevel level in levels ?? [])
        {
            low = Math.Min(low, level.Price);
            high = Math.Max(high, level.Price);
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
            IsEmpty: false,
            [.. (levels ?? []).Select(l => new LevelGeometry(l.Name, l.Price, Y(l.Price, low, high, plotHeight)))]);
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

    /// <summary>
    /// Lays a session's minutes out inside a box, with horizontal lines across it at named prices.
    ///
    /// <b>A second entry point rather than a widening of the one above, and the axis is why.</b>
    /// <see cref="Lay"/> carries a <c>DateOnly</c> through to every bar, because a daily chart's
    /// x-axis is a calendar. A minute chart's is a clock, and giving every bar of one session the
    /// same date would put one label under a thousand candles. The scaling arithmetic is shared:
    /// both call the same <see cref="Y"/>, so the two pictures cannot disagree about where a price
    /// sits in a box.
    ///
    /// <b>The levels widen the scale, on exactly the terms an average does.</b> A stop drawn outside
    /// the box is a line the reader silently loses, and the whole point of this picture is that the
    /// four prices are visible against the session that reached or missed them.
    /// </summary>
    public static MinuteChartGeometry LayMinutes(
        IReadOnlyList<MinuteCandle> candles,
        IReadOnlyList<PriceLevel> levels,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, PriceGutter + 10);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, DateGutter + 10);

        if (candles.Count == 0)
        {
            return MinuteChartGeometry.Empty(width, height);
        }

        decimal low = candles.Min(c => c.Low);
        decimal high = candles.Max(c => c.High);

        foreach (PriceLevel level in levels)
        {
            low = Math.Min(low, level.Price);
            high = Math.Max(high, level.Price);
        }

        if (high == low)
        {
            low -= 0.5m;
            high += 0.5m;
        }

        double plotWidth = width - PriceGutter;
        double plotHeight = height - DateGutter;
        double step = plotWidth / candles.Count;
        double bodyWidth = Math.Max(1.0, step * 0.62);

        var bars = new List<MinuteGeometry>(candles.Count);

        for (int i = 0; i < candles.Count; i++)
        {
            MinuteCandle candle = candles[i];
            double centre = (i * step) + (step / 2);
            double top = Y(Math.Max(candle.Open, candle.Close), low, high, plotHeight);
            double bottom = Y(Math.Min(candle.Open, candle.Close), low, high, plotHeight);

            bars.Add(new MinuteGeometry(
                candle.At,
                centre,
                Y(candle.High, low, high, plotHeight),
                Y(candle.Low, low, high, plotHeight),
                top,
                Math.Max(1.0, bottom - top),
                candle.Close >= candle.Open,
                bodyWidth));
        }

        return new MinuteChartGeometry(
            width,
            height,
            plotWidth,
            plotHeight,
            low,
            high,
            bars,
            [.. levels.Select(l => new LevelGeometry(l.Name, l.Price, Y(l.Price, low, high, plotHeight)))],
            false);
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
    bool IsEmpty,
    IReadOnlyList<LevelGeometry>? Levels = null)
{
    /// <summary>The lines drawn across the window, which is empty on every chart that named none.</summary>
    public IReadOnlyList<LevelGeometry> Lines => Levels ?? [];

    /// <summary>
    /// A chart with nothing in it, which draws a message rather than an empty box. There is no
    /// store behind this component until 1.10, and a blank rectangle would read as a stock that
    /// did not move.
    /// </summary>
    public static CandlestickGeometry Empty(int width, int height) =>
        new(width, height, width - CandlestickChart.PriceGutter, height - CandlestickChart.DateGutter,
            0m, 0m, [], [], [], IsEmpty: true);
}

/// <summary>One minute of a session. Labelled by a clock rather than a calendar.</summary>
public sealed record MinuteCandle(string At, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>One price to draw a line across the session at, and what it is.</summary>
public sealed record PriceLevel(string Name, decimal Price);

public sealed record MinuteGeometry(
    string At,
    double Centre,
    double HighY,
    double LowY,
    double BodyTop,
    double BodyHeight,
    bool Up,
    double BodyWidth);

/// <summary>One level's line, at the y the same scaling put every candle at.</summary>
public sealed record LevelGeometry(string Name, decimal Price, double Y);

/// <summary>
/// A session's minutes laid out, with the lines across them.
///
/// <see cref="IsEmpty"/> draws a message rather than an empty box, on the terms the daily geometry
/// already sets: a blank rectangle reads as a stock that did not move.
/// </summary>
public sealed record MinuteChartGeometry(
    int Width,
    int Height,
    double PlotWidth,
    double PlotHeight,
    decimal Low,
    decimal High,
    IReadOnlyList<MinuteGeometry> Candles,
    IReadOnlyList<LevelGeometry> Levels,
    bool IsEmpty)
{
    public static MinuteChartGeometry Empty(int width, int height) =>
        new(width, height, width - CandlestickChart.PriceGutter, height - CandlestickChart.DateGutter,
            0m, 0m, [], [], true);
}
