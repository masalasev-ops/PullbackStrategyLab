using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// What the researcher returns: one proposal, or a recorded abstention.
///
/// <b>The change is the five fields the version register already stores.</b> A proposal accepted
/// becomes a version, and if the two carried different shapes there would be a translation step
/// between them and a place for the meaning to move.
/// see: A proposal is a document whose one change is the five fields the version register already stores
///
/// <b>Abstention is a value of the outcome and the change fields are conditional on it.</b> A
/// document with nothing in the change field and a document that says there is nothing to propose
/// are different answers and only one of them is a result. Until 6.5 the schema required a change
/// whatever the outcome, so an abstaining model had to put something in the gate field, and a model
/// shown a near-empty pack put the planted null there: a correct abstention failed a pack version
/// under a tripwire meant for a proposal that rummaged. The fields are absent on an abstention
/// rather than empty, so there is nowhere for that to happen.
/// see: Abstention is a valid recorded proposal outcome
/// see: The planted-null tripwire is scoped to what a proposal rests on, and an abstention rests on nothing
/// </summary>
public sealed record ProposalDocument
{
    /// <summary>A proposal: a change, its family, its mechanism, its evidence and its refutation.</summary>
    public const string Proposed = "proposed";

    /// <summary>
    /// A signal request: the second kind, and the one that lifts the ceiling rather than
    /// rearranging what is under it.
    ///
    /// <b>The library is a hard ceiling on the proposal space and the model cannot lift it.</b> A
    /// request says the opposite of a rule change: I cannot separate these two setups with what I
    /// have, compute this and I could. It is a build task rather than a variant, and without it the
    /// loop plateaus within months because there are only so many ways to rearrange ten checks.
    /// see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal
    /// </summary>
    public const string Requested = "requested";

    /// <summary>An abstention: no change, and the reason there is none.</summary>
    public const string Abstained = "abstained";

    public static IReadOnlyList<string> Outcomes { get; } = [Proposed, Requested, Abstained];

    /// <summary>Which of the two this document is.</summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// The one thing that changes, present exactly on a proposal.
    ///
    /// Null on an abstention rather than a record of empty strings, which is the whole of what the
    /// 6.4 finding cost: a required field on a document that proposes nothing is a field a model
    /// fills with whatever the pack put in front of it.
    /// </summary>
    public ProposedChange? Change { get; init; }

    /// <summary>
    /// Selection or execution, present exactly on a proposal.
    ///
    /// One value and never both: a proposal spanning the two families is rejected, because the two
    /// are scored on different quantities and a version that moved one of each could not be
    /// attributed to either.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>Why it should work, in one sentence, committed before any result exists.</summary>
    public string? Mechanism { get; init; }

    /// <summary>
    /// The evidence, as setup ids already in the store.
    ///
    /// Ids rather than prose, so the claim can be checked against rows rather than believed. A
    /// fabricated id is a defect the registry can see; a paragraph of reasoning is not.
    /// </summary>
    public IReadOnlyList<string> EvidenceSetupIds { get; init; } = [];

    /// <summary>
    /// The signals the reasoning rests on, named from the library the pack showed.
    ///
    /// This is one of the two inputs to the tripwire and the reason it is a field of its own: the
    /// signals a proposal rests on are a shorter and more answerable question than which words
    /// appear in its mechanism sentence.
    /// </summary>
    public IReadOnlyList<string> EvidenceSignals { get; init; } = [];

    /// <summary>What would refute it.</summary>
    public string? Refutation { get; init; }

    /// <summary>How many observations settle the question.</summary>
    public int? ObservationsToSettle { get; init; }

    /// <summary>Why there is nothing to propose, present exactly on an abstention.</summary>
    public string? AbstainedBecause { get; init; }

    /// <summary>
    /// The signal a request wants computed, present exactly on a request.
    ///
    /// A name rather than a formula. What to compute is decided by a person reading the request,
    /// which is what keeps the library a ceiling the model cannot lift for itself: a seat that
    /// supplied the formula would be widening its own proposal space.
    /// </summary>
    public string? RequestedSignal { get; init; }

    /// <summary>
    /// Which axis of measurement the request belongs to, present exactly on a request.
    ///
    /// The axes are the thing worth enumerating rather than the signals: the library today is almost
    /// entirely price path, and a run of requests all naming price path is legible as the library's
    /// actual shape rather than as eight separate ideas.
    /// </summary>
    public string? RequestedAxis { get; init; }

