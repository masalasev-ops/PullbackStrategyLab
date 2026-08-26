namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// One check's verdict on one setup, and the number it turned on.
///
/// The value is kept beside the verdict deliberately. A pass or a fail says whether a threshold was
/// cleared; the value says by how much, which is what a later proposal moves the threshold against.
/// Recording only the verdict would make every threshold experiment start by recomputing what the
/// night already knew, from bars that may since have been restated.
/// see: Failed checks are recorded rather than discarded
/// </summary>
public sealed record CheckResult(string Name, bool Passed, decimal? Value, string? Note = null)
{
    /// <summary>A check that could not be evaluated at all. Not a pass, and not silently absent.</summary>
    public static CheckResult Unknown(string name, string why) => new(name, false, null, why);
}

/// <summary>
/// The two directions, as the store constrains them and as every reader compares them.
///
/// In Core rather than on the detectors, because the read surface separates a night's setups by
/// direction and may not reference the Worker: a constant that lived on the detector would be copied
/// into a string literal on the other side of that boundary, and a literal is what stops matching
/// silently. The detectors declare their own direction in terms of these.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class SetupDirection
{
    public const string Long = "long";

    public const string Short = "short";

    /// <summary>Both, in the order every screen and every report lists them.</summary>
    public static IReadOnlyList<string> Both { get; } = [Long, Short];
}

/// <summary>
/// The check names, exactly as ARCHITECTURE.html's two gate lists carry them.
///
/// Declared here rather than read from the document at runtime, because the detector is production
/// code and the document is not something it should parse. The two are reconciled by
/// `check-completeness`, which reads the document's gate ids and asserts them against these lists
/// in both directions: a gate the detector does not run, and a check no gate names, are both
/// failures. That is what makes the document the single statement of what the strategy is.
/// </summary>
public static class SetupChecks
{
    /// <summary>The ten long checks, in the order the document lists them.</summary>
    public static IReadOnlyList<string> Long { get; } =
    [
        "tradable",
        "moves-enough",
        "uptrend",
        "thrust",
        "dip-shape",
        "held-floor",
        "contraction",
        "trigger-near",
        "exit-tight",
        "cluster",
    ];

    /// <summary>The ten short checks. Not a mirror: three of them are their own rule.</summary>
    public static IReadOnlyList<string> Short { get; } =
    [
        "tradable-shortable",
        "moves-enough",
        "downtrend",
        "averages-squeezing",
        "thrust",
        "bounce-shape",
        "reached-ceiling",
        "no-reclaim",
        "exit-tight",
        "cluster",
    ];

    /// <summary>
    /// The checks that are recorded and never required.
    ///
    /// One today, on both sides. Grouped movement suggests an industry shift rather than one
    /// company's news, which is worth measuring and is not evidence enough to gate on, and the
    /// authored parameter says so: recorded, never gating in the baseline.
    /// </summary>
    public static IReadOnlySet<string> RecordedNotRequired { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "cluster" };

    /// <summary>Whether every gating check passed, which is what `passed_all` means.</summary>
    public static bool PassedAll(IEnumerable<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.All(r => r.Passed || RecordedNotRequired.Contains(r.Name));
    }
}
