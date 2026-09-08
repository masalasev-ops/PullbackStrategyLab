using System.Globalization;
using PullbackStrategyLab.Core.Measurement;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// What settles a version, derived rather than typed.
///
/// <b>The target is prose and the gate that settles against it cannot read a word of it.</b>
/// `variant.target` is a sentence an operator gives at registration, and this is the first component
/// that has to act on one. 5.1 made exactly this repair for `definition` and stopped there: a
/// selection version's definition is derived from the threshold it moves and may not be typed,
/// because a sentence somebody types can disagree with the columns beside it and the day it does
/// there is nothing to say which of the two the version is. The same is true of the target and it is
/// worse, because the target is the thing pre-registration exists to fix. So the rule is here, in one
/// place, and <see cref="Describe"/> is what a registration writes into the column, so the sentence
/// on the ledger is the rule the gate will run.
/// see: Targets and minimum samples are written at creation and are immutable
///
/// <b>It settles on expectancy and never on win rate.</b> The quantity is the mean paired difference
/// in ten-day forward return between what the version selected and what the baseline selected on the
/// same nights, and the interval around it is the studentised moving-block bootstrap the lab already
/// runs. Win rate is computed and reported beside it, and a version that raised the win rate while
/// lowering expectancy is rejected under that name rather than under a bound that failed to clear:
/// widening the stop does exactly that, and the two rejections are different facts about the version.
/// see: Acceptance measures expectancy, never win rate
///
/// <b>Nothing here decides on the calendar.</b> A version short of its pre-registered minimum is left
/// open with the shortfall stated, however long it has been open. A timeout would be a decision made
/// by the calendar rather than by evidence, and the failure table says so in those words.
/// </summary>
public static class AcceptanceTest
{
    /// <summary>
    /// What the register writes as a selection version's target, so the column says what the gate does.
    ///
    /// <b>Derived and not typed</b>, on `definition`'s terms. It states the quantity, the interval,
    /// the bound that accepts, the diagnostic that rejects on its own, and the sample the question
    /// may not be asked before, because a target that omits any of the five is a target two readers
    /// can settle differently.
    /// </summary>
    public static string Describe(int minimumSample, string minimumSampleUnit) =>
        "Accepted where the lower bound of the "
        + Count(MeasurementParameters.BootstrapDraws) + "-draw studentised moving-block interval around the "
        + "mean paired difference in " + Count(MeasurementParameters.ScoringHorizonSessions) + "-session forward "
        + "return, over the nights the two rules selected differently on, clears nought in the version's "
        + "favour. Rejected where it does not, and rejected under its own name where the version raised the "
        + "win rate while lowering expectancy. Neither asked before " + Count(minimumSample) + " "
        + Unit(minimumSampleUnit) + " have accumulated, and never settled by the calendar.";

    /// <summary>
    /// One night of one version's difference series, as the score row records it.
    ///
    /// <paramref name="BaselineScored"/> and <paramref name="VariantScored"/> are the rows each mean
    /// was actually taken over, which is not the same as what each rule selected: a selection whose
    /// forward return has not landed is in the second count and not the first. The win counts are
    /// over the scored rows, so the two rates have their own denominators on the row.
    /// </summary>
    public sealed record Night(
        DateOnly Date,
        decimal MeanDifference,
        int BaselineOnly,
        int VariantOnly,
        int BaselineScored,
        int VariantScored,
        int BaselineWins,
        int VariantWins)
    {
        /// <summary>
        /// The setups the two rules disagreed about, which is the population the night's difference
        /// rests on.
        ///
        /// <b>Not the flagged count and not either selection.</b> A name both rules picked is in both
        /// means, so it is not what the difference is about; the difference is about the names one
        /// rule took and the other did not.
        /// </summary>
        public int Disagreements => BaselineOnly + VariantOnly;
    }

