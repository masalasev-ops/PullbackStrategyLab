using System.Diagnostics;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The shell entry points are recorded executable.
///
/// Another property neither development machine can see, and one that cost a red macOS runner
/// to find. Windows has no executable bit, so a script committed from there is recorded
/// 100644 and is unrunnable the first time anything invokes it directly on macOS or on a
/// runner, while working perfectly on the machine that wrote it.
///
/// Asserted against the recorded mode rather than against the working tree, because the
/// working tree's mode is exactly the thing Windows does not have.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public sealed class ShellExecutableCheck
{
    private readonly ITestOutputHelper _output;

    public ShellExecutableCheck(ITestOutputHelper output) => _output = output;

    private static readonly string[] EntryPoints =
        ["tools/ci.sh", "tools/migrate", "tools/snapshot-db", "tools/verify-phase"];

    /// <summary>
    /// Files under `tools/` that carry a shebang and are deliberately not entry points, with the
    /// reason each is not one.
    ///
    /// <b>Named so the other direction has somewhere to put an answer.</b> The check read the four
    /// above and never asked `tools/` which files claim to be executable, so a fifth could arrive
    /// carrying a shebang and mode 100644 and nothing would say so. That is what `derive-indicators.py`
    /// already did.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotEntryPoints { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tools/derive-indicators.py"] =
                "a one-time verification aid, run by hand by a person who has python and is not "
                + "invoked by CI, by the nightly job or by any script. CLAUDE.md's repository layout "
                + "says so on its own line",
            ["tools/derive-authored-parameters.py"] =
                "the independent restatement of the authored-parameters figures, run by hand when "
                + "those figures are re-derived. Nothing invokes it either",
        };

    private const string ExecutableMode = "100755";

    [Fact]
    [Trait("check", "shell-executable")]
    public void Every_shell_entry_point_is_recorded_executable()
    {
        var coverage = new CheckCoverage("shell-executable", _output);
        string? index = RecordedModes();

        if (index is null)
        {
            // Reported rather than skipped. A check that could not run has not passed, and a
            // skip that reads as a pass is the failure mode the coverage line exists to catch.
            coverage.NotExamined("shell entry points", EntryPoints.Length,
                "the recorded file mode could not be read, so nothing here was asserted");
            coverage.Report();
            return;
        }

        string[] lines = index.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var wrong = new List<string>();

        foreach (string entryPoint in EntryPoints)
        {
            string? recorded = lines.FirstOrDefault(l => l.TrimEnd().EndsWith(entryPoint, StringComparison.Ordinal));

            if (recorded is null)
            {
                wrong.Add($"{entryPoint} is not tracked at all.");
            }
            else if (!recorded.StartsWith(ExecutableMode, StringComparison.Ordinal))
            {
                wrong.Add($"{entryPoint} is recorded {recorded.Split(' ')[0]} rather than {ExecutableMode}.");
            }
        }

        // The other direction: which files under tools/ claim to be executable and are on neither
        // list. A check reading a hand-named list in one direction only reports nothing when it is
        // the list that is short, and a shebang is a file saying outright that it expects to be run.
        string[] claiming =
        [
            .. Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "tools"))
                .Where(HasAShebang)
                .Select(f => "tools/" + Path.GetFileName(f))
                .Order(StringComparer.Ordinal),
        ];

        string[] unplaced =
        [
            .. claiming
                .Where(f => !EntryPoints.Contains(f, StringComparer.Ordinal))
                .Where(f => !NotEntryPoints.ContainsKey(f)),
        ];

        coverage.Examined("shell entry points checked against their recorded mode", EntryPoints.Length);
        coverage.Examined("files under tools that carry a shebang", claiming.Length);
        coverage.OutOfScope(
            "of those, the ones that are deliberately not entry points",
            NotEntryPoints.Count,
            CheckCoverage.OutOfScopeReason.ByDesign(
                "each is a hand-run aid that nothing invokes, named with the reason: "
                + string.Join("; ", NotEntryPoints.Select(e => $"{e.Key}, {e.Value}"))));
        coverage.NoSourceScan(
            "the mode is read from the git index, which is the thing that travels to a runner. It is the state "
            + "itself rather than a description of it, and Windows cannot see it any other way");
        coverage.Report();

        Assert.True(unplaced.Length == 0,
            "These files under tools/ carry a shebang and are on neither list, so nothing says whether "
            + "they are entry points and nothing asserts their recorded mode: "
            + string.Join(", ", unplaced)
            + ". Add each to EntryPoints, or to NotEntryPoints with why it is not one.");

        Assert.True(wrong.Count == 0,
            $"{wrong.Count} shell entry point(s) are not recorded executable:\n  "
            + string.Join("\n  ", wrong)
            + "\n  Windows cannot see this and macOS cannot run it. Fix with:"
            + "\n  git update-index --chmod=+x " + string.Join(" ", EntryPoints));
    }

    /// <summary>
    /// Whether a file opens with a shebang, which is a file saying outright that it expects to be
    /// executed.
    ///
    /// Read as bytes rather than as text, because a file this check has never seen may be anything
    /// and a decoder given a binary is a decoder that throws in the middle of a check about modes.
    /// </summary>
    private static bool HasAShebang(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);

            return file.ReadByte() == '#' && file.ReadByte() == '!';
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The recorded mode of every tracked file under tools. Read through git, which is present
    /// wherever this repository is and is the same program on both platforms. Returns null
    /// rather than throwing if it cannot run, so the check reports itself unexamined instead of
    /// failing for a reason that is not the property.
    /// </summary>
    private static string? RecordedModes()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = RepositoryLayout.Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("ls-files");
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add("tools");

            using Process? git = Process.Start(start);
            if (git is null)
            {
                return null;
            }

            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit(milliseconds: 15_000);

            return git.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
