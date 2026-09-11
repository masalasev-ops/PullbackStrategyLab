using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every clause SOURCES.md says rests on a source still names one, and still quotes it.
///
/// <b>What this guards is a citation being removed quietly.</b> The twenty clause forms were traced
/// on 2026-09-08 against the corpus alone, which recorded six of them as resting on a stated source
/// and fourteen as an author's reading. Reading the material moved nine of them, and every one of
/// those nine now rests on a table cell naming a source and a quotation carrying it. A later edit
/// that reworded a row, dropped an identifier or deleted a block quote would put the clause back
/// where it was with nothing saying so, which is the same shape as a check that narrows its own
/// scope: the document would still read as traced.
/// see: A clause states where it came from, and a sourced clause quotes rather than paraphrases
///
/// <b>Reconciled in every direction, because two of the three sides could agree while the third
/// drifts.</b> <see cref="SetupChecks"/> is what the detectors run. SOURCES.md's two clause tables
/// are what the corpus says each clause rests on. The source table is what the identifiers in those
/// rows resolve against. A clause in the code and not the document, a row naming a clause no
/// detector runs, an identifier resolving to no source, and a source nothing refers to are all
/// failures.
///
/// <b>The quotation requirement is the half that is specific to this document.</b> Paraphrase is
/// what produced the fourteen readings: a sentence describing what a trader does reads, once
/// written down, exactly like a sentence describing what the writer took him to mean. So a row
/// claiming his own words has to carry his own words, and a section with no block quote in it fails
/// however confident its prose is.
///
/// <b>What this check does not do is judge the trace.</b> Whether a quotation supports the verdict
/// beside it is a reading, and a check asserting readings would be asserting its author's. What it
/// asserts is that the citation is present, resolves, and is accompanied by the source's own words,
/// which is the part a later edit can remove without anybody noticing.
/// </summary>
public sealed partial class ClauseProvenanceCheck
{
    private readonly ITestOutputHelper _output;

    public ClauseProvenanceCheck(ITestOutputHelper output) => _output = output;

    /// <summary>The document this check reads. Its own subject, so there is no source scan here.</summary>
    public const string Document = "docs/SOURCES.md";

    /// <summary>Where the long clause table starts, cited by heading text like every other reader.</summary>
    private const string LongTable = "### The long clauses";

    private const string ShortTable = "### The short clauses";

    private const string SourceTable = "## The source material";

    /// <summary>
    /// The four verdicts a clause row may carry, and nothing else.
    ///
    /// A closed vocabulary rather than free text, because the count in "What the count comes to" is
    /// derived by matching these strings and a reworded cell would silently stop being counted. That
    /// is the failure this corpus has shipped four times under another name: the subject goes away
    /// and the assertion says what it always said.
    /// </summary>
    public static IReadOnlySet<string> Verdicts { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HisOwnWords, InPart, NoSource, NoThreshold,
    };

    public const string HisOwnWords = "his own words";

    public const string InPart = "his own words, in part";

    public const string NoSource = "no source found";

    public const string NoThreshold = "carries no threshold";

    /// <summary>
    /// The two tiers a source may declare, which is the distinction the whole document turns on.
    ///
    /// <see cref="HisOwnWords"/> is shared with the clause verdicts on purpose: a clause resting on
    /// his own words and a source that is his own words are the same claim seen from the two ends,
    /// and one string for both is what stops them drifting into two vocabularies.
    /// </summary>
    public static IReadOnlySet<string> Tiers { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        HisOwnWords, "somebody's summary",
    };

