using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The night's dispatcher, its own parameter set, the worker's roster and RUNBOOK's schedule all
/// name the same slots and the same stages.
///
/// <b>Four lists say what the lab runs and none of them was reconciled against another.</b>
/// <c>tools/nightly.ps1</c> declares a <c>$slots</c> table mapping a slot to the verbs it runs, and
/// a <c>ValidateSet</c> attribute deciding which slot names the script will accept at all. The
/// worker advertises its stages through <c>Program.StageNames</c>. RUNBOOK's schedule table is what
/// the registered tasks were written from. Each is maintained by hand and each was correct on the
/// day it was written.
///
/// <b>What that cost, found at 4.5 and stated in numbers.</b> The <c>ValidateSet</c> held eighteen
/// names while <c>$slots</c> held twenty-two, so <c>spread-open</c>, <c>spread-close</c>,
/// <c>watchlist</c> and <c>vwap</c> were declared as slots and refused by the script's own parameter
/// validation: the two spread passes built at 4.3, the watchlist stage built at 4.1 and the averages
/// built at 4.4 could not be dispatched at all. <c>plans</c> was in neither list, so PlanBuilder was
/// registered, dispatched and scheduled by the runbook at 18:30 and run by nothing. Five stages of
/// phase 4, and nothing in the corpus was looking.
///
/// <b>This is the seventh failure shape, one step back from the store.</b> A green report is a
/// statement about the build and never about the lab, and the reason the corpus gives is that every
/// check takes its subject from the source, the documents, the fixture or a store it builds, and the
/// running lab is in none of those. The dispatcher is the seam: it is a file in this repository, so
/// it is a subject a check may have, and it decides what the running lab does. Nothing here opens
/// the live store or reads a night's log; it reads four lists that are supposed to agree.
///
/// <b>Reconciled in every direction, because a one-way sweep is what let this happen.</b>
/// <c>ComponentReachabilityTests</c> asked that every advertised stage has a dispatch arm and never
/// the reverse, and the missing half is where the fault was. So each pair below is compared both
/// ways and a shortfall in either names the list that is short.
/// </summary>
public sealed partial class SlotRosterCheck
{
    private readonly ITestOutputHelper _output;

    public SlotRosterCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Slots RUNBOOK's schedule does not name as a verb, and why each is exempt.
    ///
    /// Named rather than filtered by shape, and each is asserted to exist, so an exemption that
    /// stops applying fails here instead of quietly covering something else.
    /// </summary>
    private static readonly Dictionary<string, string> NotInTheSchedule = new(StringComparer.Ordinal)
    {
        ["rebuild"] = "RUNBOOK schedules it as `backfill --rebuild`, which is the verb plus a switch "
            + "rather than a bare verb, and the table states the whole command an operator would type",
    };

