using System.Globalization;
using System.Text.Json;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The authored boundary cases: two per gate, one just inside its threshold and one just outside.
///
/// An instrument of its own, answering a question the captured fixture cannot. Thirty real names on
/// one session record two setups, so a gate with two results is one-sided unless those two happen to
/// disagree, and eight of the ten long gates were exactly that when the detector first ran. Buying a
/// wider fixture buys a bigger instance of the same problem; two constructed cases per gate give
/// every gate a pass and a fail at no vendor call.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
///
/// <b>These are AUTHORED and are never evidence about the market.</b> They are the same tier as the
/// synthetic split at 1.5 and carry the same limitation: they encode this author's reading of each
/// gate. Nothing here is written into `setup`, so no stage downstream of the detectors can read a
/// constructed number as a night's observation, and the per-gate market counts the phase report
/// prints are the detectors' rows alone.
///
/// The verdicts come from the shipped rules over the constructed evidence. What is authored is the
/// input and the expected side, which is the part a real day did not supply.
/// </summary>
public static class GateCases
{
    /// <summary>
    /// The file, in `fixtures/` beside `expectations.json` and `checks-baseline.json` rather than
    /// inside `fixtures/captured/`, which holds verbatim vendor responses and nothing else.
    /// </summary>
    public const string FileName = "gate-cases.json";

    private static string Path_ => Path.Combine(RepositoryLayout.Root, "fixtures", FileName);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One constructed case: which gate, which side of it, and what makes it that side.</summary>
    public sealed record GateCase(
        string Direction,
        string Gate,
        string Side,
        string Expect,
        string Why,
        IReadOnlyDictionary<string, string> Set)
    {
        /// <summary>The measurement id this case reports under. Self-describing, and not a ticker.</summary>
        public string Id => $"gate.{Direction}.{Gate}.{Side}";

        public bool ExpectsPass => string.Equals(Expect, "pass", StringComparison.Ordinal);
    }

    private sealed record CaseFile(
        string Tier,
        IReadOnlyDictionary<string, Dictionary<string, string>> Baseline,
        IReadOnlyList<GateCase> Cases);

    private static CaseFile Read()
    {
        CaseFile? file = JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(Path_), Json);

