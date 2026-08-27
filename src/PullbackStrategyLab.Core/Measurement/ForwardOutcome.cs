namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// What a subject did over a horizon: the signed return and how far it ran either way on the path.
///
/// <b>In Core, and computed from a window rather than read from a store</b>, so the nightly fill,
/// the replay and a test share one implementation. The arithmetic is small and the two places it
/// could disagree are exactly the two that matter: the sign convention and which bar the horizon
/// lands on.
///
/// <b>The sign is the direction's, not the market's.</b> A short that fell is a positive result. A
/// table that recorded the market's sign would need every reader to know the direction and flip it,
/// and one reader that forgot would produce a scoreboard where the short side looks like a disaster
/// and nothing says why.
/// see: Forward returns are recorded for every flagged setup, traded or not
///
/// <b>The excursions are the half a plain return cannot express.</b> A name that rose 15% after
/// first dropping 4% is a good spot with a badly placed exit; one that rose 15% smoothly is a good
/// spot with a well placed one. The terminal return cannot tell them apart and any sensible
/// proposal about stop placement depends on the distinction.
/// </summary>
public static class ForwardOutcome
{
    /// <summary>The horizons every subject is measured at, in trading sessions.</summary>
    public static IReadOnlyList<int> Horizons { get; } = [1, 3, 5, 10];

    /// <summary>One session as the outcome reads it, on the adjusted basis throughout.</summary>
    public sealed record Bar(DateOnly Date, decimal High, decimal Low, decimal Close);

    /// <summary>
    /// The outcome over one horizon, or null where the window does not reach that far.
    ///
    /// Null rather than a partial answer, because a ten-session return measured over six sessions is
    /// not a smaller version of the right number: it is a different quantity that would be pooled
    /// with the right ones and never be visible again.
    /// </summary>
    /// <param name="path">
    /// The subject's own bars from the session it was flagged on, inclusive, forward. Index 0 is the
    /// as-of session, whose close is what the return is measured from.
    /// </param>
    /// <param name="horizonSessions">How many trading sessions forward, being 1, 3, 5 or 10.</param>
    /// <param name="isLong">Which way the subject was taken, which is what signs the result.</param>
    /// <param name="averageTrueRange">
    /// The subject's ATR on its own as-of date, which is what the excursions are expressed in. A
    /// zero or absent range makes the excursions undefined rather than infinite.
    /// </param>
    public static Outcome? Of(
        IReadOnlyList<Bar> path,
        int horizonSessions,
        bool isLong,
        decimal averageTrueRange)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonSessions, 1);

        // The as-of session plus the horizon. A window that stops short has not finished the
        // horizon yet, which is a real state on any night and is why this returns null.
        if (path.Count <= horizonSessions)
        {
            return null;
        }

        Bar from = path[0];
        Bar to = path[horizonSessions];

        if (from.Close == 0m)
        {
            return null;
        }

        decimal move = (to.Close - from.Close) / from.Close;
        decimal signed = isLong ? move : -move;

        // The excursions are measured over the sessions after the as-of, up to and including the
        // horizon. The as-of session itself is excluded: the lab flagged the name on its close, so
        // what that session's own high and low did is not something the position could have lived
        // through.
        decimal best = decimal.MinValue;
        decimal worst = decimal.MaxValue;

        for (int i = 1; i <= horizonSessions; i++)
        {
            decimal favourable = isLong ? path[i].High - from.Close : from.Close - path[i].Low;
            decimal adverse = isLong ? path[i].Low - from.Close : from.Close - path[i].High;

            best = Math.Max(best, favourable);
            worst = Math.Min(worst, adverse);
        }

        decimal? mfe = averageTrueRange == 0m ? null : best / averageTrueRange;
        decimal? mae = averageTrueRange == 0m ? null : worst / averageTrueRange;

        return new Outcome(to.Date, signed, mfe, mae);
    }

    /// <summary>
    /// One measured outcome. <paramref name="MaximumAdverseExcursion"/> is negative or zero by
    /// construction, because the worst the path went is never in the subject's favour.
    /// </summary>
    public sealed record Outcome(
        DateOnly ActualDate,
        decimal ReturnSigned,
        decimal? MaximumFavourableExcursion,
        decimal? MaximumAdverseExcursion);
}
