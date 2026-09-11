using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Core.Detection;

/// <summary>
/// Generation 1's two gate lists, clause by clause from <c>SOURCES.md</c>, from 7.5.
///
/// <b>Written from the decision on what generation 1 is, and nothing else.</b> Where the source gives a
/// form and a number the clause ships both; where it gives a form and no number, the form ships with
/// the number carried from generation 0 and marked as the author's; where it gives a qualifier the
/// clause is recorded and never required. `exit-tight` and `trigger-near` leave the selection list
/// altogether, because their sourced forms are tests at the minute of entry and move to 7.8. And a
/// clause the source never mentions on the short side is the long side's sourced form mirrored and
/// marked as the author's mirror, on his own word that he trades shorts by flipping the long side
/// around. The weekly screen is new on both sides.
/// see: Generation 1's baseline is written clause by clause from SOURCES.md, and a stated qualifier makes a gate recorded rather than screening
/// see: The give-up gate is retired at selection and reborn as the entry-time ceiling
/// see: Generation 1's selection keeps what the evening can decide, and the short side's unsourced clauses are the long side's mirrored
///
/// <b>Registered nowhere until 7.11.</b> The detector that runs these is proved against the golden
/// fixture and writes nothing live, because generation 1 registers once, when its rule is whole
/// across both families, and a baseline frozen before then would be frozen on an execution model
/// that does not yet exist.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
///
/// <b><c>SOURCES.md</c> traces these lists, and <c>clause-provenance</c> holds the two together</b>, in
/// both directions and in the closed verdict vocabulary the twenty generation 0 clauses carry.
/// </summary>
public static class GenerationOneChecks
{
    /// <summary>The generation these lists belong to, written on every vector they score.</summary>
    public const int Generation = 1;

    /// <summary>The nine long checks.</summary>
    public static IReadOnlyList<string> Long { get; } =
    [
        "tradable",
        "moves-enough",
        "uptrend",
        "thrust",
        "dip-shape",
        "held-floor",
        "contraction",
        "cluster",
        "weekly-trend",
    ];

    /// <summary>The ten short checks.</summary>
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
        "cluster",
        "weekly-trend",
    ];

    /// <summary>
    /// The clauses recorded and never required. `moves-enough` because his own words set the figure
    /// aside in the same breath; `held-floor` and `no-reclaim` because his reclaim happens at the
    /// minute of entry and the evening can only describe the session before; `cluster` as in
    /// generation 0.
    /// </summary>
    public static IReadOnlySet<string> RecordedNotRequired { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "moves-enough", "held-floor", "no-reclaim", "cluster" };

    /// <summary>
    /// What decides a name is worth recording under generation 1: the cheap gating clauses. Not
    /// `moves-enough`, which no longer screens, so generation 1 records the slower names generation 0
    /// discarded unread.
    /// </summary>
    public static IReadOnlyList<string> RecordingFloorLong { get; } = ["tradable", "uptrend", "thrust"];

    public static IReadOnlyList<string> RecordingFloorShort { get; } = ["tradable-shortable", "downtrend", "thrust"];

    /// <summary>Whether every gating clause passed.</summary>
    public static bool PassedAll(IEnumerable<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.All(r => r.Passed || RecordedNotRequired.Contains(r.Name));
    }

    /// <summary>Whether the side's floor clauses all passed.</summary>
    public static bool ClearsRecordingFloor(IReadOnlyList<CheckResult> results, bool isLong)
    {
        ArgumentNullException.ThrowIfNull(results);
        IReadOnlyList<string> floor = isLong ? RecordingFloorLong : RecordingFloorShort;
        return floor.All(name => results.Any(r => r.Name == name && r.Passed));
    }
}

/// <summary>
/// The figures generation 1 reads beyond generation 0's evidence, all for the setup's own session and
/// all on the adjusted basis. Null where the store could not supply one, and a clause handed a null
/// fails with the reason.
/// </summary>
public sealed record GenerationOneFigures(
    decimal? EmaShort,
    decimal? EmaMedium,
    decimal? EmaLong,
    decimal? High,
    decimal? Low,
    decimal? Close,
    decimal? WeeklyMediumDistance,
    decimal? WeeklySqueezeRatio,
    decimal? CeilingConfluenceRanges);

/// <summary>
/// Generation 1's clauses as arithmetic over one name's evidence and figures.
///
/// <b>A clause whose form is unchanged is generation 0's own check, called rather than rewritten</b>,
/// so the two generations cannot disagree about `tradable`, `thrust`, `contraction` or `cluster` for
/// any reason but a changed rule. Every clause returns a result and none short-circuits.
/// </summary>
public static class GenerationOneRules
{
    /// <summary>The most of the thrust a dip may give back, carried from generation 0 as the author's.</summary>
    public const decimal MaximumRetrace = LongPullbackRules.MaximumRetrace;

