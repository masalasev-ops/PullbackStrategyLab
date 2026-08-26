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

        foreach (TierBreakdown tier in byTier)
        {
            coverage.Examined($"{tier.Tier} expectations diffed", tier.Total);
        }

        coverage.Examined("captured responses the replay read", result.ResponsesServed);

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
                "the endpoint has captured evidence and was asked about a name or a market day the fixture does not hold");
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
            $"the fixture holds {result.ScreeningSessions} captured market day(s) and the floor is a median over "
            + $"{new UniverseOptions().LiquidityWindowSessions}. Ends when the capture holds twenty bulk days, which costs "
            + "1,900 calls and about 130 MB. The per-ticker half of the same floor is measured, not deferred: see the "
            + "liquidity.* expectations");

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
            "all 30 measured names clear both floors, the closest at 1.7 times the liquidity floor, and the three "
            + "trackers are excluded by security type rather than by a floor. Ends when the capture holds one name "
            + "that fails a floor, which is one per-ticker call at the next capture");

        if (result.AskedOnAnUncoveredEndpoint.Count > 0)
        {
            coverage.NotExamined("requests on an endpoint with no captured response", result.AskedOnAnUncoveredEndpoint.Count,
                "the replay exercised the path and the fixture answered it with nothing: "
                + string.Join(", ", result.AskedOnAnUncoveredEndpoint.Take(6)));
        }

        coverage.Report();

        DiffRow[] broken = rows.Where(r => r.Verdict is not ("matched" or "void")).ToArray();

        Assert.True(broken.Length == 0,
            $"{broken.Length} expectation(s) did not hold over the fixture:\n  "
            + string.Join("\n  ", broken.Take(20).Select(r =>
                $"{r.Id} [{r.Tier}, {r.Checkpoint}] expected {r.Expected}, got {r.Actual ?? "nothing"}"))
            + (broken.Length > 20 ? $"\n  ... and {broken.Length - 20} more" : string.Empty));

        // Done condition seven, asserted rather than remembered. A fixture of nothing but frozen
        // values detects change and verifies nothing, and a checkpoint that added only those has
        // added regression detection and called it verification.
        int independent = rows.Count(r => r.Tier is Derived or Confirmed);
        Assert.True(independent > 0,
            "Every expectation in the fixture is FROZEN. At least one has to be DERIVED or CONFIRMED, or the fixture "
            + "only says the code still agrees with itself.");

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

    public sealed record ExpectationFile(string AsOf, string InputTier, IReadOnlyList<Expectation> Expectations);

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
