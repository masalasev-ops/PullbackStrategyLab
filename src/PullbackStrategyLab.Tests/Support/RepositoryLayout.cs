namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Where everything is, found once by walking up from the test assembly to the solution
/// file. The checks read the corpus and the source from disk, so they need the repository
/// rather than the build output, and nothing here hardcodes a depth.
/// </summary>
public static class RepositoryLayout
{
    public const string SolutionFileName = "PullbackStrategyLab.sln";

    public static string Root { get; } = FindRoot();

    public static string Docs => Path.Combine(Root, "docs");

    public static string Source => Path.Combine(Root, "src");

    public static string Tools => Path.Combine(Root, "tools");

    public static string Artifacts => Path.Combine(Root, "artifacts");

    /// <summary>The five specs and the three records, plus the artefact. Everything a citation can live in.</summary>
    public static IReadOnlyList<string> CorpusFiles { get; } =
    [
        Path.Combine(Root, "CLAUDE.md"),
        Path.Combine(Docs, "ARCHITECTURE.html"),
        Path.Combine(Docs, "SCHEMA.md"),
        Path.Combine(Docs, "BUILD_PLAN.md"),
        Path.Combine(Docs, "RUNBOOK.md"),
        Path.Combine(Docs, "DECISIONS.md"),
        Path.Combine(Docs, "PROGRESS.md"),
        Path.Combine(Docs, "CHANGELOG.md"),
        Path.Combine(Docs, "SCREENS.html"),
    ];

    /// <summary>Every C# file in the solution, build output excluded.</summary>
    public static IReadOnlyList<string> SourceFiles { get; } = Directory
        .EnumerateFiles(Source, "*.cs", SearchOption.AllDirectories)
        .Where(NotBuildOutput)
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Source outside the test project. The checks that assert a property of the shipped
    /// code use this: the test project references everything by design and asserts things
    /// the shipped code is not allowed to do.
    /// </summary>
    public static IReadOnlyList<string> ProductionSourceFiles { get; } = SourceFiles
        .Where(f => !f.Replace(Path.DirectorySeparatorChar, '/').Contains("/PullbackStrategyLab.Tests/", StringComparison.Ordinal))
        .ToArray();

    public static string Read(string file) => File.ReadAllText(file);

    /// <summary>A path as it reads in a failure message: relative to the repository, forward slashes.</summary>
    public static string Relative(string path) =>
        Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool NotBuildOutput(string file)
    {
        string normalised = file.Replace(Path.DirectorySeparatorChar, '/');
        return !normalised.Contains("/bin/", StringComparison.Ordinal)
            && !normalised.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFileName} above {AppContext.BaseDirectory}. The checks read the corpus from " +
            "the repository, so they cannot run from a published output that does not sit inside it.");
    }
}
