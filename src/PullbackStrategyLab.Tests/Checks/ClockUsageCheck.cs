using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Nothing outside the clock implementation reads the machine clock directly.
///
/// The ban is on <c>DateTime.Now</c>, <c>DateTime.UtcNow</c> and <c>DateTimeOffset.UtcNow</c>,
/// with <c>DateTime.Today</c> and <c>DateTimeOffset.Now</c> banned alongside them because they
/// are the same mistake spelled differently.
///
/// Run as a named step of tools/ci.* rather than written twice in shell. A grep in PowerShell
/// and a grep in bash are different dialects over different escaping, and two spellings of one
/// property is exactly the kind of pair that drifts apart without either side failing.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public sealed class ClockUsageCheck
{
    private readonly ITestOutputHelper _output;

    public ClockUsageCheck(ITestOutputHelper output) => _output = output;

    /// <summary>The one file permitted to read the machine clock.</summary>
    private const string ClockImplementation = "src/PullbackStrategyLab.Core/Time/SystemClock.cs";

    [Fact]
    [Trait("check", "clock-usage")]
    public void Nothing_outside_the_clock_reads_the_machine_clock_directly()
    {
        var coverage = new CheckCoverage("clock-usage", _output);
        var offences = new List<string>();
        int filesScanned = 0;
        int allowedReads = 0;

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            filesScanned++;
            string relative = RepositoryLayout.Relative(file);

            foreach (ClockRead read in ClockReads.In(RepositoryLayout.Read(file)))
            {
                if (string.Equals(relative, ClockImplementation, StringComparison.Ordinal))
                {
                    allowedReads++;
                    continue;
                }

                offences.Add($"{relative}:{read.Line}  {read.Text}");
            }
        }

        coverage
            .Examined("shipped source files scanned", filesScanned)
            .Examined("direct clock reads inside the clock implementation", allowedReads)
            .Report();

        Assert.True(offences.Count == 0,
            $"{offences.Count} direct read(s) of the machine clock outside {ClockImplementation}:\n  "
            + string.Join("\n  ", offences)
            + "\n  Everything asks IClock, so a session boundary is resolved in one place and a test can move time.");

        // The clock implementation itself must still read the machine clock. If it stopped, the
        // scanner would find nothing anywhere and this check would pass over a clock that had
        // been quietly replaced by something else.
        Assert.True(allowedReads > 0,
            $"{ClockImplementation} contains no direct clock read at all, so either the scanner stopped matching or the "
            + "clock is no longer the thing that reads the machine clock.");

        Assert.True(filesScanned > 0, "No shipped source files were scanned.");
    }
}
