using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Every stage the entry point advertises can actually be reached.
///
/// <b>What this backs, and why a scan could not.</b> `architecture-conformance` asserts that every
/// component in the catalogue exists and is registered, by scanning the shipped source for a
/// registration and reading the stage table. Its own coverage record says what that cannot catch: a
/// registration whose line does not match the pattern fails loudly, which is fine, but <b>one whose
/// registration is present and unreachable passes</b>. A stage listed in the table with no arm in
/// the dispatch is registered in every sense a scan can see and in none that matters, because the
/// scheduler invokes it by name and gets "Unknown stage".
///
/// So this reaches them. It is the fourth instance of the rule that an assertion must fail when the
/// thing it guards is removed: a source scan that finds a pattern is not evidence the behaviour
/// exists, and where both are cheap the scan reports coverage while a behavioural test carries the
/// claim.
///
/// <b>Reached rather than run.</b> Running every stage would need a store, a vendor and a clock, and
/// would be testing the stages rather than the wiring. What is asserted is that the dispatch has an
/// arm for each advertised name, which is exactly the gap between "registered" and "reachable".
/// </summary>
public sealed partial class ComponentReachabilityTests
{
    [Fact]
    public void Every_stage_the_entry_point_advertises_has_an_arm_in_the_dispatch()
    {
        string program = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Program.cs"));

        // The arms of the switch, resolved to the names they actually match on. The dispatch says
        // `MigrateStage.Name`, which is a reference rather than a value, so the constant is read off
        // the type through reflection: comparing the source text would compare a reference to a
        // value and report every stage unreachable, which is a check that fails for the wrong reason.
        var dispatched = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match arm in DispatchArm().Matches(program))
        {
            string? value = ConstantValue(arm.Groups["owner"].Value, arm.Groups["member"].Value);

            if (value is not null)
            {
                dispatched.Add(value);
            }
        }

        foreach (Match literal in LiteralArm().Matches(program))
        {
            dispatched.Add(literal.Groups["name"].Value);
        }

        // Stated in advance. A pattern that stopped matching would hand this an empty set, and every
        // stage would read as unreachable, which fails loudly rather than quietly. The guard is here
        // so the message says which fault it was.
        Assert.True(dispatched.Count >= 15,
            $"Only {dispatched.Count} dispatch arm(s) resolved out of Program.cs. A count this low "
            + "means the pattern or the reflection stopped matching rather than that the worker got "
            + "smaller.");

        var unreachable = Program.StageNames.Where(name => !dispatched.Contains(name)).ToList();

        Assert.True(unreachable.Count == 0,
            $"{unreachable.Count} stage(s) are advertised by `list-stages` and have no arm in the "
            + "dispatch, so the scheduler invoking one by name gets \"Unknown stage\":\n  "
            + string.Join("\n  ", unreachable));
    }

    /// <summary>
    /// The value of a stage-name constant, read off the type rather than out of the source text.
    ///
    /// Null where the type or the member does not resolve, which is itself the failure this test is
    /// about: a dispatch arm naming a constant that no longer exists would not compile, and one
    /// naming a type outside this assembly is not a stage.
    /// </summary>
    private static string? ConstantValue(string owner, string member)
    {
        Type? type = typeof(Program).Assembly.GetTypes()
            .FirstOrDefault(t => string.Equals(t.Name, owner, StringComparison.Ordinal));

        return type?.GetField(member)?.GetValue(null) as string
            ?? type?.GetProperty(member)?.GetValue(null) as string;
    }

    /// <summary>
    /// An arm keyed on a constant, whatever the constant is called.
    ///
    /// It named the two it knew about, <c>Name</c> and <c>BackfillName</c>, until a third arrived:
    /// <c>FixtureCapture.CaptureResponseName</c> was advertised by <c>list-stages</c>, dispatched two
    /// lines away, and read as unreachable because the pattern had a list rather than a shape. Any
    /// member ending in Name now matches, and the value is still read off the type by reflection.
    /// </summary>
    [GeneratedRegex(@"^\s*(?<owner>[A-Za-z_][A-Za-z0-9_]*)\.(?<member>[A-Za-z0-9_]*Name)\s*=>", RegexOptions.Multiline)]
    private static partial Regex DispatchArm();

    [GeneratedRegex(@"^\s*""(?<name>[a-z-]+)""\s*=>", RegexOptions.Multiline)]
    private static partial Regex LiteralArm();
}
