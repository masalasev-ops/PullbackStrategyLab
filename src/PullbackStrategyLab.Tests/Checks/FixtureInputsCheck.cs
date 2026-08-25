using System.Text.Json;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every vendor endpoint the lab reads has a captured input, and every captured input is a real
/// response with its endpoint, query and instant recorded beside it.
///
/// The tier is the point. An authored fixture encodes its author's beliefs about the vendor,
/// and that is exactly what a fixture cannot check, because the person writing the assumption
/// and the person writing the test are the same. Two defects in phase 1 passed their unit tests
/// and failed on live data for that one reason.
/// see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one
///
/// It reports what it examined rather than only what passed, and it reports the endpoints with
/// no captured input as unexamined rather than passing over them, because that count is the
/// whole measurement.
/// </summary>
public sealed class FixtureInputsCheck
{
    private readonly ITestOutputHelper _output;

    public FixtureInputsCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The endpoints a live run exercises, named here and matched against the manifest. Named
    /// rather than scraped from the vendor client, because a scraper that stopped matching would
    /// leave the check quietly narrower and reporting full coverage of nothing.
    /// </summary>
    public static IReadOnlyList<string> EndpointsALiveRunExercises { get; } =
    [
        "exchange-symbol-list",
        "eod-bulk-last-day",
        "eod",
    ];

    [Fact]
    [Trait("check", "fixture-inputs")]
    public void Every_endpoint_a_live_run_exercises_has_a_captured_input()
    {
        var coverage = new CheckCoverage("fixture-inputs", _output);
        string manifestFile = Path.Combine(RepositoryLayout.Fixtures, "manifest.json");

        if (!File.Exists(manifestFile))
        {
            coverage.NotExamined("vendor endpoints with a captured input", EndpointsALiveRunExercises.Count,
                "no fixture has been captured, so every path rests on authored evidence alone");
            coverage.Report();

            Assert.Fail(
                $"No captured fixture at {RepositoryLayout.Relative(manifestFile)}. Capture one with the "
                + $"{FixtureCapture.Name} stage; a path with no captured input is unexamined however many "
                + "authored cases pass.");
            return;
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestFile));
        JsonElement root = manifest.RootElement;

        Assert.Equal("CAPTURED", root.GetProperty("tier").GetString());

        JsonElement[] responses = [.. root.GetProperty("responses").EnumerateArray()];
        var failures = new List<string>();
        var endpointsSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonElement response in responses)
        {
            string file = response.GetProperty("file").GetString() ?? string.Empty;
            string endpoint = response.GetProperty("endpoint").GetString() ?? string.Empty;
            string query = response.GetProperty("query").GetString() ?? string.Empty;
            string capturedAt = response.GetProperty("capturedAt").GetString() ?? string.Empty;
            int length = response.GetProperty("bytes").GetInt32();

            endpointsSeen.Add(endpoint.Split('/')[0]);

            string path = Path.Combine(RepositoryLayout.Fixtures, file);
            if (!File.Exists(path))
            {
                failures.Add($"{file} is in the manifest and not on disk.");
                continue;
            }

            string body = File.ReadAllText(path);
            if (body.Length != length)
            {
                failures.Add($"{file} is {body.Length} characters and the manifest says {length}.");
            }

            if (capturedAt.Length == 0)
            {
                failures.Add($"{file} records no capture instant, so it cannot say when the vendor said it.");
            }

            // The one field that must never reach a file this repository holds.
            if (query.Contains("api_token", StringComparison.OrdinalIgnoreCase)
                || body.Contains("api_token", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{file} carries an api_token. A captured response records the query, never the credential.");
            }
        }

        string[] uncovered = EndpointsALiveRunExercises.Where(e => !endpointsSeen.Contains(e)).ToArray();

        coverage
            .Examined("captured responses in the manifest", responses.Length)
            .Examined("vendor endpoints a live run exercises", EndpointsALiveRunExercises.Count)
            .Examined("of those with at least one captured input", EndpointsALiveRunExercises.Count - uncovered.Length);

        if (uncovered.Length > 0)
        {
            coverage.NotExamined("endpoints resting on authored evidence alone", uncovered.Length,
                "no captured response covers them: " + string.Join(", ", uncovered));
        }

        coverage.Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} problem(s) with the captured fixture:\n  " + string.Join("\n  ", failures));

        Assert.True(uncovered.Length == 0,
            "These endpoints have no captured input, so anything verified through them rests on a fixture "
            + "written by the same hand as the code: " + string.Join(", ", uncovered));

        // Every name the fixture is built from, and the three trackers beside them.
        int expectedHistories = FixtureTickers.All.Count + new PullbackStrategyLabOptions().IndexSymbols.Count;
        int histories = responses.Count(r => (r.GetProperty("file").GetString() ?? string.Empty)
            .StartsWith("history-", StringComparison.Ordinal));

        Assert.True(histories == expectedHistories,
            $"The fixture holds {histories} captured histories and names {expectedHistories} symbols. A fixture "
            + "whose membership drifts from the list that defines it is a fixture nobody can reason about.");

        Assert.True(FixtureTickers.All.Count == 30,
            $"The fixture names {FixtureTickers.All.Count} tickers and BUILD_PLAN says 30.");
    }
}
