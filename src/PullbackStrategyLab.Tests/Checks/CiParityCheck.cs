using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// tools/ci.ps1 and tools/ci.sh run the same steps in the same order, and the shell entry
/// points are recorded executable.
///
/// The two files are not translations of each other and cannot be: <c>and-and</c> is a parse
/// error in Windows PowerShell, so the syntax differs by necessity. What has to hold is that
/// the same work happens in the same order, so the check reads the step names each file
/// declares and compares those, not the text around them.
///
/// A step added to one file and not the other is the failure this exists to catch, and it is
/// invisible until somebody runs the other platform.
///
/// <b>It also asserts that a failing step fails the script it runs in, by running the shipped
/// function against a command that fails.</b> That half exists because the shell half did not
/// hold for the whole of phase 3 and nothing noticed. <c>if ! "$@"; then local code=$?</c>
/// captures the status of the negated pipeline, which is 0 exactly when the command failed, so
/// every one of the twenty-seven steps aborted the run and then reported success. The macOS half
/// of the matrix and the Linux rehearsal job both enter through <c>tools/ci.sh</c>, so neither
/// could report a failure, and the parity half above could not see it: the two scripts declare
/// identical step names in identical order and disagreed only in what they did with a non-zero
/// status.
///
/// Run rather than read, because a source scan for the corrected form would have passed against
/// the broken one. The broken form contained the word <c>exit</c>, the name of the variable, and
/// the printf naming the failure, and was wrong anyway.
/// see: A phase branch merges on CI green, and the sign-off reviews what is already on the default branch
/// </summary>
public sealed partial class CiParityCheck
{
    private readonly ITestOutputHelper _output;

    public CiParityCheck(ITestOutputHelper output) => _output = output;

