using System.Diagnostics;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// <c>tools/nightly-window.ps1</c> run through the real interpreter against a probe dispatcher, from
/// 7.17, so what the window does is observed rather than read off its text.
///
/// <b>The probe stands in for <c>tools/nightly.ps1</c> and for nothing else.</b> It records the slot
/// it was asked for, writes a line to stderr, and exits with the code the test chose for that slot.
/// Everything about how one slot runs is <c>nightly.ps1</c>'s and is not under test here; what is, is
/// the order, that a failure does not stop the window, that a child's stderr does not either, and the
/// code the window hands the scheduler.
///
/// Windows only, because it runs <c>powershell.exe</c> and the window exists for Task Scheduler alone.
/// Returning rather than skipping, as the entry-point tests do, and the return is a fact about the
/// runner rather than about the property.
/// </summary>
public sealed class NightWindowScriptTests
{
    [Fact]
    public void A_window_runs_every_slot_in_order_through_a_failure_and_exits_with_the_first_code_that_was_not_nought()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string work = Directory.CreateTempSubdirectory("night-window").FullName;

        try
        {
            string calls = Path.Combine(work, "calls.txt");
            string probe = Path.Combine(work, "probe.ps1");

            // bars refuses the way the tree guard does, detect fails the way a stage does, and every
            // slot writes to stderr, which is what stopped nightly.ps1's own log in 2026-08 under Stop.
            File.WriteAllText(probe, $$"""
                param([string]$Slot)
                Add-Content -Path '{{calls}}' -Value $Slot
                [Console]::Error.WriteLine("probe stderr for $Slot")
                switch ($Slot) { 'bars' { exit 4 } 'detect' { exit 1 } default { exit 0 } }
                """);

            (int code, string output) = Run("evening", probe, Path.Combine(work, "logs"));

            NightWindow evening = NightlySchedule.Windows.Single(w => w.Window == "evening");

            Assert.Equal(evening.Slots, File.ReadAllLines(calls));
            Assert.Equal(4, code);

            string log = File.ReadAllText(Directory.GetFiles(Path.Combine(work, "logs")).Single());
            Assert.Contains("window evening: bars exited 4", log, StringComparison.Ordinal);
            Assert.Contains("window evening: detect exited 1", log, StringComparison.Ordinal);
            Assert.Contains($"window evening ends, 2 of {evening.Slots.Count} slot(s) exited other than nought", log, StringComparison.Ordinal);
            Assert.Contains("nightly-window: shell", output, StringComparison.Ordinal);

            // A window line is never mistaken for a slot's own start or finish by the two readers of the log.
            Assert.DoesNotMatch(@"slot \S+ starting,|slot \S+ clean$", log);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void A_window_whose_slots_all_ran_clean_exits_nought()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string work = Directory.CreateTempSubdirectory("night-window-clean").FullName;

        try
        {
            string probe = Path.Combine(work, "probe.ps1");
            File.WriteAllText(probe, "param([string]$Slot)\nexit 0\n");

            (int code, _) = Run("weekly", probe, Path.Combine(work, "logs"));

            Assert.Equal(0, code);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static (int Code, string Output) Run(string window, string dispatcher, string logs)
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
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(RepositoryLayout.Root, "tools", "nightly-window.ps1"));
        start.ArgumentList.Add("-Window");
        start.ArgumentList.Add(window);
        start.ArgumentList.Add("-Dispatcher");
        start.ArgumentList.Add(dispatcher);
        start.ArgumentList.Add("-LogDirectory");
        start.ArgumentList.Add(logs);

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
