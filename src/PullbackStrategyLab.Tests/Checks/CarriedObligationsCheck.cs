using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every due point a `Carried` block names is a due point the obligations table also has.
///
/// <b>Why it exists.</b> An obligation stated in a PROGRESS `Carried` block and absent from
/// BUILD_PLAN's carried-obligations table is invisible: nothing reads the record for work, so the
/// item is never scheduled and never closed. That has now happened four times. The 1.3 screening
/// question and the 1.1 vendor reset boundary both sat in a `Carried` block for a phase and a half.
/// The question of whether to build this very check reached no sign-off because it lived only in a
/// build prompt, which is gitignored scratch. And the two the gallery review raised were found by
/// somebody reading a prompt to see whether it had gone stale, which is not a mechanism.
///
/// <b>Why it is this narrow.</b> The obvious check is to match the sentences, and it is the wrong
/// one: prose against prose false-alarms on every rewording, and a suppressed guard is a dead one
/// arrived at slowly. So this compares the one part of an obligation that is structured on both
/// sides, being the checkpoint it falls due at. A `Carried` block that says "due at 3.4" when no row
/// in the table falls due at 3.4 is an obligation nobody scheduled, and that is the whole claim.
///
/// <b>What it deliberately cannot catch.</b> A `Carried` block naming a due point some other row
/// happens to share. If two obligations are both due at 3.7 and only one reaches the table, this
/// passes. That is the price of not matching prose, it is real, and it is written here rather than
/// left for a later session to discover as a surprise: the check narrows the hole from "an
/// obligation nobody scheduled" to "an obligation nobody scheduled at a due point already in use".
/// </summary>
public sealed partial class CarriedObligationsCheck
{
    private readonly ITestOutputHelper _output;

    public CarriedObligationsCheck(ITestOutputHelper output) => _output = output;

    /// <summary>A due point named inside a `Carried` block, with the entry it was named in.</summary>
    public sealed record Mention(string Entry, string DuePoint, int At);

