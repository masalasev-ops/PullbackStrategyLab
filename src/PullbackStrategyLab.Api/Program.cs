using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// The read surface. It opens the store read-only and has no reference to the Worker,
/// transitively or otherwise, which a test asserts against the compiled dependency file
/// rather than against the project file.
///
/// The bind address comes from configuration rather than launchSettings.json, so neither
/// host carries a hardcoded port. Local loopback is plain HTTP, so macOS never needs
/// dotnet dev-certs trusted for the lab to run.
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
        builder.AddPullbackStrategyLabStore();

        PullbackStrategyLabOptions options = builder.Configuration
            .GetSection(PullbackStrategyLabOptions.SectionName)
            .Get<PullbackStrategyLabOptions>() ?? new PullbackStrategyLabOptions();

        builder.WebHost.UseUrls(options.Api.BindAddress);

        WebApplication app = builder.Build();

        app.MapGet("/health", (StoreConnectionFactory connections, IOptions<PullbackStrategyLabOptions> configured) =>
        {
            if (!connections.StoreExists)
            {
                return Results.Ok(new HealthResponse("no-store", 0, configured.Value.DailyCallCeiling));
            }

            using SqliteConnection connection = connections.OpenReadOnly();
            return Results.Ok(new HealthResponse(
                "ready",
                MigrationRunner.ReadUserVersion(connection),
                configured.Value.DailyCallCeiling));
        });

        app.Run();
    }
}

/// <summary>What the status band needs before any store has rows in it.</summary>
public sealed record HealthResponse(string Store, int SchemaVersion, int DailyCallCeiling);
