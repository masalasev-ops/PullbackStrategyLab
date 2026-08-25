using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Core.Configuration;

/// <summary>
/// The one place configuration sources are registered, used identically by the Worker,
/// the Api, the Web project and the test suite. Two properties have to hold or the
/// secrets choice quietly breaks things, and both are the reason this is one function
/// rather than three similar ones.
///
/// The secrets file is registered <b>before</b> environment variables, so an environment
/// variable still wins, which is what CI and any future container depend on. And it is
/// optional, so a machine without one falls back to environment variables rather than
/// failing to start.
///
/// The default sources a host builder installs are cleared first. Adding a JSON source
/// to a constructed builder appends it, which would put the secrets file after the
/// environment variables and invert both properties, and doing that in one project but
/// not another makes two projects resolve the same key differently with nothing on the
/// surface to show it.
/// see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables
/// </summary>
public static class PullbackStrategyLabConfiguration
{
    public const string BaseFileName = "appsettings.json";
    public const string SecretsFileName = "appsettings.Secrets.json";

    /// <summary>
    /// Registers the three sources, in the order the decision requires. Every entry point
    /// goes through here, including the tests, so there is one order rather than one per host.
    /// </summary>
    public static IConfigurationBuilder AddPullbackStrategyLabSources(this IConfigurationBuilder builder, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        builder.Sources.Clear();

        return builder
            .SetBasePath(Path.GetFullPath(contentRoot))
            .AddJsonFile(BaseFileName, optional: true, reloadOnChange: false)
            .AddJsonFile(SecretsFileName, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();
    }

    /// <summary>
    /// Builds a configuration root from the three sources alone. What the tests use, so
    /// the order they pin is the order the hosts get.
    /// </summary>
    public static IConfigurationRoot BuildPullbackStrategyLabConfiguration(string contentRoot) =>
        new ConfigurationBuilder().AddPullbackStrategyLabSources(contentRoot).Build();

    /// <summary>
    /// Wires configuration and the services every entry point needs: the options record,
    /// the clock, and the composed paths. Does not touch the store; a host that needs the
    /// store calls AddPullbackStrategyLabStore, which calls this first.
    /// </summary>
    public static TBuilder AddPullbackStrategyLab<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Configuration.AddPullbackStrategyLabSources(builder.Environment.ContentRootPath);
        builder.Services.AddPullbackStrategyLab(builder.Configuration);
        return builder;
    }

    public static IServiceCollection AddPullbackStrategyLab(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PullbackStrategyLabOptions>()
            .Bind(configuration.GetSection(PullbackStrategyLabOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp =>
            new PullbackStrategyLabPaths(sp.GetRequiredService<IOptions<PullbackStrategyLabOptions>>().Value.DataRoot));

        return services;
    }
}