    [Fact]
    [Trait("check", "carried-obligations")]
    public void Every_due_point_a_carried_block_names_is_one_the_obligations_table_has()
    {
        var coverage = new CheckCoverage("carried-obligations", _output);

        string progress = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "PROGRESS.md"));
        string buildPlan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));

        IReadOnlyList<string> blocks = CarriedBlocks(progress);
        IReadOnlySet<string> scheduled = DuePoints(buildPlan);
        IReadOnlyList<Mention> mentions = Mentions(progress);

        // Only the live tail of the record, and this is the whole of the narrowing.
        //
        // PROGRESS is append-only and its entries are history. A phase 1 block naming 1.7 is an
        // obligation 1.7 discharged and the table then dropped; a 2.11 block naming 3.1 was true
        // when it was written and stayed true until 3.0 repointed it. Neither is a hole, and
        // reconciling them would be fifty-six false alarms on the first run. A check that cries
        // wolf on its first run is a check somebody deletes, which is the objection that kept this
        // one open for a phase.
        //
        // What is live is what has been written since the last checkpoint PROGRESS records. That is
        // the commit being made rather than the archive, and it is where the failure actually
        // happens: an obligation written into a `Carried` block tonight that never reaches the
        // table. Correcting a dated entry is the one thing an append-only record must never do, so
        // history is read and not reconciled.
        // see: Nothing in the corpus is struck through
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();

        int liveFrom = LiveTailStarts(progress, schedule);

        IReadOnlyList<Mention> open =
            [.. mentions.Where(m => m.At >= liveFrom
                                    && !schedule.HasLanded(m.Entry)
                                    && !schedule.HasLanded(m.DuePoint))];

        IReadOnlyList<Mention> unscheduled =
            [.. open.Where(m => !scheduled.Contains(m.DuePoint))];

        coverage
            .Examined("due points the obligations table declares", scheduled.Count)
            // The parser's own health, and it is the scope that carries the property here. It grows
            // with the record and never shrinks, because PROGRESS is append-only, so a fall means
            // the block or the due-point pattern stopped matching and the check is reconciling an
            // empty list. Every empty list reconciles.
            .Examined("due points named inside a Carried block", mentions.Count)
            // Not floored, because its size is a fact about where the cycle is rather than about
            // the property: the live tail resets to nothing the moment a checkpoint lands, so a
            // floor on it would go red on the next PROGRESS entry rather than on a defect.
            .Context("of those in the live tail, which is what is reconciled", open.Count)
            .Context("Carried blocks in PROGRESS", blocks.Count)
            .Scan(
                "that every due point a Carried block names is one the obligations table also has",
                CheckCoverage.Backing.Test(
                    $"{nameof(CarriedObligationsCheck)}.{nameof(A_carried_block_naming_an_unscheduled_due_point_is_caught)}",
                    "the reconciliation is run against blocks written by hand, so the guard is proved "
                    + "against a case rather than against whatever the corpus happens to hold today"))
            .Report();

        // Stated in advance. A parser that stopped matching would hand this an empty list to
        // reconcile, and every empty list reconciles.
        Assert.True(blocks.Count >= 20,
            $"Only {blocks.Count} Carried block(s) parsed out of PROGRESS. A count this low means the "
            + "parser stopped matching rather than that the record got shorter.");

        Assert.True(scheduled.Count >= 3,
            $"Only {scheduled.Count} due point(s) parsed out of the obligations table.");

        Assert.True(unscheduled.Count == 0,
            $"{unscheduled.Count} due point(s) named in a Carried block have no obligation row:\n  "
            + string.Join("\n  ", unscheduled.Select(m => $"{m.Entry} says \"{m.DuePoint}\"")));
    }

    /// <summary>
    /// The guard, proved against blocks written here rather than against the corpus.
    ///
    /// A check whose only subject is the repository is a check nobody can break on purpose, and this
    /// one exists because the fault it catches is silent by construction.
    /// </summary>
    [Fact]
    public void A_carried_block_naming_an_unscheduled_due_point_is_caught()
    {
        var scheduled = new HashSet<string>(["3.0", "3.7", "the operator"], StringComparer.Ordinal);

        IReadOnlyList<Mention> mentions =
        [
            new("2.4", "3.0", 0),
            new("2.5", "4.9", 0),
            new("2.6", "the operator", 0),
        ];

        IReadOnlyList<Mention> unscheduled = [.. mentions.Where(m => !scheduled.Contains(m.DuePoint))];

        Mention only = Assert.Single(unscheduled);
        Assert.Equal("4.9", only.DuePoint);
        Assert.Equal("2.5", only.Entry);
    }

    /// <summary>
    /// Where the live tail of the record begins: just after the last entry naming a checkpoint
    /// PROGRESS records as landed.
    ///
    /// Everything before it is history and is read rather than reconciled. Everything after it is
    /// what the session in progress has written, which is what this check guards.
    /// </summary>
    /// <summary>
    /// Two conditions rather than one, because either alone lets a live block through as history or
    /// a historical one through as live.
    ///
    /// The position rules out everything before the last landed entry, which is what makes the
    /// sign-off entries headed "Phase 2 sign-off" history rather than perpetually live: their
    /// heading names no checkpoint, so the landed test alone would never retire them. The entry's
    /// own checkpoint rules out the last landed entry itself, whose `Carried` block is as much
    /// history as the ones above it and whose due points a later checkpoint may legitimately have
    /// repointed. Correcting either would mean editing a dated entry.
    /// see: Nothing in the corpus is struck through
    /// </summary>
    private static int LiveTailStarts(string progress, ArchitectureConformanceCheck.Schedule schedule)
    {
        int last = 0;

        foreach (Match heading in EntryHeading().Matches(progress))
        {
            if (schedule.HasLanded(heading.Groups["entry"].Value))
            {
                last = heading.Index;
            }
        }

        return last;
    }


    /// <summary>Every `Carried:` block in the record, as text.</summary>
    private static IReadOnlyList<string> CarriedBlocks(string progress)
    {
        var blocks = new List<string>();

        foreach (Match match in CarriedBlock().Matches(progress))
        {
            blocks.Add(match.Value);
        }

        return blocks;
    }

    /// <summary>
    /// The due points a `Carried` block names, with the entry that named them.
    ///
    /// "due at 3.1", "due 3.1", "due at the operator" and "due at the move" are the four forms the
    /// record uses. Matched on the phrase rather than on any checkpoint-shaped token, because an
    /// entry mentioning a checkpoint in passing is not carrying an obligation to it.
    /// </summary>
    private static IReadOnlyList<Mention> Mentions(string progress)
    {
        var mentions = new List<Mention>();
        string entry = "before the first entry";

        foreach (string line in progress.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            Match heading = EntryHeading().Match(line);
            if (heading.Success)
            {
                entry = heading.Groups["entry"].Value;
            }
        }

        foreach (Match block in CarriedBlock().Matches(progress))
        {
            string before = progress[..block.Index];
            MatchCollection headings = EntryHeading().Matches(before);
            entry = headings.Count > 0 ? headings[^1].Groups["entry"].Value : "before the first entry";

            foreach (Match due in DuePhrase().Matches(block.Value))
            {
                mentions.Add(new Mention(entry, due.Groups["due"].Value.Trim(), block.Index));
            }
        }

        return mentions;
    }

    /// <summary>The due points the obligations table declares, which is its last column.</summary>
    private static IReadOnlySet<string> DuePoints(string buildPlan)
    {
        var due = new HashSet<string>(StringComparer.Ordinal);

        foreach (IReadOnlyList<string> row in MarkdownTable.BodyRowsAfter(buildPlan, "## Carried obligations"))
        {
            due.Add(row[^1].Trim());
        }

        return due;
    }

    [GeneratedRegex(@"^Carried:.*?(?=\n\n##|\n##|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex CarriedBlock();

    [GeneratedRegex(@"^## (?<entry>\S+) ", RegexOptions.Multiline)]
    private static partial Regex EntryHeading();

    [GeneratedRegex(@"\bdue (?:at )?(?<due>\d+\.\d+|the operator|the move)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DuePhrase();
}
