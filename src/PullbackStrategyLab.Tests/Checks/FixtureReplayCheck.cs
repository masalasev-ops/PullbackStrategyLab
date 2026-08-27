using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The pipeline, run over the committed golden fixture, diffed against the committed
/// expectations, broken down by the tier each expectation was produced at.
///
/// The tier is the whole measurement. A FROZEN expectation was produced by this code and can
/// only ever say that the code has not changed; a DERIVED one was produced by a second
/// implementation and a CONFIRMED one by reading a platform nobody here wrote, and only those
/// two can say the code is right. A report that added them up into one number would let a
/// checkpoint discharge its obligation with regression detection and call it verification.
/// see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything
///
/// Every measurement the replay produces and no expectation names is reported as unexamined,
/// which is how a stage that starts reporting a new figure gets noticed rather than absorbed.
/// </summary>
public sealed partial class FixtureReplayCheck
{
    /// <summary>Expectations produced by this code. They detect change and verify nothing.</summary>
    public const string Frozen = "FROZEN";

    /// <summary>Produced by a second implementation of the same arithmetic, from the same input.</summary>
    public const string Derived = "DERIVED";

    /// <summary>Read off a platform outside this project entirely.</summary>
    public const string Confirmed = "CONFIRMED";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITestOutputHelper _output;

    public FixtureReplayCheck(ITestOutputHelper output) => _output = output;

    public static string ExpectationsFile => Path.Combine(RepositoryLayout.Root, "fixtures", "expectations.json");