    /// <summary>The squeeze ratio below which the weekly averages are converging, the author's.</summary>
    public const decimal MaximumWeeklySqueezeRatio = 1m;

    /// <summary>How near two levels must both be, in daily ranges, carried from generation 0 as the author's.</summary>
    public const decimal ConfluenceReachRanges = ShortPullbackRules.CeilingReachRanges;

    public static IReadOnlyList<CheckResult> EvaluateLong(LongPullbackRules.LongEvidence evidence, GenerationOneFigures figures)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(figures);

        IReadOnlyList<CheckResult> zero = LongPullbackRules.Evaluate(evidence);

        return
        [
            Carried(zero, "tradable"),
            Carried(zero, "moves-enough"),
            Ladder("uptrend", figures, rising: true),
            Carried(zero, "thrust"),
            Depth("dip-shape", evidence.Pullback),
            Reclaim("held-floor", figures, isLong: true),
            Carried(zero, "contraction"),
            Carried(zero, "cluster"),
            WeeklyTrend(figures, isLong: true),
        ];
    }

    public static IReadOnlyList<CheckResult> EvaluateShort(ShortPullbackRules.ShortEvidence evidence, GenerationOneFigures figures)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(figures);

        IReadOnlyList<CheckResult> zero = ShortPullbackRules.Evaluate(evidence);

        return
        [
            Carried(zero, "tradable-shortable"),
            Carried(zero, "moves-enough"),
            Ladder("downtrend", figures, rising: false),
            WeeklySqueeze(figures),
            Carried(zero, "thrust"),
            Depth("bounce-shape", evidence.Bounce),
            Confluence(figures),
            Reclaim("no-reclaim", figures, isLong: false),
            Carried(zero, "cluster"),
            WeeklyTrend(figures, isLong: false),
        ];
    }

    private static CheckResult Carried(IReadOnlyList<CheckResult> zero, string name) =>
        zero.Single(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// His ladder, the three averages in order and nothing else: generation 0 also required the price
    /// above the 9-day, which his partition between the averages does not carry.
    /// </summary>
    private static CheckResult Ladder(string name, GenerationOneFigures f, bool rising) =>
        f.EmaShort is not decimal nine || f.EmaMedium is not decimal twentyOne || f.EmaLong is not decimal fifty
            ? CheckResult.Unknown(name, "no averages for the session")
            : new CheckResult(
                name,
                rising ? nine > twentyOne && twentyOne > fifty : nine < twentyOne && twentyOne < fifty,
                null,
                rising ? "9 over 21 over 50" : "9 under 21 under 50");

    /// <summary>
    /// A pullback that gave back no more than the author's share of the move. Generation 0's two to
    /// seven sessions is gone, because no source describes a drift of that length as the thing bought.
    /// </summary>
    private static CheckResult Depth(string name, PullbackGeometry.Pullback? pullback)
    {
        if (pullback is not PullbackGeometry.Pullback shape)
        {
            return CheckResult.Unknown(name, "no thrust to measure a pullback against");
        }

        bool shallowEnough = shape.RetraceDepth is decimal depth && depth >= 0m && depth <= MaximumRetrace;
        return new CheckResult(name, shape.PullbackBars >= 1 && shallowEnough, shape.RetraceDepth, $"{shape.PullbackBars} bar(s)");
    }

    /// <summary>The session undercut the 9-day average and closed back through it: recorded, never required.</summary>
    private static CheckResult Reclaim(string name, GenerationOneFigures f, bool isLong) =>
        f.High is not decimal high || f.Low is not decimal low || f.Close is not decimal close || f.EmaShort is not decimal nine
            ? CheckResult.Unknown(name, "no bar or no 9-day average for the session")
            : new CheckResult(name, SourcedForms.UndercutAndReclaimed(high, low, close, nine, isLong), null);

    /// <summary>The weekly screen: the week closing at or above its 21-week average on a long, below it on a short.</summary>
    private static CheckResult WeeklyTrend(GenerationOneFigures f, bool isLong) =>
        f.WeeklyMediumDistance is not decimal distance
            ? CheckResult.Unknown("weekly-trend", "fewer than twenty-one weeks of closes")
            : new CheckResult("weekly-trend", isLong ? distance >= 0m : distance < 0m, distance);

    private static CheckResult WeeklySqueeze(GenerationOneFigures f) =>
        f.WeeklySqueezeRatio is not decimal ratio
            ? CheckResult.Unknown("averages-squeezing", "fewer than forty weeks of closes, or no weekly gap to average")
            : new CheckResult("averages-squeezing", ratio < MaximumWeeklySqueezeRatio, ratio);

    private static CheckResult Confluence(GenerationOneFigures f) =>
        f.CeilingConfluenceRanges is not decimal distance
            ? CheckResult.Unknown("reached-ceiling", "fewer than two levels could be measured, so nothing can coincide")
            : new CheckResult("reached-ceiling", distance <= ConfluenceReachRanges, distance);
}
