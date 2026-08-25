using System.Text.Json;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The Api has no transitive reference to the Worker.
///
/// Asserted against the compiled dependency file rather than against the project file,
/// because a project file states what was asked for and the deps file states what the build
/// actually produced. A reference arriving two projects away would not appear in the first
/// and does appear in the second.
///
/// PullbackStrategyLab.Tests is the one declared exemption. It references everything by
/// design, which is why the exemption is named here and in CLAUDE.md rather than left for a
/// later session to find and assume the check is broken.
/// </summary>
public sealed class ApiIsolationCheck
{
    private readonly ITestOutputHelper _output;

    public ApiIsolationCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "api-isolation")]
    public void The_api_has_no_transitive_reference_to_the_worker()
    {
        var coverage = new CheckCoverage("api-isolation", _output);
        string depsFile = ApiDependencyFile();

        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        JsonElement libraries = deps.RootElement.GetProperty("libraries");

        string[] names = libraries.EnumerateObject().Select(p => p.Name).ToArray();
        string[] offending = names
            .Where(n => n.StartsWith("PullbackStrategyLab.Worker", StringComparison.Ordinal))
            .ToArray();

        string[] labLibraries = names
            .Where(n => n.StartsWith("PullbackStrategyLab.", StringComparison.Ordinal))
            .ToArray();

        coverage
            .Examined("libraries in the compiled dependency file", names.Length)
            .Examined("of those belonging to this solution", labLibraries.Length)
            .Report();

        Assert.True(offending.Length == 0,
            $"PullbackStrategyLab.Api depends on the Worker, transitively or otherwise: {string.Join(", ", offending)}. "
            + "The read surface must not be able to reach the writer.");

        // The check would also pass if the deps file had no solution libraries in it at all,
        // which would mean it was read from somewhere unexpected rather than that the Api is clean.
        Assert.True(labLibraries.Length >= 2,
            $"{RepositoryLayout.Relative(depsFile)} lists {labLibraries.Length} libraries from this solution. "
            + "The Api references Core and Data, so fewer than two means the wrong file was read.");
    }

    /// <summary>
    /// The Api's own dependency file, in the same configuration this test was built in. A
    /// Release run must not assert against a stale Debug output.
    /// </summary>
    private static string ApiDependencyFile()
    {
        string configuration = AppContext.BaseDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        string file = Path.Combine(
            RepositoryLayout.Source,
            "PullbackStrategyLab.Api",
            "bin",
            configuration,
            "net10.0",
            "PullbackStrategyLab.Api.deps.json");

        Assert.True(File.Exists(file),
            $"{RepositoryLayout.Relative(file)} does not exist. The check reads what the build produced, so the Api has "
            + "to have been built in this configuration before it can run.");

        return file;
    }
}
