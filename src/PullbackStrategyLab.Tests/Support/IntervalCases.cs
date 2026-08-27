using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Core.Measurement;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Nightly difference series the captured fixture cannot supply.
///
/// <b>Why it is owed.</b> Over the fixture every band 1 panel is withheld, because one night with no
/// closed horizon cannot produce a series at all. So the block bootstrap and the effective-sample
/// measurement, which are the decision this phase turns on, would be exercised by nothing.
///
/// <b>And the failure that matters here is silent.</b> An interval that is too narrow does not
/// produce a wrong number, it produces a confident one: band 1 clears zero before it should and says
/// the pattern is real. A stage that never computes an interval can be neither too narrow nor too
/// wide, so "green" over the fixture says nothing at all about the property.
/// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
/// </summary>
public static class IntervalCases
{
    public const string FileName = "interval-cases.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// One series, with the recipe it was built from and why it is worth having.
    ///
    /// <c>PairsPerNight</c> and <c>WithinNightDispersion</c> are what let a night count as more than
    /// one observation. A file holding only the means could exercise nothing but the corner where a
    /// night cannot say how its own pairs dispersed and therefore counts as one.
    /// </summary>
    public sealed record Scenario(
        string Name,
        string Why,
        string Recipe,
        int PairsPerNight,
        decimal WithinNightDispersion,
        IReadOnlyList<decimal> NightlyMeans);

    private sealed record CaseFile(string Tier, IReadOnlyList<Scenario> Scenarios);

    private static CaseFile Read() =>
        JsonSerializer.Deserialize<CaseFile>(
            File.ReadAllText(System.IO.Path.Combine(RepositoryLayout.Root, "fixtures", FileName)), Json)
        ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");

    public static string Tier => Read().Tier;

    public static IReadOnlyList<Scenario> All => Read().Scenarios;

    /// <summary>
    /// One scenario as nights the shipped interval can be handed.
    ///
    /// The dates run forward from a fixed session so the ordering is real rather than incidental:
    /// the block bootstrap resamples along the session axis, and a series whose order did not mean
    /// anything would make the blocks arbitrary.
    /// </summary>
    public static IReadOnlyList<PairedInterval.Night> Nights(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var start = new DateOnly(2026, 1, 5);

        return
        [
            .. scenario.NightlyMeans.Select((mean, i) => new PairedInterval.Night(
                start.AddDays(i), mean, scenario.PairsPerNight, scenario.WithinNightDispersion)),
        ];
    }

    public static string Figure(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);
}
