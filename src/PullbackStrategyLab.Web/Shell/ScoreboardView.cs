using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The scoreboard as the page renders it: three bands, two sides never added together.
///
/// <b>Every panel carries the condition under which it reads badly</b>, because a scoreboard that
/// can only show good news is decoration. Those conditions are written here rather than in the
/// template, so a reader of the code can see what each figure is supposed to warn about.
/// </summary>
public sealed record ScoreboardView(
    string AsOf,
    string? Absent,
    IReadOnlyList<PanelView> Health,
    IReadOnlyList<PanelView> Long,
    IReadOnlyList<PanelView> Short)
{
    public bool HasPanels => Health.Count > 0 || Long.Count > 0 || Short.Count > 0;

    public static ScoreboardView Empty(string asOf, string why) => new(asOf, why, [], [], []);
}

/// <summary>
/// One panel, said in words.
///
/// <b>The count is not optional and there is no branch that omits it.</b> A number without one is
/// not shown at all, because the failure this whole system exists to avoid is reading a pattern in
/// forty observations.
/// </summary>
public sealed record PanelView(
    string Name,
    string? Direction,
    string Figure,
    string? Low,
    string? High,
    int Rows,
    int? Effective,
    string Population)
{
    /// <summary>What the panel is, in words, rather than the identifier the store keys it on.</summary>
    public string Title => Name switch
    {
        "band0.nightsRecorded" => "Nights recorded",
        "band0.degradedRuns" => "Degraded runs",
        "band0.setupsOnFile" => "Setups on file",
        "band1.vsLoose" => "Against loose controls",
        "band1.vsTight" => "Against tight controls",
        "band2.ceilingGap" => "Ceiling gap",
        _ when Name.StartsWith("band2.decile", StringComparison.Ordinal) =>
            $"Decile {Name["band2.decile".Length..]}",
        _ => Name,
    };

    /// <summary>
    /// The condition under which this panel reads badly, which is shown beside it.
    ///
    /// Written per panel rather than as a legend, because a legend is read once and a caption is
    /// read every time.
    /// </summary>
    public string? ReadsBadlyWhen => Name switch
    {
        "band0.degradedRuns" =>
            "Reads red above 5% of the record, because excluded nights are not missing at random",
        "band1.vsLoose" =>
            "Measures the whole funnel, thrust scan included. Expected to be the larger of the two",
        "band1.vsTight" =>
            "The honest comparison, and the one that can embarrass the project. Reads green only when the lower bound clears zero",
        "band2.ceilingGap" =>
            "Near zero means the stop is the binding constraint and no selection change can help. Wide means selection has room",
        _ when Name.StartsWith("band2.decile", StringComparison.Ordinal) =>
            "A flat curve across the deciles means the rank is decorative and the cap is truncating at random",
        _ => null,
    };

    /// <summary>Whether the panel is withheld for want of a sample, which is a state rather than a value.</summary>
    public bool Withheld => string.Equals(Figure, "withheld", StringComparison.Ordinal);

    /// <summary>The interval, or null where the panel carries none.</summary>
    public string? Interval =>
        Low is null || High is null ? null : $"[{Low}, {High}]";

    /// <summary>
    /// Whether the lower bound clears zero, which is what band 1 reads green on.
    ///
    /// Null where there is no interval, and null is not false: "no interval yet" and "the interval
    /// does not clear zero" are different sentences and only one of them is a finding.
    /// </summary>
    public bool? ClearsZero =>
        Low is null ? null : decimal.Parse(Low, CultureInfo.InvariantCulture) > 0m;

    /// <summary>
    /// Which rows the figure was computed over, shown on every panel without exception.
    ///
    /// <b>Two panels on this page use different populations.</b> Band 1 is over every flagged setup;
    /// band 2's decile curve is over the capped candidates, because a decile needs a rank and only a
    /// candidate carries one. At the calibrated thresholds those differ by three orders of magnitude,
    /// so a reader comparing the two without knowing which rows each used is comparing numbers whose
    /// samples have nothing to do with each other.
    ///
    /// Shown rather than left to a legend, on the same grounds the count is: a legend is read once
    /// and a caption is read every time.
    /// see: The subject is the flagged setup population, not the trade log
    /// </summary>
    public string Over => $"over {Population}";

    /// <summary>
    /// The count, said so the two numbers cannot be confused.
    ///
    /// The effective count is shown beside the row count wherever it exists, because they are
    /// different quantities: ten-day labels overlap and same-night setups share a market factor, so
    /// the information in a thousand rows is worth fewer than a thousand observations.
    /// </summary>
    public string Count => Effective is int effective
        ? $"n {Rows.ToString("N0", CultureInfo.InvariantCulture)} rows, {effective.ToString("N0", CultureInfo.InvariantCulture)} effective"
        : $"n {Rows.ToString("N0", CultureInfo.InvariantCulture)}";
}
