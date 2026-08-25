using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PullbackStrategyLab.Core.Configuration;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The shared extension every project that touches the store calls. It wires configuration
/// through <see cref="PullbackStrategyLabConfiguration"/> and registers the store, whose
/// four pragmas are set at open in <see cref="StoreConnectionFactory"/>. One extension, so
/// there is one place where the pragmas and the configuration order are decided.
///
/// The Web project does not call this. It reads through the Api over HTTP and has no
/// reference to this assembly.
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public static class PullbackStrategyLabStore
{
    public static TBuilder AddPullbackStrategyLabStore<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddPullbackStrategyLab();
        builder.Services.AddPullbackStrategyLabStoreServices();
        return builder;
    }

    public static IServiceCollection AddPullbackStrategyLabStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPullbackStrategyLab(configuration);
        return services.AddPullbackStrategyLabStoreServices();
    }

    private static IServiceCollection AddPullbackStrategyLabStoreServices(this IServiceCollection services)
    {
        services.AddSingleton<StoreConnectionFactory>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<RunLogger>();
        services.AddSingleton<DailyBarReader>();
        return services;
    }
}
