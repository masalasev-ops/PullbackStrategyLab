using System.Globalization;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// The interval around a paired difference, and the effective sample it is really built on.
///
/// <b>This is not the textbook case and the textbook interval is wrong in the direction that
/// matters.</b> Ten-day labels overlap, so adjacent nights share most of their window and
/// consecutive observations are serially correlated by construction. Same-night setups share a
/// market factor, so forty names flagged on one night rise and fall together over that fortnight.
/// Either alone makes an interval assuming independence too narrow. Together, band 1 clears zero
/// before it should, and band 1 is the project's central question. A too-narrow interval does not
/// produce a wrong number; it produces a confident one.
/// see: The interval is a block bootstrap over paired differences, and the effective sample is measured
///
/// <b>The statistic is the paired difference</b>, a setup's return minus the mean of its own matched
/// controls, which removes the shared market factor inside a night by construction rather than by
/// adjustment. The remaining serial overlap is carried by a moving-block bootstrap with a block at
/// least as long as the scoring horizon.
///
/// <b>Deterministic, with no seed anywhere.</b> The block offsets are mixed by two coprime strides
/// rather than drawn at random, so the same series gives the same interval on every machine and the
/// phase report can diff it. A seeded bootstrap would be a figure nobody could reproduce from the
/// store alone.
///
/// <b>Mixed rather than walked, and the difference is the whole thing.</b> Walking the offsets in
/// order makes every resample the same series rotated, a rotation preserves the mean, and the
/// interval comes back with zero width. An interval of no width clears zero always, which is the
/// failure this class exists to prevent arrived at from the opposite direction. It shipped that way
/// for one run at 3.5 and was caught because four authored series all returned low equal to high.
/// </summary>
public static class PairedInterval
{
    /// <summary>One night's mean paired difference, and how many pairs it was taken over.</summary>
    public sealed record Night(DateOnly Date, decimal MeanDifference, int Pairs);

    /// <summary>
    /// The interval, the point estimate, and both counts.
    ///
    /// <paramref name="Rows"/> and <paramref name="EffectiveObservations"/> are different quantities
    /// and both are reported. A minimum sample stated against this is counted in the second.
    /// </summary>
    public sealed record Estimate(
        decimal Mean, decimal Low, decimal High, int Rows, int Nights, int EffectiveObservations);

    /// <summary>
    /// The interval over a series of nightly means, or null where there is not enough to say
    /// anything.
    ///
    /// Null rather than a wide interval, because a panel that prints an interval from three nights
    /// invites a reading, and the failure mode this whole system exists to avoid is reading a
    /// pattern in forty observations.
    /// </summary>
    public static Estimate? Of(IReadOnlyList<Night> series, int blockSessions, int draws)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSessions, 1);

        if (series.Count < blockSessions * 2)
        {
            return null;
        }

        List<Night> ordered = [.. series.OrderBy(n => n.Date)];
        decimal mean = ordered.Average(n => n.MeanDifference);
        int rows = ordered.Sum(n => n.Pairs);

        // Blocks chosen with replacement, deterministically.
        //
        // <b>The obvious deterministic scheme is wrong and it fails silently.</b> The first version
        // here walked the block offsets in order, wrapping, so every resample was the same series
        // rotated. A rotation preserves the mean, so every draw returned the same number, the
        // percentiles collapsed onto it, and the interval came back with **zero width**. That is not
        // a small error: an interval of no width clears zero always, which is exactly the failure
        // this whole decision exists to prevent, reached from the opposite direction.
        //
        // So the offsets are mixed rather than walked. Two large coprime strides spread the draw and
        // block indices across the series, which samples with replacement, reproduces exactly on any
        // machine, and needs no seed to be carried anywhere.
        const int DrawStride = 7919;
        const int BlockStride = 104729;

        int blocks = ordered.Count / blockSessions;
        var means = new List<decimal>();

        for (int draw = 0; draw < draws; draw++)
        {
            decimal total = 0m;
            int taken = 0;

            for (int block = 0; block < blocks; block++)
            {
                int start = (int)((((long)draw * DrawStride) + ((long)block * BlockStride)) % ordered.Count);

                for (int i = 0; i < blockSessions; i++)
                {
                    total += ordered[(start + i) % ordered.Count].MeanDifference;
                    taken++;
                }
            }

            means.Add(total / taken);
        }

        means.Sort();

        return new Estimate(
            mean,
            Percentile(means, 0.025m),
            Percentile(means, 0.975m),
            rows,
            ordered.Count,
            EffectiveObservations(ordered));
    }

    /// <summary>
    /// How many independent observations the series is really worth, measured from the series
    /// rather than assumed.
    ///
    /// <b>The ratio is a property of the realised autocorrelation, not of the design.</b> A series
    /// whose nights are independent is worth its own length; one whose nights repeat themselves is
    /// worth far less, and the difference is exactly what an interval assuming independence throws
    /// away. Computed from the lag-one autocorrelation through the standard variance-inflation
    /// form, floored at one because a negative correlation does not buy extra observations that a
    /// reader should spend.
    ///
    /// <b>Any minimum sample stated against this is counted here, not in rows.</b> A pre-registered
    /// target reading "160 observations" is satisfiable by 160 rows carrying far less than 160
    /// observations' worth of information, and nothing on the surface says so.
    /// </summary>
    public static int EffectiveObservations(IReadOnlyList<Night> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        if (series.Count < 3)
        {
            return series.Count;
        }

        decimal mean = series.Average(n => n.MeanDifference);
        decimal variance = 0m;
        decimal covariance = 0m;

        for (int i = 0; i < series.Count; i++)
        {
            decimal centred = series[i].MeanDifference - mean;
            variance += centred * centred;

            if (i > 0)
            {
                covariance += centred * (series[i - 1].MeanDifference - mean);
            }
        }

        if (variance == 0m)
        {
            // Every night identical. There is one observation here however many nights there are,
            // and saying so is the honest answer rather than the flattering one.
            return 1;
        }

        decimal rho = covariance / variance;

        // The variance-inflation form: n_effective = n * (1 - rho) / (1 + rho). At rho of nought it
        // is n, and it falls away as the series repeats itself.
        decimal inflated = rho <= -1m
            ? series.Count
            : series.Count * (1m - rho) / (1m + rho);

        return Math.Max(1, Math.Min(series.Count, (int)Math.Round(inflated, MidpointRounding.AwayFromZero)));
    }

    private static decimal Percentile(IReadOnlyList<decimal> sorted, decimal fraction)
    {
        if (sorted.Count == 0)
        {
            return 0m;
        }

        int index = (int)Math.Floor(fraction * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Four places, the way every other figure in this system is printed.</summary>
    public static string Figure(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);
}