    [Fact]
    [Trait("check", "fixture-replay")]
    public void The_pipeline_over_the_fixture_matches_every_committed_expectation()
    {
        var coverage = new CheckCoverage("fixture-replay", _output);

        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        PhaseReplayResult result = replay.Run();

        var actual = result.Measurements.ToDictionary(m => m.Id, m => m.Value, StringComparer.Ordinal);

        Directory.CreateDirectory(RepositoryLayout.Artifacts);
        replay.SnapshotTo(Path.Combine(RepositoryLayout.Artifacts, "replay.db"));

        // Written on every run, expectations or not. It is what a checkpoint adding behaviour
        // starts from, so adding an expectation is copying a line rather than deriving a number
        // by hand and mistyping it.
        File.WriteAllText(
            Path.Combine(RepositoryLayout.Artifacts, "expectations.proposed.json"),
            JsonSerializer.Serialize(
                new ExpectationFile(
                    result.AsOf.ToString("yyyy-MM-dd"),
                    result.InputTier,
                    [.. result.Measurements.Select(m => new Expectation(m.Id, Frozen, m.Value, "unassigned", "proposed by the replay, tier not yet decided", null))]),
                Json));

        ExpectationFile expected = ReadExpectations();
        var rows = new List<DiffRow>();

        foreach (Expectation expectation in expected.Expectations)
        {
            bool produced = actual.TryGetValue(expectation.Id, out string? value);

            // A comparison that was attempted and could not be made is void, and is kept as a
            // void row rather than dropped. The case it exists for is a CONFIRMED reading whose
            // platform defines the figure differently: comparing the lab's mean of (high-low)/close
            // against a platform's sma(high,20)/sma(low,20)-1 returns a disagreement about two
            // definitions and says nothing about either implementation. Recording that as
            // agreement would be worse than not looking, and deleting the row would lose the fact
            // that somebody did look.
            string verdict = expectation.VoidedBecause is not null
                ? "void"
                : !produced ? "missing" : value == expectation.Value ? "matched" : "differed";

            rows.Add(new DiffRow(
                expectation.Id,
                expectation.Tier,
                expectation.Checkpoint,
                expectation.Value,
                produced ? value : null,
                verdict,
                expectation.ProducedBy));
        }

        string[] unexpected = actual.Keys
            .Where(id => !expected.Expectations.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var byTier = rows
            .GroupBy(r => r.Tier, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TierBreakdown(
                g.Key,
                g.Count(),
                g.Count(r => r.Verdict == "matched"),
                g.Count(r => r.Verdict == "differed"),
                g.Count(r => r.Verdict == "missing"),
                g.Count(r => r.Verdict == "void")))
            .ToArray();

        var diff = new FixtureDiff(
            result.AsOf.ToString("yyyy-MM-dd"),
            result.InputTier,
            result.CapturedResponses,
            result.ResponsesServed,
            result.AskedOutsideTheFixture,
            result.AskedOnAnUncoveredEndpoint,
            result.ScreeningSessions,
            result.Stages,
            byTier,
            unexpected,
            rows);

        File.WriteAllText(
            Path.Combine(RepositoryLayout.Artifacts, "fixture-diff.json"),
            JsonSerializer.Serialize(diff, Json));

        // The total carries the property: an expectation deleted is coverage lost, and that is
        // what a floor here has to catch.
        coverage.Examined("expectations diffed", rows.Count);

        // Per tier, and only the independent tiers are floored. A FROZEN count falling is
        // ambiguous in a way a floor cannot resolve: it falls when an expectation is deleted,
        // which is a defect, and equally when one is promoted to DERIVED, which is the whole
        // direction of travel this corpus wants. Flooring it turns every promotion into a red run,
        // and a guard that cries wolf gets suppressed. Found on the first promotion after the
        // floors landed, when flipping fourteen 1.3-to-1.7 expectations to DERIVED took FROZEN
        // from 269 to 255 and failed the check for having improved the fixture.
        //
        // So FROZEN is context: its size is a fact about the fixture's composition. The property
        // is held by the total above, which a deletion moves and a promotion does not, and by the
        // independent tiers below, which only rise.
        foreach (TierBreakdown tier in byTier)
        {
            if (string.Equals(tier.Tier, Frozen, StringComparison.Ordinal))
            {
                coverage.Context($"{tier.Tier} expectations diffed", tier.Total);
                continue;
            }

            coverage.Examined($"{tier.Tier} expectations diffed", tier.Total);
        }

        coverage.Context("captured responses the replay read", result.ResponsesServed);

        if (unexpected.Length > 0)
        {
            coverage.NotExamined("figures the replay produced that no expectation names", unexpected.Length,
                "the fixture has widened and nothing was added to the expectations: "
                + string.Join(", ", unexpected.Take(8)) + (unexpected.Length > 8 ? ", ..." : string.Empty));
        }

        if (result.AskedOutsideTheFixture.Count > 0)
        {
            // Examined, not unexamined. The endpoint has captured evidence and was asked about a
            // name or a market day the fixture does not hold, which is the fixture's boundary
            // rather than a hole in it.
            coverage.OutOfScope("requests answered as the vendor answers a name it has nothing on",
                result.AskedOutsideTheFixture.Count,
                CheckCoverage.OutOfScopeReason.ByDesign(
                    "the endpoint has captured evidence and was asked about a name or a market day the fixture does "
                    + "not hold. That is the fixture's boundary rather than a hole in it, and a fixture has one"));
        }

        // The half of the 1.7 obligation the fixture cannot close, stated as one thing rather than
        // left inside the note on a frozen row.
        //
        // The liquidity floor is a median over twenty sessions. For the fixture's own names that
        // is computable and is computed: the replay measures the real floor against the real
        // window over their 251-session histories, and those figures carry expectations. What one
        // captured market day cannot support is the same comparison across the whole market,
        // because the bulk endpoint is charged per day and the fixture holds one of them.
        //
        // Out of scope with the condition written down, not unexamined: nineteen more bulk days
        // would end it, at 1,900 calls and about 130 MB committed for ever, to close a gap the
        // live run closes every night. That is the trade, and it is recorded here so the number is
        // argued with rather than rediscovered.
        coverage.OutOfScope("the whole-market screen under the twenty-session liquidity floor", 1,
            CheckCoverage.OutOfScopeReason.UntilDecided(
                "1,900 vendor calls and about 130 MB committed to the repository for ever",
                $"the fixture holds {result.ScreeningSessions} captured market day(s) and the floor is a median over "
                + $"{new UniverseOptions().LiquidityWindowSessions}. Ends when the capture holds twenty bulk days. The "
                + "per-ticker half of the same floor is measured, not deferred: see the liquidity.* expectations"));

        // The floor's rejecting side, which nothing in the fixture exercises.
        //
        // All thirty measured names clear the liquidity floor and the price floor, the closest at
        // 1.7 times the liquidity floor, so every expectation on the comparison is an expectation
        // on it passing. A screen tested only where it admits is a screen whose rejection could
        // stop working without a single figure moving.
        //
        // The three trackers are the obvious candidate and are the wrong one. They are the only
        // captured names the universe excludes, and the symbol list types them ETF, so they are
        // excluded before either floor is reached; they clear both by two to three orders of
        // magnitude. They pin the type filter instead, which is worth having and is not this.
        //
        // Unlike the whole-market screen this is cheap to close, and the price is worth stating so
        // the two are not carried as though they cost the same: one per-ticker history call for a
        // name chosen to fail, at the next capture. It is out of scope because no captured name
        // fails today, not because closing it is expensive.
        coverage.OutOfScope("the liquidity floor's rejecting side", 1,
            CheckCoverage.OutOfScopeReason.UntilDecided(
                "one per-ticker vendor call at the next capture",
                "all 30 measured names clear both floors, the closest at 1.7 times the liquidity floor, and the three "
                + "trackers are excluded by security type rather than by a floor. Ends when the capture holds one "
                + "name that fails a floor"));

        if (result.AskedOnAnUncoveredEndpoint.Count > 0)
        {
            coverage.NotExamined("requests on an endpoint with no captured response", result.AskedOnAnUncoveredEndpoint.Count,
                "the replay exercised the path and the fixture answered it with nothing: "
                + string.Join(", ", result.AskedOnAnUncoveredEndpoint.Take(6)));
        }

        // Done condition seven, per checkpoint, with the frozen-only ones named and each one's
        // permission read from BUILD_PLAN rather than assumed.
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();
        IReadOnlyList<string> doneConditionSeven = DoneConditionSevenProblems(
            expected.Expectations, expected.FrozenOnly ?? [], schedule.Obligations, schedule.HasLanded);

        CheckpointTier[] tiers = [.. ByCheckpoint(expected.Expectations)];
        CheckpointTier[] frozenOnly = [.. tiers.Where(t => t.Independent == 0)];

        coverage.NoSourceScan(
            "it runs the pipeline over the golden fixture and diffs what the run produced. Every figure it "
            + "compares was computed by the code rather than read out of it");

        coverage.Examined("checkpoints with expectations in the fixture", tiers.Length);
        coverage.Examined("of those carrying an independently produced expectation", tiers.Length - frozenOnly.Length);

        foreach (CheckpointTier checkpoint in frozenOnly)
        {
            Permit? permit = (expected.FrozenOnly ?? [])
                .FirstOrDefault(f => string.Equals(f.Checkpoint, checkpoint.Checkpoint, StringComparison.Ordinal));

            // Out of scope rather than unexamined, and named one checkpoint at a time. A single
            // row saying "five checkpoints are frozen-only" is the shape of report that let this
            // sit unnoticed, because it reads as one item rather than as five.
            //
            // The deferral is to the checkpoint the permit's obligation falls due at, which puts a
            // frozen-only checkpoint under the same rule as everything else here: that checkpoint
            // has to exist and has to be open. Where the permit resolves to nothing the deferral is
            // by design and says so, because the assertion above has already failed the run and a
            // second complaint about the same thing would only crowd the page.
            IReadOnlyList<ArchitectureConformanceCheck.Obligation> matches = permit is null
                ? []
                : MatchingObligations(schedule.Obligations, permit.Obligation);

            coverage.OutOfScope(
                $"checkpoint {checkpoint.Checkpoint}, whose {checkpoint.Total} expectation(s) are all FROZEN",
                1,
                matches.Count == 1
                    ? CheckCoverage.OutOfScopeReason.UntilCheckpoint(
                        matches[0].DueAt,
                        PermitReason(schedule.Obligations, permit, schedule.HasLanded))
                    : CheckCoverage.OutOfScopeReason.ByDesign(
                        PermitReason(schedule.Obligations, permit, schedule.HasLanded)));
        }

        coverage.Report();

        DiffRow[] broken = rows.Where(r => r.Verdict is not ("matched" or "void")).ToArray();

        Assert.True(broken.Length == 0,
            $"{broken.Length} expectation(s) did not hold over the fixture:\n  "
            + string.Join("\n  ", broken.Take(20).Select(r =>
                $"{r.Id} [{r.Tier}, {r.Checkpoint}] expected {r.Expected}, got {r.Actual ?? "nothing"}"))
            + (broken.Length > 20 ? $"\n  ... and {broken.Length - 20} more" : string.Empty));

        // Done condition seven, asserted per checkpoint rather than once over the fixture.
        //
        // The condition is written per checkpoint: a checkpoint that adds only frozen expectations
        // has added regression detection and called it verification. Until the 1.12 review this
        // asserted one DERIVED anywhere in the fixture, which is a condition sixty later rows can
        // satisfy on behalf of the one checkpoint that never met it, and it passed the whole time
        // five checkpoints were frozen-only. Same shape as the label on this comment claiming more
        // than the line beneath it did.
        Assert.True(doneConditionSeven.Count == 0,
            $"{doneConditionSeven.Count} checkpoint(s) do not meet done condition seven:\n  "
            + string.Join("\n  ", doneConditionSeven));

        // A CONFIRMED row is the only tier whose provenance is a person rather than a program, so
        // it is the only one whose provenance can go missing without anything noticing. The rule
        // is the same one the corpus applies to every measurement: a figure that came from outside
        // a run is labelled with where it came from, or it will be cited later as though it had
        // been measured here.
        string[] unprovenanced = [.. WithoutProvenance(expected.Expectations)];

        Assert.True(unprovenanced.Length == 0,
            $"{unprovenanced.Length} CONFIRMED expectation(s) do not name the platform they were read from and the date "
            + $"they were read: {string.Join(", ", unprovenanced)}. A confirmed value is worth more than a derived one "
            + "only because somebody outside this project produced it, so producedBy has to say who and when, in the "
            + "form \"read from <platform> on <yyyy-mm-dd>\".");

        // And the daily range is the figure most likely to be compared against something that is
        // not the same quantity, because platforms disagree about what an average daily range is.
        string[] undefinedRange = [.. WithoutARangeDefinition(expected.Expectations)];

        Assert.True(undefinedRange.Length == 0,
            $"{undefinedRange.Length} CONFIRMED daily-range expectation(s) state no definition: "
            + $"{string.Join(", ", undefinedRange)}. This lab means the mean of (high-low)/close; a platform computing "
            + "sma(high,20)/sma(low,20)-1 is reporting a different quantity, and comparing the two returns a "
            + "disagreement about definitions. Either the note records the platform's definition and that it is the "
            + "same one, or the row carries voidedBecause and is recorded as void rather than as agreement.");

        Assert.True(unexpected.Length == 0,
            $"The replay produced {unexpected.Length} figure(s) no expectation names, so they are unexamined: "
            + string.Join(", ", unexpected.Take(20)));
    }

    /// <summary>
    /// Done condition seven, taken per checkpoint: what is wrong with the fixture's tiers, or
    /// nothing.
    ///
    /// The rule the corpus states is that a checkpoint's expectations include at least one that is
    /// DERIVED or CONFIRMED. A checkpoint that cannot meet it yet is permitted, and the permission
    /// is not a note in a file: it names a carried obligation, that obligation has to be a row of
    /// BUILD_PLAN's table, and the checkpoint it falls due at has to be one PROGRESS does not yet
    /// record. That is the same test an out-of-scope architecture claim passes, for the same
    /// reason: a deferral that names nothing rests where it is forever, and one that names a
    /// checkpoint which has already landed is a checkpoint that shipped without coming back.
    ///
    /// Asserted in both directions. A permit for a checkpoint that has since gained an independent
    /// expectation is spent, and leaving it would quietly re-permit the checkpoint if that
    /// expectation were ever removed.
    ///
    /// Pure, and separated from the run so it can be proved against expectations and obligations
    /// written by hand rather than against whatever the fixture happens to hold today.
    /// see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything
    /// </summary>
    public static IReadOnlyList<string> DoneConditionSevenProblems(
        IReadOnlyList<Expectation> expectations,
        IReadOnlyList<Permit> permits,
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> obligations,
        Func<string, bool> hasLanded)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        ArgumentNullException.ThrowIfNull(permits);
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(hasLanded);

        var problems = new List<string>();

        foreach (CheckpointTier checkpoint in ByCheckpoint(expectations))
        {
            Permit? permit = permits.FirstOrDefault(
                p => string.Equals(p.Checkpoint, checkpoint.Checkpoint, StringComparison.Ordinal));

            if (checkpoint.Independent > 0)
            {
                if (permit is not null)
                {
                    problems.Add(
                        $"{checkpoint.Checkpoint} is listed as frozen-only and now carries {checkpoint.Independent} "
                        + "independently produced expectation(s). The permit is spent and leaving it would re-permit "
                        + "the checkpoint silently if that expectation were ever removed.");
                }

                continue;
            }

            if (permit is null)
            {
                problems.Add(
                    $"every one of {checkpoint.Checkpoint}'s {checkpoint.Total} expectation(s) is FROZEN, and nothing "
                    + "permits it. Done condition seven asks each checkpoint for at least one DERIVED or CONFIRMED "
                    + "expectation, so either one is added or the checkpoint names a carried obligation that is open.");
                continue;
            }

            IReadOnlyList<ArchitectureConformanceCheck.Obligation> matches =
                MatchingObligations(obligations, permit.Obligation);

            if (matches.Count == 0)
            {
                problems.Add(
                    $"{checkpoint.Checkpoint} is frozen-only and names an obligation raised at {permit.Obligation}, "
                    + "and BUILD_PLAN's carried obligations table has no row raised there. A permission resting on "
                    + "nothing is the same as no permission.");
                continue;
            }

            // More than one row raised at the same checkpoint is legitimate: the table is keyed by
            // who raised an obligation, not by the obligation. What is not legitimate is a permit
            // naming that checkpoint, because `Raised` is then being used as a key it is not. This
            // was `FirstOrDefault` until 2.1, unambiguous only because every row happened to carry
            // a distinct `Raised`; restoring BUILD_PLAN's malformed row put two at 1.12 and made
            // the lookup silently order-dependent on whatever MarkdownTable returned first.
            if (matches.Count > 1)
            {
                problems.Add(
                    $"{checkpoint.Checkpoint} is frozen-only and names the obligation raised at {permit.Obligation}, "
                    + $"and BUILD_PLAN's carried obligations table has {matches.Count} rows raised there, falling due "
                    + $"at {string.Join(" and ", matches.Select(m => m.DueAt))}. The permit resolves to whichever the "
                    + "parser returns first, so the due point it rests on is not stated. Name the obligations apart, "
                    + "or discharge this checkpoint rather than permitting it.");
                continue;
            }

            ArchitectureConformanceCheck.Obligation obligation = matches[0];

            if (hasLanded(obligation.DueAt))
            {
                problems.Add(
                    $"{checkpoint.Checkpoint} is frozen-only under the obligation raised at {obligation.Raised}, which "
                    + $"falls due at {obligation.DueAt}, and PROGRESS already records {obligation.DueAt}. That "
                    + "checkpoint shipped without discharging it and nothing said so at the time.");
            }
        }

        foreach (Permit permit in permits)
        {
            if (!expectations.Any(e => string.Equals(e.Checkpoint, permit.Checkpoint, StringComparison.Ordinal)))
            {
                problems.Add(
                    $"{permit.Checkpoint} is listed as frozen-only and has no expectations in the fixture at all, so "
                    + "the permit names a checkpoint the diff never reaches.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Every obligation raised at the given checkpoint.
    ///
    /// A list rather than a single row, because BUILD_PLAN's carried-obligations table is keyed by
    /// the checkpoint that raised each item and two obligations can legitimately be raised at one.
    /// A permit naming such a checkpoint is what is wrong, not the table, and a lookup returning
    /// the first match would hide exactly that.
    /// </summary>
    public static IReadOnlyList<ArchitectureConformanceCheck.Obligation> MatchingObligations(
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> obligations,
        string raised)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentException.ThrowIfNullOrWhiteSpace(raised);

        return [.. obligations.Where(o => string.Equals(o.Raised, raised, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Why a frozen-only checkpoint rests out of scope, as the coverage record states it.
    ///
    /// Pure, and it re-asks <paramref name="hasLanded"/> rather than resolving the obligation and
    /// stopping there. Until 2.1 it did stop there, so on a red run the record read "permitted by
    /// the obligation raised at 1.1, which falls due at 2.1" on the same page as the failure saying
    /// that permission had expired. The run was red either way; what was wrong was the page.
    /// </summary>
    public static string PermitReason(
        IReadOnlyList<ArchitectureConformanceCheck.Obligation> obligations,
        Permit? permit,
        Func<string, bool> hasLanded)
    {
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(hasLanded);

        if (permit is null)
        {
            return "nothing permits it";
        }

        IReadOnlyList<ArchitectureConformanceCheck.Obligation> matches =
            MatchingObligations(obligations, permit.Obligation);

        if (matches.Count == 0)
        {
            return $"it names an obligation raised at {permit.Obligation} and no row is raised there, so nothing permits it";
        }

        if (matches.Count > 1)
        {
            return $"it names {matches.Count} obligations raised at {permit.Obligation}, so the due point it rests on is not stated";
        }

        ArchitectureConformanceCheck.Obligation obligation = matches[0];

        return hasLanded(obligation.DueAt)
            ? $"the obligation raised at {obligation.Raised} fell due at {obligation.DueAt}, which PROGRESS already "
                + "records, so the permission is spent"
            : $"permitted by the obligation raised at {obligation.Raised}, which falls due at {obligation.DueAt}";
    }

    /// <summary>
    /// Each checkpoint in the fixture, with how many of its expectations verify anything.
    ///
    /// <b>A voided row verifies nothing, whatever tier it carries.</b> `voidedBecause` is how an
    /// expectation says its subject no longer exists or can no longer be compared, and such a row is
    /// recorded as void rather than as agreement. Counting it as independent would let a checkpoint
    /// satisfy done condition seven with a `DERIVED` expectation that compares nothing, which is
    /// exactly the state that condition exists to make visible.
    ///
    /// Theoretical until 2.9, and that is the reason to fix it here rather than when it bites:
    /// `CONFIRMED` is the tier the void mechanism was written for, because a figure a person read off
    /// a platform is the one kind that can stop being comparable without any code changing.
    /// see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything
    /// </summary>
    public static IReadOnlyList<CheckpointTier> ByCheckpoint(IReadOnlyList<Expectation> expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        return
        [
            .. expectations
                .GroupBy(e => e.Checkpoint, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CheckpointTier(
                    g.Key,
                    g.Count(),
                    g.Count(e => e.Tier is Derived or Confirmed && e.VoidedBecause is null)))
        ];
    }

    /// <summary>
    /// The CONFIRMED expectations that do not say where they were read from and when.
    ///
    /// Separated from the run so it can be proved against expectations written by hand. It is the
    /// one tier whose provenance is a person rather than a program, so it is the one that can lose
    /// its provenance with nothing noticing.
    /// </summary>
    public static IReadOnlyList<string> WithoutProvenance(IReadOnlyList<Expectation> expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        return
        [
            .. expectations
                .Where(e => e.Tier == Confirmed)
                .Where(e => !NamesAPlatformAndADate(e.ProducedBy))
                .Select(e => e.Id)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// The CONFIRMED daily-range expectations that neither state the platform's definition nor
    /// declare the comparison void.
    ///
    /// The daily range is the figure platforms disagree about. This lab means the mean of
    /// (high-low)/close; a platform computing sma(high,20)/sma(low,20)-1 is reporting a different
    /// quantity under the same name, and a comparison between them says nothing about either.
    /// </summary>
    public static IReadOnlyList<string> WithoutARangeDefinition(IReadOnlyList<Expectation> expectations)
    {
        ArgumentNullException.ThrowIfNull(expectations);

        return
        [
            .. expectations
                .Where(e => e.Tier == Confirmed && e.Id.EndsWith(".adr20", StringComparison.Ordinal))
                .Where(e => e.VoidedBecause is null && string.IsNullOrWhiteSpace(e.Note))
                .Select(e => e.Id)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Whether a producer names both a platform and the date it was read, which is what makes a
    /// confirmed figure re-checkable. Deliberately shallow: it asserts the shape rather than
    /// trying to know which platforms exist, because a check that keeps a list of vendors is a
    /// check that fails on the first one nobody thought of.
    /// </summary>
    private static bool NamesAPlatformAndADate(string producedBy) =>
        !string.IsNullOrWhiteSpace(producedBy)
        && producedBy.Contains("read from ", StringComparison.OrdinalIgnoreCase)
        && ReadOnDate().IsMatch(producedBy);

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReadOnDate();

    private static ExpectationFile ReadExpectations()
    {
        if (!File.Exists(ExpectationsFile))
        {
            throw new InvalidOperationException(
                $"No expectations at {RepositoryLayout.Relative(ExpectationsFile)}. The replay wrote what it produced "
                + "to artifacts/expectations.proposed.json; each line needs a tier and a checkpoint before it becomes "
                + "an expectation.");
        }

        return JsonSerializer.Deserialize<ExpectationFile>(File.ReadAllText(ExpectationsFile), Json)
            ?? throw new InvalidOperationException($"{RepositoryLayout.Relative(ExpectationsFile)} is not readable as expectations.");
    }

    public sealed record ExpectationFile(
        string AsOf,
        string InputTier,
        IReadOnlyList<Expectation> Expectations,
        IReadOnlyList<Permit>? FrozenOnly = null);

    /// <summary>
    /// A checkpoint permitted to be frozen-only, and the carried obligation that permits it.
    ///
    /// It lives beside the expectations rather than in a check, because it is a fact about this
    /// fixture's contents and it has to be edited in the same commit as the expectation that
    /// discharges it.
    /// </summary>
    public sealed record Permit(string Checkpoint, string Obligation, string Why);

    /// <summary>One checkpoint's expectations, counted, and how many of them verify anything.</summary>
    public sealed record CheckpointTier(string Checkpoint, int Total, int Independent);

    /// <summary>
    /// One expected figure, and how it was produced. The tier and the producer travel with the
    /// value, because a number with no provenance is indistinguishable from a number somebody
    /// pasted from the output when the test went red.
    /// </summary>
    public sealed record Expectation(
        string Id,
        string Tier,
        string Value,
        string Checkpoint,
        string ProducedBy,
        string? Note,
        string? VoidedBecause = null);

    public sealed record DiffRow(
        string Id,
        string Tier,
        string Checkpoint,
        string Expected,
        string? Actual,
        string Verdict,
        string ProducedBy);

    public sealed record TierBreakdown(string Tier, int Total, int Matched, int Differed, int Missing, int Void = 0);

    public sealed record FixtureDiff(
        string AsOf,
        string InputTier,
        int CapturedResponses,
        int ResponsesServed,
        IReadOnlyList<string> AskedOutsideTheFixture,
        IReadOnlyList<string> AskedOnAnUncoveredEndpoint,
        int ScreeningSessions,
        IReadOnlyList<StageRun> Stages,
        IReadOnlyList<TierBreakdown> ByTier,
        IReadOnlyList<string> Unexpected,
        IReadOnlyList<DiffRow> Rows);
}
