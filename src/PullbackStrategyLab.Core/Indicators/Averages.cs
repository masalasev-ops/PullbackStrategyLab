namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The averages, as arithmetic. In Core because two components need the same numbers and only
/// one of them may write them down.
///
/// IndicatorEngine computes them nightly and is the sole writer of <c>indicator_daily</c>. The
/// read surface draws the same averages as lines across a chart, and a chart needs a value at
/// every session rather than the one value at the as-of date, which is a different shape of the
/// same computation. A second implementation of it in the read surface would be a chart drawn
/// from numbers the lab never acted on, and a chart is the one place where that disagreement is
/// invisible: two exponential averages seeded differently converge to the same place and differ
/// for a long time on the way, and both look like a moving average.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// Everything here is decimal. A three-for-two split is 1.5 exactly in decimal and is not in
/// binary floating point, and an average is not a statistic in the sense the hard rule means:
/// it is a price, and it is compared against prices.
/// </summary>
public static class Averages
{
    /// <summary>
    /// The exponential moving average, seeded on the simple average of the first
    /// <paramref name="period"/> values and then recursive.
    ///
    /// The seed is a choice rather than a law and it is stated here because it is the single
    /// most common reason two correct implementations disagree. Seeding on the first value
    /// instead converges to the same place and differs for a long time on the way, which is
    /// exactly the sort of difference that is invisible in a chart and fatal in a comparison.
    /// </summary>
    public static decimal Exponential(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(values.Count, period);

        decimal average = 0m;
        for (int i = 0; i < period; i++)
        {
            average += values[i];
        }

        average /= period;

        decimal multiplier = 2m / (period + 1);
        for (int i = period; i < values.Count; i++)
        {
            average += (values[i] - average) * multiplier;
        }

        return average;
    }

    /// <summary>
    /// The average at every session, each one computed over the same trailing window the engine
    /// uses, for drawing. Null at a session with fewer than <paramref name="window"/> values
    /// behind it, because a value before then is the seed rather than the average, and a zero
    /// would be drawn at the bottom of a chart and read as a price.
    ///
    /// Recomputed per session rather than carried forward, and the difference is the whole
    /// reason this is here. A single running average over the longer window a chart reads is
    /// seeded in a different place from the one the engine computes over its warm-up, and the
    /// two differ for a long time on the way to the same place. Drawn live over 210 sessions
    /// against an engine reading 150, the nine and twenty-one day lines agreed to four decimals
    /// and the fifty-day line was 343.2979 against 343.3746 stored: a line that looks like a
    /// moving average, drawn from a number the lab never acted on.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    ///
    /// So the value at each session is what <see cref="Exponential"/> returns for the window
    /// ending there, which makes the last point of the line and the number the engine stored the
    /// same number by construction. A test asserts it rather than this comment claiming it.
    /// </summary>
    public static IReadOnlyList<decimal?> ExponentialSeries(IReadOnlyList<decimal> values, int period, int window)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, period);

        var series = new decimal?[values.Count];

        for (int end = window; end <= values.Count; end++)
        {
            series[end - 1] = Exponential(Slice(values, end - window, window), period);
        }

        return series;
    }

    /// <summary>A view of one window, so the series does not copy the whole history per session.</summary>
    private static IReadOnlyList<decimal> Slice(IReadOnlyList<decimal> values, int from, int count) =>
        values is decimal[] array
            ? new ArraySegment<decimal>(array, from, count)
            : [.. values.Skip(from).Take(count)];

    /// <summary>
    /// Wilder's average true range. True range is the greatest of the day's own range, the gap
    /// up from yesterday's close and the gap down to it, so a stock that opens ten percent away
    /// and does not move all day has a large true range and a small daily range.
    ///
    /// Wilder's smoothing, not an exponential average with the same period: they are different
    /// numbers and only one of them is what ATR has meant since 1978. The seed is the simple
    /// average of the first <paramref name="period"/> true ranges.
    /// </summary>
    public static decimal Wilder(
        IReadOnlyList<decimal> high,
        IReadOnlyList<decimal> low,
        IReadOnlyList<decimal> close,
        int period)
    {
        ArgumentNullException.ThrowIfNull(high);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentOutOfRangeException.ThrowIfLessThan(close.Count, period + 1);

        // The first bar has no previous close, so it has no true range. The series starts at the
        // second bar, which is why this needs one more session than its period.
        var trueRange = new decimal[close.Count - 1];
        for (int i = 1; i < close.Count; i++)
        {
            decimal previous = close[i - 1];
            decimal range = high[i] - low[i];
            decimal upGap = Math.Abs(high[i] - previous);
            decimal downGap = Math.Abs(low[i] - previous);
            trueRange[i - 1] = Math.Max(range, Math.Max(upGap, downGap));
        }

        decimal average = 0m;
        for (int i = 0; i < period; i++)
        {
            average += trueRange[i];
        }

        average /= period;

        for (int i = period; i < trueRange.Length; i++)
        {
            average = ((average * (period - 1)) + trueRange[i]) / period;
        }

        return average;
    }

    /// <summary>
    /// The middle value, or the mean of the two middle values. The median rather than the mean
    /// because one enormous session should not carry a stock over a liquidity floor it does not
    /// otherwise clear.
    /// </summary>
    public static decimal Median(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return 0m;
        }

        decimal[] sorted = [.. values];
        Array.Sort(sorted);

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2m;
    }
}
