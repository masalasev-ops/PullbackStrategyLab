namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// Two setups that looked the same in everything the lab records and ended somewhere else.
///
/// <b>The most informative object in the store, and the reason the loop has a signal-request
/// channel at all.</b> A pair that is near-identical on every recorded signal and diverged by more
/// than the threshold is a statement that something the lab does not measure decided the outcome.
/// That is the question the model is asked: not which one wins, which it cannot know, but what
/// would you want to see.
///
/// <b>The window is a population and this class never chooses it.</b> Every signal is standardised
/// against its own distribution over the trailing <see cref="WindowSetups"/> setups, so the same
/// two setups are a twin over one window and not over another. The caller passes the rows it means
/// and the run records how many it actually held, because a z-score over forty rows and a z-score
/// over two hundred and fifty are different quantities and one column would make them look alike.
/// see: The twin-pair threshold is reviewed at the first full window rather than at a phase
/// </summary>
public static class TwinPairs
{
    /// <summary>
    /// The trailing window every signal is standardised over.
    ///
    /// <b>Two hundred and fifty is about a year of sessions, and it is the population the two
    /// thresholds below were set for.</b> The store holds a small fraction of it and will for
    /// months, which is why the review point for those thresholds is the first full window rather
    /// than a checkpoint: a review taken against a shorter window would be comparing them to a
    /// quantity other than the one they describe.
    /// </summary>
    public const int WindowSetups = 250;

    /// <summary>
    /// How close two setups must sit, after standardising, to count as looking the same.
    ///
    /// A distance in standard deviations across the axes compared, so it moves with the number of
    /// signals in the space. That is a property of the metric rather than a defect in the threshold,
    /// and it is the second reason the review waits for a full window: the pack's signal list is
    /// what fixes the width, and it is not fixed yet.
    /// </summary>
    public const double MaximumDistance = 0.5;

    /// <summary>
    /// How far apart the two outcomes must end, in percentage points of the ten-day return.
    ///
    /// Points rather than a fraction, because that is how the authored-parameters row states it and
    /// how a person reads it. The stored outcome is a fraction, so the comparison multiplies by a
    /// hundred at the point of use and is named for it.
    /// </summary>
    public const double MinimumOutcomeGapPoints = 15.0;

    /// <summary>
    /// Every qualifying pair among the rows given, ordered by distance and then by the two ids, so
    /// a run over one store produces one answer.
    ///
    /// <paramref name="rows"/> is one point per setup in the same order as <paramref name="ids"/>
    /// and <paramref name="outcomes"/>, holding the raw signal values; standardising happens here,
    /// over exactly these rows, so the population the z-scores describe is the population the caller
    /// passed and not some wider set the store happens to hold.
    ///
    /// <b>One direction at a time, and this class cannot enforce it.</b> The outcome is signed by
    /// direction, so a long setup and a short setup that differ by 20 points may be two names that
    /// did the same thing. The caller passes one side's rows; the stage that calls it is where that
    /// is asserted (see: Long and short are never pooled into one figure).
    /// </summary>
    public static IReadOnlyList<TwinPair> Find(
        IReadOnlyList<string> ids,
        IReadOnlyList<IReadOnlyList<double>> rows,
        IReadOnlyList<double> outcomes)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(outcomes);

        if (ids.Count != rows.Count || ids.Count != outcomes.Count)
        {
            throw new ArgumentException(
                $"{ids.Count} id(s), {rows.Count} row(s) and {outcomes.Count} outcome(s): the three are one "
                + "population and have to be the same length.",
                nameof(rows));
        }

        if (rows.Count < 2 || rows[0].Count == 0)
        {
            return [];
        }

        IReadOnlyList<IReadOnlyList<double>> z = SignalSpace.ZScore(rows);
        var pairs = new List<TwinPair>();

        for (int i = 0; i < z.Count; i++)
        {
            for (int j = i + 1; j < z.Count; j++)
            {
                double distance = SignalSpace.Distance(z[i], z[j]);

                if (distance >= MaximumDistance)
                {
                    continue;
                }

                double gap = Math.Abs(outcomes[i] - outcomes[j]) * 100;

                if (gap <= MinimumOutcomeGapPoints)
                {
                    continue;
                }

                // The lower id first, so a pair has one identity whichever order the walk reached
                // it in and a rerun cannot write the same pair twice under two names.
                (string left, string right, double leftOutcome, double rightOutcome) =
                    string.CompareOrdinal(ids[i], ids[j]) <= 0
                        ? (ids[i], ids[j], outcomes[i], outcomes[j])
                        : (ids[j], ids[i], outcomes[j], outcomes[i]);

                pairs.Add(new TwinPair(left, right, distance, gap, leftOutcome, rightOutcome, rows[0].Count));
            }
        }

        return
        [
            .. pairs
                .OrderBy(p => p.Distance)
                .ThenBy(p => p.Left, StringComparer.Ordinal)
                .ThenBy(p => p.Right, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// How many pairs a population of this size can form, which is what a run counts against what it
    /// found.
    ///
    /// Reported because "nought pairs" over four setups and "nought pairs" over two hundred are
    /// different statements, and only the second is evidence about the thresholds.
    /// </summary>
    public static long CandidatePairs(int setups) => setups < 2 ? 0 : (long)setups * (setups - 1) / 2;
}

/// <summary>
/// One qualifying pair: which two, how close, and how far apart they ended.
///
/// <c>Distance</c> is in standard deviations over <c>SignalsCompared</c> axes and <c>GapPoints</c>
/// is in percentage points of the ten-day return. Both travel with the row because neither can be
/// recomputed later: the z-scores are over the window the run held, and that window grows.
/// </summary>
public sealed record TwinPair(
    string Left,
    string Right,
    double Distance,
    double GapPoints,
    double LeftOutcome,
    double RightOutcome,
    int SignalsCompared)
{
    /// <summary>The pair's identity, which is its two members in their settled order.</summary>
    public string PairId => $"{Left}--{Right}";
}
