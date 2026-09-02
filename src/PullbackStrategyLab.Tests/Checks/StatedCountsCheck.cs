using System.Globalization;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every count a spec states about itself matches the derived count.
///
/// Prose counts go stale silently. A header stating a checkpoint count over a table with a
/// different number of rows, or a total that does not add up, reads as authoritative and is
/// wrong. Any number a spec states about its own contents is derived from the document it
/// describes and checked here, or it is not written.
///
/// Records are exempt. An entry in PROGRESS states what was measured on a date; it is
/// history rather than a claim about the corpus today.
/// </summary>
public sealed partial class StatedCountsCheck
{
    private readonly ITestOutputHelper _output;

    public StatedCountsCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A row of ARCHITECTURE's authored-parameters table whose parameter name carries the OPEN mark.
    ///
    /// Anchored on the row opening and the first cell, so a later cell mentioning the word in prose
    /// is not counted. That matters here: the paragraph above the table and several Basis cells use
    /// "OPEN" in a sentence, and a bare search for the mark reads them as rows.
    /// </summary>
    /// <summary>
    /// The sentence the carried obligations table states its own total in.
    ///
    /// It sits in the 4.17 section, which is where the count has been written since that
    /// section was created, and it survived the pile the section was about being discharged.
    /// </summary>
    [GeneratedRegex(@"of the (?<total>[a-z-]+) rows above fall due at 4\.17", RegexOptions.CultureInvariant)]
    private static partial Regex ObligationsTotalSentence();

    [GeneratedRegex(@"<tr><td>[^<]*<b>OPEN</b>", RegexOptions.CultureInvariant)]
    private static partial Regex OpenParameterRow();

