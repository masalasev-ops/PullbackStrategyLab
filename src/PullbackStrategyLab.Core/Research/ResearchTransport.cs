namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The seat's whole contract with the outside world: a pack in, proposal text out.
///
/// <b>The interface is the narrow path rather than the union of the three.</b> An agent framework
/// exists to let a model act, and this component's defining property is that it cannot: it writes a
/// proposal and nothing else. One method, taking text and returning text, is what makes the three
/// implementations substitutable and what makes the substitution cheap enough that the API path can
/// sit built and unused against the day the subscription stops.
/// see: The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
///
/// <b>Nothing here can fail loudly enough to lose a week.</b> An implementation that cannot ask
/// returns an answer saying so rather than throwing, because a queued week is an ordinary outcome
/// of a lapsed subscription or a spent allowance and the caller has a row to write either way.
/// </summary>
public interface IResearchTransport
{
    /// <summary>Which of the three this is, as it is recorded on every proposal row.</summary>
    string Transport { get; }

    /// <summary>The model identifier this seat is configured against, before anything is asked.</summary>
    string ConfiguredModel { get; }

    /// <summary>
    /// Ask once, and return what came back or why nothing did.
    ///
    /// <paramref name="pack"/> is the rendered pack body, byte for byte as the version fingerprint
    /// was taken over it. Nothing is appended to it here: a transport that added a sentence would
    /// make the pack a different document from the one the version names.
    /// </summary>
    SeatAnswer Ask(string pack, CancellationToken cancellationToken);
}

/// <summary>
/// The three transports, named as they are stored.
///
/// <b>The pinned seat is the subscription and the other two are not interchangeable with it.</b>
/// The API path is the same reader reached another way and switching is neutral wherever the served
/// string is unchanged. The local path is a different reader, so its proposals are kept and excluded
/// from the hit rate.
/// see: The seat runs on the subscription against claude-opus-5, and the API path stays live for the day the subscription stops
/// see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
/// </summary>
public static class SeatTransport
{
    /// <summary>The pinned seat: the Claude Code CLI in print mode, drawing on the operator's plan.</summary>
    public const string Subscription = "subscription";

    /// <summary>The Messages API with the key in configuration. Built, tested, and not selected.</summary>
    public const string Api = "api";

    /// <summary>A local model on an OpenAI-compatible endpoint. A stopgap, never the pinned seat.</summary>
    public const string Local = "local";

    public static IReadOnlyList<string> All { get; } = [Subscription, Api, Local];

    /// <summary>
    /// Whether proposals from a transport count toward proposal hit rate by pack version.
    ///
    /// The criterion holds the reader fixed and varies the pack, so a proposal from a different
    /// reader measures the reader. Mixing readers inside one version's record is worse than forking
    /// it, because a fork is visible in the record and a mixture is not.
    /// see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
    /// </summary>
    public static bool CountsTowardHitRate(string transport) =>
        !string.Equals(transport, Local, StringComparison.Ordinal);
}

/// <summary>
/// What one ask came to: the text, what actually served it, and what pins that claim.
///
/// <b>An unanswered ask is a value here and never an exception.</b> A lapsed subscription, a spent
/// allowance and an unreachable endpoint are the same shape: no proposal, a reason, and a row. The
/// alternative is a stage that throws on the ordinary case and a night that reads as broken.
/// </summary>
public sealed record SeatAnswer
{
    public required string Transport { get; init; }

    /// <summary>The model the seat was configured against.</summary>
    public required string ConfiguredModel { get; init; }

    /// <summary>
    /// The model string the response reported as having served it.
    ///
    /// <b>The strongest of the three recordings, because it says what actually ran rather than what
    /// was asked for.</b> Null where the transport reported none, which is itself worth storing: a
    /// seat that stopped reporting it has stopped pinning anything, and a null column says so where
    /// a copy of the configured value would hide it.
    /// see: The model is a frozen parameter of the pack version, and changing it forks the record
    /// </summary>
    public string? ServedModel { get; init; }

    /// <summary>The answer text, present exactly when the seat answered.</summary>
    public string? Text { get; init; }

    /// <summary>Why nothing was asked or nothing came back, present exactly when it did not.</summary>
    public string? UnavailableBecause { get; init; }

    /// <summary>
    /// What pins this answer beyond the model string, as name and value pairs.
    ///
    /// <b>Empty on the two hosted transports and populated on the local one.</b> A hosted served
    /// string can only be taken on trust because no hosted call can be re-derived; a local seat is
    /// deterministic at temperature 0, so the weights digest, the quantisation, the runtime version
    /// and the sampling parameters together make its proposal reproducible by anyone holding the
    /// same file. The list is a list rather than four columns because the three transports pin
    /// different things and a column per transport would be mostly null.
    /// see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
    /// </summary>
    public IReadOnlyList<SeatPin> Pins { get; init; } = [];

    /// <summary>Whether this ask produced text to read.</summary>
    public bool Answered => Text is not null && UnavailableBecause is null;

    /// <summary>An ask that produced text.</summary>
    public static SeatAnswer Answer(
        string transport, string configuredModel, string? servedModel, string text,
        IReadOnlyList<SeatPin>? pins = null) =>
        new()
        {
            Transport = transport,
            ConfiguredModel = configuredModel,
            ServedModel = servedModel,
            Text = text,
            Pins = pins ?? [],
        };

    /// <summary>An ask that could not be made, or was made and returned nothing.</summary>
    public static SeatAnswer Unavailable(string transport, string configuredModel, string because) =>
        new()
        {
            Transport = transport,
            ConfiguredModel = configuredModel,
            UnavailableBecause = because,
        };
}

/// <summary>One named thing that pins an answer, stored as a name and a value.</summary>
public sealed record SeatPin(string Name, string Value)
{
    /// <summary>The digest of the weights file a local seat loaded.</summary>
    public const string WeightsDigest = "weights-digest";

    /// <summary>The quantisation the loaded file carries.</summary>
    public const string Quantisation = "quantisation";

    /// <summary>The runtime and its version, as the endpoint reports them.</summary>
    public const string Runtime = "runtime";

    /// <summary>The sampling parameters the ask was made under.</summary>
    public const string Sampling = "sampling";

    /// <summary>
    /// The tool set the session actually reported, on a transport that can report one.
    ///
    /// <b>This is the 1.5 obligation answered by the run rather than by a test.</b> The empty tool
    /// set on the subscription path is a property of the arguments the seat builds, and a test over
    /// those arguments proves what the seat asked for and not what it got. A session that reports
    /// its own tool list turns the assertion into a reading, so a tool arriving from the operator's
    /// own configuration is refused at the ask rather than found later.
    /// see: The AI writes only to the proposal store
    /// </summary>
    public const string ToolsReported = "tools-reported";
}
