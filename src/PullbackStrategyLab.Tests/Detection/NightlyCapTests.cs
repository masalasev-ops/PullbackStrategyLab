using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// The cap's arithmetic, swept rather than sampled.
///
/// The release rule's whole claim is that no priority order is needed: a slot is only released by a
/// side that ran out of candidates, and a side that ran out is not also asking for more. That is a
/// statement about every arrangement of the two counts, so it is asserted over every arrangement
/// rather than over the handful a fixture happened to produce.
/// see: A released cap slot goes to the side that still has candidates
/// </summary>
public sealed class NightlyCapTests
{
    /// <summary>How far past the cap the sweep goes on each side. Twice the total, so every boundary sits inside it.</summary>
    private const int SweepTo = NightlyCap.Total * 2;

    [Fact]
    public void The_case_file_says_it_is_authored_and_states_the_allocation_the_code_holds()
    {
        Assert.Equal("AUTHORED", CapCases.Tier);
        Assert.Equal(NightlyCap.LongAllocation, CapCases.Allocation["long"]);
        Assert.Equal(NightlyCap.ShortAllocation, CapCases.Allocation["short"]);
        Assert.Equal(NightlyCap.Total, CapCases.Allocation["total"]);
    }

    /// <summary>
    /// Over every arrangement of the two counts up to twice the cap: four properties that together
    /// are what "unused slots released" means.
    /// </summary>
    [Fact]
    public void The_release_rule_holds_over_every_arrangement_of_the_two_counts()
    {
        var problems = new List<string>();
        int arrangements = 0;

        for (int longs = 0; longs <= SweepTo; longs++)
        {
            for (int shorts = 0; shorts <= SweepTo; shorts++)
            {
                arrangements++;
                (int takenLong, int takenShort) = NightlyCap.Take(longs, shorts);

                if (takenLong > longs || takenShort > shorts)
                {
                    problems.Add($"{longs}/{shorts} takes more than it has: {takenLong}/{takenShort}");
                }

                if (takenLong + takenShort > NightlyCap.Total)
                {
                    problems.Add($"{longs}/{shorts} takes {takenLong + takenShort}, past the cap of {NightlyCap.Total}");
                }

                // A slot goes unused only when neither side has a candidate left for it. Anything
                // else is a slot released to nobody, which is the failure "unused slots released"
                // is a rule against.
                if (takenLong + takenShort < NightlyCap.Total && (takenLong < longs || takenShort < shorts))
                {
                    problems.Add(
                        $"{longs}/{shorts} leaves {NightlyCap.Total - takenLong - takenShort} slot(s) unused with "
                        + "candidates still waiting");
                }

                // Neither side is ever cut below its own allocation to make room for the other.
                if (takenLong < Math.Min(longs, NightlyCap.LongAllocation)
                    || takenShort < Math.Min(shorts, NightlyCap.ShortAllocation))
                {
                    problems.Add($"{longs}/{shorts} cut a side below its own allocation: {takenLong}/{takenShort}");
                }
            }
        }

        // Stated in advance rather than left self-validating: a loop that ran zero times would
        // satisfy every assertion above.
        Assert.Equal((SweepTo + 1) * (SweepTo + 1), arrangements);

        Assert.True(problems.Count == 0,
            $"{problems.Count} arrangement(s) break the release rule:\n  " + string.Join("\n  ", problems.Take(10)));
    }

    /// <summary>
    /// The claim that needs no tiebreak, asserted rather than argued.
    ///
    /// Offering the spare to the long side first and to the short side first must give the same
    /// answer, for every arrangement. If any arrangement disagreed, a priority order would be
    /// deciding something, and the corpus says none is needed.
    /// </summary>
    [Fact]
    public void Which_side_is_offered_the_spare_first_never_changes_the_answer()
    {
        var disagreements = new List<string>();

        for (int longs = 0; longs <= SweepTo; longs++)
        {
            for (int shorts = 0; shorts <= SweepTo; shorts++)
            {
                (int takenLong, int takenShort) = NightlyCap.Take(longs, shorts);
                (int mirrorShort, int mirrorLong) = ShortFirst(longs, shorts);

                if (takenLong != mirrorLong || takenShort != mirrorShort)
                {
                    disagreements.Add(
                        $"{longs}/{shorts}: long first gives {takenLong}/{takenShort}, short first gives "
                        + $"{mirrorLong}/{mirrorShort}");
                }
            }
        }

        Assert.True(disagreements.Count == 0,
            $"{disagreements.Count} arrangement(s) depend on which side is offered the spare first, so a tiebreak "
            + "would be deciding something:\n  " + string.Join("\n  ", disagreements.Take(10)));
    }

