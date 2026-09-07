using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Core.Configuration;

/// <summary>
/// The researcher seat: which transport it runs on, which model it is pinned to, and what each of
/// the three transports needs to reach a model.
///
/// <b>All three are configured whether or not they are selected, and that is the provision rather
/// than clutter.</b> The API path exists so the day the subscription stops is a configuration value
/// and not a build, and a path whose settings only appear when it is chosen is a path nobody can
/// switch to in a hurry.
/// see: The seat runs on the subscription against claude-opus-5, and the API path stays live for the day the subscription stops
/// </summary>
public sealed record ResearcherOptions
{
    /// <summary>
    /// The model the seat is pinned to, as ruled by the operator on 2026-09-07.
    ///
    /// A constant as well as a default because it is the figure the authored-parameters table
    /// states: the model is a confounder for the phase's own success criterion, so the identifier a
    /// document names and the identifier a request carries have to be one string.
    /// see: The model is a frozen parameter of the pack version, and changing it forks the record
    /// </summary>
    public const string PinnedModel = "claude-opus-5";

    /// <summary>
    /// How often the seat is asked, in days.
    ///
    /// Weekly, because the evidence barely moves in a day and a nightly ask would produce an idea
    /// whether or not there is one. It is a number here and a word in the document, which is what
    /// the pin reconciles.
    /// </summary>
    public const int WeeklyCadenceDays = 7;

    /// <summary>
    /// The one configuration key the researcher's API key is read from, on the transport that uses
    /// one. Named once, here, on the shape the vendor token already has.
    /// see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables
    /// </summary>
    public const string ResearcherKeyName = "Secrets:AnthropicApiKey";

    /// <summary>
    /// The environment variable that must never be set, on any transport.
    ///
    /// <b>Banned on all three and for a different reason on each of the first two.</b> On the
    /// subscription path its presence silently defeats plan authentication and bills API rates; on
    /// the API path the key belongs in configuration like every other secret, so one in the
    /// environment means two places supply the same credential and nothing on the surface says
    /// which won. Named here so the assertion and the ban read the same string.
    /// </summary>
    public const string BannedEnvironmentVariable = "ANTHROPIC_API_KEY";

    /// <summary>Which of the three transports is selected. The switch the provision rests on.</summary>
    public string Transport { get; init; } = SeatTransport.Subscription;

    /// <summary>The model identifier asked for, recorded on every proposal beside what served it.</summary>
    public string Model { get; init; } = PinnedModel;

    /// <summary>How many days apart two asks are.</summary>
    public int CadenceDays { get; init; } = WeeklyCadenceDays;

    /// <summary>The ceiling on the answer, which is a document rather than a conversation.</summary>
    public int MaximumOutputTokens { get; init; } = 4000;

    /// <summary>
    /// How long one ask may take before the seat gives up and records an unavailable week.
    ///
    /// Generous, because a weekly job has nothing to race and a seat that timed out on a slow night
    /// would lose the week for no reason. Bounded all the same: a scheduled job with no timeout is
    /// a job that can still be running when the next one starts.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// The command the subscription transport runs, resolved on the path rather than by absolute
    /// path, so the same configuration works on both machines.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    public string CommandName { get; init; } = "claude";

    /// <summary>Where the Messages API lives, on the transport that calls it.</summary>
    public string MessagesAddress { get; init; } = "https://api.anthropic.com/v1/messages";

    /// <summary>The Messages API version header.</summary>
    public string ApiVersion { get; init; } = "2023-06-01";

    /// <summary>
    /// The researcher's API key, read from <see cref="ResearcherKeyName"/> rather than from this
    /// section, on the shape the vendor token already has. Empty on a machine with no secrets file,
    /// which is a working state for everything that does not run the API transport.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Where a local OpenAI-compatible endpoint listens, on the stopgap transport.</summary>
    public string LocalAddress { get; init; } = "http://127.0.0.1:1234/v1/chat/completions";

    /// <summary>The model name the local endpoint answers to, which is a label the operator set.</summary>
    public string LocalModel { get; init; } = string.Empty;

    /// <summary>
    /// The weights file the local endpoint loaded, digested and recorded on every stopgap proposal.
    ///
    /// <b>This is what pins a local seat, and the endpoint's own model field is not.</b> That field
    /// is a label the operator configured and attests nothing about what ran, so a re-quantisation
    /// or a swapped file would be a change no record could see. A digest can be re-computed and the
    /// seat is deterministic at temperature 0, so the recording is checkable rather than asserted.
    /// see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
    /// </summary>
    public string WeightsPath { get; init; } = string.Empty;

    /// <summary>The quantisation the loaded file carries, which the file name does not always say.</summary>
    public string Quantisation { get; init; } = string.Empty;

    /// <summary>
    /// The context the local endpoint is loaded with, recorded because it decides whether the pack
    /// fits at all.
    ///
    /// Measured on 2026-09-07: at 8,192 the endpoint refuses the pack outright, at 32,768 the answer
    /// fits with 8 percent to spare and at 50,176 with 40. The pack costs about 570 tokens per
    /// declared signal, so a context that fits today begins refusing the week the library grows and
    /// the refusal looks like an outage.
    /// </summary>
    public int LocalContextTokens { get; init; } = 50_176;
}
