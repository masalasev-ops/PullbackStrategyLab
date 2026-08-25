using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed class FixtureReplayCheck
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
            string verdict = !produced ? "missing" : value == expectation.Value ? "matched" : "differed";

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
                g.Count(r => r.Verdict == "missing")))
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

        if (result.AskedOnAnUncoveredEndpoint.Count > 0)
        {
            coverage.NotExamined("requests on an endpoint with no captured response", result.AskedOnAnUncoveredEndpoint.Count,
                "the replay exercised the path and the fixture answered it with nothing: "
                + string.Join(", ", result.AskedOnAnUncoveredEndpoint.Take(6)));
        }

        coverage.Report();

        DiffRow[] broken = rows.Where(r => r.Verdict != "matched").ToArray();

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

        Assert.True(unexpected.Length == 0,
            $"The replay produced {unexpected.Length} figure(s) no expectation names, so they are unexamined: "
            + string.Join(", ", unexpected.Take(20)));
    }

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
        string? Note);

    public sealed record DiffRow(
        string Id,
        string Tier,
        string Checkpoint,
        string Expected,
        string? Actual,
        string Verdict,
        string ProducedBy);

    public sealed record TierBreakdown(string Tier, int Total, int Matched, int Differed, int Missing);

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
