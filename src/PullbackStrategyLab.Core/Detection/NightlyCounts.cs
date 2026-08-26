namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// The distribution of candidates per night, which is the only thing phase 2's thresholds are set
/// against.
///
/// No forward return exists anywhere in the store while this runs, so there is nothing to fit
/// toward: it is a row count and nothing else. What the count answers is whether the gates admit a
/// workable number of names a night, where too few is a lab with nothing to look at and too many is
/// a screen that has not screened.
/// see: Phase 2 thresholds are calibrated once against nightly counts, before phase 3
///
/// <b>Statistics are double here and the counts are int, deliberately.</b> A median of an even
/// number of nights is a half, and rounding it to keep one type would move a figure a threshold is
/// read against. Nothing in this file is a price.
/// </summary>
public static class NightlyCounts
{
    /// <summary>The fewest candidates a night the corpus calls workable.</summary>
    public const int BandLow = 5;

    /// <summary>The most.</summary>
    public const int BandHigh = 60;

    /// <summary>
    /// The five figures a distribution is read from, over one direction's nights.
    ///
    /// Quartiles rather than a mean and a deviation. The count is bounded below by zero and has no
    /// upper bound worth assuming, and a handful of violent sessions pulls a mean somewhere no night
    /// actually was.
    /// </summary>
    public static Distribution Of(IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        if (counts.Count == 0)
        {
            // Named rather than returned as zeros. A distribution over no nights is not a
            // distribution of nought candidates, and a threshold set against one would be set
            // against nothing at all.
            throw new ArgumentException(
                "a distribution over no nights says nothing, and a threshold read from it would be read from nothing",
                nameof(counts));
        }

        int[] sorted = [.. counts.Order()];

        return new Distribution(
            sorted.Length,
            sorted[0],
            sorted[^1],
            Quantile(sorted, 0.25),
            Quantile(sorted, 0.5),
            Quantile(sorted, 0.75),
            sorted.Sum(),
            sorted.Count(c => c == 0));
    }

    /// <summary>
    /// The linear-interpolation quantile, stated rather than left to a library.
    ///
    /// There are several conventions and they disagree on small samples, which is exactly the
    /// sample this runs on. Writing it out is what lets a second implementation restate the same
    /// figure and mean it.
    /// </summary>
    public static double Quantile(IReadOnlyList<int> sorted, double q)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        ArgumentOutOfRangeException.ThrowIfNegative(q);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(q, 1.0);

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        double position = q * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    /// <summary>
    /// A per-night count expressed as a rate per name, which is the figure that survives a change
    /// of universe.
    ///
    /// The band is stated for the live universe and the calibration run at 2.11 covered thirty
    /// names. A raw count from thirty compared against a band written for two thousand is a
    /// threshold set against a number that does not scale, so the run records the rate and the
    /// scaling is done here, in one place, where it can be read.
    /// </summary>
    public static double RatePerName(double count, int members)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(members);
        return count / members;
    }

    /// <summary>
    /// What a rate per name would produce over a universe of a given size.
    ///
    /// <b>An assumption, and named as one.</b> It supposes the thirty names the fixture holds flag
    /// at the same rate as the two thousand the lab screens, and nothing has checked that. The run
    /// over the live universe at 3.2 is what would replace it with a count.
    /// </summary>
    public static double ScaledTo(double ratePerName, int universeSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(universeSize);
        return ratePerName * universeSize;
    }

    /// <summary>Whether a figure sits inside the workable band, ends included.</summary>
    public static bool InsideTheBand(double candidatesPerNight) =>
        candidatesPerNight >= BandLow && candidatesPerNight <= BandHigh;

    /// <summary>What one direction's nights looked like.</summary>
    public sealed record Distribution(
        int Nights,
        int Lowest,
        int Highest,
        double LowerQuartile,
        double Median,
        double UpperQuartile,
        int Total,
        int EmptyNights);
}
