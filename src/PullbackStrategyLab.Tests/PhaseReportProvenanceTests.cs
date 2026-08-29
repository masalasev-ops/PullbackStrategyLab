using System.Diagnostics;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The phase report saying which tree produced it, and refusing to be written when it cannot.
///
/// <b>What it is for.</b> `tools/verify-phase` is a bash script with no extension, `tools/ci.*`
/// never calls it, and until 3.12 `artifacts/phase-report.json` carried no commit and no run
/// instant of its own. Invoked from PowerShell on Windows the script does not execute, returns 0,
/// and leaves the previous run's artifacts in place reading as current. The script's own rm block
/// at the top is the guard for exactly that and it is inside the thing that did not run, so the
/// fix cannot live there. Every phase sign-off in this project quotes that artifact, and the 3.12
/// sign-off quoted an earlier run's before catching it.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class PhaseReportProvenanceTests
{
    private static PhaseReportStage.Report AReport() => new(
        Phase: 3,
        LastLanded: "3.12",
        GeneratedAt: "2026-08-29 06:27:13Z",
        Commit: PhaseReportStage.Unstamped,
        TreeClean: true,
        Green: true,
        Reasons: [],
        Claims: new PhaseReportStage.ClaimSummary(1, 1, 0, 0, 0),
        Expectations: new PhaseReportStage.FixtureSummary(1, 1, 0, 0, [], []),
        IndependentExpectations: 1,
        ExpectationsChangedSinceHead: "0",
        Inputs: null,
        Fixture: null,
        ClaimDetail: [],
        Coverage: []);

    [Fact]
    public void A_report_that_cannot_name_its_commit_is_not_written_at_all()
    {
        using var artifacts = new TemporaryDirectory();

        // A directory that is not a repository, so git answers non-zero and there is no sha.
        using var notARepository = new TemporaryDirectory();
        Assert.Null(PhaseReportStage.ReadHead(notARepository.Path));

        PhaseReportStage.Report? written = PhaseReportStage.WriteReport(
            AReport(), artifacts.Path, PhaseReportStage.ReadHead(notARepository.Path));

        // Neither file, rather than one of the two or a file with a placeholder in it. A report
        // that cannot say where it came from reads exactly like a current one, which is the whole
        // fault: the operator's next act is to quote it.
        Assert.Null(written);
        Assert.False(File.Exists(Path.Combine(artifacts.Path, "phase-report.json")));
        Assert.False(File.Exists(Path.Combine(artifacts.Path, "phase-report.html")));

        Assert.Contains("could not be read",
            PhaseReportStage.WhyTheReportCannotBeWritten(null)!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_written_from_the_repository_carries_the_commit_the_tree_state_and_the_instant()
    {
        using var artifacts = new TemporaryDirectory();

        PhaseReportStage.Head head = Assert.IsType<PhaseReportStage.Head>(
            PhaseReportStage.ReadHead(RepositoryLayout.Root));

        // The other direction, so "always refuses" is not what passes the test above.
        Assert.Null(PhaseReportStage.WhyTheReportCannotBeWritten(head));

        PhaseReportStage.Report written = Assert.IsType<PhaseReportStage.Report>(
            PhaseReportStage.WriteReport(AReport(), artifacts.Path, head));

        // Forty hex characters, and not the placeholder Assemble builds with. A report stamped
        // "unstamped" would be identifiable and useless, which is the failure mode with an extra
        // step in it.
        Assert.Equal(40, written.Commit.Length);
        Assert.All(written.Commit, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.NotEqual(PhaseReportStage.Unstamped, written.Commit);

        // All three on the page, near the verdict, because the page is what a person reads and the
        // JSON is what a build session reads.
        string page = File.ReadAllText(Path.Combine(artifacts.Path, "phase-report.html"));
        Assert.Contains(written.Commit, page, StringComparison.Ordinal);
        Assert.Contains(written.GeneratedAt, page, StringComparison.Ordinal);
        Assert.Contains("working tree", page, StringComparison.Ordinal);

        string json = File.ReadAllText(Path.Combine(artifacts.Path, "phase-report.json"));
        Assert.Contains(written.Commit, json, StringComparison.Ordinal);
        Assert.Contains("treeClean", json, StringComparison.Ordinal);
        Assert.Contains("generatedAt", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure rendering the page leaves both files exactly as the previous run left them.
    ///
    /// <b>The claim was in the doc comment before it was in the code.</b> "Writes both files, or
    /// writes neither" sat above a method that wrote the JSON and then rendered and wrote the page,
    /// so a throw in the render left a current JSON beside a stale page. That is the staleness the
    /// commit stamp was added to make visible, one file over: the JSON says which tree produced it
    /// and the page beside it says nothing, and the page is the half a person reads.
    ///
    /// Asserted by making the render throw rather than by reading the method, because the property
    /// is an ordering and a scan for the corrected shape passes against the broken one. Both files
    /// are seeded with text this run would replace, so "unchanged" is a comparison against a known
    /// value rather than an absence.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    [Fact]
    public void A_page_that_cannot_be_rendered_leaves_both_files_as_the_last_run_left_them()
    {
        using var artifacts = new TemporaryDirectory();

        string json = Path.Combine(artifacts.Path, "phase-report.json");
        string page = Path.Combine(artifacts.Path, "phase-report.html");

        const string Previous = "the previous run's file";
        File.WriteAllText(json, Previous);
        File.WriteAllText(page, Previous);

        PhaseReportStage.Head head = Assert.IsType<PhaseReportStage.Head>(
            PhaseReportStage.ReadHead(RepositoryLayout.Root));

        Assert.Throws<InvalidOperationException>(() => PhaseReportStage.WriteReport(
            AReport(), artifacts.Path, head,
            _ => throw new InvalidOperationException("the page could not be rendered")));

        Assert.Equal(Previous, File.ReadAllText(json));
        Assert.Equal(Previous, File.ReadAllText(page));

        // Nothing left half-written beside them either, so a later run cannot find a temporary
        // reading as a report.
        Assert.Empty(Directory.GetFiles(artifacts.Path, "*.writing"));

        // And the same call with a renderer that works replaces both, so what the assertions above
        // hold is the ordering rather than the write never happening.
        Assert.NotNull(PhaseReportStage.WriteReport(AReport(), artifacts.Path, head, _ => "<p>a page</p>"));
        Assert.NotEqual(Previous, File.ReadAllText(json));
        Assert.Equal("<p>a page</p>", File.ReadAllText(page));
    }

    /// <summary>
    /// The wrapper that stops the Windows invocation no-opping, asserted as a file rather than run.
    ///
    /// Running the whole of it would mean running the whole gate, which is the one thing a unit test
    /// must not do. What is asserted here is what the wrapper is for: that it exists, and that it
    /// hands the work to the one bash script rather than reimplementing it. The refusal itself is
    /// asserted by running it, in the test below, because a string in a line that never executes is
    /// what the previous version of this test read.
    /// </summary>
    [Fact]
    public void The_windows_wrapper_defers_to_the_one_script()
    {
        string wrapper = Path.Combine(RepositoryLayout.Root, "tools", "verify-phase.ps1");
        Assert.True(File.Exists(wrapper), $"{wrapper} does not exist.");

        string text = File.ReadAllText(wrapper);

        // It runs the script rather than being a second implementation of it. Two implementations
        // of the gate a phase signs off against is the defect one level up.
        Assert.Contains("'tools/verify-phase'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A machine with no usable bash is told so and exits 3, proved by running the wrapper with
    /// nowhere to find one.
    ///
    /// <b>The string scan this replaces passed against a wrapper whose refusal was unreachable.</b>
    /// The file sets a Stop preference and the branch called <c>Write-Error</c>, which under Stop is
    /// terminating, so the <c>exit 3</c> beneath it never ran and the process exited 1: the code a
    /// red phase report exits with, from the branch that handles there being no gate to run at all.
    /// The old test asserted the literal "exit 3" appeared in the text, which it did.
    ///
    /// <b>And the wrapper it now runs is one that rejects a bash it cannot use.</b> On a stock
    /// Windows 11 <c>Get-Command bash</c> answers with the WSL launcher in System32, ahead of Git
    /// for Windows on the path. That is not a bash for this tree, and the fallback list naming Git
    /// for Windows was never reached because the lookup had already succeeded.
    ///
    /// Windows only, because it runs `powershell.exe`, and the wrapper exists for Windows alone:
    /// the other machine runs `tools/verify-phase` directly. Skipped rather than absent, and the
    /// skip is a fact about the runner rather than about the property.
    /// </summary>
    [Fact]
    public void The_windows_wrapper_refuses_with_exit_three_when_no_bash_can_be_found()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string empty = Directory.CreateTempSubdirectory("verify-phase-nobash").FullName;

        try
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
            start.ArgumentList.Add(Path.Combine(RepositoryLayout.Root, "tools", "verify-phase.ps1"));

            // The one candidate, and it is not a bash. Emptying PATH and the three fallbacks was
            // tried first and does not isolate the search: a child PowerShell recovers
            // ProgramFiles whatever the parent sets, so Git for Windows was found and the gate ran.
            start.Environment["PullbackStrategyLab__Bash"] = Path.Combine(empty, "not-a-bash.exe");

            using Process process = Process.Start(start)!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);

            Assert.Equal(3, process.ExitCode);
            Assert.Contains("no bash found", error, StringComparison.Ordinal);
            Assert.Contains("nothing was run", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