        return file ?? throw new InvalidOperationException($"{FileName} did not parse into a case file.");
    }

    /// <summary>The tier the file declares about itself, which must be AUTHORED and is asserted.</summary>
    public static string Tier => Read().Tier;

    /// <summary>Every case, in file order.</summary>
    public static IReadOnlyList<GateCase> All => Read().Cases;

    /// <summary>The all-passing starting point for one direction, before a case perturbs one field.</summary>
    public static IReadOnlyDictionary<string, string> Baseline(string direction) => Read().Baseline[direction];

    /// <summary>
    /// The shipped rules' verdicts over one case: the direction's baseline with the case's fields
    /// replaced.
    /// </summary>
    public static IReadOnlyList<CheckResult> Evaluate(GateCase gateCase)
    {
        ArgumentNullException.ThrowIfNull(gateCase);
        return Evaluate(gateCase.Direction, With(Baseline(gateCase.Direction), gateCase.Set));
    }

    /// <summary>The same case under a rule other than the baseline, which is what a version is.</summary>
    public static IReadOnlyList<CheckResult> Evaluate(GateCase gateCase, SelectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(gateCase);
        ArgumentNullException.ThrowIfNull(rule);
        IReadOnlyDictionary<string, string> fields = With(Baseline(gateCase.Direction), gateCase.Set);
        return string.Equals(gateCase.Direction, "long", StringComparison.Ordinal)
            ? LongPullbackRules.Evaluate(LongEvidence(fields), rule)
            : ShortPullbackRules.Evaluate(ShortEvidence(fields), rule);
    }

    /// <summary>
    /// The verdicts over a direction's baseline with the named fields removed entirely.
    ///
    /// This is what makes the degeneracy proof mechanical rather than a list somebody maintains: the
    /// fields a gate's two boundary cases move are exactly the quantities that gate turns on, so
    /// removing them is "the gate was handed nothing" without anyone having to write down which
    /// field belongs to which gate a second time.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public static IReadOnlyList<CheckResult> EvaluateWithout(string direction, IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var evidence = new Dictionary<string, string>(Baseline(direction), StringComparer.Ordinal);
        foreach (string field in fields)
        {
            evidence.Remove(field);
        }

        return Evaluate(direction, evidence);
    }

    /// <summary>The verdicts over an evidence with every field absent, which every gate must fail.</summary>
    public static IReadOnlyList<CheckResult> EvaluateEmpty(string direction) =>
        Evaluate(direction, new Dictionary<string, string>(StringComparer.Ordinal));

    private static Dictionary<string, string> With(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(baseline, StringComparer.Ordinal);
        foreach ((string key, string value) in overrides)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static IReadOnlyList<CheckResult> Evaluate(string direction, IReadOnlyDictionary<string, string> fields) =>
        string.Equals(direction, "long", StringComparison.Ordinal)
            ? LongPullbackRules.Evaluate(LongEvidence(fields))
            : ShortPullbackRules.Evaluate(ShortEvidence(fields));

    private static LongPullbackRules.LongEvidence LongEvidence(IReadOnlyDictionary<string, string> f) =>
        new()
        {
            Close = Money(f, "close"),
            MedianDollarVolume = Money(f, "medianDollarVolume"),
            AverageDailyRange = Money(f, "averageDailyRange"),
            LadderGrade = Text(f, "ladderGrade"),
            SessionsSinceThrust = Count(f, "sessionsSinceThrust"),
            Pullback = Shape(f, "pullback"),
            ClosesBeyondFloor = Count(f, "closesBeyondFloor"),
            RangeTodayOverAverage = Money(f, "rangeTodayOverAverage"),
            TriggerDistanceRanges = Money(f, "triggerDistanceRanges"),
            StopDistanceRanges = Money(f, "stopDistanceRanges"),
            ClusterCount = Count(f, "clusterCount"),
        };

    private static ShortPullbackRules.ShortEvidence ShortEvidence(IReadOnlyDictionary<string, string> f) =>
        new()
        {
            Close = Money(f, "close"),
            MedianDollarVolume = Money(f, "medianDollarVolume"),
            MarketCap = Money(f, "marketCap"),
            SessionsListed = Count(f, "sessionsListed"),
            AverageDailyRange = Money(f, "averageDailyRange"),
            LadderGrade = Text(f, "ladderGrade"),
            GapOverAverageGap = Money(f, "gapOverAverageGap"),
            SessionsSinceThrust = Count(f, "sessionsSinceThrust"),
            Bounce = Shape(f, "bounce"),
            ClosesBeyondFloor = Count(f, "closesBeyondFloor"),
            DistanceToNearestAverageRanges = Money(f, "distanceToNearestAverageRanges"),
            StopDistanceRanges = Money(f, "stopDistanceRanges"),
            ClusterCount = Count(f, "clusterCount"),
        };

    /// <summary>
    /// The two fields of the geometry the rules actually read, with the rest of the record zeroed.
    ///
    /// Zeroed rather than invented. Anything else on the record is a price or an index, none of
    /// which any gate consults, and giving them plausible-looking values would suggest the case says
    /// something about them.
    /// </summary>
    private static PullbackGeometry.Pullback? Shape(IReadOnlyDictionary<string, string> f, string prefix)
    {
        int? bars = Count(f, $"{prefix}.pullbackBars");
        if (bars is not int count)
        {
            return null;
        }

        return new PullbackGeometry.Pullback(
            ThrustIndex: 0,
            ExtremeIndex: 0,
            ThrustOrigin: 0m,
            ThrustExtreme: 0m,
            PullbackExtreme: 0m,
            PullbackBars: count,
            RetraceDepth: Money(f, $"{prefix}.retraceDepth"),
            Trigger: 0m,
            Stop: 0m);
    }

    private static decimal? Money(IReadOnlyDictionary<string, string> f, string key) =>
        f.TryGetValue(key, out string? value) ? decimal.Parse(value, CultureInfo.InvariantCulture) : null;

    private static int? Count(IReadOnlyDictionary<string, string> f, string key) =>
        f.TryGetValue(key, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : null;

    private static string? Text(IReadOnlyDictionary<string, string> f, string key) =>
        f.TryGetValue(key, out string? value) ? value : null;
}