    /// <summary>Whether this document proposes a change at all.</summary>
    [JsonIgnore]
    public bool IsAbstention => string.Equals(Outcome, Abstained, StringComparison.Ordinal);

    /// <summary>
    /// Whether this document asks for a signal rather than proposing a rule.
    ///
    /// The two kinds go to different places: a rule change goes to the screen and then to a paired
    /// test, and a request is a build task. A registry that treated them as one would send a request
    /// to a replay that has nothing to replay.
    /// </summary>
    [JsonIgnore]
    public bool IsRequest => string.Equals(Outcome, Requested, StringComparison.Ordinal);

    /// <summary>
    /// The signals this proposal rests on: the ones it cites, and the ones the threshold it moves is
    /// replayed against.
    ///
    /// <b>This is the tripwire's subject and it is not the document.</b> The rule is that a proposal
    /// citing the planted null fails that pack version, and the failure it was written against is a
    /// model that rummaged through the deciles and came back with the one signal that cannot matter.
    /// Scanning the whole document for the name catches that and also catches an abstention naming
    /// it, a mechanism sentence saying the control was checked and discarded, and a refutation
    /// condition that mentions it. Those are the opposite of rummaging.
    /// see: One meaningless signal is planted in the conditional tables
    ///
    /// <b>An abstention rests on nothing and returns empty.</b> There is no change, so there is no
    /// threshold and no signals behind one, and a reason field is prose rather than a citation.
    /// </summary>
    public IReadOnlyList<string> SignalsRestedOn(SelectionRule ruleInForce)
    {
        ArgumentNullException.ThrowIfNull(ruleInForce);

        // A request rests on nothing a tripwire can read either. It names a signal that does not
        // exist yet, so it cannot be resting on one that does, and the evidence it cites is the pair
        // it could not separate rather than a signal it reasoned from.
        if (IsAbstention || IsRequest || Change is null)
        {
            return [];
        }

        IReadOnlyList<string> behindTheThreshold =
            ruleInForce.Find(Change.ThresholdName)?.FrozenSignals ?? [];

        return [.. EvidenceSignals
            .Concat(behindTheThreshold)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)];
    }

    /// <summary>
    /// What is wrong with this document, or nothing.
    ///
    /// <b>Every problem is reported rather than the first.</b> A seat that returned one reason a
    /// week would take as many weeks as the document has fields to find out what a model gets wrong
    /// about the contract, and the contract is the thing being learned.
    ///
    /// <b>The rule in force is an input because four of the five change fields are checkable against
    /// it.</b> A direction that is neither side, a gate the rule does not have, a threshold name it
    /// does not carry, and a from-value that is not the value in force are all wrong in a way that
    /// can be said now rather than at the registry.
    /// </summary>
    public IReadOnlyList<string> Problems(SelectionRule longRule, SelectionRule shortRule)
    {
        ArgumentNullException.ThrowIfNull(longRule);
        ArgumentNullException.ThrowIfNull(shortRule);

        var problems = new List<string>();

        if (!Outcomes.Contains(Outcome, StringComparer.Ordinal))
        {
            problems.Add($"outcome is \"{Outcome}\", which is neither \"{Proposed}\" nor \"{Abstained}\"");
            return problems;
        }

        if (IsAbstention)
        {
            AbstentionProblems(problems);
            return problems;
        }

        if (IsRequest)
        {
            RequestProblems(problems);
            return problems;
        }

        ProposalProblems(problems, longRule, shortRule);
        return problems;
    }

    /// <summary>An abstention carries its reason and none of the change fields.</summary>
    private void AbstentionProblems(List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(AbstainedBecause))
        {
            problems.Add("an abstention states why there is nothing to propose, and this one states nothing");
        }

        // Reported rather than ignored: a document carrying both an abstention and a change is two
        // answers, and silently keeping one of them is the reading that loses the other.
        if (Change is not null)
        {
            problems.Add("an abstention carries no change, and this one carries one");
        }

        if (RequestedSignal is not null)
        {
            problems.Add("an abstention asks for no signal, and this one names one");
        }

        if (Family is not null)
        {
            problems.Add("an abstention belongs to no family, and this one names one");
        }

        if (ObservationsToSettle is not null)
        {
            problems.Add("an abstention settles no question, and this one states an observation count");
        }
    }

    /// <summary>
    /// A request names the signal it wants, why it would separate the pair, and the pair.
    ///
    /// <b>The evidence has to be at least two setups, and that is the request's whole subject.</b>
    /// A request says two setups look identical in everything recorded and did opposite things, so
    /// one id names no pair and none names nothing at all. The twin-pair section of the pack is
    /// where the pairs come from, which is what that section is for.
    ///
    /// <b>It states no observation count and that is not an omission.</b> Nothing is being settled
    /// by waiting: the question a request asks is answered by computing the signal and vetting it on
    /// whether it tightens outcome-similar neighbourhoods, which needs no variant and no forward
    /// period.
    /// see: Signals are admitted on whether they tighten outcome-similar neighbourhoods, independently of any rule using them
    /// </summary>
    private void RequestProblems(List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(RequestedSignal))
        {
            problems.Add("a signal request names the signal it wants computed, and this one names none");
        }

        if (string.IsNullOrWhiteSpace(RequestedAxis))
        {
            problems.Add("a signal request names the axis of measurement it belongs to, and this one names none");
        }

        if (string.IsNullOrWhiteSpace(Mechanism))
        {
            problems.Add("a signal request states why the signal would separate the pair, and this one states none");
        }

        if (EvidenceSetupIds.Count < 2)
        {
            problems.Add(
                "a signal request rests on setups it could not separate, which is at least two, and this one "
                + $"names {EvidenceSetupIds.Count}");
        }

        // The same shape as an abstention carrying a change: a document that is two answers is
        // reported as both rather than read as whichever one a caller happened to look at.
        if (Change is not null)
        {
            problems.Add("a signal request changes no threshold, and this one carries a change");
        }

        if (Family is not null)
        {
            problems.Add("a signal request belongs to no family, and this one names one");
        }

        if (ObservationsToSettle is not null)
        {
            problems.Add("a signal request settles nothing by waiting, and this one states an observation count");
        }

        if (AbstainedBecause is not null)
        {
            problems.Add("a signal request is not an abstention, and this one states why it abstained");
        }
    }

    /// <summary>A proposal carries all five change fields, a family, a mechanism, evidence and a refutation.</summary>
    private void ProposalProblems(List<string> problems, SelectionRule longRule, SelectionRule shortRule)
    {
        if (AbstainedBecause is not null)
        {
            problems.Add("a proposal is not an abstention, and this one states why it abstained");
        }

        if (RequestedSignal is not null)
        {
            problems.Add(
                "a rule change and a signal request are two kinds and this document is both: it moves a "
                + "threshold and asks for a signal");
        }

        if (!VariantFamily.All.Contains(Family, StringComparer.Ordinal) || Family == VariantFamily.Baseline)
        {
            problems.Add(
                $"family is \"{Family ?? "absent"}\", and a proposal belongs to "
                + $"\"{VariantFamily.Selection}\" or \"{VariantFamily.Execution}\"");
        }

        if (string.IsNullOrWhiteSpace(Mechanism))
        {
            problems.Add("a proposal states its mechanism in one sentence, and this one states none");
        }

        if (EvidenceSetupIds.Count == 0)
        {
            problems.Add("a proposal rests on setup ids already in the store, and this one names none");
        }

        if (string.IsNullOrWhiteSpace(Refutation))
        {
            problems.Add("a proposal states what would refute it, and this one states nothing");
        }

        if (ObservationsToSettle is not > 0)
        {
            problems.Add("a proposal states how many observations settle the question, and this one states none");
        }

        if (Change is null)
        {
            problems.Add("a proposal changes one threshold, and this one changes none");
            return;
        }

        ChangeProblems(problems, longRule, shortRule);
    }

    /// <summary>The five change fields, read against the rule they claim to move.</summary>
    private void ChangeProblems(List<string> problems, SelectionRule longRule, SelectionRule shortRule)
    {
        ProposedChange change = Change!;

        SelectionRule? rule =
            change.Direction == SetupDirection.Long ? longRule
            : change.Direction == SetupDirection.Short ? shortRule
            : null;

        if (rule is null)
        {
            problems.Add($"direction is \"{change.Direction}\", which is neither long nor short");
            return;
        }

        RuleThreshold? threshold = rule.Find(change.ThresholdName);

        if (threshold is null)
        {
            problems.Add(
                $"the {change.Direction} rule has no threshold named \"{change.ThresholdName}\"");
            return;
        }

        if (!string.Equals(threshold.Gate, change.Gate, StringComparison.Ordinal))
        {
            problems.Add(
                $"\"{change.ThresholdName}\" belongs to gate \"{threshold.Gate}\" on the "
                + $"{change.Direction} side and the proposal names \"{change.Gate}\"");
        }

        // The from-value is the one field a model cannot invent from the library, which is why the
        // pack carries the rule in force from 6.5 and why a wrong one is a defect rather than a
        // rounding. A proposal moving a threshold from a value it does not hold is a proposal about
        // a rule that is not running.
        if (threshold.Value != change.From)
        {
            problems.Add(
                $"\"{change.ThresholdName}\" is in force at "
                + $"{threshold.Value.ToString(CultureInfo.InvariantCulture)} on the {change.Direction} "
                + $"side and the proposal moves it from "
                + $"{change.From.ToString(CultureInfo.InvariantCulture)}");
        }

        if (threshold.Value == change.To)
        {
            problems.Add(
                $"\"{change.ThresholdName}\" would move to the value it already holds, which is no change");
        }

        if (threshold.Family == ThresholdFamily.Recorded)
        {
            problems.Add(
                $"\"{change.ThresholdName}\" is recorded on every row and gates nothing, so moving it "
                + "selects nothing differently");
        }

        if (Family is not null
            && !string.Equals(threshold.Family.ToString(), Family, StringComparison.OrdinalIgnoreCase)
            && threshold.Family != ThresholdFamily.Recorded)
        {
            problems.Add(
                $"\"{change.ThresholdName}\" belongs to the "
                + $"{threshold.Family.ToString().ToLowerInvariant()} family and the proposal claims "
                + $"\"{Family}\"");
        }
    }

    /// <summary>
    /// A document read out of the text a seat returned, or the reasons it could not be.
    ///
    /// <b>A malformed answer is a recorded outcome and never a partial proposal.</b> The seat writes
    /// what it got and why it could not be read, so a week the model returned prose is
    /// distinguishable from a week it was never asked.
    /// </summary>
    public static ProposalParse Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProposalParse(null, ["the seat returned nothing to read"]);
        }

        try
        {
            ProposalDocument? document = JsonSerializer.Deserialize<ProposalDocument>(text, ParseOptions);

            return document is null
                ? new ProposalParse(null, ["the seat returned the JSON literal null"])
                : new ProposalParse(document, []);
        }
        catch (JsonException failure)
        {
            return new ProposalParse(null, [$"the seat's answer is not the agreed JSON: {failure.Message}"]);
        }
    }

    /// <summary>
    /// How a document is read and written.
    ///
    /// Property names are the snake-cased field names the contract states, matched
    /// case-insensitively on the way in, because a model that returns <c>fromValue</c> for
    /// <c>from_value</c> has answered the question and lost a week to a naming convention.
    /// </summary>
    public static JsonSerializerOptions ParseOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new QuotedDecimalConverter() },
    };
}

