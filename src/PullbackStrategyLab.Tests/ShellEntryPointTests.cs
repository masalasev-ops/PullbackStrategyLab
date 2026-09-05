using System.Diagnostics;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The bash cells of CLAUDE.md's Commands table, invoked the way an operator on Windows invokes
/// them.
///
/// <b>A gate can return success without executing, and this is the whole of that mechanism.</b>
/// `tools/migrate`, `tools/snapshot-db` and `tools/verify-phase` are bash scripts with no
/// extension. Called by name from a PowerShell session PowerShell will not execute them: the call
/// writes nothing, leaves <c>$LASTEXITCODE</c> unset and leaves <c>$?</c> true, so a command that
/// never ran is indistinguishable from one that passed. RUNBOOK step 6 and the stale-store recovery
/// both instruct the operator to run `tools/migrate` by name.
///
/// 3.14 repaired this for `verify-phase` and for `verify-phase` alone. The other two kept cells
/// reading "same", which is true of the file and false of the shell, and 6.10 is where they get the
/// same wrapper. The assertions here are written over all three rather than over the one, because
/// the fault was never about which script it happened to be found in.
///
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class ShellEntryPointTests
{
    /// <summary>Each wrapper, and the one script it must hand the work to.</summary>
    public static TheoryData<string, string> Wrappers => new()
    {
        { "migrate.ps1", "tools/migrate" },
        { "snapshot-db.ps1", "tools/snapshot-db" },
        { "verify-phase.ps1", "tools/verify-phase" },
    };

    /// <summary>
    /// A wrapper hands the work to its one script rather than reimplementing it.
    ///
    /// Two implementations of a command an operator runs would drift, and the one that drifted
    /// would be the one somebody ran. Asserted as a file rather than by running, because running
    /// `migrate` writes to a store and running `verify-phase` runs the whole gate.
    /// </summary>
    [Theory]
    [MemberData(nameof(Wrappers))]
    public void A_wrapper_defers_to_the_one_script_rather_than_reimplementing_it(string wrapper, string script)
    {
        string path = Path.Combine(RepositoryLayout.Root, "tools", wrapper);
        Assert.True(File.Exists(path), $"{path} does not exist.");

        string text = File.ReadAllText(path);

        Assert.Contains($"-Script '{script}'", text, StringComparison.Ordinal);
        Assert.Contains("shell-provenance.ps1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet run", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine with no usable bash is told so and exits 3, proved by running each wrapper with
    /// nowhere to find one.
    ///
    /// Three is kept apart from the scripts' own codes, so "no usable interpreter" is never read as
    /// "the command ran and said no". A wrapper that exited 1 here would report a failing gate,
    /// which is what the first version of the phase-gate wrapper did on the machine it was written
    /// for.
    ///
    /// Windows only, because it runs `powershell.exe` and the wrappers exist for Windows alone: the
    /// other machine runs the bash scripts directly. Returning rather than skipping, and the return
    /// is a fact about the runner rather than about the property.
    /// </summary>
    [Theory]
    [MemberData(nameof(Wrappers))]
    public void A_wrapper_with_no_usable_bash_refuses_with_exit_three_and_says_nothing_was_run(
        string wrapper, string script)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string empty = Directory.CreateTempSubdirectory("entry-point-nobash").FullName;

        try
        {
            var start = PowerShell(Path.Combine(RepositoryLayout.Root, "tools", wrapper));

            // The one candidate, and it is not a bash. Emptying PATH and the fallbacks was tried
            // first and does not isolate the search: a child PowerShell recovers ProgramFiles
            // whatever the parent sets, so Git for Windows is found and the command runs.
            start.Environment["PullbackStrategyLab__Bash"] = Path.Combine(empty, "not-a-bash.exe");

            using Process process = Process.Start(start)!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            Assert.Equal(3, process.ExitCode);
            Assert.Contains("no bash found", error, StringComparison.Ordinal);
            Assert.Contains("nothing was run", error, StringComparison.Ordinal);
            Assert.Contains(script, error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// A wrapper that finds a bash passes the script's output through and exits with the script's
    /// own code.
    ///
    /// <b>This is the test the first version of the wrapper would have failed, and it failed it for
    /// the same reason the whole part exists.</b> `Invoke-BashEntryPoint` returned
    /// <c>$LASTEXITCODE</c> and the wrappers wrote <c>exit (Invoke-BashEntryPoint ...)</c>. A
    /// PowerShell function's return value is its output stream, so everything the bash script wrote
    /// to standard output was consumed into that expression: the wrapper printed its own two lines,
    /// showed nothing of the script's, and exited 0. A wrapper written to remove a silent no-op,
    /// reproducing one a layer up. It was caught by running it and this is what keeps it caught.
    ///
    /// Run against a probe script rather than against `migrate` or the gate, because those write to
    /// a store and run the whole suite. The probe is a real bash script under a real path, invoked
    /// through the real function, which is every part of the mechanism except which script it is.
    /// </summary>
    [Fact]
    public void A_wrapper_that_finds_a_bash_passes_the_output_through_and_returns_the_scripts_code()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Under artifacts/, which is gitignored and is where every generated file in this
        // repository already goes. The probe's path has to be relative to the repository root,
        // because that is what the bash search asks a candidate to prove it can read.
        string artifacts = Path.Combine(RepositoryLayout.Root, "artifacts");
        Directory.CreateDirectory(artifacts);
        string probe = Path.Combine(artifacts, "wrapper-probe.sh");

        // The probe takes the bash half of the provenance helper as well as printing and exiting,
        // so one invocation carries every part of the mechanism: the wrapper announces its own
        // shell, finds a bash, hands over, the script announces the shell it is running under, its
        // output reaches the reader and its exit code comes back.
        File.WriteAllText(
            probe,
            "#!/usr/bin/env bash\n"
            + ". \"$(dirname \"${BASH_SOURCE[0]}\")/../tools/shell-provenance.sh\"\n"
            + "shell_provenance probe\n"
            + "printf 'the probe ran and said this\\n'\n"
            + "exit 7\n");

        try
        {
            var start = PowerShell("-");
            start.ArgumentList.Clear();
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                ". ./tools/shell-provenance.ps1; "
                + "Invoke-BashEntryPoint -Name probe -RepositoryRoot (Get-Location).Path "
                + "-Script 'artifacts/wrapper-probe.sh'; "
                + "exit $script:BashEntryExitCode");

            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(120_000);

            // The output reaching a reader is the half that was lost, and the exit code being the
            // script's own is the half that made the loss silent.
            Assert.Contains("the probe ran and said this", output, StringComparison.Ordinal);
            Assert.Contains("probe: using", output, StringComparison.Ordinal);
            Assert.Equal(7, process.ExitCode);

            // And a green states what produced it, on both sides of the hand-over: the wrapper's
            // own shell and the shell the script ran under are different answers and a transcript
            // that carries one of them cannot be read for the other. The machine is on both.
            Assert.Contains("probe: shell bash", output, StringComparison.Ordinal);
            Assert.Contains(Environment.MachineName, output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static ProcessStartInfo PowerShell(string file)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = RepositoryLayout.Root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(file);

        return start;
    }
}
