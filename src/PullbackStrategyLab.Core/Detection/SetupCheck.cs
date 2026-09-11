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
    /// What a distance gate says where the pullback it measures has no range at all, so the distance
    /// it would report is nought.
    ///
    /// <b>A nought is not evidence, which is the whole of the ruling this carries.</b> A gate handed
    /// nothing reads as empty and fails. A gate reading nought does not: it reads as the tightest
    /// possible pass, and `exit-tight` at nought claims the give-up sits nought daily ranges from the
    /// entry, which is the most favourable answer the gate can give and is produced by bars with no
    /// range in them. The two are opposite readings of the same absence, and only one of them was
    /// being recorded.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public const string NoRangeInThePullback =
        "the pullback has no range at all, so the distance is nought rather than tight";

    /// <summary>
    /// The same for a ratio taken against the session's own bar, where that bar has no range.
    ///
    /// Its own sentence rather than the one above, because the quantity and the reader's question
    /// are different: a contraction of nought says the session was quiet to a degree no session can
    /// be, and the bar it was measured from is the setup's own rather than the pullback's.
    /// </summary>
    public const string NoRangeInTheSession =
        "the session's own bar has no range at all, so the ratio is nought rather than contracted";

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
///
/// <b>Where each of these twenty came from is `docs/SOURCES.md`, clause by clause.</b> Fifteen of
/// the twenty forms rest on the trader's own words and are quoted there; five rest on nothing any
/// source states. Four of the twenty thresholds rest on his own words and fourteen are somebody's
/// choice, which is a fact about this list that reading it cannot show and that nothing in the
/// corpus recorded until 2026-09-09. A clause is not better for being sourced and thirteen of the
/// twenty carry a named disagreement with the source they cite, so the document is where a later
/// session tells a rule the strategy states from a rule this lab invented.
/// see: A clause states where it came from, and a sourced clause quotes rather than paraphrases
///
/// <b>The names below are what that trace is keyed on.</b> `clause-provenance` reconciles these two
/// lists against the document's two clause tables in both directions, so renaming a check here
/// without editing the trace fails rather than leaving a clause whose provenance silently describes
/// a gate that no longer exists.
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
    public static bool PassedAll(IEnumerable<CheckResult> results) => GatingFailures(results) == 0;

    /// <summary>
    /// How many gating checks a setup failed, which is its distance from being a candidate.
    ///
    /// <b>The same rule <see cref="PassedAll"/> runs, expressed once so the two cannot drift.</b>
    /// Passing everything is this count at nought and was written out separately until 6.12; a
    /// gallery filter asking "how close did this come" needs the count rather than the boolean, and
    /// two spellings of one rule is how the answer to "did it pass" and the answer to "by how much
    /// did it miss" end up disagreeing about a name that failed <c>cluster</c>.
    ///
    /// <b><see cref="RecordedNotRequired"/> is why this is not a count of failed rows.</b> A setup
    /// failing only <c>cluster</c> is a candidate, so a count over every failed check would put it
    /// one gate away when it is nought gates away. Over the 367 rows the store held on 2026-09-08
    /// the two readings differ by eight at a distance of one, being 31 against 23, so the difference
    /// is measurable rather than theoretical.
    /// </summary>
    public static int GatingFailures(IEnumerable<CheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return GatingFailures(results.Select(r => (r.Name, r.Passed)));
    }

    /// <summary>
    /// The same rule over a name and a verdict, for a caller holding the read surface's shape rather
    /// than the detector's.
    ///
    /// <b>One rule reached two ways rather than two rules.</b> `CheckResult` is what the detector
    /// writes and `SetupCheckView` is what the read surface publishes, and they are deliberately
    /// different types: the wire shape is not the domain shape. Without this overload the gallery's
    /// filter would have counted gating failures itself, against its own copy of
    /// <see cref="RecordedNotRequired"/>, and the day <c>cluster</c> stops being the only recorded
    /// and never required check the two would disagree with nothing saying so.
    /// </summary>
    public static int GatingFailures(IEnumerable<(string Name, bool Passed)> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks.Count(c => !c.Passed && !RecordedNotRequired.Contains(c.Name));
    }

    /// <summary>
    /// The same count under the gate set a row was scored with, from 7.11.
    ///
    /// <b>A row's gate set is its generation's</b>, and the two differ in which clauses are recorded
    /// and never required: generation 0 records `cluster` alone and generation 1 also records
    /// `moves-enough`, `held-floor` and `no-reclaim`. Counting a generation 1 row against generation
    /// 0's set would put a candidate one gate away for failing a clause it was never required to pass.
    /// </summary>
    public static int GatingFailures(IEnumerable<(string Name, bool Passed)> checks, int generation)
    {
        ArgumentNullException.ThrowIfNull(checks);
        IReadOnlySet<string> recorded = RecordedNotRequiredFor(generation);
        return checks.Count(c => !c.Passed && !recorded.Contains(c.Name));
    }

    /// <summary>The clauses recorded and never required under one generation's gate set.</summary>
    public static IReadOnlySet<string> RecordedNotRequiredFor(int generation) =>
        generation >= GenerationOneChecks.Generation ? GenerationOneChecks.RecordedNotRequired : RecordedNotRequired;
}

