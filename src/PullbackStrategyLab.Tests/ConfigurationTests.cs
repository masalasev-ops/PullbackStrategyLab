using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two properties that have to hold or the secrets choice quietly breaks things. Both are
/// pinned by a test rather than by a comment, because both fail silently: a machine resolves
/// a key from the wrong source and nothing on the surface shows it.
/// see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables
/// </summary>
public sealed class ConfigurationTests
{
    private const string ApiKeyVariable = "PullbackStrategyLab__Vendor__ApiKey";

    private const string BaseSettings = """
        {
          "PullbackStrategyLab": {
            "DataRoot": "data",
            "SessionZone": "America/New_York",
            "DailyCallCeiling": 5000,
            "Vendor": { "Name": "EODHD", "Exchange": "US" }
          }
        }
        """;

    private const string SecretsSettings = """
        {
          "PullbackStrategyLab": {
            "Vendor": { "ApiKey": "from-the-secrets-file" }
          }
        }
        """;

    [Fact]
    public void An_environment_variable_overrides_a_value_present_in_the_secrets_file()
    {
        using var content = new TemporaryDirectory();
        content.Write(PullbackStrategyLabConfiguration.BaseFileName, BaseSettings);
        content.Write(PullbackStrategyLabConfiguration.SecretsFileName, SecretsSettings);

        // With no environment variable, the secrets file supplies the key.
        using (Environment(ApiKeyVariable, null))
        {
            IConfigurationRoot withoutVariable =
                PullbackStrategyLabConfiguration.BuildPullbackStrategyLabConfiguration(content.Path);

            Assert.Equal("from-the-secrets-file", Bind(withoutVariable).Vendor.ApiKey);
        }

        // With one, it wins. The file is registered before environment variables precisely so
        // that this holds, which is what CI and any future container depend on.
        using (Environment(ApiKeyVariable, "from-the-environment"))
        {
            IConfigurationRoot withVariable =
                PullbackStrategyLabConfiguration.BuildPullbackStrategyLabConfiguration(content.Path);

            Assert.Equal("from-the-environment", Bind(withVariable).Vendor.ApiKey);
        }
    }

    [Fact]
    public void A_project_starts_cleanly_with_no_secrets_file_on_disk()
    {
        using var content = new TemporaryDirectory();
        content.Write(PullbackStrategyLabConfiguration.BaseFileName, BaseSettings);
        Assert.False(File.Exists(content.File(PullbackStrategyLabConfiguration.SecretsFileName)));

        using (Environment(ApiKeyVariable, null))
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(
                new HostApplicationBuilderSettings { ContentRootPath = content.Path });

            builder.AddPullbackStrategyLabStore();
            using IHost host = builder.Build();

            PullbackStrategyLabOptions options =
                host.Services.GetRequiredService<IOptions<PullbackStrategyLabOptions>>().Value;

            Assert.Equal(5000, options.DailyCallCeiling);
            Assert.False(options.Vendor.HasApiKey);

            // The store is registered and resolvable. Everything that does not call the vendor
            // works on a machine that has never had a secrets file.
            Assert.NotNull(host.Services.GetRequiredService<StoreConnectionFactory>());
        }
    }

    [Fact]
    public void The_sources_are_registered_in_the_order_the_decision_requires()
    {
        using var content = new TemporaryDirectory();
        content.Write(PullbackStrategyLabConfiguration.BaseFileName, BaseSettings);
        content.Write(PullbackStrategyLabConfiguration.SecretsFileName, SecretsSettings);

        IConfigurationRoot configuration =
            PullbackStrategyLabConfiguration.BuildPullbackStrategyLabConfiguration(content.Path);

        string[] sources = configuration.Providers.Select(p => p.GetType().Name).ToArray();

        // Three sources and no others. A host builder installs several of its own, and one left
        // in place would sit after the two files and change which value wins.
        Assert.Equal(3, sources.Length);
        Assert.Equal("JsonConfigurationProvider", sources[0]);
        Assert.Equal("JsonConfigurationProvider", sources[1]);
        Assert.Equal("EnvironmentVariablesConfigurationProvider", sources[2]);
    }

    private static PullbackStrategyLabOptions Bind(IConfiguration configuration) =>
        configuration.GetSection(PullbackStrategyLabOptions.SectionName).Get<PullbackStrategyLabOptions>()
        ?? throw new InvalidOperationException("The section did not bind.");

    /// <summary>Sets an environment variable for the length of a block and puts back what was there.</summary>
    private static IDisposable Environment(string name, string? value)
    {
        string? original = System.Environment.GetEnvironmentVariable(name);
        System.Environment.SetEnvironmentVariable(name, value);
        return new Restore(name, original);
    }

    private sealed class Restore : IDisposable
    {
        private readonly string _name;
        private readonly string? _value;

        public Restore(string name, string? value)
        {
            _name = name;
            _value = value;
        }

        public void Dispose() => System.Environment.SetEnvironmentVariable(_name, _value);
    }
}