    /// <summary>The keys of the $slots table: one slot name per line of the hashtable.</summary>
    [GeneratedRegex(@"^\s*'(?<slot>[a-z-]+)'\s*=\s*@\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SlotKey();

    /// <summary>The verbs each slot runs, as the hashtable declares them.</summary>
    [GeneratedRegex(@"@\('(?<verb>[a-z-]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex SlotVerb();

    /// <summary>The whole ValidateSet attribute, however many lines it wraps over.</summary>
    [GeneratedRegex(@"\[ValidateSet\((?<body>[^\]]*)\)\]", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ValidateSet();

    [GeneratedRegex(@"'(?<name>[a-z-]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedName();

    [Fact]
    [Trait("check", "slot-roster")]
    public void The_dispatcher_its_parameter_set_the_worker_and_the_runbook_name_the_same_slots()
    {
        var coverage = new CheckCoverage("slot-roster", _output);

        string script = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "nightly.ps1"));
        string runbook = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "RUNBOOK.md"));

        string[] slots = [.. SlotKey().Matches(script).Select(m => m.Groups["slot"].Value).Order(StringComparer.Ordinal)];
        string[] verbs = [.. SlotVerb().Matches(script).Select(m => m.Groups["verb"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Match attribute = ValidateSet().Match(script);
        string[] accepted = attribute.Success
            ? [.. QuotedName().Matches(attribute.Groups["body"].Value).Select(m => m.Groups["name"].Value).Order(StringComparer.Ordinal)]
            : [];

        string[] documented = [.. slots.Where(slot =>
            SlotVerbsOf(script, slot).Any(verb => runbook.Contains($"`{verb}`", StringComparison.Ordinal)))];

        coverage
            .Examined("slots declared in tools/nightly.ps1", slots.Length)
            .Examined("slot names its own parameter set accepts", accepted.Length)
            .Examined("distinct worker verbs the slots run", verbs.Length)
            .Examined("stages the worker advertises", Program.StageNames.Count)
            .Examined("slots whose verbs RUNBOOK's schedule names", documented.Length)
            .Scan(
                "the slot table, the parameter set, the worker's roster and RUNBOOK's schedule name the same things",
                CheckCoverage.Backing.Runner(
                    "slot-diagnostics",
                    "whether a slot name this reconciles is one the script will actually accept is a property "
                    + "of PowerShell's own parameter binding, and no assertion about the text of an attribute "
                    + "establishes it. The job dispatches a real slot through the real interpreter"))
            .Report();

        // Stated in advance, on the rule a sweep expecting a non-zero count states that count. A
        // pattern that stopped matching would hand every comparison below two empty sets, which agree.
        Assert.True(slots.Length >= 22,
            $"tools/nightly.ps1 declares {slots.Length} slots. It has held at least twenty-two since 4.5, "
            + "so the parser stopped matching rather than the schedule getting shorter.");

        Assert.True(attribute.Success && accepted.Length >= 22,
            $"The dispatcher's ValidateSet resolved to {accepted.Length} name(s). It accepts one name per "
            + "slot, so a count below the slot count means the attribute was not read rather than that it "
            + "rejects something.");

        // 1. The slot table against the parameter set, both ways. A slot the attribute rejects is a
        //    stage nobody can run; a name the attribute accepts with no slot behind it fails on the
        //    hashtable lookup instead of on the parameter, which is a worse message for the same fault.
        string[] rejected = [.. slots.Except(accepted, StringComparer.Ordinal)];
        string[] hollow = [.. accepted.Except(slots, StringComparer.Ordinal)];

        Assert.True(rejected.Length == 0,
            $"{rejected.Length} slot(s) are declared in tools/nightly.ps1 and refused by its own ValidateSet, "
            + "so the scheduler dispatching one gets a parameter binding error and the stage never runs:\n  "
            + string.Join("\n  ", rejected));

        Assert.True(hollow.Length == 0,
            $"{hollow.Length} slot name(s) are accepted by the ValidateSet and declared nowhere in $slots, "
            + "so a run passes validation and then dispatches no verb at all:\n  "
            + string.Join("\n  ", hollow));

        // 2. The verbs against the worker's roster. A slot running a verb the worker does not
        //    advertise is a slot that will be told "Unknown stage" at three in the morning.
        string[] unknown = [.. verbs.Where(v => !Program.StageNames.Contains(v, StringComparer.Ordinal))];

        Assert.True(unknown.Length == 0,
            $"{unknown.Length} verb(s) the night dispatches are not advertised by the worker, so the slot "
            + "reaches the entry point and exits 2 with \"Unknown stage\":\n  "
            + string.Join("\n  ", unknown));

        // 3. RUNBOOK against the slot table. The document is what the registered tasks were written
        //    from, so a slot missing from it is a slot nobody scheduled, and that is exactly what
        //    happened to `plans`: built, dispatchable, in the runbook and in no $slots entry.
        List<string> undocumented = [.. slots.Except(documented, StringComparer.Ordinal)];

        foreach ((string exempt, string _) in NotInTheSchedule)
        {
            Assert.Contains(exempt, slots);
            undocumented.Remove(exempt);
        }

        Assert.True(undocumented.Count == 0,
            $"{undocumented.Count} slot(s) run a verb RUNBOOK's schedule never names, so the operator's own "
            + "table of what the night does is short by that many and the task that runs them was written "
            + "from somewhere else:\n  " + string.Join("\n  ", undocumented));
    }

    /// <summary>The verbs of one slot, read out of that slot's own line of the hashtable.</summary>
    private static IEnumerable<string> SlotVerbsOf(string script, string slot)
    {
        Match line = Regex.Match(
            script,
            $@"^\s*'{Regex.Escape(slot)}'\s*=\s*(?<verbs>.*)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return line.Success
            ? SlotVerb().Matches(line.Groups["verbs"].Value).Select(m => m.Groups["verb"].Value)
            : [];
    }
}
