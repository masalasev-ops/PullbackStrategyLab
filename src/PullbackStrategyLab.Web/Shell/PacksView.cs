using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The pack comparison surface as the page renders it: every evidence-pack version side by side.
///
/// <b>The sixth screen, and the navigation said five while the component catalogue held six.</b>
/// Every other screen is keyed on one night, one setup or one trade; the subject of this one is the
/// set of pack versions, so hanging it off any of the five would key a comparison across versions to
/// a single night (see: The pack comparison surface is the sixth screen and the navigation says six).
///
/// <b>Nothing here computes a rate.</b> Proposal hit rate by pack version is band 3's, computed by
/// the builder and read from the store. This page shows the counts the rate is over, so a reader can
/// see which version a rate came from.
/// see: The averages are one implementation, computed nightly and drawn on demand
/// </summary>
public sealed record PacksView(
    string AsOf,
    string? Absent,
    IReadOnlyList<PackVersionView> Versions,
    PackCutView? LastCut)
{
    public static PacksView Empty(string asOf, string why) => new(asOf, why, [], null);

    public bool HasVersions => Versions.Count > 0;

    /// <summary>
    /// The tuple components on which the versions differ, which is what a reader of this page came
    /// for.
    ///
    /// <b>Derived rather than declared, and empty is a statement.</b> One version differs from
    /// nothing, and two versions agreeing on every component cannot both exist, because the
    /// fingerprint is the identity: a cut computing the same tuple finds the row it already has.
    /// So a non-empty list on two or more versions is a fact about what changed between them.
    /// </summary>
    public IReadOnlyList<string> DifferOn =>
        Versions.Count < 2
            ? []
            : [.. new[]
            {
                Component("the sections shown", v => v.Sections),
                Component("the screened set", v => v.SignalsScreened),
                Component("the correction form", v => v.CorrectionForm),
                Component("the correction level", v => v.Level),
                Component("the family-wise threshold", v => v.Threshold ?? "none"),
                Component("the model", v => v.ModelIdentifier),
            }.Where(c => c is not null).Select(c => c!)];

    private string? Component(string name, Func<PackVersionView, string> of) =>
        Versions.Select(of).Distinct(StringComparer.Ordinal).Count() > 1 ? name : null;
}

/// <summary>
/// One pack version, said in words.
///
/// The tuple's components stay apart rather than being rendered as one string, because what a reader
/// is doing on this page is finding the one component two versions differ in.
/// </summary>
public sealed record PackVersionView(
    int Version,
    string Fingerprint,
    string Sections,
    string SignalsScreened,
    int SignalsScreenedCount,
    string CorrectionForm,
    double CorrectionLevel,
    double? FamilyWiseThreshold,
    string ModelIdentifier,
    int Proposals,
    int RuleChanges,
    int SignalRequests,
    int Abstentions,
    int NoAnswer,
    IReadOnlyList<string> Statuses)
{
    /// <summary>The version as a reader names it.</summary>
    public string Label => "v" + Version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The fingerprint's first twelve characters, which is what a person compares by eye.</summary>
    public string ShortFingerprint =>
        Fingerprint.Length <= 12 ? Fingerprint : Fingerprint[..12];

    public string Level => CorrectionLevel.ToString("0.####", CultureInfo.InvariantCulture);

    public string? Threshold =>
        FamilyWiseThreshold?.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>How the correction reads, level and screened count together, because neither says it alone.</summary>
    public string Correction =>
        $"{CorrectionForm} at {Level} over {SignalsScreenedCount.ToString(CultureInfo.InvariantCulture)} screened"
        + (Threshold is null ? string.Empty : $", giving {Threshold}");

    /// <summary>
    /// What was proposed against it, counted by kind and never summed.
    ///
    /// A version that abstained every week and one that was never asked have the same proposal
    /// count, and one total would make them read alike.
    /// </summary>
    public string Outcomes =>
        $"{RuleChanges} rule change(s), {SignalRequests} signal request(s), "
        + $"{Abstentions} abstention(s), {NoAnswer} week(s) with no answer";

    /// <summary>Where they stand in the funnel, as the set rather than as a count.</summary>
    public string Where => Statuses.Count == 0 ? "nothing filed" : string.Join(", ", Statuses);
}

/// <summary>The last cut of a pack, which is how a version nobody has been shown is told from one with no cut behind it.</summary>
public sealed record PackCutView(
    string AsOf,
    int? Version,
    int SectionsRendered,
    int SectionsEmpty,
    int SignalsScreened,
    bool NullControlPlanted,
    string Outcome,
    string? RefusedBecause)
{
    /// <summary>
    /// How full the cut was, both counts together.
    ///
    /// <b>Empty against rendered rather than either alone.</b> Five of the ten sections rest on
    /// outcomes that have not closed, so this is expected to read five for months, and a count of
    /// sections rendered on its own would make an almost-empty pack read as a pack.
    /// </summary>
    public string Fullness =>
        $"{SectionsRendered.ToString(CultureInfo.InvariantCulture)} section(s) rendered, "
        + $"{SectionsEmpty.ToString(CultureInfo.InvariantCulture)} of them empty";
}
