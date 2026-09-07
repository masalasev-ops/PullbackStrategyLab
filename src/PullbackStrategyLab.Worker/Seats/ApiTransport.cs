using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;

namespace PullbackStrategyLab.Worker.Seats;

/// <summary>
/// The Messages API with the key in configuration. Built, tested, and not selected.
///
/// <b>It is built now so the day the subscription stops is a configuration value.</b> The operator
/// ruled the subscription and ruled that this path stays live beside it; a fallback written later
/// is not a provision, because the day it is needed is the day nobody wants to be writing one.
/// see: The seat runs on the subscription against claude-opus-5, and the API path stays live for the day the subscription stops
///
/// <b>The empty tool set is structural here rather than configured.</b> The request carries no
/// <c>tools</c> key, so the model has no mechanism to act through at all: there is nothing to
/// disable and nothing that a later edit could re-enable without adding a field. That asymmetry is
/// the price of the path the operator chose, and it is recorded rather than rediscovered at the
/// switch.
/// see: The researcher transport is a configuration switch between subscription and API key, over a deliberately narrow interface
/// </summary>
public sealed class ApiTransport : IResearchTransport
{
    private readonly HttpClient _http;
    private readonly ResearcherOptions _options;

    public ApiTransport(HttpClient http, IOptions<PullbackStrategyLabOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value.Researcher;
    }

    public string Transport => SeatTransport.Api;

    public string ConfiguredModel => _options.Model;

    /// <summary>
    /// The request body, built where a test can read it.
    ///
    /// <b>There is no tools key and that is the assertion.</b> A test proving the absence of a
    /// field is a strange-looking test and it is the right one: the property being held is that
    /// nothing here can grow into a capability without a field being added, so the field's absence
    /// is what a later edit would have to break.
    /// </summary>
    public Dictionary<string, object> Body(string pack) =>
        new(StringComparer.Ordinal)
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaximumOutputTokens,
            ["system"] = ProposalPrompt.Instruction,
            ["messages"] = new[]
            {
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

        if (!_options.HasApiKey)
        {
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"no researcher key is configured at \"{ResearcherOptions.ResearcherKeyName}\", so the "
                + "API seat could not be asked");
        }

        try
        {
            return Send(pack, cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel, $"the API seat could not be reached: {failure.Message}");
        }
    }

    private SeatAnswer Send(string pack, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.MessagesAddress)
        {
            Content = JsonContent.Create(Body(pack)),
        };

        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", _options.ApiVersion);

        using HttpResponseMessage response =
            _http.Send(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

        string payload = response.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            // A spent allowance and a lapsed key are the same shape as any other refusal: no
            // proposal, a reason, and a row. The status is in the reason because the two are told
            // apart by it and by nothing else the response carries.
            return SeatAnswer.Unavailable(
                Transport, ConfiguredModel,
                $"the API seat answered {(int)response.StatusCode}: {Summarise(payload)}");
        }

        return ReadResponse(payload, ConfiguredModel);
    }

    /// <summary>
    /// The text and the served model out of one Messages response.
    ///
    /// Pure and public, so the reading is proved against payloads written by hand rather than only
    /// against a live call nobody can make in the suite.
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
                SeatTransport.Api, configuredModel, $"the API seat's answer is not JSON: {failure.Message}");
        }

        string? servedModel = root.TryGetProperty("model", out JsonElement m) ? m.GetString() : null;

        if (!root.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
        {
            return SeatAnswer.Unavailable(
                SeatTransport.Api, configuredModel, "the API seat's answer carried no content block");
        }

        // Every text block joined rather than the first taken, because a response split across
        // blocks would otherwise lose everything after the first and the loss would look like a
        // model that answered badly.
        string text = string.Concat(content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out JsonElement t) && t.GetString() == "text")
            .Select(b => b.TryGetProperty("text", out JsonElement x) ? x.GetString() ?? string.Empty : string.Empty));

        return string.IsNullOrWhiteSpace(text)
            ? SeatAnswer.Unavailable(SeatTransport.Api, configuredModel, "the API seat returned no text")
            : SeatAnswer.Answer(SeatTransport.Api, configuredModel, servedModel, text);
    }

    /// <summary>An error payload, shortened, because a reason column is not a place for a page of HTML.</summary>
    private static string Summarise(string payload) =>
        payload.Length <= 400 ? payload.Trim() : payload[..400].Trim() + "...";
}
