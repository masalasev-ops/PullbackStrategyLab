using System.Globalization;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The correction a pack is judged under: a false-discovery threshold admission is decided on, and
/// a family-wise threshold recorded beside it.
///
/// <b>Both, and the reason is that they are different claims rather than two views of one.</b> A
/// claim admitted under false-discovery control is not a claim that cleared family-wise control,
/// and a record keeping only the looser answer cannot be re-read under the stricter one later.
/// Carrying both costs one line in the pack and makes a later switch readable backwards over
/// everything already admitted.
/// see: Admission is on the false-discovery rate and the family-wise threshold is recorded beside it
///
/// <b>Screened, never shown.</b> Both thresholds are computed over the signals a pack screened,
/// which is every signal the conditional tables were built over including the planted null, and not
/// over the subset a proposal ends up citing. Computing over what was shown would let a pack screen
/// thirty signals, show three and pay the correction for three.
/// see: The correction threshold scales with signals screened, not signals shown
/// </summary>
public static class MultipleComparison
{
    /// <summary>
    /// The level both thresholds are taken at.
    ///
    /// Conventional, and stated here once so the two thresholds cannot drift apart: they are two
    /// corrections of one level, and a pack computing them from different levels would be reporting
    /// two answers to two questions nobody asked.
    /// </summary>
    public const double Level = 0.05;

    /// <summary>The correction the admission decision is taken under, named on every pack.</summary>
    public const string Form = "benjamini-hochberg";

    /// <summary>
    /// The family-wise threshold over <paramref name="screened"/> signals: Bonferroni, the level
    /// divided by the number screened.
    ///
    /// A single number rather than a step, because it does not depend on the p-values: it is the
    /// bar every claim must clear for the probability of any false claim in the family to stay
    /// under the level. That independence is what lets a pack state it before any claim exists,
    /// which is the state the lab is in and will be in for months.
    /// </summary>
    public static double? FamilyWiseThreshold(int screened) =>
        screened <= 0 ? null : Level / screened;

    /// <summary>
    /// The false-discovery threshold over <paramref name="screened"/> signals with p-values
    /// <paramref name="pValues"/>: the Benjamini-Hochberg step-up.
    ///
    /// <b>It depends on the p-values and the family-wise threshold does not, which is why a pack
    /// with no closed outcome can state one and not the other.</b> The step-up finds the largest
    /// rank k whose p-value is at or below k·level/m and admits every claim to that rank, so with
    /// no p-values there is no k and the answer is that nothing was admitted rather than that the
    /// threshold is <see cref="Level"/>. Returning the level there would state a bar no claim was
    /// measured against and would read on the page as a threshold in force.
    ///
    /// Where nothing clears, the answer is null and the pack says the section is empty and why. A
    /// zero would be indistinguishable from a threshold so strict nothing could pass.
    /// </summary>
    public static double? FalseDiscoveryThreshold(int screened, IReadOnlyList<double> pValues)
    {
        ArgumentNullException.ThrowIfNull(pValues);

        if (screened <= 0 || pValues.Count == 0)
        {
            return null;
        }

        // Sorted ascending with no tiebreak needed: the step-up reads values rather than identities,
        // and two equal p-values give the same answer in either order. Every other ordering in this
        // phase names a tiebreak because the order reaches the pack body; this one does not.
        double[] ascending = [.. pValues.OrderBy(p => p)];
        double? threshold = null;

        for (int i = 0; i < ascending.Length; i++)
        {
            double bar = (i + 1) * Level / screened;
            if (ascending[i] <= bar)
            {
                threshold = bar;
            }
        }

        return threshold;
    }

    /// <summary>
    /// The correction a pack states, over the signals it screened.
    ///
    /// The library size is carried on the reading rather than left to be inferred from the date,
    /// because the threshold moves with the library and a figure with no population beside it is
    /// the one this corpus refuses most often.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    public static CorrectionReading Read(int screened, IReadOnlyList<double> pValues) =>
        new(Form,
            Level,
            screened,
            FalseDiscoveryThreshold(screened, pValues),
            FamilyWiseThreshold(screened));
}

/// <summary>
/// What a pack states about the correction: the form, the level, how many signals were screened,
/// and the two thresholds.
///
/// Both thresholds are nullable and neither absence means the same thing. A null family-wise
/// threshold means nothing was screened. A null false-discovery threshold means nothing was
/// measured or nothing cleared, which is the state a pack cut before any outcome closes is in.
/// </summary>
public sealed record CorrectionReading(
    string Form,
    double Level,
    int SignalsScreened,
    double? FalseDiscoveryThreshold,
    double? FamilyWiseThreshold)
{
    /// <summary>
    /// A threshold rendered for the pack body, or the phrase that says there is not one.
    ///
    /// Fixed to nine decimal places under the invariant culture, because the family-wise threshold
    /// over a library of this size is 0.0015 and a general-purpose format would render it in
    /// exponent notation on one machine and not on another. The pack is compared byte for byte.
    /// </summary>
    public static string Render(double? threshold) =>
        threshold is null
            ? "none, nothing was measured against one"
            : threshold.Value.ToString("F9", CultureInfo.InvariantCulture);
}
