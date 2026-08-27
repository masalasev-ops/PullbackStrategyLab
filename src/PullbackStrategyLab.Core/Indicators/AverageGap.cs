namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The distance between the two longer averages, session by session, and what the squeeze test
/// makes of it.
///
/// One implementation with two callers, on the terms the averages themselves already established.
/// ShortSetupDetector decides `averages-squeezing` on it and SignalVectorizer freezes it as
/// evidence, and a second implementation would eventually disagree with the first in a way nobody
/// could see: every figure here is a small ratio that looks reasonable whichever way it was
/// computed.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>Computed from bars rather than from stored indicator rows.</b> The engine writes one row a
/// session and a night it did not run leaves a hole; a mean over twenty rows would step over that
/// hole silently and average nineteen sessions while reporting twenty. Reading the bars means the
/// window is the window whether or not every night's job completed.
/// </summary>
public static class AverageGap
{
    /// <summary>The window the squeeze test compares against, and the one the contraction test uses.</summary>
    public const int Window = 20;

    /// <summary>
    /// The gap at every session the window can support, as a signed fraction of the longer average.
    ///
    /// Signed, because this is also the frozen signal and a proposal may want to know which way
    /// round the two averages sat. The squeeze test takes absolute values of its own and says why.
    /// Sessions before the warm-up produce no gap and are absent rather than zero.
    /// </summary>
    public static IReadOnlyList<decimal> Series(
        IReadOnlyList<decimal> closes,
        int mediumPeriod,
        int longPeriod,
        int warmup)
    {
        ArgumentNullException.ThrowIfNull(closes);

        IReadOnlyList<decimal?> medium = Averages.ExponentialSeries(closes, mediumPeriod, warmup);
        IReadOnlyList<decimal?> longer = Averages.ExponentialSeries(closes, longPeriod, warmup);

        var gaps = new List<decimal>();

        for (int i = 0; i < closes.Count; i++)
        {
            if (medium[i] is decimal m && longer[i] is decimal l && l != 0m)
            {
                gaps.Add((m - l) / l);
            }
        }

        return gaps;
    }

    /// <summary>The mean signed gap over the last <see cref="Window"/> sessions, or null.</summary>
    public static decimal? Average(IReadOnlyList<decimal> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        if (gaps.Count < Window)
        {
            return null;
        }

        decimal total = 0m;
        for (int i = gaps.Count - Window; i < gaps.Count; i++)
        {
            total += gaps[i];
        }

        return total / Window;
    }

    /// <summary>
    /// Today's gap over its own average across the window, both taken absolute, or null.
    ///
    /// <b>Absolute, and that is the whole subtlety.</b> This check runs on the short side only, where
    /// the 21-day sits below the 50-day and the signed gap is negative. Compared signed, "narrower"
    /// would read as "further below", which is the opposite rule: a squeeze would fail and a widening
    /// decline would pass, and both verdicts would look perfectly reasonable in the record. The gap
    /// the trader is describing is a distance, so a distance is what is compared.
    ///
    /// Below one is a squeeze. Null where the window is short of sessions or the mean distance is
    /// zero, which is a gate handed nothing rather than a gate that cleared.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public static decimal? SqueezeRatio(IReadOnlyList<decimal> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        if (gaps.Count < Window)
        {
            return null;
        }

        decimal total = 0m;
        for (int i = gaps.Count - Window; i < gaps.Count; i++)
        {
            total += Math.Abs(gaps[i]);
        }

        decimal average = total / Window;
        return average == 0m ? null : Math.Abs(gaps[^1]) / average;
    }
}
