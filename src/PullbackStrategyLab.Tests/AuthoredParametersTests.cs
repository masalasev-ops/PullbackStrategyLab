using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The authored-parameters table claims completeness only while it has it.
///
/// <b>The sentence was the defect rather than the rows.</b> The table asserted that nothing in it
/// was left open, while the strategy's exit rules carried five figures appearing nowhere in it, used
/// a term defined nowhere in the corpus, tested against a bar the store does not hold, and stated
/// one of the two directions the project exists to compare. A current-state document asserting a
/// completeness it does not have is a spec defect like any other, and it is the worse kind: it tells
/// a reader to stop looking.
///
/// <b>The claim came out at 3.8, eleven rows were opened, and it returns at 4.15.</b> One was
/// answered at 4.4 and the other ten in the sitting 4.15 records. So this file's subject has
/// inverted: it used to hold that the claim stays out while rows are open, and it now holds that the
/// claim is in and no row is open, with the mutual exclusion between the two unchanged and asserted
/// in both directions.
///
/// <b>The floor it used to carry was a proxy and had to be replaced rather than lowered.</b> It read
/// that at least six rows were marked OPEN, stated in advance so that a sweep finding none would
/// mean the marker had changed rather than that the rows were filled. That is the right worry and
/// the floor was the wrong instrument for it the moment the rows were legitimately filled: at nought
/// open rows the two cases it separates look identical. What replaces it is a permanent proof that
/// the detector still detects, run against an authored table carrying an open row, on the rule that
/// a test proving a check works must be permanent rather than a break-and-revert done once by hand.
/// </summary>
public sealed class AuthoredParametersTests
{
    /// <summary>The claim, verbatim. It came back at 4.15 and it came back in these words.</summary>
    private const string CompletenessClaim = "Nothing here is left open";

    /// <summary>
    /// An authored table in the document's own markup, carrying one open row and one filled one.
    ///
    /// It exists so the two predicates below are exercised against a table with something to find.
    /// The real document has nothing open, and a predicate that has nothing to find reports the same
    /// thing whether it works or not.
    /// </summary>
    private const string AuthoredTable = """
        <h2 id="authored-parameters">Authored parameters</h2>
        <table>
        <tr><th>Parameter</th><th>Value</th><th>Review point</th><th>Basis</th></tr>
        <tr><td>A value that is settled</td><td>7</td><td>Never</td><td>Authored</td></tr>
        <tr><td>A value nobody has chosen <b>OPEN</b></td><td>Unstated</td><td>4.8</td><td>Authored</td></tr>
        <tr><td>A value deferred to nowhere <b>OPEN</b></td><td>Unstated</td><td>Soon</td><td>Authored</td></tr>
        </table>
        """;

    private static string Architecture() =>
        RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

    /// <summary>The parameter cells of every row the table marks OPEN.</summary>
    private static IReadOnlyList<string> OpenRows(string architecture) =>
    [
        .. HtmlTable.BodyRowsUnder(architecture, "Authored parameters")
            .Where(r => r.Count > 0 && r[0].Contains("OPEN", StringComparison.Ordinal))
            .Select(r => r[0]),
    ];

    /// <summary>Open rows whose review point is not a checkpoint BUILD_PLAN has.</summary>
    private static IReadOnlyList<string> Unclosed(string architecture, string plan) =>
    [
        .. HtmlTable.BodyRowsUnder(architecture, "Authored parameters")
            .Where(r => r.Count > 2 && r[0].Contains("OPEN", StringComparison.Ordinal))
            .Where(r => !plan.Contains($"| {r[2].Trim()} |", StringComparison.Ordinal))
            .Select(r => $"{r[0]} is open and its review point '{r[2].Trim()}' is not a checkpoint BUILD_PLAN.md has"),
    ];

    /// <summary>
    /// The table claims completeness and has it, which is the state 4.15 put it in.
    ///
    /// Both halves are asserted rather than only the claim, because a document that dropped the
    /// table altogether would satisfy "no row is open" and satisfy nothing else.
    /// </summary>
    [Fact]
    public void The_table_claims_completeness_and_no_row_is_open()
    {
        string architecture = Architecture();

        Assert.Contains(CompletenessClaim, architecture, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<IReadOnlyList<string>> rows =
            HtmlTable.BodyRowsUnder(architecture, "Authored parameters");

        Assert.True(rows.Count >= 25,
            $"only {rows.Count} authored parameters were parsed, and the table has held more than that "
            + "since before any code existed. A number this low means the parser stopped matching, which "
            + "would make the open-row count below nought for a reason that is not the rows being filled.");

        IReadOnlyList<string> open = OpenRows(architecture);

        Assert.True(open.Count == 0,
            $"ARCHITECTURE.html claims \"{CompletenessClaim}\" while {open.Count} row(s) of the "
            + $"authored-parameters table are marked OPEN:\n  {string.Join("\n  ", open)}\n"
            + "Fill the rows or take the claim back out. A document asserting a completeness it does not "
            + "have tells a reader to stop looking, which is worse than saying nothing.");
    }

    /// <summary>
    /// The other direction, and it is the half that guards the future. Reopening a row without
    /// removing the sentence is exactly the state 3.8 found and 4.15 is allowed to leave behind only
    /// because it filled the rows.
    /// </summary>
    [Fact]
    public void An_open_row_and_the_claim_cannot_both_stand()
    {
        string reopened = Architecture().Replace(
            "<tr><td>Entry slippage</td>",
            "<tr><td>Entry slippage <b>OPEN</b></td>",
            StringComparison.Ordinal);

        Assert.Contains(CompletenessClaim, reopened, StringComparison.OrdinalIgnoreCase);
        Assert.Single(OpenRows(reopened));
    }

    /// <summary>
    /// The detector still detects, proved against a table that has something to find.
    ///
    /// This is what replaces the floor of six. The floor said "at least six rows are open" and its
    /// whole purpose was to fail if the marker changed shape; at nought open rows it could no longer
    /// tell a changed marker from a filled table, so the property moves to a case that is authored
    /// and permanent instead of resting on the document happening to be incomplete.
    /// </summary>
    [Fact]
    public void The_open_marker_is_still_read_off_a_table_that_has_one()
    {
        IReadOnlyList<string> open = OpenRows(AuthoredTable);

        Assert.Equal(2, open.Count);
        Assert.All(open, cell => Assert.Contains("OPEN", cell, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every open row names a review point that is a real checkpoint, so an open value has somewhere
    /// to be closed rather than resting for ever.
    ///
    /// The same rule an out-of-scope claim carries, applied to the other kind of deferral: a row that
    /// says OPEN and names nothing reads as permanent while looking pending. The document has no open
    /// row today, so asserting this over the document alone would pass by comparing nothing, which is
    /// the defect 3.10 counted separately from a pass. The authored table carries the case.
    /// </summary>
    [Fact]
    public void Every_open_row_names_the_checkpoint_that_closes_it()
    {
        string plan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));

        IReadOnlyList<string> real = Unclosed(Architecture(), plan);

        Assert.True(real.Count == 0,
            $"{real.Count} open parameter(s) name no checkpoint that would close them:\n  "
            + string.Join("\n  ", real));

        IReadOnlyList<string> authored = Unclosed(AuthoredTable, plan);

        Assert.Single(authored);
        Assert.Contains("'Soon' is not a checkpoint", authored[0], StringComparison.Ordinal);
    }
}
