using PullbackStrategyLab.Core.Configuration;

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
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.AddPullbackStrategyLab();

        PullbackStrategyLabOptions options = builder.Configuration
            .GetSection(PullbackStrategyLabOptions.SectionName)
            .Get<PullbackStrategyLabOptions>() ?? new PullbackStrategyLabOptions();

        builder.Services.AddRazorPages();
        builder.Services.AddHttpClient<LabApiClient>(client =>
            client.BaseAddress = new Uri(options.Api.BaseAddress, UriKind.Absolute));

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
}
