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

    /// <summary>
    /// What a distance gate says where the session has no stop or no daily range to divide it by.
    ///
    /// <b>A constant rather than a literal in each detector, from 4.11.</b> It is the text
    /// `surface-claims` names as what a card must carry for a check handed nothing, and the claim
    /// resolved against it by hand: the reconciliation is now the claim naming this member, so the
    /// two cannot drift apart in silence. Shared by both directions because both write the same
    /// sentence, and two literals saying one thing is how one of them stops saying it.
    /// </summary>
    public const string NoStopOrRange = "no stop or no daily range for the session";

    /// <summary>
    /// The clauses a multi-clause gate tested, each with its own verdict, or null on a gate that has
    /// only itself to answer for.
    ///
    /// <b>This is the 2.9 obligation, discharged at 4.1.</b> `tradable-shortable` tests liquidity,
    /// price, market capitalisation and listing age and recorded one number, so a failing verdict
    /// told a reader nothing about which of the four it failed on. The screen could already say
    /// which clause the number came from and could not say which clause the gate fell over, which is
    /// the question a person actually asks in front of a greyed row.
    ///
    /// <b>Null rather than an empty list on a single-clause gate</b>, so the stored JSON gains a
    /// field only on the gates that have something to say. An empty array on every check would be a
    /// shape change on rows where nothing changed, and it would read as "this gate has no clauses"
    /// where the truth is "this gate is its own clause".
    ///
    /// The value per clause is the half that makes it useful rather than decorative: a threshold
    /// experiment moves one clause's floor, and the distribution it needs is that clause's numbers
    /// over the rows that failed it, which a single recorded value could never supply.
    /// see: Failed checks are recorded rather than discarded
    /// </summary>
    public IReadOnlyList<ClauseResult>? Clauses { get; init; }

    /// <summary>
    /// The clauses this gate failed on, in the order it tests them. Empty on a pass, and empty on a
    /// gate that records no clauses, which are different states and are told apart by
    /// <see cref="Clauses"/> being null.
    /// </summary>
    public IReadOnlyList<ClauseResult> FailedClauses =>
        Clauses is null ? [] : [.. Clauses.Where(c => !c.Passed)];
}

/// <summary>
/// One clause of a multi-clause gate: what it tests, whether it held, and the number it turned on.
///
/// <b>Named rather than numbered</b>, on the same grounds every component is: "the second clause"
/// needs a lookup and half the time the lookup does not happen, where "market capitalisation" is the
/// thing itself. The names are what a screen shows and what a later threshold experiment selects on.
/// </summary>
public sealed record ClauseResult(string Name, bool Passed, decimal? Value = null)
{
    /// <summary>
    /// The capitalisation clause of `tradable-shortable`, named once.
    ///
    /// <b>A constant rather than a literal, from 4.11.</b> `surface-claims` asserts that a short
    /// verdict on the gallery says which clauses ran, and the text it looks for is this name. The
    /// claim resolves against this member now rather than against a copy of the words, which is what
    /// the 3.5 obligation asked for: a clause renamed here fails the claim rather than leaving a
    /// green check over a screen carrying different words.
    /// </summary>
    public const string MarketCapitalisation = "market capitalisation";
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
