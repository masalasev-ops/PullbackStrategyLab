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
///
/// <b>And in both directions from 4.5, because it held one and the missing half shipped a stage.</b>
/// PlanBuilder landed at 4.16 registered with the container, dispatched by name, scheduled in the
/// runbook and absent from <c>Program.StageNames</c>, so <c>list-stages</c> and the usage text both
/// under-reported by one and nothing failed. This test could not see it, because a stage that is
/// dispatched and not advertised is invisible to a sweep that starts from what is advertised.
/// <c>architecture-conformance</c> could not either, because it adds the stage list to the set of
/// things that count as registered rather than reconciling it. It is the roster failure this
/// corpus's own Checks section is about, one level down from a check: a hand-kept list compared in
/// one direction reports nothing when it is the list that is short.
/// </summary>
public sealed partial class ComponentReachabilityTests
{
    /// <summary>The separator each failure message lists its offenders on, one per line.</summary>
    private const string Newline = "\n  ";

    [Fact]
    public void Every_stage_the_entry_point_advertises_has_an_arm_in_the_dispatch()
    {
        string program = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Program.cs"));

        HashSet<string> dispatched = DispatchedNames(program);

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
    /// Every stage the dispatch can reach is advertised by `list-stages`.
    ///
    /// The other direction, and the one that was missing. A dispatched stage the roster does not
    /// carry runs perfectly well when the scheduler names it and does not exist for anybody reading
    /// the worker's own list of what it does, which is how a shipped stage went a checkpoint without
    /// a slot: the runbook said 18:30 and the operator's list of stages did not mention it.
    ///
    /// The two exemptions are named rather than filtered by shape. <c>list-stages</c> is the command
    /// that prints the roster and is not on it, and a fixture capture verb is a build tool rather
    /// than a stage of the night; both are asserted to be dispatched, so an exemption that stops
    /// existing fails here rather than quietly widening.
    /// </summary>
    [Fact]
    public void Every_stage_the_dispatch_reaches_is_advertised_by_the_entry_point()
    {
        string program = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Program.cs"));

        HashSet<string> dispatched = DispatchedNames(program);

        // Stated in advance, on the same grounds the sweep above states its own floor: a pattern that
        // stopped matching would hand this an empty set and every stage would read as advertised.
        Assert.True(dispatched.Count >= 15,
            $"Only {dispatched.Count} dispatch arm(s) resolved out of Program.cs, so this compared "
            + "almost nothing rather than finding almost nothing.");

        string[] notOnTheRoster = ["list-stages"];

        foreach (string exempt in notOnTheRoster)
        {
            Assert.Contains(exempt, dispatched);
        }

        var unadvertised = dispatched
            .Where(name => !Program.StageNames.Contains(name, StringComparer.Ordinal))
            .Where(name => !notOnTheRoster.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unadvertised.Count == 0,
            $"{unadvertised.Count} stage(s) have an arm in the dispatch and are not advertised by "
            + "`list-stages`, so the worker's own roster of what it does is short by that many:" + Newline
            + string.Join(Newline, unadvertised));
    }

    /// <summary>
    /// The arms of the switch, resolved to the names they actually match on.
    ///
    /// The dispatch says <c>MigrateStage.Name</c>, which is a reference rather than a value, so the
    /// constant is read off the type through reflection: comparing the source text would compare a
    /// reference to a value and report every stage unreachable, which is a check that fails for the
    /// wrong reason. One reading feeds both directions, so the two can never be looking at different
    /// sets of arms.
    /// </summary>
    private static HashSet<string> DispatchedNames(string program)
    {
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

        return dispatched;
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
