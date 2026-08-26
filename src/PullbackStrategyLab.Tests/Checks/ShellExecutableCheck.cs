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

        coverage.Examined("shell entry points checked against their recorded mode", EntryPoints.Length);
        coverage.Report();

        Assert.True(wrong.Count == 0,
            $"{wrong.Count} shell entry point(s) are not recorded executable:\n  "
            + string.Join("\n  ", wrong)
            + "\n  Windows cannot see this and macOS cannot run it. Fix with:"
            + "\n  git update-index --chmod=+x " + string.Join(" ", EntryPoints));
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
