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
/// <b>The other direction, from the 4.11 correction.</b> The reconciliation above reads the record
/// and asks the table about it, and it says nothing at all about a row already in the table. So an
/// obligation row whose due point has landed is an obligation the checkpoint shipped without coming
/// back to, and nothing said so: three rows fell due at 4.11 and 4.11 landed with all three still
/// pointing at it. That is the shape `architecture-conformance` refuses of a deferred claim and
/// `fixture-replay` refuses of a frozen-only permit, and it was missing from the one check whose
/// entire subject is the obligations table. The clause is a narrowing in the reconciliation above
/// for a reason that does not carry over: a landed due point in the record is history and reading
/// it back would be a false alarm about work that is done, where a landed due point in the table is
/// the table asking for work nobody will ever come for.
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

        // The table read as a set of rows rather than as a set of due points, which is the other
        // direction and the one the 4.11 correction added. `Overdue` fires on a row the build has
        // walked past; `Unplaced` fires on a row pointing at a checkpoint BUILD_PLAN does not have,
        // which is the same fault one step earlier.
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> rows = schedule.Obligations;
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> atACheckpoint =
            [.. rows.Where(o => IsACheckpoint(o.DueAt))];
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> overdue =
            Overdue(rows, schedule.HasLanded);
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> unplaced =
            Unplaced(rows, schedule.Exists);

        coverage
            .Examined("due points the obligations table declares", scheduled.Count)
            // The rows whose due point is a checkpoint, which is the population the overdue clause
            // governs. Floored, because it is the property scope of the second direction, and low
            // for the same reason the reconciled count above is: discharging obligations legitimately
            // empties it and a floor at today's figure would fire on the work being done.
            .Examined("obligation rows falling due at a checkpoint", atACheckpoint.Count)
            .Context("obligation rows falling due at a named event rather than a checkpoint",
                rows.Count - atACheckpoint.Count)
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
            .Scan(
                "that no obligation row falls due at a checkpoint the build has already walked past",
                CheckCoverage.Backing.Test(
                    $"{nameof(CarriedObligationsCheck)}.{nameof(A_row_falling_due_at_a_landed_checkpoint_is_caught)}",
                    "the same two functions the run calls are given a table holding a landed due point, an "
                    + "unlanded one, one naming a checkpoint the plan does not have and one naming an event, "
                    + "so each of the four dispositions is proved rather than only the two today's corpus "
                    + "happens to hold"))
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

        // Stated in advance, on the same grounds as the two above: a parse that stopped matching
        // would hand both clauses below an empty list, and every empty list holds.
        Assert.True(rows.Count >= 20,
            $"Only {rows.Count} obligation row(s) parsed out of the table. A count this low means the "
            + "parser stopped matching rather than that the corpus discharged them.");

        Assert.True(unplaced.Count == 0,
            $"{unplaced.Count} obligation row(s) fall due at a checkpoint BUILD_PLAN.md does not have, so "
            + "nothing will ever bring them due:" + Newline
            + string.Join(Newline, unplaced.Select(o => $"raised at {o.Raised}, due at {o.DueAt}")));

        Assert.True(overdue.Count == 0,
            $"{overdue.Count} obligation row(s) fall due at a checkpoint PROGRESS.md already records, so "
            + "that checkpoint shipped without discharging or repointing them and nothing said so at the "
            + "time. Discharge each and remove its row, or repoint it with the reason and the price:" + Newline
            + string.Join(Newline, overdue.Select(o => $"raised at {o.Raised}, due at {o.DueAt}: {Opening(o.What)}")));

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

    /// <summary>The two-space indent a multi-line failure message hangs its items on.</summary>
    private const string Newline = "\n  ";

    /// <summary>
    /// Obligation rows falling due at a checkpoint the build has already walked past.
    ///
    /// <b>A checkpoint due point only.</b> "the operator" and "the move" are events rather than rows
    /// in the plan, so nothing can say they have landed and a row naming one is open by construction.
    /// Treating an unparsable due point as still ahead is what
    /// <see cref="ArchitectureConformanceCheck.Obligation"/> already documents, and this reads it the
    /// same way rather than a second way.
    /// </summary>
    public static IReadOnlyList<ArchitectureConformanceCheck.Obligation> Overdue(
        IEnumerable<ArchitectureConformanceCheck.Obligation> obligations, Func<string, bool> hasLanded)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(hasLanded);

        return [.. obligations.Where(o => IsACheckpoint(o.DueAt) && hasLanded(o.DueAt))];
    }

    /// <summary>
    /// Obligation rows falling due at a checkpoint BUILD_PLAN.md does not have.
    ///
    /// The same fault one step earlier than <see cref="Overdue"/>: a row pointing at 4.18 is not
    /// overdue and never will be, because nothing will ever record it, and it reads exactly like a
    /// row that is waiting.
    /// </summary>
    public static IReadOnlyList<ArchitectureConformanceCheck.Obligation> Unplaced(
        IEnumerable<ArchitectureConformanceCheck.Obligation> obligations, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(exists);

        return [.. obligations.Where(o => IsACheckpoint(o.DueAt) && !exists(o.DueAt))];
    }

    /// <summary>Whether a due point names a checkpoint rather than an event.</summary>
    private static bool IsACheckpoint(string duePoint) => CheckpointShape().IsMatch(duePoint);

    /// <summary>The opening of an obligation, so a failure names the row without printing it whole.</summary>
    private static string Opening(string what)
    {
        string plain = what.Replace("*", string.Empty, StringComparison.Ordinal).Trim();
        return plain.Length <= 90 ? plain : plain[..90] + "...";
    }

    [GeneratedRegex(@"^\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointShape();

    /// <summary>
    /// The second direction, proved against a table written here.
    ///
    /// <b>Four dispositions and the corpus holds two of them.</b> A run over the live BUILD_PLAN
    /// exercises the unlanded checkpoint and the named event and nothing else, so the two clauses
    /// that can fail would both be filtering an empty list and would read exactly as they read when
    /// they hold. That is the shape the 4.17 pass found in `fixture-replay`, where every permit in
    /// the file took the branch no proof ever ran.
    /// </summary>
    [Fact]
    public void A_row_falling_due_at_a_landed_checkpoint_is_caught()
    {
        var landed = new HashSet<string>(["4.11"], StringComparer.Ordinal);
        var planned = new HashSet<string>(["4.11", "5.1"], StringComparer.Ordinal);

        ArchitectureConformanceCheck.Obligation[] rows =
        [
            new("4.16", "4.11", "the watchlist has no share count column"),
            new("3.0", "5.1", "a minimum sample computed as though observations were independent"),
            new("2.2", "9.9", "a due point the plan has no checkpoint for"),
            new("3.6", "the operator", "one elevated command, which no build session may run"),
        ];

        ArchitectureConformanceCheck.Obligation only = Assert.Single(Overdue(rows, landed.Contains));
        Assert.Equal("4.16", only.Raised);

        ArchitectureConformanceCheck.Obligation nowhere = Assert.Single(Unplaced(rows, planned.Contains));
        Assert.Equal("9.9", nowhere.DueAt);

        // The two that are open, each for a different reason, and neither is either failure.
        Assert.DoesNotContain(Overdue(rows, landed.Contains), o => o.DueAt is "5.1" or "the operator");
        Assert.DoesNotContain(Unplaced(rows, planned.Contains), o => o.DueAt is "5.1" or "the operator");
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

    /// <summary>
    /// The parser, proved against every form the record writes a due point in.
    ///
    /// <b>Why this exists separately from the test above.</b> That one proves the reconciliation and
    /// builds its <see cref="Mention"/> values by hand, which steps over the parser completely. So
    /// the rule was guarded and the reading of the record was not, and the reading is the half that
    /// was broken: the pattern matched four of the six forms the corpus actually uses and dropped
    /// "due before 5.1" on the floor, which was a real obligation nobody had scheduled.
    ///
    /// <b>Nine, stated in advance.</b> A sweep expecting a non-zero count states that count before
    /// running, because "it found some" is self-validating. Four in the first block and five in the
    /// second, the fifth being a phrase wrapped across a line break, which the literal space in the
    /// old pattern could not cross either.
    /// </summary>
    [Fact]
    public void The_parser_reads_every_form_the_record_writes_a_due_point_in()
    {
        const string progress = """
## 2.4 — 2026-01-01 — a-branch — the first entry

Carried:    One due at 3.1, one due 3.2, one due at the operator and one due at the move.

## 2.5 — 2026-01-02 — a-branch — the second entry

Carried:    **One new, due before 5.1**: a minimum sample restated. Due **4.1**, with the other
            band 0 item. Another is due **at 3.6**, and one is Due at **the operator**: it decides
            whether a night's evidence may be completed after the fact. A last one is due at
            4.6 with the rest of the verification work.

            A sentence about a due point that moves at every sign-off names no due point at all,
            and neither does a due date, a due diligence or the word due on its own.
""";

        IReadOnlyList<Mention> mentions = Mentions(progress);

        Assert.Equal(9, mentions.Count);

        Assert.Equal(
            ["3.1", "3.2", "the move", "the operator"],
            mentions.Where(m => m.Entry == "2.4").Select(m => m.DuePoint).Order(StringComparer.Ordinal));

        // The five the old pattern could not see, named one at a time so a failure says which form
        // regressed rather than only that a count moved.
        IReadOnlyList<string> second =
            [.. mentions.Where(m => m.Entry == "2.5").Select(m => m.DuePoint)];

        Assert.Contains("5.1", second);          // "due before 5.1", the one that was lost
        Assert.Contains("4.1", second);          // "Due **4.1**", emphasis before the checkpoint
        Assert.Contains("3.6", second);          // "due **at 3.6**", emphasis before the "at"
        Assert.Contains("the operator", second); // "Due at **the operator**", emphasis after it
        Assert.Contains("4.6", second);          // "due at\n4.6", wrapped across a line break

        // And the negative half, which is what stops the pattern being widened into a token scan:
        // a checkpoint mentioned in passing is not an obligation carried to it.
        Assert.DoesNotContain(mentions, m => m.DuePoint is "5.7" or "1.1");
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
    /// Matched on the phrase rather than on any checkpoint-shaped token, because an entry
    /// mentioning a checkpoint in passing is not carrying an obligation to it.
    ///
    /// <b>The pattern named four forms and the record writes six.</b> It was
    /// `\bdue (?:at )?` with a literal space, so it saw "due at 3.1", "due 3.1",
    /// "due at the operator" and "due at the move", and silently missed "Due **4.1**",
    /// "due **at 3.6**", "Due at **the operator**" and "due before 5.1": markdown emphasis inside
    /// the phrase, and "before" where the record says before rather than at. The literal space
    /// missed a further form nothing had noticed, being a phrase wrapped across a line break, which
    /// is the whitespace tolerance CLAUDE.md requires of greps over markdown and this did not have.
    ///
    /// <b>What it cost, measured before the change.</b> 65 due points recognised of 71 present in
    /// the same blocks. Five of the six missed name a checkpoint the old pattern would have matched
    /// but for the markup, so they reconciled correctly by luck through some other mention. The
    /// sixth is the one that mattered: "due before 5.1", the 160-observation minimum sample raised
    /// at 3.0, whose due point was in no obligation row at all. So the check that exists to catch
    /// an obligation nobody scheduled was holding one for a phase and a half, and none of its own
    /// numbers could show it. A due point the pattern never matched never enters `mentions`, and
    /// the floor under that count catches a fall from where the count already was rather than a
    /// scope it never reached. That is this docstring's own failure a fifth time, and the first
    /// instance the check itself was hiding.
    ///
    /// <b>Public because the parser now has a proof.</b> The reconciliation was proved against
    /// hand-built <see cref="Mention"/> values, which is the right shape for that rule and steps
    /// over the parser entirely, so nothing exercised the one part that was broken.
    /// </summary>
    public static IReadOnlyList<Mention> Mentions(string progress)
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

    [GeneratedRegex(
        @"\bdue\s+(?:\*\*\s*)?(?:(?:at|before)\s+)?(?:\*\*\s*)?(?<due>\d+\.\d+|the operator|the move)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DuePhrase();
}
