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
/// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
///
/// <b>The statistic is the paired difference</b>, a setup's return minus the mean of its own matched
/// controls, which removes the shared market factor inside a night by construction rather than by
/// adjustment. The remaining serial overlap is carried by a moving-block bootstrap with a block at
/// least as long as the scoring horizon.
///
/// <b>Deterministic, from a fixed published seed rather than from a scheme with no seed at all.</b>
/// The block starts are drawn from splitmix64 started at <see cref="Seed"/>, so the same series
/// gives the same interval on every machine and the phase report can diff it. Every operation is
/// IEEE-754 double addition, division and square root, all correctly rounded, so the two platforms
/// agree bit for bit. <b>An independent restatement in another language agrees to every place
/// printed rather than bit for bit</b>, and the difference is worth naming: CPython's built-in
/// <c>sum</c> has been compensated since 3.12, so the restatement in `tools/derive-indicators.py`
/// accumulates a slightly different rounding error. Four places is where the two are compared and
/// the gap sits far below it, but "agrees exactly" would be a claim neither side holds.
///
/// <b>The scheme this replaces was not a bootstrap and the way it failed is worth keeping.</b> It
/// mixed the block offsets by two coprime strides, which reads as spreading the draws across the
/// series and is not. Every start in draw <c>d</c> was the corresponding start in draw 0 shifted by
/// the same <c>d * 7919</c>, so **every draw was one fixed lattice rotated**, at most <c>N</c>
/// distinct resample means existed however many draws were asked for, and ten thousand draws was
/// bit-identical to <c>N</c> draws. On the five committed scenarios long enough to produce an
/// interval, of six in the fixture, the intervals came back two to three point seven times narrower
/// than a real moving-block bootstrap, worst on the AR(1) series written to exercise exactly the
/// serial overlap it got most wrong. **This is the third route to
/// the failure this class exists to prevent**: the first was walking the offsets in order, which
/// gave an interval of no width, the second was assuming independence, and this one wore the shape
/// of a fix for the first.
///
/// <b>Studentised rather than percentile, because independent block starts alone do not get there.</b>
/// Over 300 authored null series per row, all three schemes seeing the same series, at a nominal 5%:
/// the scheme this replaces clears zero 48.3% of the time at twenty independent nights and 46.0% at
/// forty; a percentile interval over correctly drawn blocks clears it 20.3% and 12.3%; studentising
/// each resampled mean by its own block-to-block standard error clears it 4.7% and 5.0%. With an
/// AR(1) of 0.7 the three read 78.7%, 37.3% and 6.0% at twenty nights. The quantity band 1 turns on
/// is whether a bound clears zero, so an interval that clears it four times too often is not a
/// narrower version of the right answer.
///
/// <b>Where it holds and where it does not, because the envelope is the honest part.</b> Studentising
/// clears zero 3.7% to 7.7% of the time over independent nights and an AR(1) up to 0.7, from twenty
/// to a hundred nights. Against the process a ten-session overlapping label actually creates, being a
/// moving average of order nine whose correlation cuts off inside the block length, it reads 3.0% to
/// 11.7% from twenty to two hundred and forty nights. Against an AR(1) of 0.9 it reads 7.0% to 24.0%,
/// and that is a limit of the block length rather than of the method: correlation at 0.9 runs well
/// past ten sessions and no block of ten absorbs it. If the realised series turns out to carry
/// dependence beyond the horizon, the block length is what has to move.
/// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
/// </summary>
public static class PairedInterval
{
    /// <summary>
    /// The seed the block draws start from, fixed and written down.
    ///
    /// A published constant rather than no seed at all. The scheme this replaces avoided a seed by
    /// making the draws a deterministic function of their own index, and that is what collapsed the
    /// resample space to one rotated lattice. Reproducibility is what a seed has to buy, and a
    /// constant in the source buys it: any reader can restate the interval from the store and this
    /// number, on any machine, in any language.
    /// </summary>
    public const ulong Seed = 0x5EED1F7UL;

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
    ///
    /// <b>Null also where the series cannot disperse.</b> A series whose blocks all carry the same
    /// mean has no standard error to studentise by, and the interval it would produce has no width.
    /// An interval of no width clears zero always, so it is withheld rather than shown, which is the
    /// one thing this class must never do quietly.
    /// </summary>
    public static Estimate? Of(IReadOnlyList<Night> series, int blockSessions, int draws)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(draws, 1);

