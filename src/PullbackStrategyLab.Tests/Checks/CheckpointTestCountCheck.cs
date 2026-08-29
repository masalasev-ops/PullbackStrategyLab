using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every checkpoint PROGRESS records states a test count somewhere in its own entries.
///
/// <b>Why it exists.</b> Done condition 2 is "`tools/ci.*` is green, with the test count recorded
/// in PROGRESS", and 3.12 met the first half and not the second: its entry carries the night it
/// recovered and the repairs it made and no figures at all, where every entry from 3.10 carries
/// them. Nothing noticed, because the condition is prose in BUILD_PLAN and the only reader of a
/// done condition is whoever happens to be looking.
///
/// <b>Why the test count and not the rest of the figures.</b> The condition names one number, and
/// a check whose name says one thing while it asserts several is the shape this corpus keeps
/// finding in its own instruments. The steps, the claims and the coverage figures are worth
/// recording and are not what condition 2 requires, so they are not asserted here.
///
/// <b>Why per checkpoint rather than per entry.</b> A checkpoint can land across several dated
/// entries, and a correction entry is not a second run of CI. What the condition asks is that the
/// checkpoint's figure is in the record, so the question is asked of the checkpoint and answered
/// by any of its entries.
/// see: Nothing in the corpus is struck through
/// </summary>
public sealed partial class CheckpointTestCountCheck
{
    private readonly ITestOutputHelper _output;

    public CheckpointTestCountCheck(ITestOutputHelper output) => _output = output;

    /// <summary>An entry heading: the checkpoint, then the date, then the branch, then the title.</summary>
    [GeneratedRegex(@"^## (?<key>\S+)\s+—.*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EntryHeading();

    /// <summary>A checkpoint identifier, with the letter a lettered part carries.</summary>
    [GeneratedRegex(@"^(?<checkpoint>\d+\.\d+)(\([a-z]\))*$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointKey();

    /// <summary>The label that says an entry records work built rather than a correction or a ruling.</summary>
    [GeneratedRegex(@"^Built:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BuiltBlock();

    /// <summary>
    /// A test count, in the form every entry that carries one uses.
    ///
    /// The number and the word, rather than the word alone: "the suite passes" is not a count, and
    /// the condition asks for the figure because the figure is what a later reader compares against.
    /// </summary>
    [GeneratedRegex(@"\b\d[\d,]*\s+tests?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestCount();

    /// <summary>
    /// The one checkpoint whose entries predate this check and cannot be given the figure.
    ///
    /// 3.9 landed across nine entries and none of them states a test count. PROGRESS is append-only
    /// and a dated entry is corrected by a new dated entry rather than edited, so the figure cannot
    /// be put into the entries that owed it, and a new entry today stating a count from a run in
    /// August 2026 would be a measurement nobody took. Named here rather than absorbed into a
    /// cutoff date, so that a second name appearing in this list is a change somebody has to make
    /// on purpose.
    /// see: Nothing in the corpus is struck through
    /// </summary>
    public static IReadOnlyList<string> WrittenBeforeThisCheck { get; } = ["3.9"];

    [Fact]
    [Trait("check", "checkpoint-test-count")]
    public void Every_checkpoint_the_record_carries_states_a_test_count()
    {
        var coverage = new CheckCoverage("checkpoint-test-count", _output);

        string progress = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "PROGRESS.md"));
        IReadOnlyList<Entry> entries = Entries(progress);

        var byCheckpoint = entries
            .GroupBy(e => e.Checkpoint, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        string[] exempt = [.. byCheckpoint
            .Select(g => g.Key)
            .Where(k => WrittenBeforeThisCheck.Contains(k, StringComparer.Ordinal))];

        string[] silent = [.. byCheckpoint
            .Where(g => !WrittenBeforeThisCheck.Contains(g.Key, StringComparer.Ordinal))
            .Where(g => !g.Any(e => e.StatesATestCount))
            .Select(g => g.Key)];

        coverage
            // The scope carrying the property. It grows by one per checkpoint and never falls,
            // because PROGRESS is append-only, so a drop means the heading or the Built label
            // stopped matching and this is asking the question of a shorter list than it thinks.
            .Examined("checkpoints PROGRESS records as built", byCheckpoint.Count - exempt.Length)
            .Context("entries under those checkpoints", entries.Count)
            .NoSourceScan(
                "the subject is the record itself rather than a description of it. The entry either "
                + "carries the figure or it does not, and there is no behaviour behind it for a test "
                + "to exercise");

        foreach (string checkpoint in exempt)
        {
            coverage.OutOfScope($"checkpoint {checkpoint}", 1,
                CheckCoverage.OutOfScopeReason.ByDesign(
                    "its nine entries were written before this check and state no test count. A dated "
                    + "entry is corrected by a new dated entry rather than edited, and a new entry "
                    + "stating a count today would be a measurement nobody took"));
        }

        coverage.Report();

        // Stated in advance. A heading pattern that stopped matching would hand this an empty list,
        // and every empty list passes.
        Assert.True(byCheckpoint.Count >= 30,
            $"Only {byCheckpoint.Count} checkpoint(s) parsed out of PROGRESS as built. A count this "
            + "low means the entry heading or the Built label stopped matching rather than that the "
            + "record got shorter.");

        Assert.True(silent.Length == 0,
            $"{silent.Length} checkpoint(s) record work built and state no test count, which is the "
            + $"second half of done condition 2: {string.Join(", ", silent)}. Add the figure to the "
            + "checkpoint's own entry, from a run rather than from the commit message.");
    }

    /// <summary>One PROGRESS entry that records work built, and whether it carries the figure.</summary>
    public sealed record Entry(string Checkpoint, string Heading, bool StatesATestCount);

    /// <summary>
    /// Every entry recording work built, keyed by the checkpoint it belongs to.
    ///
    /// A lettered part is folded onto its checkpoint: 3.9(c) is 3.9's work and the condition is
    /// asked of 3.9. Entries whose first field is not a checkpoint identifier are the phase
    /// handovers and the sign-offs, which build nothing and record no CI run of their own.
    ///
    /// Public so <see cref="CheckProofTests"/> can run it against authored entries. A parser proved
    /// only against the corpus it happens to be reading is proved against one input, and the input
    /// it most needs to handle is the one that does not exist yet.
    /// </summary>
    public static IReadOnlyList<Entry> Entries(string progress)
    {
        Match[] headings = [.. EntryHeading().Matches(progress).Cast<Match>()];
        var entries = new List<Entry>();

        for (int i = 0; i < headings.Length; i++)
        {
            Match key = CheckpointKey().Match(headings[i].Groups["key"].Value);
            if (!key.Success)
            {
                continue;
            }

            int start = headings[i].Index;
            int end = i + 1 < headings.Length ? headings[i + 1].Index : progress.Length;
            string body = progress[start..end];

            if (!BuiltBlock().IsMatch(body))
            {
                continue;
            }

            entries.Add(new Entry(
                key.Groups["checkpoint"].Value,
                headings[i].Value.Trim(),
                TestCount().IsMatch(body)));
        }

        return entries;
    }
}