    [GeneratedRegex(@"^\s*(?<n>\d+)\.\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"-?\d[\d,]*", RegexOptions.CultureInvariant)]
    private static partial Regex Integer();

    [GeneratedRegex(@"^## Phase \d", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseHeading();

    [GeneratedRegex(@"(?<n>\d*)N", RegexOptions.CultureInvariant)]
    private static partial Regex TermInN();

    /// <summary>
    /// The sentence the classification section opens with, which states the count due at 4.1 and
    /// the obligations table's own total in one breath. Both were C# literals until 3.15's fifth
    /// finding; matching the sentence pins its shape as well as reading the two figures out of it.
    /// </summary>
    [GeneratedRegex(@"(?<due>[A-Za-z][a-z-]*) of the (?<total>[a-z-]+) rows above fall due at 4\.1",
        RegexOptions.CultureInvariant)]
    private static partial Regex OpeningSentence();

    /// <summary>
    /// The classification heading, which states the same count a third time and is the anchor its
    /// table is read from. Matched rather than spelled out, so the count is not written here.
    /// </summary>
    [GeneratedRegex(@"^### What the (?<due>[a-z-]+) due at 4\.1 are[^\n]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ClassificationHeading();

    /// <summary>
    /// The heading over the operator's questions, which states how many there are. The count in it
    /// went stale at 3.14 and nothing here was reading it.
    /// </summary>
    [GeneratedRegex(@"^### The (?<count>[a-z-]+) that are the operator's[^\n]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex OperatorHeading();

    /// <summary>
    /// The same two sentences for the 4.17 pile, which is the third classification of the same table
    /// and was written when 4.6 emptied the second.
    ///
    /// A pattern per section rather than one taking the checkpoint as a group, because the sections
    /// are read separately and a pattern matching any of them would let one section's figures answer
    /// for another's. That is the eighth failure shape in CLAUDE.md, which is a clause applied to a
    /// population other than the one it governs, and this registry is not the place to introduce it.
    ///
    /// The 4.1 and 4.6 patterns are gone rather than pointed at their emptied sections, on the rule
    /// those sections state: a count in a section with nothing left to count is a sentence written
    /// to keep a claim passing.
    /// </summary>
    [GeneratedRegex(@"(?<due>[A-Za-z][a-z-]*) of the (?<total>[a-z-]+) rows above fall due at 4\.17",
        RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionsOpeningSentence();

    /// <summary>
    /// The count of obligations due before the freeze, stated three times in one paragraph and in
    /// the 5.1 row above it.
    ///
    /// It is here because it went stale exactly the way this check exists to prevent: the paragraph
    /// read "the ten obligations due before the freeze" while eleven rows carried 5.1 as their due
    /// point, and nothing was comparing the two. Every occurrence is matched rather than the first,
    /// so a pass that updates one of the four and forgets the rest fails.
    /// </summary>
    [GeneratedRegex(@"[Tt]he (?<due>[a-z-]+) (?:obligations due before the freeze|sit\s+between 4\.13 and 5\.1)",
        RegexOptions.CultureInvariant)]
    private static partial Regex FreezeObligationCount();

    [GeneratedRegex(@"^### What the (?<due>[a-z-]+) due at 4\.17 are[^\n]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionsClassificationHeading();

    /// <summary>
    /// The mark a carried obligation row carries when it is one of the phase 5 questions.
    ///
    /// A mark rather than a pattern over the prose, because the thing being counted is not visible
    /// in the sentence: a question and a repair read alike, and the reading beside the operator's
    /// table used to state how many rows block a checkpoint, which no derivation survives. A blocks
    /// cell naming the row it is a twin of looks exactly like one naming a checkpoint it stops.
    /// </summary>
    [GeneratedRegex(@"\*\*Question \d of the phase 5 sitting", RegexOptions.CultureInvariant)]
    private static partial Regex PhaseFiveQuestion();

    /// <summary>The same mark at the head of the operator table's own cell, which is the other half
    /// of the reconciliation: a question that leaves one table has to leave the other.</summary>
    [GeneratedRegex(@"^Question \d\.", RegexOptions.CultureInvariant)]
    private static partial Regex MarkedQuestion();

    /// <summary>
    /// The ruling half of a question whose row was split in two, counted from the sentence that
    /// names what it was split from rather than from the sentence that states how many were split.
    /// The work halves say "on the rule four rows took", which holds the figure being asserted, and
    /// a claim reading its own subject's statement of itself asserts nothing.
    /// </summary>
    [GeneratedRegex(@"Split on 2026-09-02 from the row raised at", RegexOptions.CultureInvariant)]
    private static partial Regex SplitQuestionRow();

    [Fact]
    [Trait("check", "stated-counts")]
    public void Every_count_a_spec_states_about_itself_is_derived_and_matches()
    {
        var coverage = new CheckCoverage("stated-counts", _output);
        string claude = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, "CLAUDE.md"));
        string architecture = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));
        string buildPlan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));
        string runbook = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "RUNBOOK.md"));

        var claims = new List<Claim>();

        // CLAUDE.md, the seven done conditions over the numbered list that follows.
        string doneSection = Between(claude, "## Definition of done for a checkpoint", "## Stopping rules");
        Assert.Contains("All seven, or it is not done", doneSection, StringComparison.Ordinal);
        claims.Add(new Claim(
            "CLAUDE.md, all seven done conditions",
            Stated: 7,
            Derived: NumberedItem().Matches(doneSection).Count,
            Derivation: "numbered items under Definition of done for a checkpoint"));

        // CLAUDE.md, five specs and three records, over the lifecycle table.
        //
        // <b>The artefact row went at 4.12 and its claim went with it, rather than being kept at
        // nought.</b> A claim asserting that the table holds no artefact would pass on a table with
        // no rows at all, and the total below it is what actually holds the shape: eight rows, five
        // of them specs and three records, reconciled in three directions.
        IReadOnlyList<IReadOnlyList<string>> lifecycle = MarkdownTable.BodyRowsAfter(claude, "## Document lifecycle");
        Assert.Contains("Five specs and three records.", claude, StringComparison.Ordinal);
        claims.Add(new Claim("CLAUDE.md, five specs", 5, KindCount(lifecycle, "spec"), "lifecycle rows marked spec"));
        claims.Add(new Claim("CLAUDE.md, three records", 3, KindCount(lifecycle, "record"), "lifecycle rows marked record"));
        claims.Add(new Claim("The corpus is eight documents", 8, lifecycle.Count, "rows of the lifecycle table"));

        // BUILD_PLAN.md, the authored parameters still open, over the table itself.
        //
        // <b>Registered at 4.4 because it had just been wrong in three documents at once.</b> The
        // table carried eleven OPEN rows and 4.4 filled one, and the row, the CHANGELOG entry and
        // the PROGRESS entry all said nine remained. Nothing derived it, so a count stated three
        // times was one out and no check could see it. It is a count a spec states about another
        // document's contents rather than its own, which is why it was never registered; the rule is
        // about numbers a spec states, and the document being counted is not what makes it stale.
        // <b>The stated figure was a C# literal until 4.15, which is the same defect one level
        // down.</b> The commit that registered this claim said it was registered so the count could
        // not drift again, and it left the number it was comparing against typed into this file: a
        // count derived on one side and hard-coded on the other still needs a person to remember.
        // 4.15 filled the last ten rows, so the figure had to move, and moving it is what showed
        // that nothing would have failed if it had not. It is read out of BUILD_PLAN's own sentence
        // now, so the document that states it is the document that has to be edited.
        claims.Add(new Claim(
            "BUILD_PLAN.md, the authored parameters left open",
            Stated: InWords(buildPlan, "and ", " rows of the authored-parameters table remain open"),
            Derived: OpenParameterRow().Matches(architecture).Count,
            Derivation: "rows of the authored-parameters table marked OPEN"));

        // ARCHITECTURE.html, the component count over the catalogue itself.
        IReadOnlyList<IReadOnlyList<string>> catalogue = HtmlTable.BodyRowsUnder(architecture, "Component catalogue");
        claims.Add(new Claim(
            "ARCHITECTURE.html, the components listed by layer",
            StatedBetween(architecture, "The ", " components are listed by layer"),
            catalogue.Count,
            "rows of the component catalogue"));

        // ARCHITECTURE.html, the two check lists, each stated as ten in the catalogue.
        IReadOnlyList<string> longChecks = HtmlCheckList.NamesUnder(architecture, "The long checks buy");
        IReadOnlyList<string> shortChecks = HtmlCheckList.NamesUnder(architecture, "The short checks sell");
        Assert.Contains("ten checks, all results kept", architecture, StringComparison.Ordinal);
        claims.Add(new Claim("LongSetupDetector, ten checks", 10, longChecks.Count, "rows of the long check list"));
        claims.Add(new Claim("ShortSetupDetector, ten checks", 10, shortChecks.Count, "rows of the short check list"));

        // ARCHITECTURE.html, the split stated above the long check list.
        Assert.Contains("The first four are cheap filters", architecture, StringComparison.Ordinal);
        Assert.Contains("The last six are the pattern test", architecture, StringComparison.Ordinal);
        claims.Add(new Claim(
            "ARCHITECTURE.html, the first four and the last six",
            4 + 6,
            longChecks.Count,
            "rows of the long check list against the split stated above it"));

        // ARCHITECTURE.html, the rows the one-time calibration at 2.11 revisits. It read "four
        // thresholds" over three marked rows until the 2.1 spec pass, because the stated count was
        // of numbers and the table is of rows, and the pullback-shape row carries two numbers.
        // Nothing derived either figure, so both drifted. Counted over rows now, which is the unit
        // the table actually has.
        IReadOnlyList<IReadOnlyList<string>> authored = HtmlTable.BodyRowsUnder(architecture, "Authored parameters");
        Assert.Contains(
            "Five rows of the authored-parameters table are marked \"phase 2 count check\"",
            architecture,
            StringComparison.Ordinal);
        claims.Add(new Claim(
            "ARCHITECTURE.html, the rows marked phase 2 count check",
            5,
            authored.Count(r => r.Count > 2 && r[2].Equals("Phase 2 count check", StringComparison.OrdinalIgnoreCase)),
            "rows of the authored parameters table whose review point is the phase 2 count check"));

        // BUILD_PLAN.md, six phases.
        Assert.Contains("Six phases.", buildPlan, StringComparison.Ordinal);
        claims.Add(new Claim("BUILD_PLAN.md, six phases", 6, PhaseHeading().Matches(buildPlan).Count, "phase headings"));

        // BUILD_PLAN.md, the obligations classified at 4.1 against the obligations table itself.
        //
        // Four numbers in one section, and every one of them is a count of rows somewhere else in
        // the same document: the total due at 4.1, and the three groups it is split into. The
        // classification's whole value is that the three add up to the pile, so a group that
        // silently stops covering the table is the one thing that would make the section worse than
        // not having written it. Derived from the obligations table rather than from the prose,
        // which is what "a number a spec states about its own contents" means.
        IReadOnlyList<IReadOnlyList<string>> obligations =
            MarkdownTable.BodyRowsAfter(buildPlan, "## Carried obligations");
        int dueAtTheWatchlist = obligations.Count(
            r => r.Count > 2 && r[2].Trim().Equals("4.1", StringComparison.Ordinal));

        // <b>The 4.1 classification stated four of these figures and states none now</b>, because
        // 4.1 discharged both its rows and the pile is nought. Its section is a record of what the
        // two were rather than a classification of what is left, so there is no count in it to
        // derive, and the claims that read it are removed rather than pointed at a sentence written
        // to keep them passing. The same four figures are asserted of the 4.6 pile below, which was
        // registered in the commit that wrote it for exactly this reason: a second classification
        // existed before the first one emptied, so nothing was lost when it did.
        //
        // `dueAtTheWatchlist` stays as the assertion that the pile really is empty, which is the one
        // thing worth saying about it and is not a stated count.
        Assert.True(dueAtTheWatchlist == 0,
            $"{dueAtTheWatchlist} obligation row(s) fall due at 4.1, which PROGRESS records as landed with both of "
            + "its rows discharged. Either a row was repointed there after the fact, or the discharge did not happen.");

        // <b>The 4.6 classification stated four of these figures and states none now</b>, for the
        // reason the 4.1 one does: 4.6 discharged six of its nineteen and repointed the other
        // thirteen whole, so its section is a record of what the pile was. The same four figures are
        // asserted of the 4.17 pile below, which was registered in the commit that created it, so
        // nothing was lost when this one emptied. That is the second time this has happened and it
        // is the pattern rather than a coincidence: a pile forms at the next checkpoint that
        // plausibly cares, is classified, and empties when that checkpoint lands.
        int dueAtTheRiskGate = obligations.Count(
            r => r.Count > 2 && r[2].Trim().Equals("4.6", StringComparison.Ordinal));

        Assert.True(dueAtTheRiskGate == 0,
            $"{dueAtTheRiskGate} obligation row(s) fall due at 4.6, which PROGRESS records as landed with six of "
            + "its rows discharged and thirteen repointed. Either a row was repointed there after the fact, or "
            + "the ruling did not happen.");

        // BUILD_PLAN.md, the obligations due before the freeze, stated three times in three places.
        //
        // Found stale at 4.7, by one. The paragraph and the 5.1 row both read "ten" while eleven rows
        // carried 5.1 as their due point, and the sentence says outright that they are the rows in
        // the table with 5.1 as their due point, so the figure was derivable the whole time and
        // nothing derived it. Every occurrence is asserted rather than the first, because a count
        // stated in three places is three places to forget.
        //
        // <b>The basis is three checkpoints from the phase 5 planning pass of 2026-09-02, and it was
        // one until then.</b> The property this figure holds is "due before the baseline is frozen",
        // and that was the same set as "due at 5.1" only while 5.1 was the first checkpoint of its
        // phase. The plan now opens phase 5 at 5.0 and holds the repairs at 5.8, both of which land
        // before the freeze, so a derivation keyed on 5.1 alone would have read nought against a
        // stated seventeen and the count would have looked wrong when the basis was. Keyed on the
        // set rather than on the one identifier, which is what the sentence beside it has always
        // meant: 5.1's own deliverable is the freeze, so an obligation due before it is due before
        // that done condition rather than alongside it.
        string[] beforeTheFreeze = ["5.0", "5.8", "5.1"];
        int dueBeforeTheFreeze = obligations.Count(
            r => r.Count > 2 && beforeTheFreeze.Contains(r[2].Trim(), StringComparer.Ordinal));

        MatchCollection freezeCounts = FreezeObligationCount().Matches(buildPlan);

        Assert.True(freezeCounts.Count >= 3,
            $"BUILD_PLAN.md states the count of obligations due before the freeze {freezeCounts.Count} time(s). "
            + "It has stated it at least three times since the phase was planned, so the pattern stopped "
            + "matching rather than the paragraph getting shorter.");

        int stated = 0;

        foreach (Match occurrence in freezeCounts)
        {
            claims.Add(new Claim(
                $"BUILD_PLAN.md, the obligations due before the freeze, statement {++stated}",
                FromWordsOrFail(occurrence.Groups["due"].Value),
                dueBeforeTheFreeze,
                "rows of the carried obligations table falling due at 5.1"));
        }

        // BUILD_PLAN.md, the carried obligations table's own total.
        //
        // <b>The three figures beside it went at 4.17 with the pile they counted.</b> They were the
        // count due at 4.17, the same count in the section's heading, and the three group counts
        // summing to it. 4.17 discharged twelve of the thirteen and repointed the last, so the pile
        // is nought and its section is a record of what it was, on the terms the 4.6 section became
        // one. A claim asserting that a discharged pile has nought rows in it would pass over a
        // table with no rows at all, and the total below is what actually holds the shape.
        //
        // Registered in the commit that writes it rather than after the first time it goes stale,
        // which is what happened to the operator's heading below and to the permit sentence further
        // down: both were prose counts of the same table that nothing read, and both were wrong by
        // the time anyone looked.
        Match obligationsTotal = ObligationsTotalSentence().Match(buildPlan);
        Assert.True(obligationsTotal.Success,
            "BUILD_PLAN.md no longer states the carried obligations table's own total in the sentence "
            + "this claim reads it from, so the one figure that holds the table's shape is unstated.");

        claims.Add(new Claim(
            "BUILD_PLAN.md, the carried obligations table's own total",
            FromWordsOrFail(obligationsTotal.Groups["total"].Value),
            obligations.Count,
            "rows of the carried obligations table"));

        // BUILD_PLAN.md, phase 4's own checkpoint count, stated in its preamble.
        //
        // The one phase whose section says how many checkpoints it has, because it is the one whose
        // numbering stopped being its build order: three checkpoints were added on 2026-08-31 and
        // took the next free identifiers rather than being inserted, so the preamble has to say how
        // many rows there are for the reader to know none is missing. A count stated for that
        // reason is exactly the kind this registry exists for.
        claims.Add(new Claim(
            "BUILD_PLAN.md, phase 4's checkpoint count",
            InWords(buildPlan, "The phase is ", " checkpoints"),
            MarkdownTable.BodyRowsAfter(buildPlan, "## Phase 4 — Trading").Count,
            "rows of the phase 4 table"));

        // BUILD_PLAN.md, the operator's own list, and the count nothing here derived.
        //
        // Its heading states how many rows the table below it has, and two sentences beside it
        // state the same figure again. It read "eight" in both of those while the table held nine,
        // from 3.14 until this pass, and nothing could see it: this registry named the total, the
        // count due at 4.1, the three groups and the permits, and not this one. A registry cannot
        // catch an unregistered figure, which is the row raised at 3.7 about checks that reconcile
        // a hand-named list in one direction only. Registering it closes the instance rather than
        // the row.
        //
        // The second claim is the reconciliation the first cannot make: the section is a reading of
        // the obligations table, so a question that leaves the table has to leave the section too.
        Match operatorHeading = OperatorHeading().Match(buildPlan);
        Assert.True(operatorHeading.Success,
            "BUILD_PLAN.md has no \"### The <count> that are the operator's\" heading, which is both a stated "
            + "count and the anchor the operator's table is read from.");

        IReadOnlyList<IReadOnlyList<string>> operatorQuestions =
            MarkdownTable.BodyRowsAfter(buildPlan, operatorHeading.Value);

        claims.Add(new Claim(
            "BUILD_PLAN.md, the questions that are the operator's",
            FromWordsOrFail(operatorHeading.Groups["count"].Value),
            operatorQuestions.Count,
            "rows of the operator's table"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the operator's table against the obligations table",
            operatorQuestions.Count,
            obligations.Count(r => r.Count > 2 && r[2].Trim().Equals("the operator", StringComparison.Ordinal)),
            "rows of the carried obligations table falling due at the operator"));

        // BUILD_PLAN.md, phase 5's own figures, registered by the planning pass that wrote them.
        //
        // Registered in the commit that states them rather than after the first one goes stale,
        // which is the lesson the operator's heading and the permit sentence above both cost.
        //
        // <b>The nine questions are counted from a marker rather than from a pattern over prose.</b>
        // The reading beside the operator's table used to state how many rows block a checkpoint,
        // and no derivation of that survives contact with the cells: a blocks column naming the row
        // it is a twin of reads the same as one naming a checkpoint it stops. So the register carries
        // an explicit mark, "Question N of the phase 5 sitting" in the obligations table and
        // "Question N." at the head of the operator table's own cell, and the two are reconciled
        // against each other. A count nobody can derive is a count this registry declines rather than
        // one it approximates.
        claims.Add(new Claim(
            "BUILD_PLAN.md, phase 5's row count",
            InWords(buildPlan, "The phase is ", " rows"),
            MarkdownTable.BodyRowsAfter(buildPlan, "## Phase 5 — Variants, without any AI").Count,
            "rows of the phase 5 table"));

        // <b>The population is the marked rows still due at the operator, not every marked row.</b>
        // It was every marked row until the sitting of 2026-09-02, which was the same set while every
        // question was unanswered and stopped being one the moment two were repointed to 5.0 with
        // their ruling given: the obligations table then held three marked rows and the operator's
        // table one, and the reconciliation compared the two and failed. The operator's table is a
        // reading of the operator's rows and of nothing else, so that is the population, which is
        // the eighth failure shape caught by the claim it governs rather than by a reader.
        int questionsAtTheOperator = obligations.Count(
            r => r.Count > 2
                && r[2].Trim().Equals("the operator", StringComparison.Ordinal)
                && PhaseFiveQuestion().IsMatch(string.Join(" ", r)));

        claims.Add(new Claim(
            "BUILD_PLAN.md, the phase 5 questions against the operator's table",
            questionsAtTheOperator,
            operatorQuestions.Count(r => r.Count > 1 && MarkedQuestion().IsMatch(r[1])),
            "rows of the operator's table marked as a phase 5 question"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the phase 5 questions the operator's reading states",
            InWords(buildPlan, "put here: **", " of the nine rows is a phase 5 question**"),
            questionsAtTheOperator,
            "rows of the carried obligations table marked as a phase 5 question and due at the operator"));

        claims.Add(new Claim(
            "BUILD_PLAN.md, the obligations 5.8 holds",
            InWords(buildPlan, "**The repair pile**, being ", " of the eighteen"),
            obligations.Count(r => r.Count > 2 && r[2].Trim().Equals("5.8", StringComparison.Ordinal)),
            "rows of the carried obligations table falling due at 5.8"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the obligations 5.0 holds",
            InWords(buildPlan, "The other ", " fall due at 5.0"),
            obligations.Count(r => r.Count > 2 && r[2].Trim().Equals("5.0", StringComparison.Ordinal)),
            "rows of the carried obligations table falling due at 5.0"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the question rows split in two",
            InWords(buildPlan, "**Of the eighteen, ", " is two rows each"),
            SplitQuestionRow().Matches(buildPlan).Count,
            "obligation rows carrying the ruling half of a split question"));

        // BUILD_PLAN.md, the frozen-only permits, derived from the fixture rather than from the prose.
        //
        // The one number in this registry whose subject is another file, and it is here because the
        // rule is about a number a spec states rather than about where the number is derived from.
        // It read "seven" from the commit that made it eight until 3.14: the paragraph is the
        // standing instruction for what must be discharged before 4.1's entry is written, and it
        // understated the set by one. `stated-counts` is a registry and could not have caught an
        // unregistered figure, which is the row raised at 3.7 about checks that reconcile a
        // hand-named list in one direction; registering this one closes the instance rather than
        // the row.
        IReadOnlyList<FixtureReplayCheck.Permit> allPermits =
            FixtureReplayCheck.ReadExpectations().FrozenOnly ?? [];
        int permits = allPermits.Count;

        // The two figures are different quantities and were the same number until the permit shape
        // gained its settled form. How many permits the fixture holds is one thing; how many of
        // them the first run after 4.1 turns red is another, and only the ones still resting on an
        // obligation are in the second. Reading the second off the first would restate a figure
        // that has stopped meaning what it says the moment a permit is settled, which is the shape
        // this whole registry exists to refuse.
        // see: A frozen-only permit names an open obligation or the settled reason nothing could close it
        int open = allPermits.Count(p => p.Obligation is not null);

        claims.Add(new Claim(
            "BUILD_PLAN.md, the frozen-only permits the fixture holds",
            InWords(buildPlan, "`fixtures/expectations.json` names ", " frozen-only checkpoints"),
            permits,
            "entries under frozenOnly in fixtures/expectations.json"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the frozen-only permits still resting on an open obligation",
            InWords(buildPlan, "of which ", " still rest on an open obligation"),
            open,
            "entries under frozenOnly carrying an obligation rather than a settled reason"));
        claims.Add(new Claim(
            "BUILD_PLAN.md, the times the first run after 4.1 turns red",
            InWords(buildPlan, "turns red ", " times over"),
            open,
            "entries under frozenOnly carrying an obligation rather than a settled reason"));

        // BUILD_PLAN.md 1.11, all ten steps of the move procedure in RUNBOOK.
        IReadOnlyList<IReadOnlyList<string>> moveSteps = MarkdownTable.BodyRowsAfter(runbook, "## Moving the store to another machine");
        Assert.Contains("all ten steps", buildPlan, StringComparison.Ordinal);
        claims.Add(new Claim("BUILD_PLAN.md 1.11, all ten steps", 10, moveSteps.Count, "rows of the move procedure in RUNBOOK"));
        claims.Add(new Claim(
            "The move procedure, stated in two documents",
            moveSteps.Count,
            HtmlTable.BodyRowsUnder(architecture, "The procedure").Count,
            "rows of the same procedure in ARCHITECTURE"));

        // RUNBOOK.md, the nightly total against the sum of its own rows.
        IReadOnlyList<IReadOnlyList<string>> nightly = MarkdownTable.BodyRowsAfter(runbook, "## Daily operation");
        claims.Add(new Claim(
            "RUNBOOK.md, the nightly call total",
            SumColumn(nightly.Where(r => !IsTotalRow(r[0])), 2),
            FirstInteger(nightly.Single(r => IsTotalRow(r[0]))[2]),
            "the sum of the stage rows"));

        // ARCHITECTURE.html, the same budget stated again.
        IReadOnlyList<IReadOnlyList<string>> budget = HtmlTable.BodyRowsUnder(architecture, "Data budget");
        claims.Add(new Claim(
            "ARCHITECTURE.html, the daily call total",
            SumColumn(budget.Where(r => !r[0].StartsWith("Daily total", StringComparison.Ordinal)), 1),
            FirstInteger(budget.Single(r => r[0].StartsWith("Daily total", StringComparison.Ordinal))[1]),
            "the sum of the job rows"));

        // The three rows that make one request a night: their calls a night is their cost per
        // request, and stating both numbers only helps if the arithmetic between them is checked.
        // Otherwise a cost can move while the nightly figure, and the total built from it, stay
        // where they were. The rows making several requests a night are named out, because for
        // them the two figures are genuinely different quantities.
        foreach (string job in OneRequestANight)
        {
            IReadOnlyList<string> row = budget.Single(r => r[0].StartsWith(job, StringComparison.Ordinal));
            claims.Add(new Claim(
                $"ARCHITECTURE.html, {job} makes one request a night",
                FirstInteger(row[1]),
                FirstInteger(row[2]),
                "the cost per request against the calls a night"));
        }

        // RUNBOOK.md, the backfill total, which carries a term in N.
        IReadOnlyList<IReadOnlyList<string>> backfill = MarkdownTable.BodyRowsAfter(runbook, "### Backfill, one time");
        List<IReadOnlyList<string>> backfillJobs = backfill.Where(r => !IsTotalRow(r[1])).ToList();
        IReadOnlyList<string> backfillTotal = backfill.Single(r => IsTotalRow(r[1]));
        claims.Add(new Claim(
            "RUNBOOK.md, the backfill total, fixed term",
            SumColumn(backfillJobs.Where(r => Integer().IsMatch(r[2])), 2),
            FirstInteger(backfillTotal[2]),
            "the sum of the priced rows"));
        claims.Add(new Claim(
            "RUNBOOK.md, the backfill total, term in N",
            backfillJobs.Count(r => string.Equals(r[2].Trim(), "N", StringComparison.Ordinal)),
            NCoefficient(backfillTotal[2]),
            "rows priced per surviving name against the coefficient of N in the total"));

        // ARCHITECTURE.html, the nightly cap against its own split.
        string cap = ParameterValue(architecture, "Nightly setup cap");
        int[] capNumbers = Integer().Matches(cap).Select(m => ToInteger(m.Value)).ToArray();
        Assert.True(capNumbers.Length >= 3, $"The nightly setup cap reads {cap}, which states fewer than three numbers.");
        claims.Add(new Claim(
            "ARCHITECTURE.html, the nightly cap splits into its own parts",
            capNumbers[0],
            capNumbers[1] + capNumbers[2],
            cap));

        foreach (Claim claim in claims)
        {
            coverage.Examined(claim.What, 1);
        }

        coverage.NoSourceScan(
            "every claim compares a number a document states about itself against the number derived from that "
            + "same document. The text is the subject on both sides, and nothing here concludes anything about "
            + "what the shipped code does");

        // Out of scope rather than unexamined, and reclassified at 2.1 rather than left as it was.
        //
        // It was NotExamined with a count of zero, which summed to nothing, so the record carried
        // the admission and the report read "unexamined 0" on the same page. Counting admissions
        // rather than their sizes made it visible, and visible it has to be classified honestly.
        //
        // CLAUDE.md's own definitions decide it. Unexamined means a claim this phase should have
        // been able to assert and could not; out of scope means the check exempts something by name
        // and says why. This is the second: the check is a registry, and it exempts prose counts
        // nobody registered. It is the same shape as no-superseded-citation exempting citations
        // inside a record, which is already recorded this way.
        //
        // The count stays zero and stays honest about what it is. The check does not scan prose for
        // numbers, so it cannot say how many it is missing; zero is the number of exempted items it
        // can name, not a measurement of the hole. Closing it means teaching the check to find every
        // number in the specs and report which are registered, which is a decision nobody has taken
        // and which the out-of-scope naming rule at 2.2 will require to be priced.
        coverage.OutOfScope(
            "numbers stated in prose that this registry does not name",
            0,
            CheckCoverage.OutOfScopeReason.UntilDecided(
                "teaching this check to find every number in the five specs and report which are registered",
                "the check is a registry and exempts counts nobody added to it. The zero is the number of exempted "
                + "items it can name, not a measurement of the hole: it does not scan prose for numbers, so it cannot "
                + "say how many it is missing"));
        coverage.Report();

        string[] wrong = claims
            .Where(c => c.Stated != c.Derived)
            .Select(c => $"{c.What}: states {c.Stated}, derived {c.Derived} from {c.Derivation}")
            .ToArray();

        Assert.True(wrong.Length == 0,
            $"{wrong.Length} stated count(s) no longer match what the document contains:\n  " + string.Join("\n  ", wrong));

        Assert.True(claims.Count >= 15,
            $"Only {claims.Count} stated counts were checked. This check is a registry, so a number this low means "
            + "entries were removed rather than that the corpus stopped stating counts.");
    }

    /// <summary>
    /// The data budget rows that make exactly one request an evening, so their cost per request
    /// and their contribution to a night are the same number. Named rather than derived from the
    /// cadence column, because a row that stopped matching would leave the check quietly narrower.
    /// </summary>
    private static readonly string[] OneRequestANight =
        ["Whole-market daily bars", "Splits, bulk", "Dividends, bulk"];

    private static bool IsTotalRow(string cell) =>
        cell.Contains("total", StringComparison.OrdinalIgnoreCase);

    private static int KindCount(IReadOnlyList<IReadOnlyList<string>> rows, string kind) =>
        rows.Count(r => r.Count > 1 && string.Equals(r[1], kind, StringComparison.OrdinalIgnoreCase));

    private static string Between(string text, string from, string to)
    {
        int start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{from} does not appear.");
        int end = text.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>
    /// The number named in words between two phrases, because this corpus writes small counts out.
    ///
    /// Only as far as twelve, which is as far as any count this registry holds goes. A word outside
    /// the table fails loudly rather than reading as nothing, on the grounds a name that does not
    /// resolve is better than one that silently does.
    /// </summary>
    private static int InWords(string text, string before, string after)
    {
        // Whitespace-tolerant across the span it matches, which is the corpus's own rule for a grep
        // over markdown and which this did not obey. Every literal space in `before` and `after`
        // matches any run of whitespace, so a sentence the prose happens to wrap reads the same as
        // one that fits on a line. It failed on exactly that: the permit sentence was rewrapped and
        // "names seven frozen-only checkpoints" put the number at the start of the next line, so
        // the pattern found nothing and the claim it feeds could not be made at all.
        static string Loose(string literal) =>
            string.Join(@"\s+", literal.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(Regex.Escape))
            + (literal.EndsWith(' ') ? @"\s+" : string.Empty);

        Match match = Regex.Match(
            text,
            (before.StartsWith(' ') ? @"\s+" : string.Empty) + Loose(before)
                + @"(?<n>[a-z-]+)"
                + (after.StartsWith(' ') ? @"\s+" : string.Empty) + Loose(after),
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"No word appears between {before} and {after}.");

        string word = match.Groups["n"].Value;
        int? value = FromWords(word);
        Assert.True(value is not null, $"\"{word}\" between {before} and {after} is not a number word.");
        return value.Value;
    }

    /// <summary>
    /// A number word, or a failure naming the word that was not one.
    ///
    /// Separate from <see cref="InWords"/> because the obligation counts are read out of a matched
    /// sentence rather than from between two fixed strings, and both routes need the same parse.
    /// </summary>
    private static int FromWordsOrFail(string word)
    {
        int? value = FromWords(word);
        Assert.True(value is not null, $"\"{word}\" is not a number word.");
        return value.Value;
    }

    /// <summary>
    /// One to nineteen, the round tens, and the compound tens this corpus writes its larger counts
    /// in, case-insensitively because a count at the start of a sentence is capitalised.
    ///
    /// It was a flat lookup of one to twelve until 3.15's fifth finding took the obligation counts
    /// off C# literals. "fifty-nine" and "thirty-one" are the two that needed the compound form,
    /// and they are the two whose whole point is that a row can be added or repointed by editing
    /// the document alone.
    /// </summary>
    public static int? FromWords(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        string lower = word.ToLowerInvariant();

        if (NumberWords.TryGetValue(lower, out int direct))
        {
            return direct;
        }

        string[] parts = lower.Split('-');

        if (parts.Length == 1)
        {
            return Tens.TryGetValue(parts[0], out int round) ? round : null;
        }

        return parts.Length == 2
            && Tens.TryGetValue(parts[0], out int tens)
            && NumberWords.TryGetValue(parts[1], out int units)
            && units is > 0 and < 10
                ? tens + units
                : null;
    }

    // `nought` and `none` are here because a count this registry reads can legitimately reach
    // zero, and until the permits were discharged none ever had. A table that cannot say zero
    // forces the prose into a number, or forces the claim to be dropped at exactly the moment the
    // thing it counts is finished, which is when the count is most worth stating.
    private static readonly IReadOnlyDictionary<string, int> NumberWords = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["nought"] = 0, ["none"] = 0,
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly IReadOnlyDictionary<string, int> Tens = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    /// <summary>
    /// The number the prose states in digits between two fixed phrases, so a claim is parsed rather
    /// than assumed. The digit form beside <see cref="InWords"/>'s word form, because this corpus
    /// writes small counts out and large ones as numerals.
    /// </summary>
    private static int StatedBetween(string text, string before, string after)
    {
        Match match = Regex.Match(
            text,
            Regex.Escape(before) + @"(?<n>\d[\d,]*)" + Regex.Escape(after),
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"No number appears between {before} and {after}.");
        return ToInteger(match.Groups["n"].Value);
    }

    private static int SumColumn(IEnumerable<IReadOnlyList<string>> rows, int column) =>
        rows.Sum(r => column < r.Count ? FirstIntegerOrZero(r[column]) : 0);

    private static int FirstInteger(string cell)
    {
        Match match = Integer().Match(cell);
        Assert.True(match.Success, $"No number in {cell}.");
        return ToInteger(match.Value);
    }

    private static int FirstIntegerOrZero(string cell)
    {
        Match match = Integer().Match(cell);
        return match.Success ? ToInteger(match.Value) : 0;
    }

    /// <summary>The coefficient of N in a total such as 3,005 + 2N.</summary>
    private static int NCoefficient(string cell)
    {
        Match match = TermInN().Match(cell);
        Assert.True(match.Success, $"No term in N appears in {cell}.");
        return match.Groups["n"].Value.Length == 0 ? 1 : ToInteger(match.Groups["n"].Value);
    }

    private static string ParameterValue(string architecture, string parameter) =>
        HtmlTable.BodyRowsUnder(architecture, "Authored parameters")
            .Single(r => r[0].StartsWith(parameter, StringComparison.Ordinal))[1];

    private static int ToInteger(string text) =>
        int.Parse(text.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

    private sealed record Claim(string What, int Stated, int Derived, string Derivation);
}
