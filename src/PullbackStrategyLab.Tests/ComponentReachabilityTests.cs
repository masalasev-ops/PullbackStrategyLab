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

    /// <summary>
    /// Every stage the entry point advertises has an arm in the dispatch.
    ///
    /// <b>The dispatch itself rather than the text of the file it lives in, from 4.17.</b> This read
    /// `Program.cs` and matched switch-arm shapes with a regex until then, and
    /// `architecture-conformance` named it as the behavioural backing for its own catalogue scan: a
    /// scan backed by a scan, and the run recorded it as backed. The dispatch is a table now, so a
    /// name can be resolved without being run and what is asserted is what comes back.
    /// </summary>
    [Fact]
    public void Every_stage_the_entry_point_advertises_has_an_arm_in_the_dispatch()
    {
        var unreachable = Program.StageNames.Where(name => Program.Arm(name) is null).ToList();

        Assert.True(unreachable.Count == 0,
            $"{unreachable.Count} stage(s) are advertised by `list-stages` and have no arm in the "
            + "dispatch, so the scheduler invoking one by name gets \"Unknown stage\":" + Newline
            + string.Join(Newline, unreachable));

        // Stated in advance, because every assertion here holds over an empty table.
        Assert.True(Program.Dispatch.Count >= 15,
            $"The dispatch holds {Program.Dispatch.Count} arm(s). A count this low means the table "
            + "stopped being populated rather than that the worker got smaller.");

        // And the other direction of the same question: a name this build has no stage for resolves
        // to nothing rather than to whichever arm a fall-through reached. The second is the same name
        // in the wrong case, because the lookup is ordinal and a scheduler that shouted would
        // otherwise be answered.
        Assert.Null(Program.Arm("not-a-stage-this-build-has"));
        Assert.Null(Program.Arm("Fills"));
    }

    /// <summary>
    /// Every stage the dispatch can reach is advertised by `list-stages`.
    ///
    /// The other direction, and the one that was missing. A dispatched stage the roster does not
    /// carry runs perfectly well when the scheduler names it and does not exist for anybody reading
    /// the worker's own list of what it does, which is how a shipped stage went a checkpoint without
    /// a slot: the runbook said 18:30 and the operator's list of stages did not mention it.
    ///
    /// The exemption is named rather than filtered by shape. <c>list-stages</c> is the command that
    /// prints the roster and is not on it, and it is asserted to be dispatched, so an exemption that
    /// stops existing fails here rather than quietly widening.
    /// </summary>
    [Fact]
    public void Every_stage_the_dispatch_reaches_is_advertised_by_the_entry_point()
    {
        string[] notOnTheRoster = ["list-stages"];

        foreach (string exempt in notOnTheRoster)
        {
            Assert.NotNull(Program.Arm(exempt));
        }

        var unadvertised = Program.Dispatch.Keys
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
