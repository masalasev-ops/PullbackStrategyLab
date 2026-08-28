using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// Two properties over the gate list as a list, so a gate admitted in phase 6 inherits both.
///
/// <b>Every gate has a pass and a fail from a case built to be exactly that.</b> Eight of the ten
/// long gates were one-sided over the captured fixture when the detector first ran, and the cause
/// was arithmetic rather than the gates: two setups cannot exercise twenty branches. The authored
/// cases answer branch coverage; the captured fixture answers whether the arithmetic still does what
/// it did on a real day. Neither substitutes for the other.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
///
/// <b>Every gate handed nothing fails.</b> The failure this closes is a pass rather than a crash: a
/// thrust whose extreme is the current session puts the entry and the give-up point at the same
/// price, and a give-up distance of zero clears every threshold written as a maximum. `exit-tight`,
/// the check the corpus calls the most informative in the system, passed on that.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
///
/// Written over <see cref="SetupChecks"/> rather than over a list of its own, which is what makes it
/// inherited: a gate with no boundary cases and a gate with no degenerate case both fail here, so
/// the eleventh check cannot arrive without them.
/// </summary>
public sealed class GateBoundaryTests
{
    public static TheoryData<string> Directions => new("long", "short");

    private static IReadOnlyList<string> Gates(string direction) =>
        string.Equals(direction, "long", StringComparison.Ordinal) ? SetupChecks.Long : SetupChecks.Short;

