using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// tools/ci.ps1 and tools/ci.sh run the same steps in the same order.
///
/// The two files are not translations of each other and cannot be: `&amp;&amp;` is a parse error
/// in Windows PowerShell, so the syntax differs by necessity. What has to hold is that the
/// same work happens in the same order, so the check reads the step names each file declares
/// and compares those, not the text around them.
///
/// A step added to one file and not the other is the failure this exists to catch, and it is
/// invisible until somebody runs the other platform.
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

        coverage
            .Examined("steps declared in tools/ci.ps1", windows.Length)
            .Examined("steps declared in tools/ci.sh", unix.Length)
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
    }
}