        if (series.Count < blockSessions * 2)
        {
            return null;
        }

        List<Night> ordered = [.. series.OrderBy(n => n.Date)];
        int nights = ordered.Count;
        int blocks = nights / blockSessions;

        // Statistics are double, on the same grounds ForwardDispersion states: these are variances
        // of ratios rather than prices or money, the arithmetic needs a square root, and forcing
        // them through decimal would cost the ability to restate the figure in another tool. The
        // crossing happens here and at the return, and nowhere in between.
        double[] values = [.. ordered.Select(n => (double)n.MeanDifference)];

        decimal mean = ordered.Average(n => n.MeanDifference);
        int rows = ordered.Sum(n => n.Pairs);

        double observed = Mean(values);

        // The standard error of the observed mean, estimated the same way each resample's own is,
        // which is what makes the studentised ratio a ratio of like quantities.
        //
        // <b>A whole number of non-overlapping blocks, anchored at the recent end.</b> A resample's
        // error is the sample error of `blocks` block means drawn independently, so the matching
        // estimate on the observed series is the sample error of `blocks` non-overlapping block
        // means. Any such tiling leaves `n mod blockSessions` nights out of the scale estimate, and
        // that is a property of the estimator rather than an oversight: those nights still enter the
        // point estimate, the effective sample and every resample.
        //
        // Anchored at the end so the nights left out are the oldest. Taking the tiling from the
        // start instead was measured and calibrates identically, but it excludes the newest evidence,
        // which is the half a reader is watching.
        //
        // The obvious alternative, estimating over all `n` wrapping blocks, was measured and is
        // worse: overlapping block means spread wider than the draws do, so the interval comes back
        // conservative rather than calibrated, clearing zero 0.0% to 2.3% of the time under a true
        // null at twenty to forty nights against a nominal 5%.
        if (ObservedStandardError(values, blockSessions, blocks) is not double error || error <= 0d)
        {
            return null;
        }

        var ratios = new List<double>(draws);

        foreach ((double resampled, double? resampledError) in Resamples(values, blockSessions, blocks, draws))
        {
            if (resampledError is double scale && scale > 0d)
            {
                ratios.Add((resampled - observed) / scale);
            }
        }

        if (ratios.Count == 0)
        {
            // Every resample was internally flat, so nothing can be studentised. Withheld for the
            // same reason a zero-width interval is.
            return null;
        }

        ratios.Sort();

        // The tails swap: the upper quantile of the ratio gives the lower bound. Writing it the
        // other way produces an interval that looks ordinary and is reflected about the estimate,
        // which is the kind of error nothing downstream would catch.
        double low = observed - (Percentile(ratios, 0.975d) * error);
        double high = observed - (Percentile(ratios, 0.025d) * error);

        return new Estimate(
            mean,
            (decimal)low,
            (decimal)high,
            rows,
            nights,
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

    /// <summary>
    /// How many distinct resample means the scheme actually produces over a series.
    ///
    /// <b>Here because a class whose defect is invisible to itself is how the last one survived.</b>
    /// The scheme this replaces asked for ten thousand draws and produced at most one per night,
    /// every one of them the same lattice rotated, and nothing anywhere could say so: the intervals
    /// it returned were ordinary-looking numbers and the count that would have given it away was
    /// never computed. This is that count, exposed so a test can hold it rather than a reader having
    /// to reason about strides.
    ///
    /// A real bootstrap answers with a number that grows with the draws asked for. The rotation
    /// answered with the night count whatever it was asked, which is the assertion that would have
    /// failed on the day it shipped.
    /// </summary>
    public static int DistinctResampleMeans(IReadOnlyList<Night> series, int blockSessions, int draws)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSessions, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(draws, 1);

        if (series.Count < blockSessions * 2)
        {
            return 0;
        }

        double[] values = [.. series.OrderBy(n => n.Date).Select(n => (double)n.MeanDifference)];
        int blocks = values.Length / blockSessions;

        var seen = new HashSet<double>();

        foreach ((double resampled, double? _) in Resamples(values, blockSessions, blocks, draws))
        {
            seen.Add(resampled);
        }

        return seen.Count;
    }

