using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The proposal document: what a seat's answer has to be before anything will accept it.
///
/// <b>The contract is checked against the rule in force, not against a shape.</b> Four of the five
/// change fields are answerable from the rule the pack states, so a direction that is neither side,
/// a gate the threshold does not belong to, a threshold name the rule does not carry and a
/// from-value the rule does not hold are all wrong in a way that can be said at the seat rather than
/// discovered at the registry.
/// see: A proposal is a document whose one change is the five fields the version register already stores
/// </summary>
public sealed class ProposalDocumentTests
{
    private static readonly decimal InForce = SelectionRule.Long.Value(SelectionRule.MaximumRetrace);

    [Fact]
    public void A_well_formed_proposal_reads_and_has_nothing_wrong_with_it()
    {
        ProposalDocument document = Read($$"""
            {"outcome":"proposed","family":"selection",
             "change":{"direction":"long","gate":"dip-shape","threshold_name":"maximum-retrace",
                       "from":{{InForce}},"to":{{InForce - 0.05m}}},
             "mechanism":"a shallower dip keeps the thrust intact",
             "evidence_setup_ids":["AAPL-2026-08-20-long"],
             "evidence_signals":["retrace_depth"],
             "refutation":"no better over a full quarter","observations_to_settle":400}
            """);

        Assert.Empty(document.Problems(SelectionRule.Long, SelectionRule.Short));
        Assert.False(document.IsAbstention);
        Assert.Equal(InForce, document.Change!.From);
    }

    [Fact]
    public void A_quoted_number_reads_and_a_worded_one_does_not()
    {
        // Tolerance in the one place it saves a week and nowhere else. A quoted number is the
        // commonest way a model answers the question correctly and fails the schema; a value that
        // is not a decimal at all is still a parse failure rather than a guess.
        ProposalDocument quoted = Read($$"""
            {"outcome":"proposed","family":"selection",
             "change":{"direction":"long","gate":"dip-shape","threshold_name":"maximum-retrace",
                       "from":"{{InForce}}","to":"{{InForce - 0.05m}}"},
             "mechanism":"m","evidence_setup_ids":["a"],"evidence_signals":["retrace_depth"],
             "refutation":"r","observations_to_settle":400}
            """);

        Assert.Equal(InForce, quoted.Change!.From);

        ProposalParse worded = ProposalDocument.Parse("""
            {"outcome":"proposed","change":{"direction":"long","gate":"dip-shape",
             "threshold_name":"maximum-retrace","from":"about 0.5","to":0.45}}
            """);

        Assert.False(worded.Read);
        Assert.Contains(worded.Problems, p => p.Contains("agreed JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void An_abstention_carrying_a_change_is_two_answers_and_is_refused_as_both()
    {
        // The 6.4 finding at its root: a schema that required a change whatever the outcome made a
        // model fill the gate field with whatever the pack put in front of it. Reported rather than
        // silently keeping one of the two, because keeping one is the reading that loses the other.
        ProposalDocument document = Read("""
            {"outcome":"abstained","abstained_because":"thin",
             "change":{"direction":"long","gate":"day_of_month","threshold_name":"maximum-retrace",
                       "from":0.5,"to":0.45},
             "family":"selection","observations_to_settle":400}
            """);

        IReadOnlyList<string> problems = document.Problems(SelectionRule.Long, SelectionRule.Short);

        Assert.Contains(problems, p => p.Contains("carries no change", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("belongs to no family", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("states an observation count", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proposal_moving_a_threshold_from_a_value_it_does_not_hold_is_refused()
    {
        // The field the pack's first section exists to supply. A proposal that moves a threshold
        // from a value it does not hold is a proposal about a rule that is not running.
        ProposalDocument document = Read($$"""
            {"outcome":"proposed","family":"selection",
             "change":{"direction":"long","gate":"dip-shape","threshold_name":"maximum-retrace",
                       "from":{{InForce + 0.11m}},"to":{{InForce}}},
             "mechanism":"m","evidence_setup_ids":["a"],"evidence_signals":["retrace_depth"],
             "refutation":"r","observations_to_settle":400}
            """);

        Assert.Contains(
            document.Problems(SelectionRule.Long, SelectionRule.Short),
            p => p.Contains("is in force at", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gate_a_threshold_does_not_belong_to_and_a_move_to_the_value_it_holds_are_both_refused()
    {
        ProposalDocument wrongGate = Read($$"""
            {"outcome":"proposed","family":"selection",
             "change":{"direction":"long","gate":"tradable","threshold_name":"maximum-retrace",
                       "from":{{InForce}},"to":{{InForce}}},
             "mechanism":"m","evidence_setup_ids":["a"],"evidence_signals":["retrace_depth"],
             "refutation":"r","observations_to_settle":400}
            """);

        IReadOnlyList<string> problems = wrongGate.Problems(SelectionRule.Long, SelectionRule.Short);

        Assert.Contains(problems, p => p.Contains("belongs to gate \"dip-shape\"", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("which is no change", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recorded_threshold_gates_nothing_so_moving_it_is_refused()
    {
        // The cluster threshold is recorded on every row and gates nothing, so a version moving it
        // selects nothing differently. Named in the rule in force so the model can read why.
        decimal cluster = SelectionRule.Long.Value(SelectionRule.ClusterThreshold);

        ProposalDocument document = Read($$"""
            {"outcome":"proposed","family":"selection",
             "change":{"direction":"long","gate":"cluster","threshold_name":"cluster-threshold",
                       "from":{{cluster}},"to":{{cluster + 1m}}},
             "mechanism":"m","evidence_setup_ids":["a"],"evidence_signals":["cluster_count"],
             "refutation":"r","observations_to_settle":400}
            """);

        Assert.Contains(
            document.Problems(SelectionRule.Long, SelectionRule.Short),
            p => p.Contains("selects nothing differently", StringComparison.Ordinal));
    }

    [Fact]
    public void An_abstention_rests_on_no_signal_at_all()
    {
        ProposalDocument document = Read($$"""
            {"outcome":"abstained","abstained_because":"the only separation is in
             {{SignalLibrary.NullControl}}, which is not a mechanism"}
            """.ReplaceLineEndings(" "));

        Assert.Empty(document.Problems(SelectionRule.Long, SelectionRule.Short));
        Assert.Empty(document.SignalsRestedOn(SelectionRule.Long));
        Assert.False(PackVersions.CitesTheNullControl(document.SignalsRestedOn(SelectionRule.Long)));
    }

    [Fact]
    public void Prose_and_an_empty_answer_are_both_recorded_failures_rather_than_exceptions()
    {
        Assert.False(ProposalDocument.Parse("I would loosen the retrace gate.").Read);
        Assert.False(ProposalDocument.Parse("   ").Read);
        Assert.False(ProposalDocument.Parse(null).Read);
        Assert.False(ProposalDocument.Parse("null").Read);

        Assert.Contains(
            ProposalDocument.Parse(null).Problems,
            p => p.Contains("returned nothing to read", StringComparison.Ordinal));
    }

    private static ProposalDocument Read(string json)
    {
        ProposalParse parse = ProposalDocument.Parse(json);
        Assert.True(parse.Read, string.Join("; ", parse.Problems));
        return parse.Document!;
    }
}