    /// <summary>
    /// What the gate read of one version, whether or not it settled it.
    ///
    /// Written on every run rather than only on the run that settles, because a version that never
    /// matures is the case the failure table names and its record is the sequence of readings that
    /// says so.
    /// </summary>
    public sealed record Reading(
        int NightsScored,
        int NightsCarryingADifference,
        int NightsIdentical,
        int NightsInSeries,
        int Disagreements,
        int EffectiveObservations,
        int MinimumSample,
        string MinimumSampleUnit,
        bool Matured,
        decimal? Mean,
        decimal? Low,
        decimal? High,
        decimal? BaselineWinRate,
        decimal? VariantWinRate,
        string Verdict,
        string? SettledBecause,
        string? WithheldBecause,
        string Population);

    /// <summary>Recorded where the version has been scored on no night at all.</summary>
    public const string NothingScored =
        "the scorer has written no night for this version, so there is no series to take an interval "
        + "over. That is the ordinary state of a version registered inside the scoring horizon";

    /// <summary>
    /// Recorded where every scored night carries no difference or no disagreement.
    ///
    /// The two are one sentence because a reader wants the same act from both: wait. They are two
    /// counts on the row, because a night withheld for want of an outcome and a night on which the
    /// two rules picked the same names are different facts about the version.
    /// </summary>
    public const string NothingToDifference =
        "every night scored for this version either carries no figure or was a night the two rules "
        + "selected the same names on, so the series the interval would be taken over is empty";

    /// <summary>Recorded where the series is too short or too flat for the bootstrap to say anything.</summary>
    public const string NoInterval =
        "the minimum has been reached and the series cannot produce an interval: it is shorter than "
        + "twice the block length, or its blocks carry the same mean and there is no standard error "
        + "to studentise by. An interval of no width clears nought always, so it is withheld";

    /// <summary>The name a rejection takes where the version bought win rate with expectancy.</summary>
    public const string WinRateForExpectancy =
        "the version raised the win rate and lowered expectancy, which is what widening a stop does "
        + "and is rejected on its own terms rather than on a bound that failed to clear";

    /// <summary>
    /// The population every figure in a reading is over, stated in the same breath as the figures.
    ///
    /// <b>It is not the flagged population and it is not either selection.</b> A night on which the
    /// two rules selected identically carries a difference of exactly nought by construction, and
    /// counting it would drive the mean toward nought with nights the version was never exercised on
    /// while advancing the sample it is settled at.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    public static string PopulationOf(string direction, int nights) =>
        "the " + direction + " side of this version alone, over the " + Count(nights) + " scored night(s) on "
        + "which its rule and the baseline's selected different names. A night they selected identically on "
        + "carries a difference of exactly nought by construction and is not an observation of the version";

    /// <summary>
    /// The reading, and the verdict that follows from it.
    ///
    /// <b>A night is one paired observation and the store cannot say otherwise.</b> The score row is
    /// a difference of two means over two overlapping selections, so there is no per-name pairing to
    /// take and no within-night dispersion to record: what is paired is the night, which migration
    /// 052 says in those words. <see cref="PairedInterval.Disperse"/> reads a series that cannot say
    /// how its own pairs dispersed as one observation a night, which is the reading that cannot
    /// overstate, so a version's effective count rises at most one a night and never at the rate the
    /// minimum was derived at.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    public static Reading Of(
        string direction,
        int minimumSample,
        string minimumSampleUnit,
        IReadOnlyList<Night> scored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(scored);

        int carrying = scored.Count(n => n.BaselineScored > 0 && n.VariantScored > 0);
        int identical = scored.Count(n => n.Disagreements == 0);

        IReadOnlyList<Night> exercised =
            [.. scored.Where(n => n.Disagreements > 0 && n.BaselineScored > 0 && n.VariantScored > 0)];

        IReadOnlyList<PairedInterval.Night> series =
            [.. exercised.Select(n => new PairedInterval.Night(n.Date, n.MeanDifference, n.Disagreements, 0m))];

        int disagreements = exercised.Sum(n => n.Disagreements);
        int effective = series.Count == 0 ? 0 : PairedInterval.Disperse(series).Effective;
        bool matured = effective >= minimumSample;

