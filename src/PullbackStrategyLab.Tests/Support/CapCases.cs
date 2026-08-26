using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The authored cap scenarios: candidate lists a real night did not produce.
///
/// The captured day records two setups and neither clears every gating check, so the cap over the
/// fixture caps nothing. A release rule that has only ever run on an empty list is a rule nothing has
/// tested, and the arrangements that matter, both release directions and both sides overflowing, are
/// exactly the ones a thirty-name fixture cannot reach.
/// see: A released cap slot goes to the side that still has candidates
///
/// <b>AUTHORED, and about the rule rather than about the market.</b> They say nothing about how many
/// candidates a night has; that is what the calibration run measures.
/// </summary>
public static class CapCases
{
    public const string FileName = "cap-cases.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One arrangement of the two counts, and why it is worth having.</summary>
    public sealed record Scenario(string Name, int Long, int Short, string Why);

    /// <summary>One candidate in the ordering case, as the file writes it.</summary>
    public sealed record OrderingCandidate(string SetupId, string Ticker, string Direction, string StopDistanceRanges)
    {
        public NightlyCap.Candidate ToCandidate() => new(
            SetupId, Ticker, Direction, decimal.Parse(StopDistanceRanges, CultureInfo.InvariantCulture));
    }

    private sealed record Ordering(string Why, IReadOnlyList<OrderingCandidate> Candidates);

    private sealed record CaseFile(
        string Tier,
        IReadOnlyDictionary<string, int> Allocation,
        IReadOnlyList<Scenario> Scenarios,
        Ordering Ordering);

    private static CaseFile Read() =>
        JsonSerializer.Deserialize<CaseFile>(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");

    public static string Tier => Read().Tier;

    public static IReadOnlyDictionary<string, int> Allocation => Read().Allocation;

    public static IReadOnlyList<Scenario> Scenarios => Read().Scenarios;

    /// <summary>The ordering case, as candidates the shipped cap can be handed.</summary>
    public static IReadOnlyList<NightlyCap.Candidate> OrderingCandidates =>
        [.. Read().Ordering.Candidates.Select(c => c.ToCandidate())];
}
