using System.Globalization;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// Which unflagged names sit closest to a flagged one, and how close on each dimension separately.
///
/// <b>In Core so the nightly draw, the replay and a test share one implementation.</b> The
/// arithmetic is small and the two places it could disagree are the two that decide what a
/// comparison means: which dimensions count, and how ties break.
///
/// <b>Deterministic nearest neighbour, no randomness anywhere.</b> A seeded draw would be a second
/// thing to keep point in time, a value the phase report cannot diff, and a figure nobody could
/// reproduce from the store alone. Nearest neighbour also makes the match quality the ranking
/// rather than an afterthought.
/// see: Controls are drawn by nearest neighbour on the matched dimensions, five per set, with no randomness
///
/// <b>The distance is per dimension and is never blended into one number.</b> A blended distance
/// cannot say which dimension a match was bad on, which is exactly what a later reader needs when a
/// comparison looks surprising. The ordering uses the sum only to break the list into an order, and
/// the per-dimension figures are what get stored.
/// </summary>
public static class ControlMatching
{
    /// <summary>
    /// A name the draw may choose from, on one session, with the figures it is matched on.
    ///
    /// <b>It is a name on a session rather than a name.</b> The loose pool holds one session, so the
    /// distinction cost nothing until the tight set was allowed to reach across nights. A control's
    /// forward return is measured over its own bars from its own session, so the session is part of
    /// what a candidate is and not context around it.
    /// </summary>
    public sealed record Candidate(
        string Ticker,
        decimal MedianDollarVolume,
        decimal AverageDailyRange,
        string? LadderGrade,
        DateOnly AsOf = default,
        string? MarketMood = null);

    /// <summary>
    /// One drawn control: which name, on which session, how close on each dimension, and its place
    /// in the five.
    /// </summary>
    public sealed record Draw(
        string Ticker, int Rank, IReadOnlyDictionary<string, string> MatchQuality, DateOnly AsOf);

    /// <summary>
    /// The nearest <paramref name="count"/> candidates to <paramref name="subject"/>.
    ///
    /// <paramref name="tight"/> adds the trend ladder and the market mood to the two dimensions the
    /// loose set uses, and a candidate differing on either is excluded rather than merely penalised:
    /// the tight set's whole purpose is to isolate the pullback checks from owning stocks in an
    /// uptrend, and a tight set that admits a different ladder at a distance is a loose set wearing
    /// the name.
    ///
    /// <b>The market mood is a real dimension here as of 2026-08-30, and until then it was not.</b>
    /// The mood is a property of the session, so within one night every candidate carries the same
    /// one and matching on it excludes nothing. The draw left it out rather than performing a
    /// comparison true by construction, because a dimension that always matches reads in the record
    /// as a dimension that was checked. The operator ruled that the dimension is kept and made real:
    /// the tight set draws from any session sharing the mood, the loose set stays within the night,
    /// and what that costs is that a setup and its tight controls may come from different sessions,
    /// so the market factor common to one night no longer cancels between them.
    /// see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
    ///
    /// <b>Comparing two moods is not branching on one.</b> Nothing here names a mood, prefers one,
    /// or behaves differently in any of them; the comparison is equality between the subject's
    /// session and the candidate's. The decision that the label filters nothing in the baseline is
    /// about the baseline choosing stocks, and this is the measurement choosing what to compare
    /// those stocks against.
    /// see: The market-mood label is recorded on every setup and filters nothing in the baseline
    ///
    /// <b>The mood is held here and narrowed in the caller, and which is which matters.</b> This
    /// method is the one implementation of what a comparison is made of, so the dimension is
    /// asserted here: the recorded "same" is true because it was checked rather than because the
    /// caller promised it. `ControlSampler.MoodPool` also selects sessions by mood, and that is a
    /// cost measure and not the guard, because a pool of every session ever recorded would be loaded
    /// to be thrown away. The two are redundant on purpose and only one of them is proved by
    /// `A_tight_draw_excludes_a_candidate_from_a_session_carrying_a_different_mood`, which passes a
    /// mixed pool straight in. A test going through the sampler cannot fail when this clause is
    /// removed, because the pool it was handed had already excluded the rows.
    ///
    /// <b>One row per name, however many sessions it qualifies on.</b> The tight pool holds a name
    /// once per session, so without this a single ticker could take several of the five. Five per set
    /// exists so the comparison does not inherit one name's idiosyncratic move, and a set holding one
    /// name on five adjacent sessions would inherit it while looking like five.
    /// </summary>
    public static IReadOnlyList<Draw> Nearest(
        Candidate subject,
        IReadOnlyList<Candidate> candidates,
        int count,
        bool tight)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var scored = new List<(Candidate Candidate, decimal Liquidity, decimal Range)>();

