using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The research ledger as the page renders it: the register of rule versions, each version's
/// difference series per side, and the holdout budget.
///
/// <b>Designed from ARCHITECTURE's description rather than from a drawing.</b> `SCREENS.html` drew
/// five screens and was retired at 4.12 once four of them existed; this is the fifth, and its band
/// of figures, its table of versions, its holdout register and the twin-pair panel beside it were
/// drawn nowhere else. What the mockup held was a picture rather than a specification: the one
/// sentence in it about how the lab works, being what a twin pair is put to the model as, is in
/// ARCHITECTURE under the heading The question and was there first. So the layout here comes from
/// the two sections that describe what this page is for, Two experiment families and Replay and
/// holdout windows, and from the build order's own sentence about what phase 5 makes visible.
/// see: The corpus is eight documents and a ninth requires retiring one
///
/// <b>There is no property here over both sides.</b> A version's long score and its short score are
/// two figures and the type has no field that could hold one number over the pair, so the rule is
/// held by the shape rather than by a reader remembering it.
/// see: Long and short are never pooled into one figure
///
/// <b>And no property here averages a series.</b> The nightly difference is VariantScorer's and the
/// settlement against a target is AcceptanceGate's at <b>6.7</b>. A ledger that showed a running
/// mean would be answering the question the gate exists to answer, months before the gate is built
/// and with no pre-registration in front of it.
/// </summary>
public sealed record ResearchView(
    string AsOf,
    string? Absent,
    int? Generation,
    IReadOnlyList<VersionView> Versions,
    HoldoutView Holdout,
    ScoreRunView? LastScoreRun,
    string TwinPairsArriveAt)
{
    public static ResearchView Empty(string asOf, string why) =>
        new(asOf, why, null, [], HoldoutView.Unknown(why), null, "6.3");

    public bool HasVersions => Versions.Count > 0;

    /// <summary>The versions of the generation in force, which is what a night fans a plan out to.</summary>
    public IReadOnlyList<VersionView> Live => [.. Versions.Where(v => v.Live)];

    /// <summary>
    /// Versions of an earlier generation, still readable and no longer fanned out to.
    ///
    /// Shown apart rather than mixed in, because a version closed as unresolved by a baseline edit
    /// was never measured and a version rejected was. Both are history and only one of them is a
    /// finding.
    /// </summary>
    public IReadOnlyList<VersionView> EarlierGenerations => [.. Versions.Where(v => !v.Live)];
}

/// <summary>
/// One registered version, said in words.
///
/// <see cref="Sides"/> is a list rather than a long field and a short field, so a version that
/// touched both would render two blocks and nothing would be in a position to add them.
/// </summary>
public sealed record VersionView(
    string VariantId,
    int Generation,
    string Family,
    string Definition,
    string Target,
    int MinimumSample,
    string MinimumSampleUnit,
    string Status,
    string? ResolvedAt,
    string CreatedAt,
    bool IsBaseline,
    bool Live,
    string? Direction,
    string? Gate,
    string? ThresholdName,
    string? ThresholdFrom,
    string? ThresholdTo,
    string? Moved,
    IReadOnlyList<SideView> Sides)
{
    /// <summary>The pre-registration, unit included, because 1802 effective observations and 200 rows are not comparable.</summary>
    public string Sample =>
        $"{MinimumSample.ToString("N0", CultureInfo.InvariantCulture)} {Unit}";

    /// <summary>The unit as a person reads it rather than as the column stores it.</summary>
    public string Unit => MinimumSampleUnit switch
    {
        "effective_paired_setup_observations" => "effective paired setup observations",
        "paired_trades" => "paired trades",
        _ => MinimumSampleUnit,
    };

    /// <summary>
    /// Why this version has no accumulated figure against its minimum, or null where one exists.
    ///
    /// <b>The minimum is in effective observations and nothing converts a version's nights into
    /// them.</b> The design effect is computed for the scoreboard's own panels and not per version,
    /// so a ledger printing nights scored beside a minimum in effective observations would be
    /// putting two numbers in different units side by side under one heading. Said rather than
    /// silently omitted, and it is AcceptanceGate at <b>6.7</b> that closes it.
    /// </summary>
    public string? NoAccumulatedSample =>
        string.Equals(MinimumSampleUnit, "effective_paired_setup_observations", StringComparison.Ordinal)
            ? "the minimum is in effective observations and nothing converts this version's scored "
              + "nights into them yet, so the two are not shown as a fraction. AcceptanceGate settles "
              + "this version at 6.7 and that is the checkpoint that converts them"
            : null;

    /// <summary>Whether the version is still open, which is what makes its minimum a thing being waited on.</summary>
    public bool Open => string.Equals(Status, "open", StringComparison.Ordinal);

    /// <summary>How the version's status reads, with the one word that is not self-explanatory spelled out.</summary>
    public string StatusReads => Status switch
    {
        "open" => "open, accumulating",
        "accepted" => "accepted",
        "rejected" => "rejected, measured and short of its target",
        "unresolved" => "unresolved, closed without ever being measured because the baseline it was "
            + "compared against was edited",
        _ => Status,
    };
}

