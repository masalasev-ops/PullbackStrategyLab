using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// One market-mood label from two scores summed, and the one implementation of that arithmetic.
///
/// <b>It lives here for the reason the detector rules do.</b> The nightly stage reads its ladder
/// counts out of `indicator_daily` and a calibration walk has none, because a night the lab was not
/// running has no indicator row and may not be given one. Both paths still have to produce the same
/// label from the same inputs, and a second implementation is the defect this corpus has met four
/// times: the count would become a fact about which code path asked rather than about the session.
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
///
/// <b>What is here is the scoring and not the reading.</b> Where the ladder counts come from is the
/// caller's business, being a query on the nightly path and an in-memory tally on the reconstructed
/// one, and that is exactly the seam that differs. Everything downstream of the two counts and the
/// tracker windows is the same on both, so it is here.
///
/// <b>The label filters nothing in the baseline</b> and that is a property of the components around
/// this one rather than of this one. Defining the three states is not branching on them; a scan
/// asserts that nothing else in the shipped source names the two extremes at all.
/// see: The market-mood label is recorded on every setup and filters nothing in the baseline
///
/// The three-state form buffers itself: risk-on needs both scores at +1 and risk-off needs both at
/// -1, so the label cannot go from one to the other without passing through mixed.
/// </summary>
public static class MarketMood
{
    /// <summary>Both scores at +1.</summary>
    public const string RiskOn = "risk_on";

    /// <summary>Anything in between, which is most nights.</summary>
    public const string Mixed = "mixed";

    /// <summary>Both scores at -1.</summary>
    public const string RiskOff = "risk_off";

    /// <summary>The average each tracker is measured against.</summary>
    public const int IndexAveragePeriod = 21;

    /// <summary>Above this ratio of long-ladder names to short-ladder names, breadth scores +1.</summary>
    public const decimal BreadthUpper = 1.5m;

    /// <summary>Below this ratio, breadth scores -1.</summary>
    public const decimal BreadthLower = 0.67m;

    /// <summary>
    /// One tracker's window, as the caller read it.
    ///
    /// The adjusted closes and the date of the last of them, which is all the scoring needs. Taking
    /// the bars themselves would put a storage type in Core for no gain, and taking only the closes
    /// would lose the one thing the measurability rule turns on, being whether the last bar is the
    /// session being scored.
    /// </summary>
    public readonly record struct Tracker(IReadOnlyList<decimal> AdjustedCloses, DateOnly LastBarDate);

    /// <summary>
    /// The whole scoring for one session: both scores, both raw inputs, and the label.
    ///
    /// <b>A tracker without a full window is not measured, rather than measured as below.</b>
    /// Counting it as below would move the score toward risk-off on exactly the nights the data is
    /// thin, which is a bias rather than a missing value.
    ///
    /// <paramref name="requiredSessions"/> is passed rather than read from a constant here, because
    /// the seeding rule it encodes belongs to the engine's warm-up and the engine is a stage. Two
    /// averages differing only in their seed converge to the same place and differ for a long time
    /// on the way, and both look like a moving average.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public static MoodScore Of(
        IReadOnlyList<Tracker> trackers,
        DateOnly asOf,
        int requiredSessions,
        int longLadder,
        int shortLadder)
    {
        ArgumentNullException.ThrowIfNull(trackers);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredSessions);

        int above = 0;
        int measured = 0;

        foreach (Tracker tracker in trackers)
        {
            if (tracker.AdjustedCloses is null
                || tracker.AdjustedCloses.Count < requiredSessions
                || tracker.LastBarDate != asOf)
            {
                continue;
            }

            measured++;
            decimal average = Averages.Exponential(tracker.AdjustedCloses, IndexAveragePeriod);

            if (tracker.AdjustedCloses[^1] > average)
            {
                above++;
            }
        }

        int indexScore = IndexScore(above, measured);
        int breadthScore = BreadthScore(longLadder, shortLadder);

        return new MoodScore(
            measured, above, longLadder, shortLadder,
            indexScore, breadthScore, LabelFor(indexScore, breadthScore));
    }

    /// <summary>
    /// +1 when every tracker closed above its own average, -1 when none did, 0 otherwise.
    ///
    /// Pure, and it takes how many were measured rather than assuming three. With no tracker
    /// measurable the answer is 0 and not -1: "none of nothing was above" is not the same statement
    /// as "none of three was above", and scoring it -1 would read a missing feed as a falling market.
    /// </summary>
    public static int IndexScore(int above, int measured)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(above);
        ArgumentOutOfRangeException.ThrowIfNegative(measured);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(above, measured);

        if (measured == 0)
        {
            return 0;
        }

        return above == measured ? 1 : above == 0 ? -1 : 0;
    }

    /// <summary>
    /// +1 above 1.5, -1 below 0.67, 0 between, on the ratio of rising names to falling ones.
    ///
    /// With no falling names the ratio is undefined and the answer is +1 rather than a division by
    /// zero: every name that laddered at all laddered upward, which is the strongest reading of the
    /// score there is. With neither the answer is 0, because nothing laddered either way.
    /// </summary>
    public static int BreadthScore(int longLadder, int shortLadder)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(longLadder);
        ArgumentOutOfRangeException.ThrowIfNegative(shortLadder);

        if (shortLadder == 0)
        {
            return longLadder == 0 ? 0 : 1;
        }

        decimal ratio = (decimal)longLadder / shortLadder;
        return ratio > BreadthUpper ? 1 : ratio < BreadthLower ? -1 : 0;
    }

    /// <summary>The label from the sum, which is why the three states buffer themselves.</summary>
    public static string LabelFor(int indexScore, int breadthScore) =>
        (indexScore + breadthScore) switch
        {
            2 => RiskOn,
            -2 => RiskOff,
            _ => Mixed,
        };
}

/// <summary>
/// One session's mood, with both scores and both raw counts beside the label.
///
/// The raw inputs travel with the verdict because a label alone cannot be argued with. `regime_daily`
/// stores all five for the same reason.
/// </summary>
public sealed record MoodScore(
    int IndexesMeasured,
    int IndexesAbove,
    int LongLadderCount,
    int ShortLadderCount,
    int IndexScore,
    int BreadthScore,
    string Label);