        foreach (Candidate candidate in candidates)
        {
            if (string.Equals(candidate.Ticker, subject.Ticker, StringComparison.Ordinal))
            {
                continue;
            }

            if (tight && !string.Equals(candidate.LadderGrade, subject.LadderGrade, StringComparison.Ordinal))
            {
                continue;
            }

            if (tight && !string.Equals(candidate.MarketMood, subject.MarketMood, StringComparison.Ordinal))
            {
                continue;
            }

            scored.Add((candidate, Apart(subject.MedianDollarVolume, candidate.MedianDollarVolume),
                Apart(subject.AverageDailyRange, candidate.AverageDailyRange)));
        }

        // Ordered on the sum, with ticker as the tiebreak, exactly as the scans and the cap break
        // theirs. The sum orders the list and is not stored; what is stored is the pair.
        //
        // The third key is the session, most recent first, and it only ever separates two rows for
        // the same name at the same distance. Recent rather than earliest on the same grounds the
        // interval anchors its block tiling at the recent end: where nothing distinguishes two
        // readings, the newer evidence is the half a reader is watching.
        //
        // `DistinctBy` after the ordering rather than before it, so the row kept per name is the
        // nearest one rather than whichever the pool happened to yield first.
        return
        [
            .. scored
                .OrderBy(s => s.Liquidity + s.Range)
                .ThenBy(s => s.Candidate.Ticker, StringComparer.Ordinal)
                .ThenByDescending(s => s.Candidate.AsOf)
                .DistinctBy(s => s.Candidate.Ticker, StringComparer.Ordinal)
                .Take(count)
                .Select((s, i) => new Draw(
                    s.Candidate.Ticker,
                    i + 1,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["liquidity"] = Figure(s.Liquidity),
                        ["dailyRange"] = Figure(s.Range),
                        ["ladderGrade"] = tight ? "same" : candidateGrade(s.Candidate),

                        // The mood is "same" on the tight set by construction, exactly as the ladder
                        // grade is, and says so rather than being absent. What is not by
                        // construction is how far the control had to reach to be that: the session
                        // distance is the price the ruling accepted, and it is recorded per row so
                        // it can be measured later rather than argued about.
                        ["marketMood"] = tight ? "same" : "not matched",
                        ["sessionsApart"] = tight
                            ? Math.Abs(s.Candidate.AsOf.DayNumber - subject.AsOf.DayNumber)
                                .ToString(CultureInfo.InvariantCulture)
                            : "0",
                    },
                    s.Candidate.AsOf)),
        ];

        static string candidateGrade(Candidate candidate) => candidate.LadderGrade ?? "ungraded";
    }

    /// <summary>
    /// How far apart two figures are, as a fraction of the subject's own.
    ///
    /// Relative rather than absolute, because a $50m difference in turnover is a wide gap for a
    /// small name and a rounding for a large one, and a distance that said otherwise would draw
    /// every control from the biggest names in the universe.
    /// </summary>
    private static decimal Apart(decimal subject, decimal candidate) =>
        subject == 0m ? decimal.MaxValue / 4 : Math.Abs(candidate - subject) / Math.Abs(subject);

    private static string Figure(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);
}