/// <summary>
/// One side of one version: what it was differenced over, and the series itself.
///
/// There is deliberately no mean here. See the type comment above.
/// </summary>
public sealed record SideView(
    string Direction,
    int NightsScored,
    int NightsCarryingADifference,
    int Unscoreable,
    int BaselineOutsideCap,
    int VariantOutsideCap,
    IReadOnlyList<ScoredNightView> Nights)
{
    /// <summary>
    /// The count, said so the two numbers cannot be confused.
    ///
    /// A night inside its scoring horizon is counted and carries no difference, so the two figures
    /// answer different questions and a page showing one of them would say a version had been
    /// measured over nights it is still waiting on.
    /// </summary>
    public string Count =>
        $"{NightsScored.ToString("N0", CultureInfo.InvariantCulture)} night(s) scored, "
        + $"{NightsCarryingADifference.ToString("N0", CultureInfo.InvariantCulture)} carrying a difference";

    /// <summary>The population every figure on this block was computed over, shown beside them.</summary>
    public string Over =>
        $"over the {Direction} side's own nights, on the setups flagged on each";
}

/// <summary>One night of the difference series.</summary>
public sealed record ScoredNightView(
    string SessionDate,
    int HorizonDays,
    int Flagged,
    int BaselineSelected,
    int VariantSelected,
    int BothSelected,
    int BaselineOnly,
    int VariantOnly,
    string? BaselineMeanReturn,
    string? VariantMeanReturn,
    string? MeanDifference,
    int Unscoreable,
    string? WithheldBecause)
{
    /// <summary>Whether the night produced a difference at all, which is a state rather than a nought.</summary>
    public bool Withheld => MeanDifference is null;
}

/// <summary>
/// The holdout budget, said in words.
///
/// <b>An empty register reports why it is empty, and that is the whole of what this type adds.</b>
/// A register with no matured window and one whose runs never recorded a matured window both hold
/// nothing, and for the first months of a lab's life they hold the same noughts on every other
/// figure too. The reason is carried rather than inferred from a count.
/// </summary>
public sealed record HoldoutView(
    int Capacity,
    int Matured,
    int Recorded,
    int Spent,
    int Available,
    string? FirstSession,
    string? EmptyBecause,
    bool Exhausted,
    IReadOnlyList<string> Missing,
    IReadOnlyList<WindowView> Windows)
{
    /// <summary>What the ledger shows where the read surface answered with nothing at all.</summary>
    public static HoldoutView Unknown(string why) =>
        new(8, 0, 0, 0, 0, null, why, false, [], []);

    /// <summary>Whether the register is short of a window the calendar says should be in it, which is the one defect state.</summary>
    public bool ShortOfAWindow => Missing.Count > 0;

    /// <summary>
    /// The budget, stated as a fraction of what will ever exist.
    ///
    /// The denominator is eight and never the number matured, because the point of a finite budget
    /// is what it costs against the whole of it rather than against what happens to be available
    /// tonight.
    /// </summary>
    public string Count =>
        $"{Spent.ToString("N0", CultureInfo.InvariantCulture)} spent, "
        + $"{Available.ToString("N0", CultureInfo.InvariantCulture)} available, "
        + $"of {Capacity.ToString("N0", CultureInfo.InvariantCulture)} that will ever exist";

    /// <summary>What the register should hold by now against what it does.</summary>
    public string Against =>
        $"{Matured.ToString("N0", CultureInfo.InvariantCulture)} matured by this date, "
        + $"{Recorded.ToString("N0", CultureInfo.InvariantCulture)} recorded";
}

/// <summary>One holdout window and the spend on it, where it carries one.</summary>
public sealed record WindowView(
    string WindowId,
    int Ordinal,
    string Start,
    string End,
    string MaturesOn,
    string? SpentOn,
    string? Outcome,
    string? SpentAt)
{
    public bool Available => SpentOn is null;
}

/// <summary>
/// What the last scoring run settled.
///
/// <b>It is here so a ledger showing no difference can say which kind of nothing it is.</b> A night
/// that found no version to difference and a night on which the scorer never ran read identically
/// off the score rows alone, and that is the shape the whole corpus keeps finding one level up.
/// </summary>
public sealed record ScoreRunView(
    string SessionDate,
    int VersionsLive,
    int VersionsScored,
    int NightsScored,
    int NightsWaiting,
    int Longs,
    int Shorts,
    int Unscoreable,
    string Outcome,
    string? StoppedBecause);