    [Fact]
    public void The_case_file_says_it_is_authored()
    {
        // The marking is on the artefact rather than on a comment about it, because what stops a
        // constructed number being read as evidence about the market is that the file says so where
        // a reader of the file will see it.
        Assert.Equal("AUTHORED", GateCases.Tier);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Every_gate_has_a_case_either_side_of_its_own_threshold(string direction)
    {
        IReadOnlyList<GateCases.GateCase> cases =
            [.. GateCases.All.Where(c => string.Equals(c.Direction, direction, StringComparison.Ordinal))];

        var missing = new List<string>();

        foreach (string gate in Gates(direction))
        {
            GateCases.GateCase[] forGate =
                [.. cases.Where(c => string.Equals(c.Gate, gate, StringComparison.Ordinal))];

            if (!forGate.Any(c => c.Side == "inside" && c.ExpectsPass))
            {
                missing.Add($"{direction} {gate} has no case just inside its threshold expecting a pass");
            }

            if (!forGate.Any(c => c.Side == "outside" && !c.ExpectsPass))
            {
                missing.Add($"{direction} {gate} has no case just outside its threshold expecting a fail");
            }
        }

        // Both directions, so a case filed under the wrong one is a hole rather than a duplicate.
        string[] orphans =
        [
            .. cases.Select(c => c.Gate).Distinct(StringComparer.Ordinal)
                .Where(g => !Gates(direction).Contains(g, StringComparer.Ordinal))
                .Select(g => $"{direction} has cases for \"{g}\" and no gate of that name"),
        ];

        Assert.True(missing.Count == 0 && orphans.Length == 0,
            string.Join("\n  ", [.. missing, .. orphans]));
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Each_case_lands_on_the_side_of_the_threshold_it_was_built_for(string direction)
    {
        var wrong = new List<string>();

        foreach (GateCases.GateCase gateCase in
                 GateCases.All.Where(c => string.Equals(c.Direction, direction, StringComparison.Ordinal)))
        {
            IReadOnlyList<CheckResult> results = GateCases.Evaluate(gateCase);
            CheckResult result = results.Single(r => string.Equals(r.Name, gateCase.Gate, StringComparison.Ordinal));

            if (result.Passed != gateCase.ExpectsPass)
            {
                wrong.Add(
                    $"{gateCase.Id} expects {gateCase.Expect} and the rules returned "
                    + $"{(result.Passed ? "pass" : "fail")}. The case says: {gateCase.Why}");
            }
        }

        Assert.True(wrong.Count == 0, $"{wrong.Count} case(s) on the wrong side:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The baseline passes everything, which is what makes a boundary case say one thing.
    ///
    /// Without it a case could sit outside its own gate's threshold and outside three others, and
    /// the difference between the two sides would not be the field the case moved.
    /// </summary>
    [Theory]
    [MemberData(nameof(Directions))]
    public void The_baseline_clears_every_gate(string direction)
    {
        IReadOnlyList<CheckResult> results = GateCases.EvaluateWithout(direction, []);
        string[] failed = [.. results.Where(r => !r.Passed).Select(r => r.Name)];

        Assert.True(failed.Length == 0,
            $"The {direction} baseline was built to clear every gate and fails: {string.Join(", ", failed)}");
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void A_gate_handed_nothing_at_all_fails(string direction)
    {
        IReadOnlyList<CheckResult> results = GateCases.EvaluateEmpty(direction);

        string[] passed = [.. results.Where(r => r.Passed).Select(r => r.Name)];
        string[] absent =
        [
            .. Gates(direction).Where(g => !results.Any(r => string.Equals(r.Name, g, StringComparison.Ordinal))),
        ];

        Assert.True(absent.Length == 0,
            $"The {direction} rules returned no result for: {string.Join(", ", absent)}. A gate with no verdict is "
            + "not a gate that failed.");

        Assert.True(passed.Length == 0,
            $"With every figure absent, these {direction} gates still passed: {string.Join(", ", passed)}. A gate "
            + "handed nothing has not cleared its threshold.");
    }

    /// <summary>
    /// Per gate, its own quantity removed and nothing else.
    ///
    /// Stronger than the all-absent case above, which a gate reading no evidence at all would also
    /// satisfy. The fields removed are the ones that gate's two boundary cases move, so the mapping
    /// from gate to quantity is derived from the case file rather than written down twice.
    /// </summary>
    [Theory]
    [MemberData(nameof(Directions))]
    public void A_gate_whose_own_quantity_is_absent_fails_while_the_rest_of_the_evidence_stands(string direction)
    {
        var wrong = new List<string>();

        foreach (string gate in Gates(direction))
        {
            string[] fields =
            [
                .. GateCases.All
                    .Where(c => string.Equals(c.Direction, direction, StringComparison.Ordinal)
                        && string.Equals(c.Gate, gate, StringComparison.Ordinal))
                    .SelectMany(c => c.Set.Keys)
                    .Distinct(StringComparer.Ordinal),
            ];

            Assert.True(fields.Length > 0,
                $"{direction} {gate} has no boundary case, so there is nothing to say which quantity it turns on.");

            CheckResult result = GateCases.EvaluateWithout(direction, fields)
                .Single(r => string.Equals(r.Name, gate, StringComparison.Ordinal));

            if (result.Passed)
            {
                wrong.Add($"{direction} {gate} passed with {string.Join(" and ", fields)} absent");
            }
        }

        Assert.True(wrong.Count == 0,
            $"{wrong.Count} gate(s) passed on a quantity that was never there:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>
    /// The instance that shipped, kept as a case of its own rather than left to the class above.
    ///
    /// The rules were never wrong about a zero distance: zero is inside half a range and the rule
    /// says so. What was wrong was the assembly one layer up, which computed a distance from a
    /// geometry that had no pullback and handed it over as though it were a measurement. So the
    /// assertion sits where the defect was, on the geometry and on what the detector makes of it.
    /// </summary>
    [Fact]
    public void A_thrust_that_has_not_pulled_back_yields_no_distances_rather_than_zero_ones()
    {
        // Five sessions rising to the last bar, so the extreme is the last bar and nothing follows.
        PullbackGeometry.Bar[] bars =
        [
            new(10m, 11m, 9m, 10m, 11m, 9m),
            new(11m, 13m, 10m, 12m, 13m, 10m),
            new(12m, 15m, 11m, 14m, 15m, 11m),
            new(14m, 18m, 13m, 17m, 18m, 13m),
            new(17m, 22m, 16m, 21m, 22m, 16m),
        ];

        PullbackGeometry.Pullback? pullback = PullbackGeometry.Of(bars, thrustIndex: 1, thrustSpanSessions: 1, isLong: true);

        Assert.NotNull(pullback);
        Assert.Equal(0, pullback.PullbackBars);
        Assert.Equal(pullback.Trigger, pullback.Stop);

        // What the detector must not do with it: a give-up distance of zero is not a tight stop.
        var evidence = new LongPullbackRules.LongEvidence
        {
            Close = 21m,
            MedianDollarVolume = 50_000_000m,
            AverageDailyRange = 0.08m,
            LadderGrade = "rising",
            SessionsSinceThrust = 4,
            Pullback = pullback,
            ClosesBeyondFloor = 0,
            RangeTodayOverAverage = 0.8m,
            TriggerDistanceRanges = null,
            StopDistanceRanges = null,
            ClusterCount = 3,
        };

        IReadOnlyList<CheckResult> results = LongPullbackRules.Evaluate(evidence);

        Assert.False(results.Single(r => r.Name == "exit-tight").Passed);
        Assert.False(results.Single(r => r.Name == "trigger-near").Passed);
        Assert.False(results.Single(r => r.Name == "dip-shape").Passed);
    }
}
