namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The nine sections an evidence pack contains, in the order the architecture lists them.
///
/// <b>This is the runnable copy of ARCHITECTURE.html's "What the pack contains" and not a second
/// statement of it.</b> The document is the specification, this list is what the packer builds
/// from, and `architecture-conformance` reconciles the two in both directions: a section in the
/// document and not here fails, and a section here and not in the document fails. The alternative
/// is a packer whose section list drifts from the table a person reads, which the table would go
/// on describing correctly and nobody would notice.
///
/// <b>The order is part of the version, so it is total and explicit here.</b> Byte-stability
/// requires that no section rests on the order a set or a dictionary happened to enumerate, and
/// the cheapest way to hold that is a list whose order is the document's own.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
///
/// <b>Every section is present in every pack, including the ones with nothing in them.</b> A
/// section over an empty population renders with a count of nought rather than being left out,
/// because absent and empty are different statements and only one of them is a fact about the
/// evidence. Five of the nine are empty today and will be until outcomes close, which is exactly
/// the case the rule is for.
/// </summary>
public static class PackSections
{
    /// <summary>The nine, in document order.</summary>
    public static IReadOnlyList<PackSection> Declared { get; } =
    [
        new("Population",
            "Count, date span, regime mix, setups per night, long against short",
            RestsOnClosedOutcomes: false),
        new("Loss taxonomy",
            "The four causes and unclassified, with counts",
            RestsOnClosedOutcomes: true),
        new("Ceiling gap",
            "Achieved win rate against the computed bound",
            RestsOnClosedOutcomes: true),
        new("Signal conditionals",
            "Each shown signal in deciles against forward return, best and worst excursion, with sample counts",
            RestsOnClosedOutcomes: true),
        new("Multiple comparison",
            "How many signals were screened, the false-discovery threshold admission is decided on, "
            + "and the family-wise threshold recorded beside it",
            RestsOnClosedOutcomes: false),
        new("Twin pairs",
            "A sample of near-identical setups with divergent outcomes",
            RestsOnClosedOutcomes: true),
        new("Variant history",
            "Every past proposal, what it predicted, what happened",
            RestsOnClosedOutcomes: true),
        new("Signal library",
            "The explicit list of computable signals",
            RestsOnClosedOutcomes: false),
        new("Planted null",
            "One meaningless signal, such as day of month",
            RestsOnClosedOutcomes: false),
    ];

    /// <summary>The section names alone, which is the half that enters the version tuple.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Declared.Select(s => s.Name)];
}

/// <summary>
/// One declared section: what it is called, what the document says it holds, and whether it can say
/// anything before a ten-day horizon closes.
///
/// <b><see cref="RestsOnClosedOutcomes"/> is on the record rather than inferred at the sign-off.</b>
/// Five sections rest on outcomes that have not closed, so each of their verdicts is provisional
/// against the population it will later be read over. Leaving that to be worked out from a pack
/// would make the five indistinguishable from the four that are genuinely computable now, which is
/// the fifth failure shape waiting to happen by the passage of time.
///
/// <b>The flag says the section's substance needs closed outcomes, not that it renders empty.</b>
/// The two came apart the first time the packer ran over the golden fixture: Twin pairs is one of
/// the five and it rendered with lines in it, because 6.3 made a run's window reading content in its
/// own right and a side that found no pair still states what its window held. So a pack whose
/// horizons have all closed renders none of the five empty, a pack cut before the twin finder has
/// ever run renders five, and a pack cut after it renders four. Counting the flag as a prediction of
/// emptiness would have been a figure over a population other than the one beside it.
/// </summary>
public sealed record PackSection(string Name, string Contents, bool RestsOnClosedOutcomes);
