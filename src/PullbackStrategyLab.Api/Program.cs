using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
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
        builder.Services.AddSingleton<LabSetups>();

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

        // What the status band across the top of every screen reads. One request per page load,
        // answered from the store read-only.
        app.MapGet("/status", (StoreConnectionFactory connections, IClock clock, IOptions<PullbackStrategyLabOptions> configured) =>
            Results.Ok(LabStatus.Read(connections, clock, configured.Value.DailyCallCeiling)));

        // One stock's window. The only endpoint that takes a name from the caller, so the name
        // reaches the store as a parameter and never as text in a statement.
        app.MapGet("/chart/{ticker}", (
            string ticker,
            StoreConnectionFactory connections,
            IClock clock,
            IOptions<PullbackStrategyLabOptions> configured,
            int? sessions,
            string? asOf) =>
        {
            DateOnly session = asOf is null
                ? clock.SessionDate(clock.UtcNow, configured.Value.SessionZone)
                : DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            return Results.Ok(LabChart.Read(
                connections,
                ticker,
                session,
                sessions ?? LabChart.DefaultSessions,
                clock.UtcNow));
        });

        // A night's setups, both directions, each with every check's verdict and a window to read it
        // against. The date is in the path because a night is what the gallery is about; the failed
        // check is a query because it is a filter over that night rather than a different night.
        // One day's scoreboard panels, read back as the builder wrote them. Nothing is recomputed
        // here: a read surface that recomputed a bound or an interval would be a second
        // implementation of the arithmetic the phase turns on.
        app.MapGet("/scoreboard/{asOf}", (string asOf, StoreConnectionFactory connections) =>
            Results.Ok(LabScoreboard.Read(
                connections,
                DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture))));

        app.MapGet("/setups/{asOf}", (string asOf, LabSetups setups, IClock clock, string? failed) =>
            Results.Ok(setups.Read(
                DateOnly.ParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                clock.UtcNow,
                string.IsNullOrWhiteSpace(failed) ? null : failed)));

        // The one write this surface makes, and it is a person's opinion of one setup rather than
        // anything the lab computed. Two columns of one row, named in the route so a reader of this
        // file can see the whole of what the read surface can change.
        // see: The agreement a person records is written through the read surface, and it is the only write it makes
        app.MapPost("/setups/{setupId}/agreement", (string setupId, AgreementRequest request, LabSetups setups) =>
        {
            AgreementResult result = setups.RecordAgreement(setupId, request.Agreement, request.Note);

            return result.Recorded ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.Run();
    }
}

/// <summary>What the status band needs before any store has rows in it.</summary>
public sealed record HealthResponse(string Store, int SchemaVersion, int DailyCallCeiling);

/// <summary>
/// What a person recorded about one setup. A null agreement clears it, which is a different fact
/// from disagreeing: "I have not looked at this one" and "I looked and I disagree" are both worth
/// being able to say.
/// </summary>
public sealed record AgreementRequest(string? Agreement, string? Note);
