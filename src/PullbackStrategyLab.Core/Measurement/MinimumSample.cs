using System.Globalization;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// How many observations band 1 needs before it is allowed to answer, from the arithmetic rather
/// than from a number somebody wrote down.
///
/// <b>The figure it replaces was an estimate wearing a derivation's clothes.</b> The corpus stated a
/// minimum of 160 paired setup observations "detecting about a two-point difference in ten-day
/// forward return", and read as a derived quantity from the day it was written. It was not: nothing
/// had measured the dispersion the calculation turns on, and nothing said whether the observations
/// were rows or independent ones. Both halves are fixed here, and the second is the one that moves
/// the answer, because ten-day labels overlap and rows are worth less than they look.
///
/// <b>Three inputs, and only one of them is measured.</b> The difference worth detecting and the
/// confidence are judgements that belong to a person; the dispersion is a fact about the market.
/// Naming which is which is the whole point of writing the arithmetic down rather than the number:
/// a later session can move the two judgements and watch the minimum move with them, and cannot
/// quietly move the fact.
/// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
/// </summary>
public static class MinimumSample
{
    /// <summary>
    /// The two-sided 95% critical value, which is the confidence the interval already carries.
    ///
    /// Not an independent choice: band 1 reads green when a 2.5th-to-97.5th percentile bound clears
    /// zero, so the sample sized against that bound uses the same tail. A minimum computed at one
    /// confidence and read at another is two instruments pretending to be one.
    /// </summary>
    public const double ZAlphaTwoSided95 = 1.959964d;

    /// <summary>
    /// The critical value for 90% power, ratified rather than conventional.
    ///
    /// <b>Power is the question "if the effect is really there, how often does this sample find
    /// it".</b> A sample sized on confidence alone controls only the false positive, so it can be
    /// arbitrarily small and still honest about what it claims, while finding a real effect almost
    /// never. Nothing in the corpus stated a power at all until this was ratified.
    ///
    /// <b>Ninety rather than the conventional eighty, because the costs here are asymmetric in an
    /// unusual direction.</b> A false positive is caught downstream: the forward paired test and the
    /// variant machinery both sit after band 1 and a spurious reading does not survive them. A false
    /// negative is caught by nothing, because band 1 reading flat means the pattern has nothing in it
    /// and the project stops. There is no downstream from that. At about eleven effective
    /// observations a night the extra power costs roughly six sessions, against a one-in-ten chance
    /// of abandoning a working strategy.
    ///
    /// <b>Named as ratified so a later session does not read it as a default.</b> Eighty is what
    /// would otherwise be assumed to have been meant, and the whole reason this constant carries a
    /// paragraph is that the convention was rejected rather than not considered.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    public const double ZBetaPower90 = 1.281552d;

    /// <summary>
    /// The minimum, in observations, for detecting <paramref name="detectableDifference"/> in the
    /// paired difference.
    ///
    /// <c>n = ((z_alpha + z_beta) * sigma / delta)^2</c>, the one-sample form, because the statistic
    /// is a difference already: pairing has turned two populations into one series tested against
    /// zero, so the two-sample factor of two does not belong here and putting it in would double the
    /// answer for nothing.
    ///
    /// <b>Rounded up rather than to nearest.</b> A fractional observation cannot be had, and rounding
    /// up is the direction that asks for more evidence. Rounding to nearest would be an authored step
    /// in a figure whose whole point is that no step in it is authored.
    /// </summary>
    public static int Of(double pairedDispersion, double detectableDifference, double zAlpha, double zBeta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pairedDispersion, 0d);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(detectableDifference, 0d);

        double scaled = (zAlpha + zBeta) * pairedDispersion / detectableDifference;

        return (int)Math.Ceiling(scaled * scaled);
    }

    /// <summary>The minimum at the corpus's own inputs, which is what the scoreboard reports against.</summary>
    public static int Of(double pairedDispersion) =>
        Of(pairedDispersion, MeasurementParameters.DetectableDifference, ZAlphaTwoSided95, ZBetaPower90);

    /// <summary>Six places, which is the precision the dispersion is measured and reported to.</summary>
    public static string Figure(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.000000", CultureInfo.InvariantCulture);
}
