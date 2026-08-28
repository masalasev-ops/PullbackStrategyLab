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

        // What is reconciled is every mention whose due point has not landed, and the narrowing is
        // that one clause and nothing else.
        //
        // PROGRESS is append-only and its entries are history. A phase 1 block naming 1.7 is an
        // obligation 1.7 discharged and the table then dropped, so reconciling it would be a false
        // alarm about work that is done. An obligation whose due point is still ahead is the
        // opposite: it has to be in the table, because the table is the only place anything reads
        // for work. Correcting a dated entry is the one thing an append-only record must never do,
        // so history is read and not reconciled.
        // see: Nothing in the corpus is struck through
        //
        // <b>It used to filter on the entry as well, and that made it reconcile nothing.</b> The
        // clause was `!schedule.HasLanded(m.Entry)`, and every `Carried` block sits under a PROGRESS
        // entry heading whose existence is what makes that checkpoint landed. So the guard's window
        // closed in the same commit that created the thing to guard, for every numbered checkpoint,
        // and the run's own coverage line recorded "of those in the live tail, which is what is
        // reconciled: 0" from 3.0 onward with nothing floored to notice. The live-tail cutoff on
        // top of it was the same defect a second time: the tail begins after the last checkpoint
        // PROGRESS records, and a checkpoint's own entry is that record.
        //
        // Reconciling on the due point alone took the count from 0 to 10 and found the hole on the
        // first run: the 1.5 corrections entry carries an obligation due at 6.5 that never reached
        // the table. 6.5's own done condition names the test, so it was scheduled in substance and
        // absent from the one place obligations are supposed to live, which is exactly the failure
        // the check's own docstring says has happened four times.
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();

        IReadOnlyList<Mention> open = Live(mentions, schedule.HasLanded);
        IReadOnlyList<Mention> unscheduled = Unscheduled(open, scheduled);

        coverage
            .Examined("due points the obligations table declares", scheduled.Count)
            // The parser's own health, and it is the scope that carries the property here. It grows
            // with the record and never shrinks, because PROGRESS is append-only, so a fall means
            // the block or the due-point pattern stopped matching and the check is reconciling an
            // empty list. Every empty list reconciles.
            .Examined("due points named inside a Carried block", mentions.Count)
            // Floored, and it is the scope that carries the property: this is the number of
            // obligations actually put to the table. It was 0 for the whole of phase 3, recorded as
            // context, which is a floor's absence doing exactly what the corpus says it does.
            //
            // The floor is 1 rather than today's 10, and the direction of the risk is why. This
            // count legitimately *falls* as due points land, so a floor at the current value would
            // go red on the commit that discharges 4.1's seventeen rows, which is a false alarm
            // about work being done. What it has to catch is the count reaching nothing, and the
            // assertion below says that in words rather than leaving it to a number nobody reads.
            .Examined("of those whose due point has not landed, which is what is reconciled", open.Count)
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

        // The one this check spent phase 3 failing to say. Reconciling nothing is not a pass, and
        // it is indistinguishable from a pass in every number above except this one.
        Assert.True(open.Count > 0,
            $"{mentions.Count} due point(s) are named in a Carried block and none of them was reconciled, "
            + "because every one names a due point that has already landed. Either the record holds no open "
            + "obligation at all, which the obligations table can be read to confirm, or the filter has "
            + "emptied itself and this check is asserting over nothing. The second is what happened from 3.0 "
            + "until 3.10.");

        Assert.True(unscheduled.Count == 0,
            $"{unscheduled.Count} due point(s) named in a Carried block have no obligation row:\n  "
            + string.Join("\n  ", unscheduled.Select(m => $"{m.Entry} says \"{m.DuePoint}\"")));
    }

    /// <summary>
    /// Which mentions are still open: the ones whose due point has not landed.
    ///
    /// A named function rather than a lambda inside the check, so the proof below can run the
    /// reconciliation itself instead of a copy of it. The copy is how the previous proof passed
    /// while the live filter reconciled nothing: it re-implemented the intended rule inline and
    /// asserted against its own re-implementation, so the clause that emptied the real set was
    /// never on the path the proof took.
    /// </summary>
    public static IReadOnlyList<Mention> Live(IEnumerable<Mention> mentions, Func<string, bool> hasLanded)
    {
        ArgumentNullException.ThrowIfNull(mentions);
        ArgumentNullException.ThrowIfNull(hasLanded);

        return [.. mentions.Where(m => !hasLanded(m.DuePoint))];
    }

    /// <summary>Open mentions whose due point no obligation row declares.</summary>
    public static IReadOnlyList<Mention> Unscheduled(
        IEnumerable<Mention> live, IReadOnlySet<string> scheduled)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(scheduled);

        return [.. live.Where(m => !scheduled.Contains(m.DuePoint))];
    }

    /// <summary>
    /// The guard, proved by running the reconciliation against blocks written here.
    ///
    /// A check whose only subject is the repository is a check nobody can break on purpose, and this
    /// one exists because the fault it catches is silent by construction.
    ///
    /// <b>It calls <see cref="Live"/> and <see cref="Unscheduled"/>, which is what the check calls.</b>
    /// The earlier version of this test re-implemented the filter inline and asserted against its
    /// own copy, so it proved a rule the check did not run: the live filter also demanded that the
    /// mention's own entry had not landed, which is never true of a numbered checkpoint's own
    /// PROGRESS entry, and this test passed for the whole of phase 3 over a reconciliation of zero
    /// items. The two cases below are the two directions, and the third asserts that a mention
    /// whose due point has already landed is not reconciled, which is the one narrowing that stays.
    /// </summary>
    [Fact]
    public void A_carried_block_naming_an_unscheduled_due_point_is_caught()
    {
        var scheduled = new HashSet<string>(["3.0", "3.7", "the operator"], StringComparer.Ordinal);
        var landed = new HashSet<string>(["1.7"], StringComparer.Ordinal);

        IReadOnlyList<Mention> mentions =
        [
            new("2.4", "3.0", 0),
            new("2.5", "4.9", 0),
            new("2.6", "the operator", 0),
            new("1.3", "1.7", 0),
        ];

        IReadOnlyList<Mention> open = Live(mentions, landed.Contains);

        // The landed one drops out and the other three are reconciled. A filter that emptied the
        // set would fail here rather than further down, where an empty set has nothing unscheduled
        // in it and reads as a pass.
        Assert.Equal(3, open.Count);
        Assert.DoesNotContain(open, m => m.DuePoint == "1.7");

        Mention only = Assert.Single(Unscheduled(open, scheduled));
        Assert.Equal("4.9", only.DuePoint);
        Assert.Equal("2.5", only.Entry);
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
