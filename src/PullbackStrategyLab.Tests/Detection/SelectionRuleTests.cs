using System.Text.Json;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Tests.Checks;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// The representation a selection rule is written in, proved to be the one thing the detector reads
/// and the one thing a harness could read, over the authored gate cases.
///
/// <b>Authored cases, because no version exists and none can until 5.1.</b> What is proved here is a
/// property of the representation and of the one implementation over it, not a fact about the
/// market: the same evidence under the same rule gives the same verdicts whichever caller asks, a
/// moved threshold moves its own gate and no other, and admission tells the three failing shapes
/// apart by name.
/// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
/// see: A version is admitted when exactly one selection threshold differs from the baseline, and the assertion names the gate that moved
/// </summary>
public sealed class SelectionRuleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TheoryData<string> Directions => new(SetupDirection.Long, SetupDirection.Short);

    private static string Verdicts(IReadOnlyList<CheckResult> results) => JsonSerializer.Serialize(results, Json);

    [Fact]
    public void The_detector_and_a_harness_read_one_implementation()
    {
        // The detector calls Evaluate(evidence), a harness calls Evaluate(evidence, rule) with the
        // baseline. If the two could differ there would be two implementations, and the acceptance
        // test at 5.3 would be reproducing one of them with the other.
        foreach (GateCases.GateCase gateCase in GateCases.All)
        {
            string detector = Verdicts(GateCases.Evaluate(gateCase));
            string harness = Verdicts(GateCases.Evaluate(gateCase, SelectionRule.For(gateCase.Direction)));
            Assert.True(detector == harness, $"{gateCase.Id}: the detector's verdicts and the baseline rule's differ.");
        }
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void The_gate_list_is_the_documents_and_every_threshold_belongs_to_a_gate_on_it(string direction)
    {
        string architecture = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "docs", "ARCHITECTURE.html"));
        IReadOnlyList<string> documented = HtmlCheckList.NamesUnder(
            architecture,
            direction == SetupDirection.Long ? CheckCompletenessCheck.LongHeading : CheckCompletenessCheck.ShortHeading);
        SelectionRule rule = SelectionRule.For(direction);

        Assert.Equal(documented, rule.Gates);
        foreach (RuleThreshold threshold in rule.Thresholds)
        {
            Assert.Contains(threshold.Gate, rule.Gates);
        }

        // Names are unique within a rule, because admission compares thresholds by name.
        Assert.Equal(rule.Thresholds.Count, rule.Thresholds.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Every_frozen_signal_a_threshold_names_is_an_active_signal_the_schema_defines(string direction)
    {
        // The harness at 5.3 rebuilds evidence from setup_signal, so a threshold whose quantity is
        // frozen nowhere is a threshold no replay can apply. Asserted now, against SCHEMA's own
        // signal tables, so 5.3 cannot find one.
        string schema = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "docs", "SCHEMA.md"));
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(schema, @"^\| `(?<name>[a-z0-9_]+)` \|.*\| active \|\s*$", RegexOptions.Multiline))
        {
            active.Add(match.Groups["name"].Value);
        }

        Assert.True(active.Count >= 20, $"Only {active.Count} active signals were parsed from SCHEMA.md, which means the pattern stopped matching.");

        foreach (RuleThreshold threshold in SelectionRule.For(direction).Thresholds)
        {
            Assert.NotEmpty(threshold.FrozenSignals);
            foreach (string signal in threshold.FrozenSignals)
            {
                Assert.True(active.Contains(signal), $"{direction} {threshold.Gate} {threshold.Name} names '{signal}', which SCHEMA.md does not define as an active signal.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Moving_one_threshold_moves_its_own_gate_and_no_other(string direction)
    {
        SelectionRule baseline = SelectionRule.For(direction);
        GateCases.GateCase[] cases = [.. GateCases.All.Where(c => c.Direction == direction)];

        foreach (RuleThreshold threshold in baseline.Thresholds)
        {
            bool flippedSomething = false;

            foreach (decimal moved in Moves(threshold.Value))
            {
                SelectionRule version = baseline.With(threshold.Name, moved);

                foreach (GateCases.GateCase gateCase in cases)
                {
                    IReadOnlyList<CheckResult> before = GateCases.Evaluate(gateCase, baseline);
                    IReadOnlyList<CheckResult> after = GateCases.Evaluate(gateCase, version);

                    for (int i = 0; i < before.Count; i++)
                    {
                        bool same = Verdicts([before[i]]) == Verdicts([after[i]]);
                        if (before[i].Name == threshold.Gate)
                        {
                            flippedSomething |= before[i].Passed != after[i].Passed;
                        }
                        else
                        {
                            Assert.True(same, $"{direction}: moving {threshold.Name} to {moved} changed {before[i].Name} on {gateCase.Id}, and it belongs to {threshold.Gate}.");
                        }
                    }
                }
            }

            Assert.True(flippedSomething, $"{direction} {threshold.Gate} {threshold.Name}: no move of the threshold changed any case's verdict, so the threshold is not live.");
        }
    }

    // Near moves and far ones. A gate whose authored cases sit well inside the threshold, as the
    // price floor's do at $50 against $5, is only shown live by a move that reaches them.
    private static IEnumerable<decimal> Moves(decimal value) =>
        value == 0m
            ? [1m, 2m, 1000m]
            : [value / 2m, value * 2m, value + 1m, Math.Max(0m, value - 1m), value / 1000m, value * 1000m];

    [Theory]
    [MemberData(nameof(Directions))]
    public void Admission_refuses_the_baseline_itself_by_name(string direction)
    {
        AdmissionVerdict verdict = RuleAdmission.Assert(SelectionRule.For(direction), SelectionRule.For(direction));
        Assert.False(verdict.IsAdmitted);
        Assert.Equal(RuleAdmission.NothingMoved, verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Admission_refuses_two_moved_thresholds_and_names_both(string direction)
    {
        SelectionRule baseline = SelectionRule.For(direction);
        SelectionRule candidate = direction == SetupDirection.Long
            ? baseline.With(SelectionRule.MaximumRetrace, 0.45m).With(SelectionRule.TriggerReachRanges, 2m)
            : baseline.With(SelectionRule.MaximumRetrace, 0.45m).With(SelectionRule.CeilingReachRanges, 0.75m);

        AdmissionVerdict verdict = RuleAdmission.Assert(candidate, baseline);
        Assert.False(verdict.IsAdmitted);
        Assert.StartsWith(RuleAdmission.MoreThanOneMoved, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(SelectionRule.MaximumRetrace, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains(direction == SetupDirection.Long ? SelectionRule.TriggerReachRanges : SelectionRule.CeilingReachRanges, verdict.Reason, StringComparison.Ordinal);
        Assert.NotEqual(RuleAdmission.NothingMoved, verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Admission_admits_one_selection_threshold_and_names_the_gate(string direction)
    {
        SelectionRule baseline = SelectionRule.For(direction);
        AdmissionVerdict verdict = RuleAdmission.Assert(baseline.With(SelectionRule.MaximumRetrace, 0.45m), baseline);

        Assert.True(verdict.IsAdmitted, verdict.Reason);
        Assert.Equal(direction == SetupDirection.Long ? "dip-shape" : "bounce-shape", verdict.Gate);
        Assert.Equal(SelectionRule.MaximumRetrace, verdict.Threshold);
        Assert.Equal(0.40m, verdict.From);
        Assert.Equal(0.45m, verdict.To);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Admission_refuses_an_execution_threshold_a_recorded_one_and_a_wider_window(string direction)
    {
        SelectionRule baseline = SelectionRule.For(direction);

        AdmissionVerdict execution = RuleAdmission.Assert(baseline.With(SelectionRule.GiveUpRanges, 0.75m), baseline);
        Assert.False(execution.IsAdmitted);
        Assert.StartsWith(RuleAdmission.NotSelectionFamily, execution.Reason, StringComparison.Ordinal);

        AdmissionVerdict recorded = RuleAdmission.Assert(baseline.With(SelectionRule.ClusterThreshold, 3m), baseline);
        Assert.False(recorded.IsAdmitted);
        Assert.StartsWith(RuleAdmission.NotSelectionFamily, recorded.Reason, StringComparison.Ordinal);

        AdmissionVerdict wider = RuleAdmission.Assert(baseline.With(SelectionRule.ThrustWindowSessions, 15m), baseline);
        Assert.False(wider.IsAdmitted);
        Assert.StartsWith(RuleAdmission.WidensAssembly, wider.Reason, StringComparison.Ordinal);

        AdmissionVerdict tighter = RuleAdmission.Assert(baseline.With(SelectionRule.ThrustWindowSessions, 5m), baseline);
        Assert.True(tighter.IsAdmitted, tighter.Reason);
        Assert.Equal("thrust", tighter.Gate);
    }

    [Fact]
    public void Admission_refuses_a_different_gate_list_as_structural()
    {
        AdmissionVerdict verdict = RuleAdmission.Assert(SelectionRule.Short, SelectionRule.Long);
        Assert.False(verdict.IsAdmitted);
        Assert.Equal(RuleAdmission.DifferentGateList, verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void The_baseline_is_built_from_the_pinned_constants(string direction)
    {
        SelectionRule rule = SelectionRule.For(direction);
        Assert.Equal(LongPullbackRules.GiveUpRanges, rule.Value(SelectionRule.GiveUpRanges));
        Assert.Equal(LongPullbackRules.MaximumRetrace, rule.Value(SelectionRule.MaximumRetrace));
        Assert.Equal(LongPullbackRules.ThrustWindowSessions, rule.Value(SelectionRule.ThrustWindowSessions));
        Assert.Equal(
            direction == SetupDirection.Long ? LongPullbackRules.LiquidityFloor : ShortPullbackRules.LiquidityFloor,
            rule.Value(SelectionRule.LiquidityFloor));
    }
}
