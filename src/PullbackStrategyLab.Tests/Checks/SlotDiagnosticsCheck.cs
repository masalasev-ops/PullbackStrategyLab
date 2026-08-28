using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The slot script keeps what a stage says on stderr, and keeps the stage's own exit code.
///
/// <b>The defect this exists for lost every stage's diagnostic, not one stage's.</b>
/// <c>tools/nightly.ps1</c> set <c>$ErrorActionPreference = 'Stop'</c> and piped a native command
/// through <c>2>&amp;1</c>. Windows PowerShell wraps each line a native command writes to stderr in
/// a NativeCommandError record, and under Stop the first one is terminating: the pipeline died
/// before the line that writes to the log ran, the slot unwound with no line saying it had stopped,
/// and PowerShell's own exit code of 1 replaced the stage's. The application was writing its message
/// correctly the whole time and this script was discarding it.
///
/// It was found the only way it could be, which is by reading the code after a stage spent 149
/// vendor calls on 2026-08-27 and left a log ending mid-slot. Nothing failed. That is the shape the
/// corpus keeps meeting from a new direction: the instrument upstream was correct, and its answer
/// was discarded by the layer that exists to carry it.
///
/// <b>Two halves, because neither is sufficient.</b> The scan below reads the script and asserts
/// that no stage is invoked outside the isolating function, which is what a later edit would undo.
/// A source scan cannot say the interpreter behaves as the fix assumes, so the behaviour is
/// exercised by the <c>slot-diagnostics</c> job: it migrates a store, runs a real slot with no
/// vendor token so a real stage throws for a real reason, and requires the stage's message and the
/// stop line to both be in the log.
///
/// <b>The job runs Windows PowerShell rather than pwsh, and that is not incidental.</b> PowerShell
/// 7 does not wrap native stderr this way, so a job written with <c>shell: pwsh</c> would pass on an
/// interpreter the scheduler never runs. The registered tasks execute <c>powershell.exe</c>, so the
/// job does too.
/// </summary>
public sealed partial class SlotDiagnosticsCheck
{
    private readonly ITestOutputHelper _output;

    public SlotDiagnosticsCheck(ITestOutputHelper output) => _output = output;

    /// <summary>The verbs each slot runs, as the hashtable declares them. One quoted verb per match.</summary>
    [GeneratedRegex(@"@\('(?<verb>[a-z-]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex SlotVerb();

    /// <summary>Any invocation of the worker as a native command, wherever it sits in the file.</summary>
    [GeneratedRegex(@"&\s+dotnet\s+run\s+--project", RegexOptions.CultureInvariant)]
    private static partial Regex WorkerInvocation();

    [GeneratedRegex(
        @"function\s+Invoke-Stage\s*\{(?<body>.*?)\r?\n\}",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InvokeStage();

    [Fact]
    [Trait("check", "slot-diagnostics")]
    public void The_slot_script_logs_a_failing_stage_message_and_its_exit_code()
    {
        var coverage = new CheckCoverage("slot-diagnostics", _output);

        string script = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "nightly.ps1"));

        Match function = InvokeStage().Match(script);
        string body = function.Success ? function.Groups["body"].Value : string.Empty;

        string[] verbs = [.. SlotVerb().Matches(script).Select(m => m.Groups["verb"].Value)];
        int invocations = WorkerInvocation().Matches(script).Count;
        int inside = function.Success ? WorkerInvocation().Matches(body).Count : 0;

        coverage
            .Examined("slot verbs routed through the isolating function", verbs.Length)
            .Examined("worker invocations in tools/nightly.ps1", invocations)
            .Scan(
                "no stage is invoked outside Invoke-Stage, and that function sets its own error preference",
                CheckCoverage.Backing.Runner(
                    "slot-diagnostics",
                    "whether Windows PowerShell raises on a native command's stderr is a property of the "
                    + "interpreter, and no assertion about the text of the script can establish it. The job "
                    + "runs a real stage that throws for a real reason and reads the log it left"))
            .Report();

        Assert.True(
            verbs.Length >= 20,
            $"tools/nightly.ps1 declares {verbs.Length} slot verbs. It has held at least twenty-one since the "
            + "schedule was registered, so the parser stopped matching.");

        Assert.True(
            function.Success,
            "tools/nightly.ps1 has no Invoke-Stage function. Every stage is invoked through it so that the "
            + "error preference lifted for a native command's stderr is lifted for that call and nowhere else.");

        Assert.True(
            invocations == 1 && inside == 1,
            $"tools/nightly.ps1 invokes the worker {invocations} time(s), {inside} of them inside Invoke-Stage. "
            + "A stage invoked outside it runs under the script's own Stop preference, where the first line the "
            + "stage writes to stderr is a terminating error: the message never reaches the log and the slot "
            + "stops without saying so.");

        Assert.Contains("$ErrorActionPreference = 'Continue'", body, StringComparison.Ordinal);

        Assert.Contains("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "$ErrorActionPreference = 'Continue'",
            script.Replace(body, string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);

        // The stop line is the other half of what the defect lost. A slot that stops with the message
        // logged and no line saying it stopped still reads as a slot that ran out of work to do.
        Assert.Contains("exited {1}; slot {2} stops here", script, StringComparison.Ordinal);
    }
}
