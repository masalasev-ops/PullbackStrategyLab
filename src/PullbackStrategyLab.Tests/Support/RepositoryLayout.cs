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

    /// <summary>The golden fixture's captured inputs, and the manifest saying where each came from.</summary>
    public static string Fixtures => Path.Combine(Root, "fixtures", "captured");

    /// <summary>
    /// The five specs and the three records. Everything a citation can live in.
    ///
    /// The artefact was the ninth and is gone: <c>SCREENS.html</c> was deleted at 4.12, once the
    /// pages it drew existed (see: The corpus is eight documents and a ninth requires retiring one).
    /// </summary>
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

    /// <summary>
    /// Every tracked text file in the repository, which is what the citation scan reads.
    ///
    /// <b>Derived rather than named, because the named version was a one-way door.</b> The scan
    /// read <see cref="CorpusFiles"/> and <see cref="SourceFiles"/> and nothing asked which other
    /// files carry a citation. The 3.7 sign-off found two that did and recorded two; the sweep at
    /// 3.8 found <b>twenty</b>, including ten migrations and six files under the web project. The
    /// undercount was the same shape as the gap: a hand-named list read in one direction reports the
    /// instances somebody happened to look at.
    ///
    /// Read from the git index rather than from the filesystem, so an untracked scratch file cannot
    /// add a citation and a tracked one cannot hide from the walk. Binary paths are excluded by
    /// extension and the exclusions are listed rather than inferred.
    /// </summary>
    public static IReadOnlyList<string> TrackedTextFiles { get; } = ReadTrackedTextFiles();


    private static IReadOnlyList<string> ReadTrackedTextFiles()
    {
        // Local rather than a static field. Static initialisers run in declaration order, so a set
        // declared after the property that reads it initialises after it and the walk sees null,
        // which is how this first ran. Listed rather than inferred: a file the repository holds is
        // scanned unless its extension says it is not text.
        HashSet<string> binary = new(StringComparer.OrdinalIgnoreCase)
        {
            ".db", ".parquet", ".png", ".jpg", ".jpeg", ".ico", ".woff", ".woff2",
        };

        var git = new System.Diagnostics.ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(git)
            ?? throw new InvalidOperationException("git could not be started, so the citation scan has no file list.");

        string listing = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git ls-files exited {process.ExitCode}. The citation scan reads the index rather than the "
                + "filesystem, so it has nothing to read.");
        }

        return
        [
            .. listing.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !binary.Contains(Path.GetExtension(p)))
                .Select(p => Path.Combine(Root, p.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .OrderBy(f => f, StringComparer.Ordinal),
        ];
    }

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
