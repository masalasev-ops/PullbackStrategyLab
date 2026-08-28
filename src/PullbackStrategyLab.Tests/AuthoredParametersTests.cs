using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The authored-parameters table does not claim to be complete while it says it is not.
///
/// <b>The sentence was the defect rather than the rows.</b> The table asserted that nothing in it
/// was left open, while the strategy's exit rules carried five figures appearing nowhere in it, used
/// a term defined nowhere in the corpus, tested against a bar the store does not hold, and stated
/// one of the two directions the project exists to compare. A current-state document asserting a
/// completeness it does not have is a spec defect like any other, and it is the worse kind: it tells
/// a reader to stop looking.
///
/// So the claim and the OPEN rows cannot both stand. Marking a row OPEN is cheap and honest;
/// deleting the sentence and quietly leaving the rows open would be neither, and putting the
/// sentence back before the rows are filled is what this exists to stop.
/// </summary>
public sealed class AuthoredParametersTests
{
    /// <summary>The claim, verbatim. If it comes back it comes back in these words.</summary>
    private const string CompletenessClaim = "Nothing here is left open";

    /// <summary>The marker an unfilled row carries, in the table's own markup.</summary>
    private const string OpenMarker = "<b>OPEN</b>";

    /// <summary>
    /// Stated in advance. Six rows are open today, and a sweep finding none would mean the marker
    /// changed rather than that the rows were filled, which is the failure that reads like success.
    /// </summary>
    private const int OpenToday = 6;

    [Fact]
    public void The_table_does_not_claim_completeness_while_a_row_is_open()
    {
        string architecture = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        IReadOnlyList<IReadOnlyList<string>> rows =
            HtmlTable.BodyRowsUnder(architecture, "Authored parameters");

        string[] open =
        [
            .. rows.Where(r => r.Count > 0 && r[0].Contains("OPEN", StringComparison.Ordinal))
                .Select(r => r[0]),
        ];

        Assert.True(open.Length >= OpenToday,
            $"the table marks {open.Length} row(s) OPEN and has marked at least {OpenToday} since 3.8. A sweep "
            + "finding fewer means the marker changed rather than that the rows were filled, unless a commit says "
            + "which value was authored and by whom.");

        bool claims = architecture.Contains(CompletenessClaim, StringComparison.OrdinalIgnoreCase);

        Assert.False(claims && open.Length > 0,
            $"ARCHITECTURE.html claims \"{CompletenessClaim}\" while {open.Length} row(s) of the authored-parameters "
            + $"table are marked OPEN:\n  {string.Join("\n  ", open)}\n"
            + "Fill the rows or leave the claim out. A document asserting a completeness it does not have tells a "
            + "reader to stop looking, which is worse than saying nothing.");
    }

    /// <summary>
    /// Every open row names a review point that is a real checkpoint, so an open value has somewhere
    /// to be closed rather than resting for ever.
    ///
    /// The same rule an out-of-scope claim carries, applied to the other kind of deferral: a row
    /// that says OPEN and names nothing reads as permanent while looking pending.
    /// </summary>
    [Fact]
    public void Every_open_row_names_the_checkpoint_that_closes_it()
    {
        string architecture = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        string plan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));

        var unclosed = new List<string>();

        foreach (IReadOnlyList<string> row in HtmlTable.BodyRowsUnder(architecture, "Authored parameters"))
        {
            if (row.Count < 3 || !row[0].Contains("OPEN", StringComparison.Ordinal))
            {
                continue;
            }

            string review = row[2].Trim();

            if (!plan.Contains($"| {review} |", StringComparison.Ordinal))
            {
                unclosed.Add($"{row[0]} is open and its review point '{review}' is not a checkpoint BUILD_PLAN.md has");
            }
        }

        Assert.True(unclosed.Count == 0,
            $"{unclosed.Count} open parameter(s) name no checkpoint that would close them:\n  "
            + string.Join("\n  ", unclosed));
    }
}
