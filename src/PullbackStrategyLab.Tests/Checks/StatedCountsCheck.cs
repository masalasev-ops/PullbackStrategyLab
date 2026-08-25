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

    [GeneratedRegex(@"^\s*(?<n>\d+)\.\s", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"-?\d[\d,]*", RegexOptions.CultureInvariant)]
    private static partial Regex Integer();

    [GeneratedRegex(@"^## Phase \d", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseHeading();

    [GeneratedRegex(@"(?<n>\d*)N", RegexOptions.CultureInvariant)]
    private static partial Regex TermInN();

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

        // CLAUDE.md, five specs and three records plus one artefact, over the lifecycle table.
        IReadOnlyList<IReadOnlyList<string>> lifecycle = MarkdownTable.BodyRowsAfter(claude, "## Document lifecycle");
        Assert.Contains("Five specs and three records, plus one artefact.", claude, StringComparison.Ordinal);
        claims.Add(new Claim("CLAUDE.md, five specs", 5, KindCount(lifecycle, "spec"), "lifecycle rows marked spec"));
        claims.Add(new Claim("CLAUDE.md, three records", 3, KindCount(lifecycle, "record"), "lifecycle rows marked record"));
        claims.Add(new Claim("CLAUDE.md, one artefact", 1, KindCount(lifecycle, "artefact"), "lifecycle rows marked artefact"));
        claims.Add(new Claim("The corpus is eight documents plus one artefact", 9, lifecycle.Count, "rows of the lifecycle table"));

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

        // BUILD_PLAN.md, six phases.
        Assert.Contains("Six phases.", buildPlan, StringComparison.Ordinal);
        claims.Add(new Claim("BUILD_PLAN.md, six phases", 6, PhaseHeading().Matches(buildPlan).Count, "phase headings"));

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

        coverage.NotExamined(
            "numbers stated in prose that this registry does not name",
            0,
            "the check is a registry, so a stated count nobody adds here is not examined at all");
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

    /// <summary>Reads the number the prose states between two fixed phrases, so the claim is parsed rather than assumed.</summary>
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
