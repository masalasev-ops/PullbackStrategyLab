namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// What the seat asks for, in one string shared by all three transports.
///
/// <b>It is here rather than in each transport because a difference between them would be a
/// confounder.</b> The success criterion compares proposals made against one pack version with
/// proposals made against another, and it holds everything else fixed. Three transports asking
/// three slightly different questions would vary the question along with the pack, and no reading
/// of the record could separate them afterwards.
/// see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version
///
/// <b>It is not part of the pack and not part of the version tuple, and that is a choice with a
/// cost.</b> The tuple is what the model was shown and judged under, and the instruction is shown
/// to it, so on a strict reading it belongs there. It is excluded because it is a property of the
/// seat rather than of the evidence, and because a version that forked when the wording of the
/// question changed would fork for a reason nobody reading the hit-rate table could interpret. What
/// closes the gap is that this string is a constant in the shipped source, so a change to it is a
/// change to a commit and is visible in the history rather than only in behaviour.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
/// </summary>
public static class ProposalPrompt
{
    /// <summary>
    /// The instruction, verbatim.
    ///
    /// <b>Abstention is offered first and named as a result.</b> A weekly job that must produce a
    /// suggestion will produce one in weeks when there is nothing there, and a prompt that mentions
    /// abstention only as an afterthought is a prompt that asks for an answer.
    /// see: Abstention is a valid recorded proposal outcome
    ///
    /// <b>It says what the null control is not.</b> It does not: naming the planted signal here
    /// would disarm the tripwire, which works precisely because a model that rummages through the
    /// deciles has no way to know which of them cannot matter.
    /// see: One meaningless signal is planted in the conditional tables
    /// </summary>
    public const string Instruction = """
        You are reading an evidence pack from a paper-trading laboratory that tests two mirror-image
        price patterns on US equities. Your task is to propose at most one change to the selection
        rule, or to ask for one measurement the lab does not yet compute, or to state that the
        evidence supports neither.

        Abstaining is a result and not a failure. If the evidence is thin, if no signal separates
        outcomes, or if the population is too small to support a claim, abstain and say why. A
        proposal made because one was asked for is worse than no proposal.

        You may propose exactly one change: one threshold, on one side, moving from the value the
        pack states it is in force at, to a new value. You may not change what a gate computes, add
        a gate, remove a gate, or move two thresholds. Those have no representation here and would
        be refused.

        Answer with a single JSON object and nothing else. No prose before it, no code fence around
        it.

        To propose a change, answer in this shape:

        {
          "outcome": "proposed",
          "family": "selection",
          "change": {
            "direction": "long",
            "gate": "dip-shape",
            "threshold_name": "maximum-retrace",
            "from": 0.5,
            "to": 0.45
          },
          "mechanism": "one sentence saying why this should work, committed before any result exists",
          "evidence_setup_ids": ["setup ids from the pack"],
          "evidence_signals": ["signal names from the pack your reasoning rests on"],
          "refutation": "what observation would show this is wrong",
          "observations_to_settle": 400
        }

        The direction is "long" or "short". The gate and the threshold name are copied from the
        rule in force, which is the pack's first section. "from" is the value that section states
        the threshold is in force at and "to" is the value you propose; both are numbers, not
        quoted strings. The family is "selection" or "execution", and the rule in force says which
        family each threshold belongs to.

        You may instead ask for a signal that does not exist. Do that when two setups in the twin
        pairs section look the same in everything the pack records and ended somewhere different,
        and you can name a measurement that would tell them apart. That is a build task rather than
        a rule change, and it is the channel that widens what every future proposal can reach.

        {
          "outcome": "requested",
          "requested_signal": "a short name for the measurement you want computed",
          "requested_axis": "which axis it belongs to: price path, volume, time, position relative
                             to the market, position relative to sector, this security's own
                             history, the event calendar, or intraday shape",
          "mechanism": "one sentence saying why this would separate the pair",
          "evidence_setup_ids": ["at least two setup ids from the pack that you cannot separate"]
        }

        A request names no threshold, no direction, no family and no observation count. It is
        answered by computing the signal and vetting it, not by waiting.

        To abstain, answer in this shape:

        {
          "outcome": "abstained",
          "abstained_because": "one sentence saying what the evidence does not support"
        }

        An abstention carries no change, no family and no observation count. Do not fill those
        fields to satisfy the schema; leave them out.

        Every setup id and signal name you cite must appear in the pack. Do not invent one.
        """;
}
