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
    /// <summary>
    /// One night's mean paired difference, how many pairs it was taken over, and how far apart those
    /// pairs were.
    ///
    /// <b>The third figure is what lets a night count as more than one observation.</b> Without it
    /// there is no way to tell a night whose eighty setups each said something from a night whose
    /// eighty setups all said the same thing, and the only safe reading of an unknown is the second.
    /// </summary>
    public sealed record Night(
        DateOnly Date, decimal MeanDifference, int Pairs, decimal WithinNightDispersion);

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
    /// <b>It starts from rows and not from nights, and that is what the pairing bought.</b> Forty
    /// names flagged on one night share a market factor, which is why an unpaired figure over them
    /// is worth about one observation however many names it has. The paired difference removes that
    /// factor by construction, so what is left inside a night is each name's own move against its own
    /// controls, and those are close to independent of each other. Counting a night as one
    /// observation would throw away exactly the thing the control draw was built to buy.
    ///
    /// <b>Two discounts are then applied, both measured.</b>
    ///
    /// The first is the label overlap across nights. A ten-session horizon means adjacent nights
    /// share most of their window, so the nightly means repeat each other; the lag-one
    /// autocorrelation through the standard variance-inflation form is what carries that, capped at
    /// one because a negative correlation does not buy extra observations a reader should spend.
    ///
    /// The second is whatever common movement the pairing failed to remove. If a night's pairs were
    /// really independent, the variance of that night's mean would be the within-night variance over
    /// the pair count; where the nightly means vary more than that, the excess is clustering the
    /// matching left behind, and the row count is divided by it. This is the ordinary design effect,
    /// and it makes the pessimistic reading the limiting case rather than the assumption: a night
    /// whose pairs all move together has a design effect of about its own pair count, and the whole
    /// series collapses back to one observation per night.
    ///
    /// <b>Where a night cannot say how its own pairs dispersed, it counts as one.</b> An unknown is
    /// read the safe way rather than the flattering one.
    ///
    /// <b>Any minimum sample stated against this is counted here, not in rows.</b> A target reading
    /// "196 observations" is satisfiable by 196 rows carrying far less than 196 observations' worth
    /// of information, and nothing on the surface would say so.
    /// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
    /// </summary>
    public static int EffectiveObservations(IReadOnlyList<Night> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        int nights = series.Count;
        int rows = series.Sum(n => n.Pairs);

        if (nights < 3)
        {
            // Too short for either discount to be measurable. A night counts as one, which is the
            // reading that cannot overstate. It is meaningless for the first fortnight and says so
            // by climbing from nothing rather than by being withheld.
            return Math.Min(rows, nights);
        }

        decimal mean = series.Average(n => n.MeanDifference);
        decimal sumSquares = 0m;
        decimal sumProducts = 0m;

        for (int i = 0; i < nights; i++)
        {
            decimal centred = series[i].MeanDifference - mean;
            sumSquares += centred * centred;

            if (i > 0)
            {
                sumProducts += centred * (series[i - 1].MeanDifference - mean);
            }
        }

        if (sumSquares == 0m)
        {
            // Every night identical. There is one observation here however many nights there are,
            // and saying so is the honest answer rather than the flattering one.
            return 1;
        }

        decimal rho = sumProducts / sumSquares;

        // n_effective scales by (1 - rho) / (1 + rho). At rho of nought it is unchanged, and it
        // falls away as the series repeats itself. Capped at one: a negative correlation is noise in
        // the estimate, not extra evidence.
        decimal serial = rho <= -1m ? 1m : (1m - rho) / (1m + rho);
        serial = Math.Clamp(serial, 0m, 1m);

        if (DesignEffect(series, sumSquares / (nights - 1)) is not decimal design || design <= 0m)
        {
            return Clamp(nights * serial, rows);
        }

        return Clamp(rows / design * serial, rows);
    }

    /// <summary>
    /// How much of the row count the within-night clustering costs, or null where nothing in the
    /// series can say.
    ///
    /// Compares the realised variance of the nightly means against the variance they would have if
    /// each night's pairs were independent of each other. Floored at one, because a series varying
    /// less than independence predicts has not found extra evidence, it has found noise in its own
    /// estimate.
    /// </summary>
    private static decimal? DesignEffect(IReadOnlyList<Night> series, decimal observedVariance)
    {
        decimal weighted = 0m;
        int degreesOfFreedom = 0;

        foreach (Night night in series)
        {
            if (night.Pairs < 2)
            {
                continue;
            }

            degreesOfFreedom += night.Pairs - 1;
            weighted += (night.Pairs - 1) * night.WithinNightDispersion * night.WithinNightDispersion;
        }

        if (degreesOfFreedom == 0 || weighted <= 0m)
        {
            // Either every night carries one pair, or no night's pairs disperse at all. Neither says
            // anything about clustering, so nothing is claimed and a night counts as one.
            return null;
        }

        decimal within = weighted / degreesOfFreedom;
        decimal expected = series.Average(n => within / n.Pairs);

        if (expected <= 0m)
        {
            return null;
        }

        return Math.Max(1m, observedVariance / expected);
    }

    private static int Clamp(decimal value, int rows) =>
        Math.Max(1, Math.Min(rows, (int)Math.Round(value, MidpointRounding.AwayFromZero)));

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