/// <summary>
/// A decimal that reads whether the model wrote it as a number or quoted it as a string.
///
/// <b>Tolerance here and nowhere else, on purpose.</b> The prompt asks for a number and a quoted
/// one is the commonest way a model answers the question correctly and fails the schema, which
/// would cost a week over a pair of quotation marks. What it does not do is guess: a value that is
/// not a decimal in the invariant culture is still a parse failure, so <c>"about 0.45"</c> is
/// refused rather than read as nought.
/// </summary>
public sealed class QuotedDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDecimal();
        }

        if (reader.TokenType == JsonTokenType.String
            && decimal.TryParse(
                reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal quoted))
        {
            return quoted;
        }

        throw new JsonException(
            $"a threshold value has to be a decimal and this one is a {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// The one thing that changes: the five columns <c>variant</c> already holds.
///
/// <b>Decimal, because it is a threshold and thresholds are compared against prices and ratios.</b>
/// A double here would cross the boundary the corpus draws and would do it in the one place where
/// the value is copied into a version register unchanged.
/// </summary>
public sealed record ProposedChange(
    string Direction,
    string Gate,
    string ThresholdName,
    decimal From,
    decimal To);

/// <summary>
/// What reading a seat's answer came to: a document, or why there is none.
///
/// The raw text is not carried here because the seat records it whatever this says, and a parse
/// result that also held the input would give a caller two places to read it from.
/// </summary>
public sealed record ProposalParse(ProposalDocument? Document, IReadOnlyList<string> Problems)
{
    public bool Read => Document is not null && Problems.Count == 0;
}
