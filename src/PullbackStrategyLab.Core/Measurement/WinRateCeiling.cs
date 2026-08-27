namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// The win rate perfect foresight could have reached, given that it still has to survive the path.
///
/// <b>Most of a win rate is geometry, not skill.</b> A give-up point at half a daily range sits
/// about 0.8 of one daily standard deviation away, so purely random movement hits it 42% of the time
/// in one day and 80% within ten. A coin flip with this stop wins about 20% of the time and the
/// observed rate is 25%, so the entire measured edge is worth about five points. Chasing the win
/// rate directly is the wrong instinct, and the number that means something is the **gap** between
/// what was achieved and what was available.
/// see: The win-rate ceiling is computed from the outcome distribution, never assumed
///
/// <b>Computed from the path, not from the terminal return.</b> A setup that ends ahead having first
/// been stopped out is not available to any selection rule, however good. Counting it would produce
/// a bound no system could reach, which is worse than no bound: it would say selection has room when
/// it has none.
///
/// <b>The units trap, and it is why this is a decision rather than a formula.</b> The excursion is
/// recorded in ATR and the give-up distance is expressed in daily ranges. Two different units on two
/// different bases, both small, both looking like volatility. Comparing them raw produces a bound
/// that reads as perfectly reasonable and is wrong. <see cref="Survived"/> is where the conversion
/// happens and it is named for it.
/// see: The ceiling is computed from the path, not from the terminal return
/// </summary>
public static class WinRateCeiling
{
    /// <summary>
    /// One closed subject, as the bound reads it.
    ///
    /// <paramref name="MaximumAdverseExcursionAtr"/> is negative or zero by construction, being how
    /// far the path went against the subject. <paramref name="AverageTrueRange"/> and
    /// <paramref name="DailyRange"/> are both prices, and they are what the conversion needs.
    /// </summary>
    public sealed record Subject(
        string SubjectId,
        string Direction,
        decimal ReturnSigned,
        decimal MaximumAdverseExcursionAtr,
        decimal AverageTrueRange,
        decimal DailyRange,
        decimal StopDistanceRanges);

    /// <summary>The bound and what was actually achieved over the same rows.</summary>
    public sealed record Bound(int Subjects, decimal Ceiling, decimal Achieved);

    /// <summary>
    /// Whether a subject's path stayed inside its own give-up point.
    ///
    /// <b>The conversion, in one place and named.</b> The excursion is in ATR, so its size in price
    /// is the excursion times the ATR. The give-up distance is in daily ranges, so its size in price
    /// is the distance times the daily range. Both are now prices and the comparison means
    /// something. Doing it any other way compares a multiple of one volatility measure against a
    /// multiple of another, which is the error this method exists to make impossible to write by
    /// accident.
    ///
    /// A subject with no range at all cannot be judged, and is treated as not having survived rather
    /// than as having survived: a bound that counted unmeasurable rows as available would be
    /// optimistic exactly where the data is worst.
    /// </summary>
    public static bool Survived(Subject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (subject.AverageTrueRange <= 0m || subject.DailyRange <= 0m)
        {
            return false;
        }

        decimal excursionInPrice = Math.Abs(subject.MaximumAdverseExcursionAtr) * subject.AverageTrueRange;
        decimal giveUpInPrice = subject.StopDistanceRanges * subject.DailyRange;

        return excursionInPrice < giveUpInPrice;
    }

    /// <summary>
    /// The bound over one population, or null where there is nothing to compute it from.
    ///
    /// Null rather than nought, because a ceiling of nought over an empty population reads on a
    /// scoreboard as "selection has no room" when what it means is "nobody has measured anything
    /// yet". Those are different sentences and only one of them is a finding.
    ///
    /// The population is one direction's. A pooled bound would inherit the short side's borrow
    /// assumption and the whole point of the figure is the gap between it and what was achieved.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    public static Bound? Of(IReadOnlyList<Subject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        if (subjects.Count == 0)
        {
            return null;
        }

        // What perfect foresight would take: the subjects that ended ahead. Knowing the outcome is
        // exactly the foresight being granted, and it is the only thing being granted.
        int ahead = subjects.Count(s => s.ReturnSigned > 0m);

        // Of those, the ones the stop let it keep. This is the constraint foresight does not lift:
        // a name that ended 15% up having first traded through its give-up point was not available
        // to any rule, however well chosen, because the position was already closed.
        int kept = subjects.Count(s => s.ReturnSigned > 0m && Survived(s));

        if (ahead == 0)
        {
            // Nothing ended ahead, so the ceiling is nought and it is a real nought rather than a
            // division by zero: no selection over this population could have won anything.
            return new Bound(subjects.Count, 0m, 0m);
        }

        // The ceiling is over what foresight would have picked; the achieved rate is over everything
        // the lab actually flagged. **The two denominators differ and that is the whole figure.**
        // A ceiling over the whole population would be the achieved rate again and the gap would be
        // nought by construction, which is a bound that can only ever say "no room".
        return new Bound(
            subjects.Count,
            (decimal)kept / ahead,
            (decimal)kept / subjects.Count);
    }
}
