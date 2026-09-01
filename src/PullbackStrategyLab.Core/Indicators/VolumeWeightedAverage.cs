namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The volume-weighted average price, as one implementation over a run of minutes.
///
/// In Core, and one implementation, for the reason <see cref="PullbackGeometry"/> is: the session
/// average annotates every stored minute and the anchored average decides a gate, so two
/// implementations would eventually disagree and the disagreement would be invisible. Every
/// quantity here is a plausible price whichever way it was computed.
///
/// <b>The price each minute contributes is its typical price, being high, low and close over
/// three.</b> That is what a volume-weighted average price means everywhere it is drawn, and the
/// alternative of weighting the close alone gives a different number with the same name. It is
/// stated here rather than left to whichever call site was written first, because a gate comparing
/// today's close against this level would move with the convention and nothing would say so.
///
/// <b>Prices are decimal and the weights are whole shares.</b> The accumulation is decimal
/// throughout, so a session of four hundred minutes does not drift the way a double would, and the
/// result is a price rather than a statistic.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
/// </summary>
public static class VolumeWeightedAverage
{
    /// <summary>
    /// What one minute contributes: the traded extremes, its close, and the shares behind them.
    ///
    /// A shape of its own rather than the stored row, so Core states the arithmetic without knowing
    /// what a store column is called.
    /// </summary>
    public readonly record struct Minute(
        DateTimeOffset OpenedAt, decimal High, decimal Low, decimal Close, long Volume);

    /// <summary>The price one minute is weighted at.</summary>
    public static decimal TypicalPrice(decimal high, decimal low, decimal close) =>
        (high + low + close) / 3m;

    /// <summary>
    /// The average over a run of minutes, or null where the run is empty or carries no volume at
    /// all.
    ///
    /// <b>Null rather than the unweighted mean on a run of no volume.</b> A name that did not trade
    /// has no volume-weighted price, and substituting the arithmetic mean would answer a different
    /// question with the same number. The one place this bites is a thin name early in a session,
    /// which is exactly where a gate reading the answer would be least entitled to it.
    /// </summary>
    public static decimal? Of(IEnumerable<Minute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        decimal weighted = 0m;
        long volume = 0;

        foreach (Minute minute in minutes)
        {
            // A minute with no shares contributes nothing and is not an error: the vendor sends
            // real prices with a volume of nought and 4.2 stores them deliberately.
            weighted += TypicalPrice(minute.High, minute.Low, minute.Close) * minute.Volume;
            volume += minute.Volume;
        }

        return volume == 0 ? null : weighted / volume;
    }

    /// <summary>
    /// The average as it stood at the end of each minute, in the order given.
    ///
    /// <b>This is what a per-minute column holds, and it is a point-in-time series rather than one
    /// figure.</b> A resolver walking a session asks what the average was at the minute it is
    /// standing on, and a single closing figure would answer with a number the session had not
    /// reached yet. Each entry is null until the run has carried any volume at all, on the same
    /// grounds <see cref="Of"/> is.
    /// </summary>
    public static IReadOnlyList<decimal?> Running(IReadOnlyList<Minute> minutes)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        var series = new List<decimal?>(minutes.Count);
        decimal weighted = 0m;
        long volume = 0;

        foreach (Minute minute in minutes)
        {
            weighted += TypicalPrice(minute.High, minute.Low, minute.Close) * minute.Volume;
            volume += minute.Volume;
            series.Add(volume == 0 ? null : weighted / volume);
        }

        return series;
    }

    /// <summary>
    /// The average over every minute at or after <paramref name="anchor"/>, which is the anchored
    /// form the short side's ceiling clause reads.
    ///
    /// <b>Inclusive of the anchor minute.</b> The anchor is the minute the swing high traded in, and
    /// the level being measured is the average price paid from that high onward, so the minute that
    /// made the high is part of what was paid. Excluding it would start the average one minute after
    /// the event it is named for.
    /// </summary>
    public static decimal? From(IEnumerable<Minute> minutes, DateTimeOffset anchor)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        return Of(minutes.Where(m => m.OpenedAt >= anchor));
    }
}