    [GeneratedRegex(@"`(?<id>[PS]\d)`", RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdentifier();

    /// <summary>A clause heading, which is the clause name and the side it is on.</summary>
    [GeneratedRegex(@"^### `?(?<clause>[a-z-]+)`?, (?<side>long|short)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ClauseHeading();

    [GeneratedRegex(@"^>", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Quotation();

    [Fact]
    [Trait("check", "clause-provenance")]
    public void Every_clause_claiming_a_source_names_one_and_quotes_it()
    {
        var coverage = new CheckCoverage("clause-provenance", _output);
        string document = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, Document));

        IReadOnlyList<SourceRow> sources = Sources(document);
        IReadOnlyList<ClauseRow> rows =
        [
            .. Clauses(document, LongTable, SetupDirection.Long),
            .. Clauses(document, ShortTable, SetupDirection.Short),
        ];

        IReadOnlyList<string> problems = Problems(rows, sources, document);

        coverage
            .Examined("clause rows reconciled against the detectors' own lists, in both directions", rows.Count)
            .Examined("source identifiers resolved against the source table", rows.Sum(r => r.Sources.Count))
            .Examined("sources declaring whose words they are", sources.Count)
            .Examined("sourced clauses required to carry a quotation", rows.Count(r => r.IsSourced))
            .NoSourceScan(
                "the document is this check's subject rather than a description of something else. The clause "
                + "names are reconciled against SetupChecks, which the detectors run and which "
                + "`check-completeness` already asserts against ARCHITECTURE's gate lists, so the behavioural end "
                + "of this reconciliation is exercised there rather than read out of the source here.")
            .Report();

        // Stated in advance, because every reconciliation above is satisfied by an empty document.
        // A parser that stopped matching would report nought rows, nought identifiers and nought
        // problems, which is the shape this corpus has shipped four times.
        Assert.True(rows.Count == 20,
            $"SOURCES.md's two clause tables parsed {rows.Count} row(s). There are twenty clauses, ten a side, so "
            + "either a row was lost or the parser stopped matching.");
        Assert.True(sources.Count >= 6,
            $"SOURCES.md's source table parsed {sources.Count} row(s) and held seven when this was written, so the "
            + "table the identifiers resolve against has stopped being read.");

        Assert.True(problems.Count == 0,
            $"{problems.Count} problem(s) with the clause provenance recorded in {Document}:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// What is wrong with the trace, or nothing.
    ///
    /// Pure and separated from the run above so the guard can be proved against rows written by
    /// hand rather than against whatever the document happens to say today. A guard nobody can break
    /// on purpose is a guard nobody knows the state of.
    /// </summary>
    public static IReadOnlyList<string> Problems(
        IReadOnlyList<ClauseRow> rows,
        IReadOnlyList<SourceRow> sources,
        string document)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(document);

        var problems = new List<string>();
        var declared = sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // 1. The document against the detectors, in both directions. A clause the code runs and the
        //    document does not trace is a clause whose provenance nobody recorded; a row naming a
        //    clause no detector runs is a trace of something that is not in the lab.
        foreach ((string side, IReadOnlyList<string> code) in
                 new[] { (SetupDirection.Long, SetupChecks.Long), (SetupDirection.Short, SetupChecks.Short) })
        {
            IReadOnlyList<string> traced = [.. rows.Where(r => r.Side == side).Select(r => r.Clause)];

            foreach (string missing in code.Except(traced, StringComparer.Ordinal))
            {
                problems.Add(
                    $"{missing} is a {side} check the detector runs and has no row in SOURCES.md, so nothing "
                    + "records what it rests on.");
            }

            foreach (string extra in traced.Except(code, StringComparer.Ordinal))
            {
                problems.Add(
                    $"{extra} has a {side} row in SOURCES.md and is not in SetupChecks.{(side == SetupDirection.Long ? "Long" : "Short")}, "
                    + "so the document traces a clause the lab does not run.");
            }

            foreach (string duplicate in traced.GroupBy(c => c, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            {
                problems.Add($"{duplicate} has more than one {side} row in SOURCES.md, so two verdicts stand for one clause.");
            }
        }

        // 2. The vocabulary. A reworded verdict stops being counted by `stated-counts` and reads as
        //    a trace that happened, which is the quiet half of a citation going missing.
        foreach (ClauseRow row in rows)
        {
            if (!Verdicts.Contains(row.Form))
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the form verdict reads \"{row.Form}\", and a verdict is one of "
                    + $"{string.Join(", ", Verdicts.Order(StringComparer.Ordinal))}.");
            }

            if (!Verdicts.Contains(row.Threshold))
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the threshold verdict reads \"{row.Threshold}\", and a verdict is "
                    + $"one of {string.Join(", ", Verdicts.Order(StringComparer.Ordinal))}.");
            }

            // A form cannot carry no threshold. The verdict says the clause compares words, which is
            // a fact about the quantity being tested and not about what the clause is.
            if (row.Form == NoThreshold)
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the form verdict reads \"{NoThreshold}\", which is a threshold "
                    + "verdict. Every clause has a form, whatever it compares.");
            }
        }

        // 3. The citation itself, which is what this check is named for. A row claiming a source
        //    names one, and every identifier it names resolves to a declared source.
        foreach (ClauseRow row in rows)
        {
            if (row.IsSourced && row.Sources.Count == 0)
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the trace claims \"{row.Form}\" for the form or \"{row.Threshold}\" "
                    + "for the threshold and the row names no source, so the claim rests on nothing.");
            }

            foreach (string unknown in row.Sources.Where(s => !declared.Contains(s)))
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: names {unknown}, which the source table does not declare, so the "
                    + "citation resolves to nothing.");
            }
        }

        // 4. Whose words. The distinction the document turns on, so a source that declines to state
        //    it makes every row citing it unreadable.
        foreach (SourceRow source in sources.Where(s => !Tiers.Contains(s.Tier)))
        {
            problems.Add(
                $"{source.Id}: declares \"{source.Tier}\", and a source declares one of "
                + $"{string.Join(" or ", Tiers.Order(StringComparer.Ordinal))}. Which of the two it is decides "
                + "whether a clause citing it rests on the trader or on somebody's reading of him.");
        }

        foreach (SourceRow source in sources.Where(s => string.IsNullOrWhiteSpace(s.Where)))
        {
            problems.Add($"{source.Id}: is declared with no location, so a later reader cannot go and check it.");
        }

        // 5. A source nothing refers to. The reverse direction of the citation check, and the one
        //    that catches a source table growing while the trace stays where it was.
        foreach (SourceRow source in sources)
        {
            int mentions = SourceIdentifier().Matches(document).Count(m => m.Groups["id"].Value == source.Id);

            // One for the source table's own row. Anything above that is a use.
            if (mentions <= 1)
            {
                problems.Add(
                    $"{source.Id}: is declared in the source table and referred to nowhere else in the document, "
                    + "so it was read and nothing rests on it.");
            }
        }

        // 6. The quotation. A sourced clause carries the source's own words, because paraphrase is
        //    what this whole document exists to remove.
        foreach (ClauseRow row in rows.Where(r => r.IsSourced))
        {
            string? section = Section(document, row.Clause, row.Side);

            if (section is null)
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the trace claims a source and the document has no \"### {row.Clause}, "
                    + $"{row.Side}\" section, so the citation is a table cell with nothing behind it.");
                continue;
            }

            if (Quotation().Matches(section).Count == 0)
            {
                problems.Add(
                    $"{row.Clause}, {row.Side}: the trace claims \"{row.Form}\" and its section carries no quotation "
                    + "and points at no section that does. A paraphrase is what produced the readings this document "
                    + "was written to replace.");
            }

            foreach (string named in row.Sources)
            {
                if (!section.Contains($"`{named}`", StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{row.Clause}, {row.Side}: the row names {named} and its section never cites it, so the "
                        + "table and the prose disagree about what the clause rests on.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// One clause's row, as the document states it.
    ///
    /// <see cref="IsSourced"/> is the predicate every other rule turns on, and it is deliberately
    /// generous: a clause sourced in part still owes a citation and a quotation, because the half
    /// that is sourced is the half a later edit would delete.
    /// </summary>
    public sealed record ClauseRow(
        string Clause, string Side, string Form, string Threshold, IReadOnlyList<string> Sources, string Disagreement)
    {
        public bool IsSourced => Form == HisOwnWords || Form == InPart || Threshold == HisOwnWords;
    }

    /// <summary>One source, its tier and where a later reader finds it.</summary>
    public sealed record SourceRow(string Id, string What, string Tier, string Where);

    /// <summary>The clause rows of one side's table.</summary>
    public static IReadOnlyList<ClauseRow> Clauses(string document, string afterText, string side)
    {
        ArgumentNullException.ThrowIfNull(document);

        return
        [
            .. MarkdownTable.BodyRowsAfter(document, afterText)
                .Where(r => r.Count == 5)
                .Select(r => new ClauseRow(
                    Bare(r[0]),
                    side,
                    r[1].Trim(),
                    r[2].Trim(),
                    [.. SourceIdentifier().Matches(r[3]).Select(m => m.Groups["id"].Value)],
                    r[4].Trim())),
        ];
    }

    /// <summary>The source table, read as data.</summary>
    public static IReadOnlyList<SourceRow> Sources(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return
        [
            .. MarkdownTable.BodyRowsAfter(document, SourceTable)
                .Where(r => r.Count == 4)
                .Select(r => new SourceRow(Bare(r[0]), r[1].Trim(), r[2].Trim(), r[3].Trim())),
        ];
    }

    /// <summary>
    /// One clause's prose section, bounded by the next heading of any level.
    ///
    /// Bounded rather than read to the end of the file, for the reason every parser in this suite
    /// is: a quotation under the next clause would otherwise satisfy this one, and the check would
    /// pass on a document where one section carried every quote and the rest carried none.
    /// </summary>
    public static string? Section(string document, string clause, string side)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (Match heading in ClauseHeading().Matches(document))
        {
            if (heading.Groups["clause"].Value != clause || heading.Groups["side"].Value != side)
            {
                continue;
            }

            int start = heading.Index + heading.Length;
            int next = document.IndexOf("\n#", start, StringComparison.Ordinal);
            return next < 0 ? document[start..] : document[start..next];
        }

        return null;
    }

    private static string Bare(string cell) =>
        cell.Replace("`", string.Empty, StringComparison.Ordinal).Trim();
}