/// <summary>
/// What a reader may ask of a night besides which gate rejected a name.
///
/// <b>A vocabulary rather than two literals at each end.</b> The read surface filters on these and
/// the gallery renders them, and a string spelled in both places is the defect this corpus greps
/// for: the page would offer an option the surface silently did not recognise, and an unrecognised
/// filter returns the whole night, which reads exactly like a night where everything qualified.
///
/// <b>Neither value is a check name and the two filters compose.</b> The failed-check filter asks
/// which gate rejected a name; this asks how far the name got. Folding them into one parameter
/// would make them alternatives, and the question a person actually has on the gallery is both at
/// once: of the names this gate rejected, which were otherwise clean.
/// </summary>
public static class SetupOutcomes
{
    /// <summary>Only the setups that cleared every gating check, which is what a candidate is.</summary>
    public const string PassedEverything = "passed-all";

    /// <summary>Only the setups one gating check away from being a candidate.</summary>
    public const string FailedOnlyOne = "failed-one";

    /// <summary>Every value the filter accepts, in the order the gallery offers them.</summary>
    public static IReadOnlyList<string> All { get; } = [PassedEverything, FailedOnlyOne];

    /// <summary>What the gallery calls each one, so the page holds no vocabulary of its own.</summary>
    public static string Label(string outcome) => outcome switch
    {
        PassedEverything => "passed everything",
        FailedOnlyOne => "failed only one",
        _ => outcome,
    };

    /// <summary>
    /// Whether one setup's checks answer the question this outcome asks.
    ///
    /// An outcome outside <see cref="All"/> matches nothing rather than everything, because a filter
    /// nobody recognises returning the whole night is the failure that cannot be seen: it renders as
    /// a night in which every name qualified.
    /// </summary>
    public static bool Matches(string outcome, IEnumerable<CheckResult> results) =>
        Matches(outcome, (results ?? throw new ArgumentNullException(nameof(results)))
            .Select(r => (r.Name, r.Passed)));

    /// <inheritdoc cref="Matches(string, IEnumerable{CheckResult})"/>
    public static bool Matches(string outcome, IEnumerable<(string Name, bool Passed)> checks) =>
        Matches(outcome, checks, generation: 0);

    /// <summary>The same question of a row scored under a given generation's gate set, from 7.11.</summary>
    public static bool Matches(string outcome, IEnumerable<(string Name, bool Passed)> checks, int generation) => outcome switch
    {
        PassedEverything => SetupChecks.GatingFailures(checks, generation) == 0,
        FailedOnlyOne => SetupChecks.GatingFailures(checks, generation) == 1,
        _ => false,
    };
}