    /// <summary>
    /// The resampled means and their own standard errors, one per draw.
    ///
    /// <b>Independent starts, one draw of the generator per block.</b> This is the whole correction.
    /// The starts within a draw are unrelated to each other and to the starts of every other draw,
    /// which is what makes the collection of resamples a sample of the resample space rather than
    /// one point in it seen from N angles. The scheme this replaces derived every start from the
    /// draw index by a fixed stride, so the space it sampled had N points in it however many draws
    /// it took.
    /// </summary>
    private static IEnumerable<(double Mean, double? Error)> Resamples(
        double[] values, int blockSessions, int blocks, int draws)
    {
        int nights = values.Length;
        ulong state = Seed;
        double[] blockMeans = new double[blocks];

        for (int draw = 0; draw < draws; draw++)
        {
            for (int block = 0; block < blocks; block++)
            {
                state = Next(state, out ulong drawn);
                int start = (int)(drawn % (ulong)nights);
                double total = 0d;

                for (int i = 0; i < blockSessions; i++)
                {
                    total += values[(start + i) % nights];
                }

                blockMeans[block] = total / blockSessions;
            }

            yield return (Mean(blockMeans), StandardError(blockMeans));
        }
    }

    /// <summary>
    /// One step of splitmix64, which is the whole generator.
    ///
    /// Chosen because it is four lines, has no state beyond one 64-bit word, and is restated
    /// identically in any language with 64-bit unsigned arithmetic. A generator a reader cannot
    /// reimplement in ten minutes would make the interval reproducible in principle and not in
    /// practice.
    /// </summary>
    private static ulong Next(ulong state, out ulong value)
    {
        state += 0x9E3779B97F4A7C15UL;

        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        value = z ^ (z >> 31);

        return state;
    }

    /// <summary>
    /// The standard error of the observed mean, over a whole number of non-overlapping blocks taken
    /// from the recent end of the series.
    ///
    /// The direct analogue of what each resample is scored by: <see cref="StandardError"/> over the
    /// <c>blocks</c> means a resample drew, and this over <c>blocks</c> means the series itself
    /// gives. The nights beyond the last whole block are the oldest ones and are excluded from this
    /// figure alone.
    /// </summary>
    private static double? ObservedStandardError(double[] values, int blockSessions, int blocks)
    {
        if (blocks < 2)
        {
            return null;
        }

        int offset = values.Length - (blocks * blockSessions);
        double[] means = new double[blocks];

        for (int block = 0; block < blocks; block++)
        {
            double total = 0d;

            for (int i = 0; i < blockSessions; i++)
            {
                total += values[offset + (block * blockSessions) + i];
            }

            means[block] = total / blockSessions;
        }

        return StandardError(means);
    }

    /// <summary>
    /// The standard error of a mean of block means, or null where there are too few to say.
    ///
    /// The sample form over the blocks, divided by their count, because the statistic being
    /// studentised is their mean rather than one of them.
    /// </summary>
    private static double? StandardError(double[] blockMeans)
    {
        if (blockMeans.Length < 2)
        {
            return null;
        }

        double mean = Mean(blockMeans);
        double sumSquares = 0d;

        foreach (double value in blockMeans)
        {
            double centred = value - mean;
            sumSquares += centred * centred;
        }

        return Math.Sqrt(sumSquares / (blockMeans.Length - 1) / blockMeans.Length);
    }

    private static double Mean(double[] values)
    {
        double total = 0d;

        foreach (double value in values)
        {
            total += value;
        }

        return total / values.Length;
    }

    private static int Clamp(decimal value, int rows) =>
        Math.Max(1, Math.Min(rows, (int)Math.Round(value, MidpointRounding.AwayFromZero)));

    private static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0d;
        }

        int index = (int)Math.Floor(fraction * (sorted.Count - 1));
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Four places, the way every other figure in this system is printed.</summary>
    public static string Figure(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);
}
