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
    /// <summary>A name the draw may choose from, with the figures it is matched on.</summary>
    public sealed record Candidate(
        string Ticker,
        decimal MedianDollarVolume,
        decimal AverageDailyRange,
        string? LadderGrade);

    /// <summary>One drawn control: which name, how close on each dimension, and its place in the five.</summary>
    public sealed record Draw(string Ticker, int Rank, IReadOnlyDictionary<string, string> MatchQuality);

    /// <summary>
    /// The nearest <paramref name="count"/> candidates to <paramref name="subject"/>.
    ///
    /// <paramref name="tight"/> adds the trend ladder to the two dimensions the loose set uses, and
    /// a candidate whose grade differs is excluded rather than merely penalised: the tight set's
    /// whole purpose is to isolate the pullback checks from owning stocks in an uptrend, and a
    /// tight set that admits a different ladder at a distance is a loose set wearing the name.
    ///
    /// <b>The market mood is not a dimension here, and that is a finding rather than an omission.</b>
    /// The mood is a property of the session, so every candidate drawn on the same night carries the
    /// same one and matching on it excludes nothing. It is left out rather than implemented as a
    /// comparison that is true by construction, because a dimension that always matches reads in the
    /// record as a dimension that was checked.
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

            scored.Add((candidate, Apart(subject.MedianDollarVolume, candidate.MedianDollarVolume),
                Apart(subject.AverageDailyRange, candidate.AverageDailyRange)));
        }

        // Ordered on the sum, with ticker as the tiebreak, exactly as the scans and the cap break
        // theirs. The sum orders the list and is not stored; what is stored is the pair.
        return
        [
            .. scored
                .OrderBy(s => s.Liquidity + s.Range)
                .ThenBy(s => s.Candidate.Ticker, StringComparer.Ordinal)
                .Take(count)
                .Select((s, i) => new Draw(
                    s.Candidate.Ticker,
                    i + 1,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["liquidity"] = Figure(s.Liquidity),
                        ["dailyRange"] = Figure(s.Range),
                        ["ladderGrade"] = tight ? "same" : candidateGrade(s.Candidate),
                    })),
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
