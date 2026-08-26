namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// The nightly cap: sixty setups, forty long and twenty short, unused slots released.
///
/// Pure and in Core, because what it decides is arithmetic over two counts and an ordering, and the
/// stage around it is a read, an update and a count. That also makes the release rule assertable
/// over every arrangement of the two counts rather than over the ones a fixture happened to produce.
///
/// <b>The split is deliberately not proportional.</b> Short setups are rarest in a strong market,
/// which is exactly when they are most interesting, and a proportional split would erase them from
/// the record on those nights.
/// see: The nightly cap is 60, split forty long and twenty short, unused slots released
///
/// <b>The ranking is give-up distance in daily-range units, ascending.</b> R is the move divided by
/// the stop, and range units normalise the stop against that stock's own noise, so a 0.30 setup earns
/// more R per unit of noise risk than a 0.48 one for the same move. Ticker alphabetical breaks a tie,
/// so the boundary does not depend on the order rows came back in.
/// see: The screen and the cap both rank on give-up distance in daily-range units, ascending
///
/// <b>Ranked within a direction and never across.</b> A pooled ranking would put a short's give-up
/// distance beside a long's and truncate one on the other's account, and a short carries a borrow
/// assumption a long does not.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class NightlyCap
{
    /// <summary>The long side's own allocation, before anything is released to it.</summary>
    public const int LongAllocation = 40;

    /// <summary>The short side's.</summary>
    public const int ShortAllocation = 20;

    /// <summary>Sixty a night, which is what the intraday call budget sets.</summary>
    public const int Total = LongAllocation + ShortAllocation;

    /// <summary>One setup competing for a slot: which side, and the number it is ranked on.</summary>
    public sealed record Candidate(string SetupId, string Ticker, string Direction, decimal StopDistanceRanges);

    /// <summary>What the cap decided about one candidate.</summary>
    public sealed record Placement(string SetupId, string Direction, int Rank, bool CappedOut);

    /// <summary>
    /// How many each side takes, given how many each side has.
    ///
    /// <b>No priority order is needed, and that is a property rather than an omission.</b> Each side
    /// takes the lesser of its candidate count and its allocation; whatever either leaves unfilled is
    /// offered to the other. A slot is only released by a side that ran out of candidates, and a side
    /// that ran out is not also asking for more, so the two conditions are mutually exclusive and one
    /// pass is deterministic. A stated tiebreak would cover a case that cannot arise, which reads to
    /// the next session as though it can. The property is swept rather than argued, in the tests.
    /// see: A released cap slot goes to the side that still has candidates
    /// </summary>
    public static (int Long, int Short) Take(int longCount, int shortCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(longCount);
        ArgumentOutOfRangeException.ThrowIfNegative(shortCount);

        int takenLong = Math.Min(longCount, LongAllocation);
        int takenShort = Math.Min(shortCount, ShortAllocation);

        takenLong += Math.Min(Total - takenLong - takenShort, longCount - takenLong);
        takenShort += Math.Min(Total - takenLong - takenShort, shortCount - takenShort);

        return (takenLong, takenShort);
    }

    /// <summary>
    /// Every candidate ranked within its own side, and told whether the cap truncated it.
    ///
    /// Every candidate gets a rank, including the ones beyond the cap. The truncated rows are what
    /// say how far past the cap the night went, and a night that recorded only what it kept could
    /// never answer whether the cap was binding.
    /// </summary>
    public static IReadOnlyList<Placement> Apply(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        Candidate[] all = [.. candidates];
        Candidate[] longs = [.. Ordered(all, "long")];
        Candidate[] shorts = [.. Ordered(all, "short")];

        (int takenLong, int takenShort) = Take(longs.Length, shorts.Length);

        return
        [
            .. Place(longs, takenLong),
            .. Place(shorts, takenShort),
        ];
    }

    private static IEnumerable<Candidate> Ordered(IEnumerable<Candidate> candidates, string direction) =>
        candidates
            .Where(c => string.Equals(c.Direction, direction, StringComparison.Ordinal))
            .OrderBy(c => c.StopDistanceRanges)
            .ThenBy(c => c.Ticker, StringComparer.Ordinal);

    private static IEnumerable<Placement> Place(IReadOnlyList<Candidate> ordered, int taken)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            yield return new Placement(ordered[i].SetupId, ordered[i].Direction, i + 1, i >= taken);
        }
    }
}
