using System.Diagnostics;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every commit that replaced lines in a spec also recorded their prior text.
///
/// <b>The direction nothing had.</b> `CHANGELOG.md` is a record and the rule is that a clean edit to
/// a spec lands there with its prior text and the decision authorising it. Until 4.17 the record was
/// read by nothing: `decision-resolves` reads it for citations and `stated-counts` for figures, and
/// neither asks whether an edit that should have produced an entry did. That is the shape the 3.7
/// row names for eight other checks, one document over.
///
/// <b>Deletions rather than any change at all, and the distinction is what keeps the guard alive.</b>
/// A commit that appends a row to an obligations table has no prior text to record, and a check that
/// demanded an entry for every touch would fire on every ordinary addition. It would then be
/// suppressed, and a suppressed guard is a dead one arrived at slowly. A commit whose diff for a
/// spec deletes a line replaced something, and replacing something is what the rule is about.
///
/// <b>The ten phase-3 commits are exempt by hash, with the reason.</b> They replaced lines and
/// recorded nothing, which is the finding that raised this row. What is owed is the reconciliation
/// rather than ten retrospective entries: an entry written now would be dated today about an edit
/// made then, which is the one thing an append-only record must not carry. They are named so the
/// exemption is countable and so an eleventh cannot join them quietly.
///
/// <b>A shallow clone is refused rather than passed over.</b> The whole subject is the history, so a
/// checkout with one commit in it would report a scope of one and say nothing. That is the
/// narrowing this corpus keeps finding, and the refusal is what stops it.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class ChangelogReconcilesCheck
{
    /// <summary>The five specs. A record is not one: it is corrected by a new dated entry.</summary>
    private static readonly string[] Specs =
    [
        "CLAUDE.md",
        "docs/ARCHITECTURE.html",
        "docs/SCHEMA.md",
        "docs/BUILD_PLAN.md",
        "docs/RUNBOOK.md",
    ];

    private const string Changelog = "docs/CHANGELOG.md";

    /// <summary>
    /// The commits that replaced spec lines and recorded no prior text, by hash and with the reason.
    ///
    /// All ten are phase 3's and all ten predate this check. Named individually rather than as a
    /// date cutoff, because a cutoff exempts whatever falls inside it and these are ten known
    /// commits rather than a period.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Exempt { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fa57a9ec"] = "Phase 3 / 3.0, the checkpoint closing with one part repointed",
            ["6afa4f4d"] = "Phase 3 / 3.8(g), the slot retrying inside its own window",
            ["fee006c5"] = "Phase 3 / 3.8, four sites swept against a count stated first",
            ["166d2735"] = "Phase 3 / 3.8, the fifteen repaired from the night's own inputs",
            ["5c2db96b"] = "Phase 3 / 3.8, the inverted job and the OPEN parameters",
            ["6ffb0bb1"] = "Phase 3 / 3.8, the interpreter comparison's first reading",
            ["38fb4957"] = "Phase 3 / 3.9(e), the rebuild that reported success",
            ["ffb673af"] = "Phase 3 / 3.9, the post-pass closing",
            ["3e88a350"] = "Phase 3 / 3.12, the store-version claim",
            ["4e9d0736"] = "Phase 3 / 3.13, the checkpoint's record",
        };

    private readonly ITestOutputHelper _output;

    public ChangelogReconcilesCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "changelog-reconciles")]
    public void Every_commit_that_replaced_a_spec_line_recorded_its_prior_text()
    {
        var coverage = new CheckCoverage("changelog-reconciles", _output);
        IReadOnlyList<Commit> history = History();

        // Stated in advance rather than derived. A history this read came back empty from would pass
        // every assertion below it, and that is exactly what a shallow checkout produces.
        Assert.True(history.Count >= 60,
            $"The history read back {history.Count} commit(s). This check's whole subject is the history, so a "
            + "shallow clone reports a scope of nothing and says nothing; the workflow fetches the full history "
            + "for that reason and a run that cannot see it is refused rather than passed over.");

        Commit[] replacing = [.. history.Where(c => c.ReplacedASpec)];
        Commit[] unrecorded = [.. replacing.Where(c => !c.TouchedTheChangelog && !IsExempt(c))];

        coverage.Examined("commits read", history.Count);
        coverage.Examined("commits that replaced a line in a spec", replacing.Length);
        coverage.Examined(
            "of those, the ones that recorded prior text",
            replacing.Count(c => c.TouchedTheChangelog));
        coverage.OutOfScope(
            "commits that replaced a spec line and recorded nothing",
            replacing.Count(IsExempt),
            CheckCoverage.OutOfScopeReason.ByDesign(
                "ten phase-3 commits, each named by hash with what it was. What is owed is this "
                + "reconciliation rather than ten retrospective entries: an entry written now would be "
                + "dated today about an edit made then, which is the one thing an append-only record "
                + "must not carry"));
        coverage.Scan(
            "that a commit deleting a line from a spec also changed the changelog",
            CheckCoverage.Backing.None(
                "the subject is the history itself rather than a behaviour of the shipped code. There "
                + "is nothing to exercise: the commits exist or they do not, and a commit deleted is "
                + "the thing itself going away rather than an assertion outliving it"));
        coverage.Report();

        Assert.True(
            unrecorded.Length == 0,
            $"{unrecorded.Length} commit(s) replaced a line in a spec and recorded no prior text in "
            + $"{Changelog}:\n"
            + string.Join('\n', unrecorded.Select(c => $"  {c.Hash[..8]}  {c.Subject}")));

        // Every exemption still names a commit the history holds. An exemption whose commit is gone
        // is an exemption nobody can check, which is the state a rebase would leave them in.
        string[] vanished =
        [
            .. Exempt.Keys.Where(h => !history.Any(c => c.Hash.StartsWith(h, StringComparison.Ordinal))),
        ];

        Assert.True(vanished.Length == 0,
            "These exemptions name a commit the history no longer holds, so they exempt nothing and "
            + "would hide a real one silently: " + string.Join(", ", vanished));
    }

    private static bool IsExempt(Commit commit) =>
        Exempt.Keys.Any(h => commit.Hash.StartsWith(h, StringComparison.Ordinal));

    /// <summary>
    /// Every commit, with whether it deleted a line from a spec and whether it touched the record.
    ///
    /// Merges are excluded: a merge commit's diff against its first parent repeats the branch's own
    /// changes, and counting them would report every branch twice and blame the merge for what a
    /// commit inside it did.
    /// </summary>
    private static IReadOnlyList<Commit> History()
    {
        var start = new ProcessStartInfo("git", "log --no-merges --numstat --format=%x01%H%x02%s")
        {
            WorkingDirectory = RepositoryLayout.Root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using Process? process = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started, so there is no history to read.");

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git log exited {process.ExitCode}, so there is no history to read.");
        }

        var commits = new List<Commit>();
        string hash = string.Empty;
        string subject = string.Empty;
        bool replaced = false;
        bool changelog = false;

        void Close()
        {
            if (hash.Length > 0)
            {
                commits.Add(new Commit(hash, subject, replaced, changelog));
            }
        }

        foreach (string line in output.Split('\n'))
        {
            string row = line.TrimEnd('\r');

            if (row.StartsWith(''))
            {
                Close();

                string[] head = row[1..].Split('', 2);
                hash = head[0];
                subject = head.Length > 1 ? head[1] : string.Empty;
                replaced = false;
                changelog = false;
                continue;
            }

            string[] parts = row.Split('\t');

            if (parts.Length != 3)
            {
                continue;
            }

            string path = parts[2].Replace('\\', '/');

            if (string.Equals(path, Changelog, StringComparison.Ordinal))
            {
                changelog = true;
                continue;
            }

            // A binary file reports its counts as "-", which parses as neither and is not a
            // deletion. Specs are text, so this only ever skips something that is not one.
            if (Specs.Contains(path, StringComparer.Ordinal)
                && int.TryParse(parts[1], out int deletions)
                && deletions > 0)
            {
                replaced = true;
            }
        }

        Close();

        return commits;
    }

    private sealed record Commit(string Hash, string Subject, bool ReplacedASpec, bool TouchedTheChangelog);
}