    [GeneratedRegex(@"^Invoke-Step\s+'(?<name>[^']+)'", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellStep();

    [GeneratedRegex(@"^step\s+'(?<name>[^']+)'", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ShellStep();

    [Fact]
    [Trait("check", "ci-parity")]
    public void The_two_ci_scripts_run_the_same_steps_in_the_same_order()
    {
        var coverage = new CheckCoverage("ci-parity", _output);

        string[] windows = PowerShellStep()
            .Matches(RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.ps1")))
            .Select(m => m.Groups["name"].Value)
            .ToArray();

        string[] unix = ShellStep()
            .Matches(RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.sh")))
            .Select(m => m.Groups["name"].Value)
            .ToArray();

        Propagation propagation = FailingStepFailsTheRun(coverage);

        coverage
            .Examined("steps declared in tools/ci.ps1", windows.Length)
            .Examined("steps declared in tools/ci.sh", unix.Length)
            .Examined("ci step functions run against a step that fails", propagation.Exercised)
            .NoSourceScan(
                "the two scripts are the subject rather than a description of one. A step deleted from a script "
                + "is a step that stops running, so there is no gap between what the text says and what happens. "
                + "The propagation half runs the shipped function rather than scanning for its shape")
            .Report();

        Assert.True(windows.Length > 0, "tools/ci.ps1 declares no steps, so the parser stopped matching.");
        Assert.True(unix.Length > 0, "tools/ci.sh declares no steps, so the parser stopped matching.");

        Assert.True(
            windows.SequenceEqual(unix, StringComparer.Ordinal),
            "The two CI scripts do not run the same steps in the same order.\n"
            + $"  ci.ps1: {string.Join(", ", windows)}\n"
            + $"  ci.sh:  {string.Join(", ", unix)}");

        // Every check this build defines has a step. A check nobody runs is not a check.
        string[] checkSteps = windows.Where(s => s.StartsWith("check-", StringComparison.Ordinal)).ToArray();
        Assert.True(checkSteps.Length >= 8,
            $"Only {checkSteps.Length} checks are run as named steps. Each check is a step of its own so a failure "
            + "names the property that broke rather than the suite.");

        Assert.True(propagation.Exercised > 0,
            "Neither ci step function could be run, so nothing here asserted that a failing step fails the run. "
            + $"{propagation.Why}");

        Assert.True(propagation.Failures.Count == 0, string.Join("\n", propagation.Failures));
    }

    /// <summary>What running the shipped step functions against a failing command found.</summary>
    private sealed record Propagation(int Exercised, IReadOnlyList<string> Failures, string Why);

    /// <summary>
    /// The exit code a proof step fails with. Distinctive rather than 1, because the assertion is
    /// that the step's own code reaches the caller and not merely that something non-zero did.
    /// </summary>
    private const int ProofExitCode = 42;

    private static Propagation FailingStepFailsTheRun(CheckCoverage coverage)
    {
        using var temporary = new TemporaryDirectory();
        var failures = new List<string>();
        var unavailable = new List<string>();
        int exercised = 0;

        string shellFunction = Function(
            RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.sh")), "step() {", "tools/ci.sh");

        temporary.Write(
            "proof.sh",
            "#!/usr/bin/env bash\n"
            + "set -euo pipefail\n"
            + "step_number=0\n"
            + shellFunction
            + $"\nstep 'proof' bash -c 'exit {ProofExitCode}'\n"
            + "printf 'the step function let the run continue\\n'\n"
            + "exit 0\n");

        string? bash = Usable(BashCandidates(), "-c \"exit " + ProofExitCode + "\"");
        int? shell = bash is null ? null : Run(bash, Quote(temporary.File("proof.sh")), temporary.Path);

        if (shell is null)
        {
            unavailable.Add("no usable bash was found");
        }
        else
        {
            exercised++;

            if (shell != ProofExitCode)
            {
                failures.Add(
                    $"tools/ci.sh's step function exited {shell} for a step that exited {ProofExitCode}. "
                    + "A failing step must fail the script, with the step's own code, or every runner entering "
                    + "through ci.sh reports green on a red run.");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            string powerShellFunction = Function(
                RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.ps1")),
                "function Invoke-Step {",
                "tools/ci.ps1");

            temporary.Write(
                "proof.ps1",
                "$script:StepNumber = 0\n"
                + powerShellFunction
                + $"\nInvoke-Step -Name 'proof' -Body {{ & cmd /c exit {ProofExitCode} }}\n"
                + "Write-Host 'the step function let the run continue'\n"
                + "exit 0\n");

            string? interpreter = Usable(
                ["powershell", "pwsh"],
                $"-NoProfile -NonInteractive -Command \"exit {ProofExitCode}\"");

            int? powerShell = interpreter is null ? null : Run(
                interpreter,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(temporary.File("proof.ps1"))}",
                temporary.Path);

            if (powerShell is null)
            {
                unavailable.Add("no usable PowerShell was found");
            }
            else
            {
                exercised++;

                if (powerShell != ProofExitCode)
                {
                    failures.Add(
                        $"tools/ci.ps1's Invoke-Step exited {powerShell} for a step that exited {ProofExitCode}.");
                }
            }
        }
        else
        {
            coverage.OutOfScope(
                "the Windows CI entry point's step function", 1,
                CheckCoverage.OutOfScopeReason.ByDesign(
                    "tools/ci.ps1 is the entry point on Windows and only there, and Windows PowerShell is the "
                    + "interpreter it is written for. The matrix runs this same assertion on its windows runner"));
        }

        return new Propagation(exercised, failures, string.Join("; ", unavailable));
    }

    /// <summary>
    /// One function lifted out of a script by its opening line and the first line that closes it
    /// at column zero, which is how both scripts format their functions.
    ///
    /// It fails rather than returning empty when the opener has moved. A proof that silently reads
    /// nothing is the shape this whole check was rewritten to stop being.
    /// </summary>
    private static string Function(string source, string opener, string file)
    {
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int start = Array.FindIndex(lines, l => l.StartsWith(opener, StringComparison.Ordinal));
        Assert.True(start >= 0,
            $"{file} no longer has a line starting \"{opener}\", so the propagation proof would have read nothing.");

        int end = Array.FindIndex(lines, start + 1, l => l == "}");
        Assert.True(end > start,
            $"{file}'s \"{opener}\" is never closed by a brace at column zero, so the proof could not lift it.");

        return string.Join('\n', lines[start..(end + 1)]);
    }

    private static string Quote(string path) =>
        "\"" + path.Replace(Path.DirectorySeparatorChar, '/') + "\"";

    /// <summary>
    /// The first candidate that answers a command exiting with a known code by exiting with that
    /// same code.
    ///
    /// The probe is the point. On Windows, <c>bash</c> on PATH is often
    /// <c>C:\Windows\System32\bash.exe</c>, the WSL launcher, which with no distribution installed
    /// prints an advertisement to stdout and exits 1. Taking that as the shell would have made this
    /// proof report a failure it did not observe, and taking a non-zero exit as evidence of
    /// propagation would have made it pass on a stub that never ran the script at all. So the
    /// interpreter has to demonstrate it can carry an exit code before it is asked to carry one.
    /// </summary>
    private static string? Usable(IEnumerable<string> candidates, string probe)
    {
        foreach (string candidate in candidates)
        {
            if (Run(candidate, probe, Path.GetTempPath()) == ProofExitCode)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Where a real bash lives, PATH first and then the places Git for Windows puts one.
    /// </summary>
    private static IEnumerable<string> BashCandidates()
    {
        yield return "bash";

        if (OperatingSystem.IsWindows())
        {
            foreach (string variable in (string[])["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"])
            {
                string? root = Environment.GetEnvironmentVariable(variable);

                if (!string.IsNullOrEmpty(root))
                {
                    yield return Path.Combine(root, "Git", "bin", "bash.exe");
                    yield return Path.Combine(root, "Programs", "Git", "bin", "bash.exe");
                }
            }
        }
        else
        {
            yield return "/bin/bash";
            yield return "/usr/bin/bash";
        }
    }

    /// <summary>
    /// The interpreter's exit code, or null when the interpreter is not on PATH. Null is reported
    /// by the caller rather than treated as a pass, on the same grounds shell-executable reports a
    /// mode it could not read.
    /// </summary>
    private static int? Run(string interpreter, string arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(interpreter, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
            {
                return null;
            }

            // Drained before the wait, so a script that writes more than a pipe buffer cannot block.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            return process.WaitForExit(30_000) ? process.ExitCode : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
