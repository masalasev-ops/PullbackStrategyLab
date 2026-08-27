using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Core.Measurement;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Outcome populations the captured fixture cannot supply, for the bound to be computed over.
///
/// <b>Why authored rather than captured.</b> The fixture holds one night and its as-of is the last
/// session, so no horizon has closed and the weekly bound over it is computed from nothing. Unlike
/// the geometry and the forward outcome, what this arithmetic needs is not bars: it is a set of
/// terminal returns and adverse excursions with a give-up beside each. Those are authored the way
/// the cap scenarios are, because the quantity under test is a rule over numbers.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
///
/// <b>The five scenarios are chosen to separate readings that a single win rate cannot.</b> An
/// achieved rate well under the bound and an achieved rate almost at it lead to opposite
/// conclusions, and telling them apart is the entire purpose of computing a bound rather than
/// assuming one.
/// </summary>
public static class CeilingCases
{
    public const string FileName = "ceiling-cases.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One subject's four figures, as the file states them.</summary>
    public sealed record AuthoredSubject(
        string Return, string MaeAtr, string Atr, string DailyRange, string StopRanges);

    /// <summary>One population, with why it is worth having.</summary>
    public sealed record Scenario(string Name, string Why, IReadOnlyList<AuthoredSubject> Subjects);

    private sealed record CaseFile(string Tier, IReadOnlyList<Scenario> Scenarios);

    private static CaseFile Read() =>
        JsonSerializer.Deserialize<CaseFile>(
            File.ReadAllText(System.IO.Path.Combine(RepositoryLayout.Root, "fixtures", FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");

    public static string Tier => Read().Tier;

    public static IReadOnlyList<Scenario> All => Read().Scenarios;

    /// <summary>
    /// One scenario as subjects the shipped bound can be handed.
    ///
    /// The direction is `long` throughout and it does not matter: the bound reads the signed return,
    /// which is already the direction's, so a short that fell arrives here as a positive number
    /// exactly as a long that rose does. That is the point of signing at the fill.
    /// </summary>
    public static IReadOnlyList<WinRateCeiling.Subject> Subjects(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        return
        [
            .. scenario.Subjects.Select((s, i) => new WinRateCeiling.Subject(
                $"{scenario.Name}-{i.ToString(CultureInfo.InvariantCulture)}",
                "long",
                Number(s.Return),
                Number(s.MaeAtr),
                Number(s.Atr),
                Number(s.DailyRange),
                Number(s.StopRanges))),
        ];
    }

    private static decimal Number(string text) =>
        decimal.Parse(text, CultureInfo.InvariantCulture);
}
