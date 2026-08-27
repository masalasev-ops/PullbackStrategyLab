using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every file path appearing as a string literal in source matches the on-disk path exactly,
/// byte for byte.
///
/// This targets a bug neither development machine can see. Case sensitivity is a property of
/// the filesystem rather than of the operating system: Windows and macOS are both insensitive
/// by default and Linux is not, so a path written with the wrong case works on both machines
/// and fails the first time it touches a Linux runner.
///
/// Exact match rather than lowercase, which is the stronger property. The lowercase half of
/// the rule governs what the application composes at runtime; .NET source and project
/// directories keep the framework's PascalCase.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public sealed partial class PathCasingCheck
{
    private readonly ITestOutputHelper _output;

    public PathCasingCheck(ITestOutputHelper output) => _output = output;

    /// <summary>A string literal, ordinary or verbatim, on one line. Raw string literals are read separately.</summary>
    [GeneratedRegex("(?<!@)\"(?<value>(?:[^\"\\\\\\r\\n]|\\\\.){1,200})\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedLiteral();

    [Fact]
    [Trait("check", "path-casing")]
    public void Every_path_literal_matches_the_on_disk_path_byte_for_byte()
    {
        var coverage = new CheckCoverage("path-casing", _output);
        var failures = new List<string>();
        int literals = 0;
        int candidates = 0;
        int verified = 0;

        foreach (string file in RepositoryLayout.SourceFiles)
        {
            // Comments blanked out first: a path named in a comment is prose, and a check that
            // fails on prose gets loosened the first time it does.
            string text = CSharpSource.WithoutComments(RepositoryLayout.Read(file));

            foreach (Match match in QuotedLiteral().Matches(text))
            {
                literals++;
                string literal = match.Groups["value"].Value;

                if (!LooksLikeAPath(literal))
                {
                    continue;
                }

                candidates++;

                string? actual = ResolveOnDisk(literal);
                if (actual is null)
                {
                    // Not a path into this repository. A URL route, a table name, a format string.
                    candidates--;
                    continue;
                }

                verified++;
                if (!string.Equals(actual, Normalise(literal), StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{RepositoryLayout.Relative(file)}: the literal \"{literal}\" resolves to \"{actual}\" on disk. "
                        + "Both work on Windows and macOS and neither works on Linux.");
                }
            }
        }

        coverage
            .Context("string literals read", literals)
            .Examined("literals naming a path into this repository", candidates)
            .Examined("paths compared against the on-disk name", verified)
            .Scan("every path literal in the source matches the on-disk name byte for byte",
                CheckCoverage.Backing.Runner(
                    "rehearsal",
                    "no test can hold this on either development machine, because case sensitivity is a property "
                    + "of the filesystem and both machines are insensitive by default. What exercises it is the "
                    + "rehearsal job opening every one of these files on ubuntu-latest on each push"));

        if (verified == 0)
        {
            // Stated rather than left as a silent pass. CLAUDE.md says to drop this check if it
            // has no work; it gains work at 1.7, when the golden fixture is read by path.
            coverage.NotExamined("paths compared against the on-disk name", 0,
                "no source file names a repository path yet; the golden fixture at 1.7 is read by path and gives this work");
        }

        coverage.Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} path literal(s) do not match the on-disk path:\n  " + string.Join("\n  ", failures));

        Assert.True(literals > 0,
            "No string literals were read at all, which means the scanner stopped matching rather than that the source "
            + "stopped containing strings.");
    }

    private static bool LooksLikeAPath(string literal)
    {
        if (literal.Length == 0 || literal.Length > 200)
        {
            return false;
        }

        if (literal.Contains("://", StringComparison.Ordinal) || literal.Contains(' ', StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string Normalise(string literal) =>
        literal.Trim('/').Replace('\\', '/');

    /// <summary>
    /// Walks the literal segment by segment against the directory listing, so the comparison is
    /// against the name the filesystem actually holds. Comparing with File.Exists would answer
    /// on a case-insensitive filesystem and prove nothing.
    /// </summary>
    private static string? ResolveOnDisk(string literal)
    {
        string[] segments = Normalise(literal).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        string current = RepositoryLayout.Root;
        var resolved = new List<string>(segments.Length);

        foreach (string segment in segments)
        {
            string? entry = Directory
                .EnumerateFileSystemEntries(current)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return null;
            }

            resolved.Add(entry);
            current = Path.Combine(current, entry);

            if (!Directory.Exists(current))
            {
                // A file: nothing may follow it.
                return resolved.Count == segments.Length ? string.Join('/', resolved) : null;
            }
        }

        return string.Join('/', resolved);
    }
}
