using System.Text.Json;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web;

/// <summary>
/// The pages. Server-rendered with no build step, and no reference to the Data assembly:
/// everything it shows arrives from the Api over HTTP through a typed client whose base
/// address is configured.
/// see: Pages are server-rendered with no build step, and any script is local rather than fetched
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        // The content root is where the binary sits, for the same reason the Worker's is: a
        // configuration file found by the current directory is found on one machine and missed
        // on the other.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.AddPullbackStrategyLab();

        PullbackStrategyLabOptions options = builder.Configuration
            .GetSection(PullbackStrategyLabOptions.SectionName)
            .Get<PullbackStrategyLabOptions>() ?? new PullbackStrategyLabOptions();

        builder.Services.AddRazorPages();
        builder.Services.AddHttpClient<LabApiClient>(client =>
        {
            client.BaseAddress = new Uri(options.Api.BaseAddress, UriKind.Absolute);

            // Every page load reads the status band, so a read surface that is down must cost a
            // page a moment rather than the client's hundred-second default. The band says so
            // and the page renders; a page that hung until the default expired would be a page
            // nobody could use to find out what was wrong.
            client.Timeout = TimeSpan.FromSeconds(LabApiClient.ReadTimeoutSeconds);
        });

        WebApplication app = builder.Build();
        app.UseStaticFiles();
        app.MapRazorPages();
        app.Run();
    }
}

/// <summary>
/// The one way a page reaches the store's contents. No page holds a store connection,
/// so a page cannot become a second connection to a file the Worker is writing.
/// </summary>
public sealed class LabApiClient
{
    /// <summary>How long a page waits for the read surface before rendering without its figures.</summary>
    public const int ReadTimeoutSeconds = 3;

    private readonly HttpClient _http;

    public LabApiClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public Uri? BaseAddress => _http.BaseAddress;

    public async Task<string> ReadHealthAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the status band reads, on every page load.
    ///
    /// It never throws. The Api and the pages are two hosts started separately, so one being
    /// down is an ordinary state of the machine, and a shell that would not render without the
    /// read surface would be a shell nobody could use to find out that the read surface was
    /// down.
    /// </summary>
    public async Task<LabStatusView> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("/status", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return LabStatusView.Down($"the read surface answered {(int)response.StatusCode}");
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            StatusPayload? payload = await JsonSerializer
                .DeserializeAsync<StatusPayload>(body, Json, cancellationToken).ConfigureAwait(false);

            if (payload is null)
            {
                return LabStatusView.Down("the read surface answered with nothing");
            }

            return new LabStatusView(
                true,
                null,
                payload.Store,
                payload.SchemaVersion,
                payload.Session,
                payload.LastRun?.Stage,
                payload.LastRun?.Outcome,
                payload.UniverseMembers,
                payload.BarsStored,
                payload.CallsUsed,
                payload.DailyCallCeiling,
                payload.MarketMood,
                payload.PositionsOpen,
                payload.ShortPositionsOpen,
                payload.RiskAtStake);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return LabStatusView.Down($"the read surface at {BaseAddress} did not answer");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The wire shape, which is all the two hosts share.</summary>
    private sealed record StatusPayload(
        string Store,
        int SchemaVersion,
        string? Session,
        RunPayload? LastRun,
        long UniverseMembers,
        long BarsStored,
        int CallsUsed,
        int DailyCallCeiling,
        string? MarketMood,
        int? PositionsOpen,
        int? ShortPositionsOpen,
        decimal? RiskAtStake);

    private sealed record RunPayload(string Stage, string StartedAt, string? EndedAt, string Outcome, int CallsUsed);
}