        (decimal? baselineRate, decimal? variantRate) = WinRates(exercised);
        string population = PopulationOf(direction, series.Count);

        Reading Open(string why) => new(
            scored.Count, carrying, identical, series.Count, disagreements, effective,
            minimumSample, minimumSampleUnit, matured,
            null, null, null, baselineRate, variantRate,
            VariantStatus.Open, null, why, population);

        if (scored.Count == 0)
        {
            return Open(NothingScored);
        }

        if (series.Count == 0)
        {
            return Open(NothingToDifference);
        }

        if (!matured)
        {
            return Open(Shortfall(effective, minimumSample, minimumSampleUnit, series.Count));
        }

        PairedInterval.Estimate? estimate = PairedInterval.Of(
            series, MeasurementParameters.BootstrapBlockSessions, MeasurementParameters.BootstrapDraws);

        if (estimate is null)
        {
            return Open(NoInterval);
        }

        // The diagnostic rejection is asked before the bound, because it is a different answer about
        // the version rather than a stronger form of the same one. A version whose bound clears
        // nought cannot also have lowered expectancy, so the two never both apply and the order is
        // about which sentence gets recorded rather than about which verdict is reached.
        bool boughtWinRate =
            baselineRate is decimal b && variantRate is decimal v && v > b && estimate.Mean < 0m;

        string verdict = boughtWinRate || estimate.Low <= 0m
            ? VariantStatus.Rejected
            : VariantStatus.Accepted;

        string because = boughtWinRate
            ? WinRateForExpectancy
            : Settlement(verdict, estimate, effective, minimumSample, minimumSampleUnit);

        return new Reading(
            scored.Count, carrying, identical, series.Count, disagreements, effective,
            minimumSample, minimumSampleUnit, matured,
            estimate.Mean, estimate.Low, estimate.High, baselineRate, variantRate,
            verdict, because, null, population);
    }

    /// <summary>
    /// What each rule's selections did, as a share of the rows each mean was taken over.
    ///
    /// <b>Two denominators and never one.</b> The version and the baseline selected different names,
    /// so a single denominator would put one rule's wins over the other's population. Null together
    /// where neither side has a scored row, because a rate over nothing is not nought.
    /// see: Acceptance measures expectancy, never win rate
    /// </summary>
    private static (decimal? Baseline, decimal? Variant) WinRates(IReadOnlyList<Night> exercised)
    {
        int baselineScored = exercised.Sum(n => n.BaselineScored);
        int variantScored = exercised.Sum(n => n.VariantScored);

        if (baselineScored == 0 || variantScored == 0)
        {
            return (null, null);
        }

        return (
            exercised.Sum(n => n.BaselineWins) / (decimal)baselineScored,
            exercised.Sum(n => n.VariantWins) / (decimal)variantScored);
    }

    private static string Shortfall(int effective, int minimum, string unit, int nights) =>
        Count(effective) + " of " + Count(minimum) + " " + Unit(unit) + " accumulated over "
        + Count(nights) + " night(s) the two rules selected differently on. The version stays open: "
        + "settling it now would be a decision made by the calendar rather than by evidence";

    private static string Settlement(
        string verdict, PairedInterval.Estimate estimate, int effective, int minimum, string unit) =>
        Count(effective) + " of " + Count(minimum) + " " + Unit(unit) + " accumulated, and the interval "
        + "around a mean paired difference of " + PairedInterval.Figure(estimate.Mean) + " runs "
        + PairedInterval.Figure(estimate.Low) + " to " + PairedInterval.Figure(estimate.High) + ", which "
        + (verdict == VariantStatus.Accepted ? "clears nought" : "does not clear nought");

    /// <summary>Invariant throughout, so a reason reads the same on both platforms.</summary>
    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The unit as a sentence reads it rather than as the column stores it.</summary>
    private static string Unit(string stored) => stored.Replace('_', ' ');
}
