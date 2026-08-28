using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The four places that state what a setup row may have rewritten say the same thing.
///
/// <b>Four statements and no decision is how the rule got too broad without anybody noticing.</b>
/// Setup-row immutability lived in a done condition, one line of SCHEMA, a migration header and a
/// doc comment, and in none of them was it a named decision. `decision-resolves` could not have
/// caught that, because there was no name to resolve; nothing reconciled the four against each
/// other, because prose against prose false-alarms. So an amendment that landed in one of them
/// would have left the other three disagreeing, and the fifth statement would have read like the
/// authority while four older ones said something else.
///
/// The count is stated in advance rather than derived. A sweep that found three would mean a site
/// lost its wording, and a sweep that found five would mean a site gained one nobody swept; both
/// read the same to a test that only checked the sites it happened to know about.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
/// </summary>
public sealed class SetupImmutabilityWordingTests
{
    /// <summary>
    /// The clause every site carries, verbatim.
    ///
    /// One string rather than four paraphrases, because a paraphrase drifts and nothing notices:
    /// the same reason decision names are matched exactly rather than approximately.
    /// </summary>
    public const string Clause = "immutable after write, except by a correction the lateness bound admits";

    /// <summary>The decision the clause defers to, cited by its exact name.</summary>
    public const string Decision =
        "A late answer is attributed to the session it was fetched for, up to a recorded lateness bound";

    /// <summary>
    /// The four sites, named. Records are not among them: PROGRESS states what was true on a date
    /// and CHANGELOG holds the prior text of every clean edit, so both legitimately carry wording
    /// the specs no longer do. That is the point of a record.
    /// </summary>
    private static readonly string[] Sites =
    [
        "docs/BUILD_PLAN.md",
        "docs/SCHEMA.md",
        "src/PullbackStrategyLab.Data/Migrations/011-setup.sql",
        "src/PullbackStrategyLab.Tests/SetupJournalTests.cs",
    ];

    /// <summary>
    /// Files that carry the wording without stating the rule, by name and with the reason.
    ///
    /// The last entry is this file, and it earned its place by failing. The sweep reads the git
    /// index, so this test was invisible while it was untracked and appeared as a fifth site the
    /// moment it was committed. That is the index-based scan working: a file cannot hide from the
    /// walk by being new. It is excluded because it declares the clause rather than states the rule,
    /// which is the one exemption a comparison against a canonical string must always make.
    /// </summary>
    private static readonly Dictionary<string, string> Records = new(StringComparer.Ordinal)
    {
        ["docs/PROGRESS.md"] = "an append-only record of what was measured on a date, corrected by a new entry",
        ["docs/CHANGELOG.md"] = "the prior text of every clean spec edit, which is the point of the file",
        ["docs/DECISIONS.md"] = "carries the superseded decision under Previously decided, reasoning intact",
        ["src/PullbackStrategyLab.Tests/SetupImmutabilityWordingTests.cs"] =
            "declares the clause the four sites are compared against rather than stating the rule",
    };

    [Fact]
    public void The_four_sites_that_state_the_rule_carry_the_same_clause()
    {
        var carrying = new List<string>();
        var stale = new List<string>();

        foreach (string file in RepositoryLayout.TrackedTextFiles)
        {
            string relative = RepositoryLayout.Relative(file);

            if (Records.ContainsKey(relative))
            {
                continue;
            }

            string text = RepositoryLayout.Read(file);

            if (text.Contains(Clause, StringComparison.OrdinalIgnoreCase))
            {
                carrying.Add(relative);
                continue;
            }

            // The old wording, standing where the clause should be. This is the half that catches a
            // site the sweep missed rather than a site that lost its wording.
            if (text.Contains("immutable after write", StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(relative);
            }
        }

        Assert.True(stale.Count == 0,
            $"{stale.Count} file(s) state the rule in wording the amendment replaced:\n  "
            + string.Join("\n  ", stale)
            + $"\nEvery site says \"{Clause}\", or it is a record and is named as one.");

        Assert.Equal(Sites.Order(StringComparer.Ordinal), carrying.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// And each of them defers to the decision by its exact name, so the clause has somewhere to
    /// send a reader asking what the bound is.
    /// </summary>
    [Fact]
    public void The_two_specs_cite_the_decision_the_clause_defers_to()
    {
        foreach (string file in new[] { "docs/BUILD_PLAN.md", "docs/SCHEMA.md" })
        {
            string text = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, file));

            Assert.True(
                text.Contains($"see: {Decision}", StringComparison.Ordinal),
                $"{file} states the clause and does not cite the decision it defers to, so a reader has nowhere "
                + "to find what the bound is or what it admits.");
        }
    }

    /// <summary>
    /// The bound is read from the parameters rather than written into the stage.
    ///
    /// A literal reappearing is the way an authored value stops being authored: the table still says
    /// twenty-four, the stage says something else, and nothing compares them because the stage's
    /// figure is not a constant anybody pinned.
    /// </summary>
    [Fact]
    public void The_recomputer_reads_the_bound_rather_than_carrying_it()
    {
        string source = RepositoryLayout.Read(Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "CheckRecomputer.cs"));

        Assert.Contains("MeasurementParameters.LatenessBoundHours", source, StringComparison.Ordinal);

        Assert.DoesNotContain("AddHours(24", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalHours > 24", source, StringComparison.Ordinal);
    }
}