    /// <summary>The same rule with the two sides swapped, written out so the comparison is a real one.</summary>
    private static (int Short, int Long) ShortFirst(int longCount, int shortCount)
    {
        int takenShort = Math.Min(shortCount, NightlyCap.ShortAllocation);
        int takenLong = Math.Min(longCount, NightlyCap.LongAllocation);

        takenShort += Math.Min(NightlyCap.Total - takenLong - takenShort, shortCount - takenShort);
        takenLong += Math.Min(NightlyCap.Total - takenLong - takenShort, longCount - takenLong);

        return (takenShort, takenLong);
    }

    [Fact]
    public void Every_authored_scenario_takes_what_the_file_says_it_should()
    {
        Assert.NotEmpty(CapCases.Scenarios);

        foreach (CapCases.Scenario scenario in CapCases.Scenarios)
        {
            (int takenLong, int takenShort) = NightlyCap.Take(scenario.Long, scenario.Short);

            Assert.True(takenLong + takenShort <= NightlyCap.Total,
                $"{scenario.Name} takes {takenLong + takenShort}: {scenario.Why}");
        }
    }

    /// <summary>
    /// Ranked within a direction and never across, on the distance and then on the ticker.
    ///
    /// The tie is the part worth having a case for: two long candidates at the same give-up distance
    /// must come back in the same order every run, or the name that falls off the boundary changes
    /// with the order the store happened to return rows in.
    /// </summary>
    [Fact]
    public void The_ordering_case_ranks_within_a_direction_on_distance_then_ticker()
    {
        IReadOnlyList<NightlyCap.Placement> placements = NightlyCap.Apply(CapCases.OrderingCandidates);

        string[] longs = [.. placements.Where(p => p.Direction == "long").OrderBy(p => p.Rank).Select(p => p.SetupId)];
        string[] shorts = [.. placements.Where(p => p.Direction == "short").OrderBy(p => p.Rank).Select(p => p.SetupId)];

        // ZZZZ at 0.10 outranks AAAA at 0.30 despite the ticker, and AAAA outranks BBBB at the same
        // distance because the ticker is the tiebreak and not the ranking.
        Assert.Equal(["o1", "o2", "o3", "o4"], longs);
        Assert.Equal(["o6", "o5"], shorts);

        // Both sides start at rank one. A pooled ranking would have the short side start at five.
        Assert.Equal(1, placements.Single(p => p.SetupId == "o1").Rank);
        Assert.Equal(1, placements.Single(p => p.SetupId == "o6").Rank);

        // Nothing is truncated on a night this small, and every candidate still carries a rank.
        Assert.All(placements, p => Assert.False(p.CappedOut));
        Assert.Equal(CapCases.OrderingCandidates.Count, placements.Count);
    }

    /// <summary>
    /// Truncated candidates keep their rank, which is what says how far past the cap a night went.
    ///
    /// A night that recorded only what it kept could never answer whether the cap was binding, and
    /// that is the question the number sixty is set against.
    /// </summary>
    [Fact]
    public void A_truncated_candidate_keeps_its_rank()
    {
        NightlyCap.Candidate[] many =
        [
            .. Enumerable.Range(0, NightlyCap.LongAllocation + 5).Select(i =>
                new NightlyCap.Candidate($"L{i:000}", $"T{i:000}", "long", 0.10m + (i * 0.001m))),
            .. Enumerable.Range(0, NightlyCap.ShortAllocation + 5).Select(i =>
                new NightlyCap.Candidate($"S{i:000}", $"U{i:000}", "short", 0.10m + (i * 0.001m))),
        ];

        IReadOnlyList<NightlyCap.Placement> placements = NightlyCap.Apply(many);

        Assert.Equal(many.Length, placements.Count);

        // Ranks within a side are one to n with nothing missing and nothing repeated, which is what
        // makes "truncated at rank forty-one" a fact rather than a description.
        foreach (string direction in new[] { "long", "short" })
        {
            int[] ranks = [.. placements.Where(p => p.Direction == direction).Select(p => p.Rank).Order()];
            Assert.Equal([.. Enumerable.Range(1, ranks.Length)], ranks);
        }

        // Both sides overflow, so neither releases anything and each is held to its own allocation.
        Assert.Equal(5, placements.Count(p => p.Direction == "long" && p.CappedOut));
        Assert.Equal(5, placements.Count(p => p.Direction == "short" && p.CappedOut));
        Assert.All(placements.Where(p => p.CappedOut), p => Assert.True(p.Rank > 0));
    }
}
