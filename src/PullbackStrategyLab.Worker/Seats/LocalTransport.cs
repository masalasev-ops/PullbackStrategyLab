using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Worker.Seats;

/// <summary>
/// A local model on an OpenAI-compatible endpoint. A stopgap, never the pinned seat.
///
/// <b>Permitted for the weeks the subscription is unexpectedly unavailable, and excluded from the
/// success criterion.</b> The criterion holds the reader fixed and varies the pack, so a proposal
/// from a different reader measures the reader. Mixing readers inside one version's record is worse
/// than forking it, because a fork is visible and a mixture is not.
/// see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
///
/// <b>It records what actually pins it, which the endpoint's model field does not.</b> That field
/// is a label the operator configured; a re-quantisation or a swapped file is a change no record
/// could otherwise see. The digest of the weights file, the quantisation, the runtime and the
/// sampling parameters are stored on every proposal, and because the seat is deterministic at
/// temperature 0 those recordings are checkable by re-running rather than taken on trust. That is
/// the one axis on which this path is the strongest of the three.
///
/// <b>Temperature is nought and is not configurable.</b> A stopgap whose sampling varied would lose
/// the only property that makes it worth recording, and a knob that must never be turned is better
/// not fitted.
/// </summary>
public sealed class LocalTransport : IResearchTransport
{
    /// <summary>The sampling this seat asks under, recorded verbatim on every proposal.</summary>
    public const string Sampling = "temperature=0";

    private readonly HttpClient _http;
    private readonly ResearcherOptions _options;

    public LocalTransport(HttpClient http, IOptions<PullbackStrategyLabOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value.Researcher;
    }

    public string Transport => SeatTransport.Local;

    /// <summary>
    /// The label the endpoint answers to, which is what was configured and not what ran.
    ///
    /// It is returned here because the interface asks what the seat was configured against and this
    /// is the honest answer to that question. What pins the run is in <see cref="SeatAnswer.Pins"/>,
    /// and the two are kept apart so nobody reads the label as provenance.
    /// </summary>
    public string ConfiguredModel =>
        string.IsNullOrWhiteSpace(_options.LocalModel) ? "unnamed-local-model" : _options.LocalModel;

    /// <summary>The request body, built where a test can read it. No tools key, as on the API path.</summary>
    public Dictionary<string, object> Body(string pack) =>
        new(StringComparer.Ordinal)
        {
            ["model"] = ConfiguredModel,
            ["temperature"] = 0,
            ["max_tokens"] = _options.MaximumOutputTokens,
            ["messages"] = new[]
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["role"] = "system",
                    ["content"] = ProposalPrompt.Instruction,
                },
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = pack,
                },
            },
        };

    public SeatAnswer Ask(string pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);

        try
        {
            return Send(pack, cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"the local seat at {_options.LocalAddress} could not be reached: {failure.Message}");
        }
    }

    private SeatAnswer Send(string pack, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.LocalAddress)
        {
            Content = JsonContent.Create(Body(pack)),
        };

        using HttpResponseMessage response =
            _http.Send(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        string payload = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            // The commonest failure by far, and it is worth naming rather than reporting as a
            // status: a context loaded smaller than the pack refuses the request outright, and the
            // refusal looks like an outage. Measured at 8,192 on 2026-09-07.
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"the local seat answered {(int)response.StatusCode}: {payload.Trim()}. The pack may exceed "
                + $"the context the endpoint is loaded with, configured here as "
                + $"{_options.LocalContextTokens.ToString(CultureInfo.InvariantCulture)} token(s)");
        }

        SeatAnswer answer = ReadResponse(payload, ConfiguredModel);

        return answer.Answered ? answer with { Pins = Pins() } : answer;
    }

    /// <summary>
    /// What pins this seat: the weights, the quantisation, the runtime and the sampling.
    ///
    /// A missing digest is recorded as such rather than omitted. A pin list that simply lacked the
    /// weights entry would be indistinguishable from one taken before the field existed, and the
    /// whole point of the list is that a later reader can tell what was and was not recorded.
    /// </summary>
    private IReadOnlyList<SeatPin> Pins() =>
    [
        new(SeatPin.WeightsDigest, DigestOf(_options.WeightsPath)),
        new(SeatPin.Quantisation,
            string.IsNullOrWhiteSpace(_options.Quantisation) ? "unrecorded" : _options.Quantisation),
        new(SeatPin.Runtime,
            $"openai-compatible endpoint at {_options.LocalAddress}, context "
            + _options.LocalContextTokens.ToString(CultureInfo.InvariantCulture)),
        new(SeatPin.Sampling, Sampling),
    ];

    /// <summary>
    /// The digest of the weights file, or why there is none.
    ///
    /// <b>Read rather than assumed, and a failure is a value rather than an exception.</b> A seat
    /// that threw because the operator had not configured a path would lose a week over provenance
    /// for a proposal that is excluded from the hit rate anyway. What it must not do is record a
    /// digest it did not compute.
    /// </summary>
    public static string DigestOf(string weightsPath)
    {
        if (string.IsNullOrWhiteSpace(weightsPath))
        {
            return "unconfigured";
        }

        try
        {
            using FileStream file = File.OpenRead(weightsPath);
            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return $"unreadable: {failure.Message}";
        }
    }

    /// <summary>
    /// The text and the served model out of one chat-completions response.
    ///
    /// Pure and public, so the reading is proved against payloads written by hand.
    /// </summary>
    public static SeatAnswer ReadResponse(string payload, string configuredModel)
    {
        ArgumentNullException.ThrowIfNull(payload);

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(payload).RootElement;
        }
        catch (JsonException failure)
        {
            return SeatAnswer.Unavailable(
                SeatTransport.Local, configuredModel, $"the local seat's answer is not JSON: {failure.Message}");
        }

        string? servedModel = root.TryGetProperty("model", out JsonElement m) ? m.GetString() : null;

        string? text =
            root.TryGetProperty("choices", out JsonElement choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out JsonElement message)
            && message.TryGetProperty("content", out JsonElement content)
                ? content.GetString()
                : null;

        return string.IsNullOrWhiteSpace(text)
            ? SeatAnswer.Unavailable(SeatTransport.Local, configuredModel, "the local seat returned no text")
            : SeatAnswer.Answer(SeatTransport.Local, configuredModel, servedModel, text!);
    }
}
