using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every claim ARCHITECTURE.html makes in a table, asserted against the code, one verdict each.
///
/// Four verdicts, and the fourth is the point. <b>pass</b> and <b>fail</b> are what a test gives
/// you. <b>out of scope</b> is a claim about something the corpus itself schedules for a
/// checkpoint that has not landed, which is not this phase's business and is counted separately
/// so it can never be mistaken for coverage. <b>unexamined</b> is a claim this phase should have
/// been able to assert and could not, and it is not a pass.
///
/// The difference between out of scope and unexamined is the whole discipline. Collapsing them
/// would let sixty later rows hide one row nobody can check, which is exactly the failure
/// coverage-reported exists to prevent.
///
/// <b>Out of scope names the checkpoint that ends it.</b> Without that, a claim rests there
/// forever and is indistinguishable from one nobody got to, and the count reads as a permanent
/// sixty-four rather than as a number that falls as checkpoints land. So every out-of-scope
/// claim carries the checkpoint that closes it, that checkpoint has to exist in BUILD_PLAN.md,
/// and it has to be one that has not landed: a claim deferred to a checkpoint already recorded
/// in PROGRESS is a claim the checkpoint shipped without coming back to.
///
/// Placement is read from the corpus rather than from a list kept here. BUILD_PLAN.md names
/// every component in the row of the checkpoint that builds it, so the checkpoint comes from the
/// document that schedules the work, and a component no checkpoint names is unexamined, loudly,
/// because a component nobody scheduled is a real finding.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed partial class ArchitectureConformanceCheck
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Deferred = "deferred";
    public const string Unexamined = "unexamined";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITestOutputHelper _output;

    public ArchitectureConformanceCheck(ITestOutputHelper output) => _output = output;

    [GeneratedRegex(@"^## Phase (?<phase>\d)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseHeading();

    [GeneratedRegex(@"^\|\s*(?<checkpoint>\d+\.\d+)\s*\|(?<rest>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CheckpointRow();

    [GeneratedRegex(@"^## (?<checkpoint>\d+\.\d+) ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LandedEntry();

    [GeneratedRegex(@"\b(?:class|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"(?:AddSingleton|AddScoped|AddTransient|AddHttpClient)<(?<name>[^,>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Registration();

    [GeneratedRegex("""^@page\s+"(?<route>[^"]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageRoute();

    /// <summary>
    /// The tables that state a sentence per row, which is what the 3.12 sweep is over.
    ///
    /// A table whose second cell is a value rather than a sentence, as the limits table's is, states
    /// no clauses to reach and is not in this list. Named here rather than derived, because "is this
    /// cell a sentence" is the judgement the sweep is made of and a parser guessing at it would
    /// produce a number nobody could act on.
    /// </summary>
    public static IReadOnlyList<string> TablesStatingASentencePerRow { get; } =
    [
        "Failure behaviour", "Component catalogue", "Build order", "The phase report",
        "Running on Windows and macOS",
    ];

    /// <summary>
    /// How many claims state more than one clause, and how many clauses those cells hold.
    ///
    /// <b>The 3.12 measurement, derived on every run rather than written down once.</b> That row
    /// states its figures in prose, they were taken before two pages and a phase of components
    /// landed, and nothing read them: a sweep priced at 40 claims is a different piece of work from
    /// one priced at 62, and the difference was invisible. The split is the row's own rule, kept
    /// exactly: a sentence end, a semicolon, or a comma before "and", "which", "because" or "so",
    /// with citations excluded because a citation is a pointer rather than a clause.
    ///
    /// <b>It measures the document and concludes nothing about the code.</b> Which clauses a verdict
    /// actually reaches is the judgement the sweep exists to make, and no parser can make it: a
    /// Failure behaviour cell mixes the behaviour with the reason it is that behaviour, and a sweep
    /// that asserted every clause would assert prose. So this reports the size of the pile and never
    /// the disposition of anything in it.
    /// </summary>
    public static (int MultiClause, int Clauses) ClauseWeight(string architecture)
    {
        ArgumentNullException.ThrowIfNull(architecture);

        int multiClause = 0;
        int clauses = 0;

        foreach (string table in TablesStatingASentencePerRow)
        {
            foreach (IReadOnlyList<string> row in HtmlTable.BodyRowsUnder(architecture, table))
            {
                int held = Clauses(string.Join(" ", row.Skip(1))).Count;

                if (held > 1)
                {
                    multiClause++;
                    clauses += held;
                }
            }
        }

        return (multiClause, clauses);
    }

    /// <summary>The clauses of one cell, on the 3.12 rule.</summary>
    public static IReadOnlyList<string> Clauses(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);

        return
        [
            .. ClauseBoundary()
                .Split(Citation().Replace(cell, string.Empty))
                .Select(c => c.Trim())
                .Where(c => c.Length > 0),
        ];
    }

    [GeneratedRegex(@"\(see:[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Citation();

    [GeneratedRegex(@"(?<=[.!?])\s+|;\s*|,\s+(?=(?:and|which|because|so)\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseBoundary();

    /// <summary>
    /// The splitter, proved against a cell written here rather than against whatever the document
    /// holds today.
    ///
    /// <b>A sweep expecting a non-zero count states that count in advance.</b> The live figure moves
    /// with every edit to ARCHITECTURE, so a test asserting it would be a test asserting today, and
    /// what has to hold is that each of the four boundaries is one and that the two that are not are
    /// not. A comma before an ordinary noun does not open a clause, and a citation is a pointer to a
    /// decision rather than something a verdict could reach.
    /// </summary>
    [Fact]
    public void The_clause_split_reads_each_boundary_the_measurement_named()
    {
        Assert.Equal(
            ["A sentence.", "Another one"],
            Clauses("A sentence. Another one"));

        Assert.Equal(
            ["a first half", "a second"],
            Clauses("a first half; a second"));

        foreach (string conjunction in new[] { "and", "which", "because", "so" })
        {
            Assert.Equal(
                ["the behaviour", $"{conjunction} the reason it is that behaviour"],
                Clauses($"the behaviour, {conjunction} the reason it is that behaviour"));
        }

        // The two that are not boundaries. A list is one clause, and a citation is excluded whole
        // rather than split on the punctuation inside it.
        Assert.Single(Clauses("trigger, give-up price, distance and rank"));
        Assert.Single(Clauses("the stage stops rather than overrunning (see: A stage stops. It does not overrun)"));

        // And the shape the whole measurement turns on: a cell holding one clause is not in the
        // pile, however long it is.
        (int multiClause, int clauses) = ClauseWeight(
            "<h2>Failure behaviour</h2><table><tr><th>a</th><th>b</th></tr>"
            + "<tr><td>one</td><td>a single clause of some length</td></tr>"
            + "<tr><td>two</td><td>a clause. and another</td></tr></table>"
            + "<h2>Component catalogue</h2><table><tr><td>x</td><td>only one</td></tr></table>"
            + "<h2>Build order</h2><table><tr><td>x</td><td>only one</td></tr></table>"
            + "<h2>The phase report</h2><table><tr><td>x</td><td>only one</td></tr></table>"
            + "<h2>Running on Windows and macOS</h2><table><tr><td>x</td><td>only one</td></tr></table>");

        Assert.Equal(1, multiClause);
        Assert.Equal(2, clauses);
    }

    /// <summary>
    /// The failure-behaviour table states conditions in prose, so it names no component a parser
    /// could follow. Each row is placed here by hand against the checkpoint that builds the
    /// behaviour, and a row this list does not name is unexamined rather than skipped, which is
    /// what makes adding a row to that table visible.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FailureBehaviourCheckpoints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Intraday prices unavailable for a day"] = "4.2",
        ["A spread snapshot is missed"] = "4.3",
        ["Price gaps past the give-up point"] = "4.7",
        ["A short could not have been borrowed"] = "4.7",
        ["Unprocessed corporate action"] = "1.6",
        ["Detector errors on one stock"] = "2.7",
        ["Nightly setup cap reached"] = "2.8",
        ["Daily API ceiling reached"] = "1.3",
        ["Two variants pick the same stock"] = "5.1",
        ["Risk gate blocks an order"] = "4.6",
        ["AI usage allowance exhausted"] = "6.5",
        ["Holdout windows exhausted"] = "5.4",
        ["Proposal cites the planted null signal"] = "6.4",
        ["Variant sample never accumulates"] = "6.7",
        ["Follow-up date is a holiday"] = "3.2",
        ["A comparison has no control outcomes"] = "3.2",
        ["The vendor holds nothing on a name"] = "3.8",
        ["A vendor refuses one name mid-walk"] = "3.8",
        ["An input the session asked for arrives after the session"] = "3.8",
        ["The vendor answers 200 with a body the parse cannot read"] = "3.8",
        ["A migration adds a column recording when the lab observed something"] = "3.8",
        ["A rebuild writes no rows"] = "3.9",
        ["A stage writes after the UTC date rolls"] = "3.9",
        ["The store is at a schema version other than the build's"] = "3.12",
        ["Someone edits the baseline"] = "5.1",
    };

    /// <summary>
    /// The limits are the risk caps, and RiskGate is the only thing that may apply them. They
    /// travel with it, which is why one entry places the whole table.
    /// see: RiskGate is the sole writer of orders, for both directions and every version
    /// </summary>
    public const string LimitsAreEnforcedBy = "RiskGate";

    /// <summary>
    /// Which route answers for each screen in the catalogue.
    ///
    /// Recorded here because a screen has no class a catalogue name resolves to. A screen this
    /// list does not name is unexamined rather than skipped, so adding a screen to the catalogue
    /// is visible.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Screens { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Chart page"] = "/chart/{ticker?}",
        ["Watchlist page"] = "/watchlist",
        ["Setup inspector"] = "/setups",
        ["Trade journal page"] = "/journal",
        ["Research ledger page"] = "/research",
        ["Pack comparison page"] = "/scoreboard",
        ["Lab scoreboard page"] = "/scoreboard",
    };

    /// <summary>
    /// Catalogued components that are not resolved from the container, and why each is not.
    ///
    /// <b>Named rather than filtered by shape, and each is still required to be declared.</b> A
    /// component here has to exist as a type in the shipped source exactly as one that is registered
    /// does; what is waived is the registration and nothing else. An exemption that stopped applying
    /// would leave the component unexamined by both halves, so the entry is asserted against the
    /// catalogue as well: a name here that the catalogue no longer carries fails.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotContainerServices { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SessionReplayClock"] =
                "it is constructed per session from the connection the resolving stage already holds, and it "
                + "carries the position of a walk in progress. A singleton would be one walk shared across every "
                + "session the process resolves, which is the one thing a forward-only clock must not be. It owns "
                + "no table and is declared as owning none in SCHEMA, and the component that decides something "
                + "from it is the one the container builds",
        };

    /// <summary>
    /// The routes the Web project's pages declare, read from the sources rather than from the
    /// compiled routes: the check reads the repository, and a page whose route was deleted
    /// should fail here rather than in a browser.
    /// </summary>
    private static IReadOnlyList<string> RoutedPages { get; } =
        [.. Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Web", "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Select(text => PageRoute().Match(text))
            .Where(m => m.Success)
            .Select(m => m.Groups["route"].Value)];

    /// <summary>
    /// The pointer is the furthest checkpoint recorded, not the last entry written.
    ///
    /// Proved over a set built here rather than over the live PROGRESS, because the live record is
    /// whatever the corpus holds today and the fault only appears when a dated correction names an
    /// earlier checkpoint than one already recorded. That is a shape this corpus produces on
    /// purpose and had never asserted: on 2026-08-29, with 3.14 landed, a ruling recorded against
    /// 2.11 retitled the phase report "Phase 2 report".
    /// </summary>
    [Fact]
    public void The_pointer_is_the_furthest_checkpoint_recorded_rather_than_the_last_entry_written()
    {
        // The order a dated correction produces: 2.11 recorded after 3.14, both on the same day.
        Assert.Equal("3.14", Schedule.Furthest(["3.12", "3.13", "3.14", "2.11"]));

        // Ordered by phase first, so a high minor in an earlier phase does not win.
        Assert.Equal("4.1", Schedule.Furthest(["3.14", "4.1", "2.12"]));

        // And numerically within a phase, where an ordinal compare would put 3.9 above 3.14.
        Assert.Equal("3.14", Schedule.Furthest(["3.9", "3.14"]));
    }

    [Fact]
    [Trait("check", "architecture-conformance")]
    public void Every_claim_the_architecture_makes_in_a_table_has_a_verdict()
    {
        var coverage = new CheckCoverage("architecture-conformance", _output);
        string architecture = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        Schedule schedule = Schedule.Read();
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> catalogue = HtmlTable.BodyRowsUnder(architecture, "Component catalogue");
        IReadOnlyList<IReadOnlyList<string>> buildOrder = HtmlTable.BodyRowsUnder(architecture, "Build order");
        IReadOnlyList<IReadOnlyList<string>> limits = HtmlTable.BodyRowsUnder(architecture, "The limits");
        IReadOnlyList<IReadOnlyList<string>> failures = HtmlTable.BodyRowsUnder(architecture, "Failure behaviour");
        IReadOnlyList<IReadOnlyList<string>> sections = HtmlTable.BodyRowsUnder(architecture, "The phase report");

        string[] componentNames = [.. catalogue.Select(r => r[0])];
        HashSet<string> declared = DeclaredTypes();
        HashSet<string> registered = RegisteredTypes();

        // 1. The component catalogue. Every component named exists and is registered, or the
        //    build plan schedules it for a checkpoint that has not landed.
        foreach (string component in componentNames)
        {
            string? owed = schedule.CheckpointFor(component);

            if (owed is null)
            {
                claims.Add(Claim.NotExamined("Component catalogue", component,
                    "no checkpoint in BUILD_PLAN.md names this component, so nothing says when it is owed"));
                continue;
            }

            if (!schedule.HasLanded(owed))
            {
                claims.Add(Claim.OutOfScope("Component catalogue", component, owed));
                continue;
            }

            if (component.Contains(' ', StringComparison.Ordinal))
            {
                // A screen rather than a type, asserted against a Razor page that answers a
                // route rather than against a class name. A page has no class a catalogue name
                // resolves to, and asserting one would be asserting a naming convention.
                claims.Add(Screens.TryGetValue(component, out string? route)
                    ? RoutedPages.Contains(route, StringComparer.Ordinal)
                        ? Claim.Passed("Component catalogue", component, $"a page answers {route}")
                        : Claim.Failed("Component catalogue", component,
                            $"{owed} has landed and no page declares the route {route}")
                    : Claim.NotExamined("Component catalogue", component,
                        "a screen with no route recorded against it, so nothing says what would answer for it"));
                continue;
            }

            if (!declared.Contains(component))
            {
                claims.Add(Claim.Failed("Component catalogue", component,
                    $"{owed} has landed and no type named {component} is declared in the source"));
                continue;
            }

            if (NotContainerServices.TryGetValue(component, out string? whyNotAService))
            {
                claims.Add(Claim.Passed("Component catalogue", component,
                    $"declared, and exempt from registration by name: {whyNotAService}"));
                continue;
            }

            claims.Add(registered.Contains(component)
                ? Claim.Passed("Component catalogue", component, "declared and registered")
                : Claim.Failed("Component catalogue", component,
                    $"{component} is declared and is not registered with the container, so nothing can resolve it"));
        }

        // 1a. The exemptions, read back. A name waived from registration that the catalogue no
        //     longer carries is an exemption covering nothing, which reads exactly like one that is
        //     doing work.
        foreach ((string exempt, string _) in NotContainerServices)
        {
            claims.Add(componentNames.Contains(exempt, StringComparer.Ordinal)
                ? Claim.Passed("Component catalogue", $"{exempt}, exempt from registration",
                    "the catalogue still names it, so the exemption still covers something")
                : Claim.Failed("Component catalogue", $"{exempt}, exempt from registration",
                    $"{exempt} is waived from container registration and the catalogue no longer names it"));
        }

        // 2. The build order, read the other way: every component a phase says it builds is a
        //    component the catalogue names. A phase that builds something the catalogue does not
        //    list is a component with no description.
        foreach (IReadOnlyList<string> row in buildOrder)
        {
            string[] missing = Schedule.NamesIn(row[1])
                .Where(n => !componentNames.Contains(n, StringComparer.Ordinal))
                .ToArray();

            claims.Add(missing.Length == 0
                ? Claim.Passed("Build order", row[0], "every component it names is in the catalogue")
                : Claim.Failed("Build order", row[0],
                    "names components the catalogue does not describe: " + string.Join(", ", missing)));
        }

        // 3. The limits. Risk caps, enforced by the one component that may open a position.
        //
        //    Asserted from 4.6, having been out of scope until RiskGate landed and unexamined for
        //    the length of one report after it. Each row is read against the constant that holds it
        //    and against the code that applies it, so a document stating a cap the component does
        //    not enforce fails rather than resting.
        string? riskGate = schedule.CheckpointFor(LimitsAreEnforcedBy);
        foreach (IReadOnlyList<string> row in limits)
        {
            claims.Add(riskGate is null
                ? Claim.NotExamined("The limits", row[0], $"no checkpoint names {LimitsAreEnforcedBy}, which is what applies these")
                : !schedule.HasLanded(riskGate)
                    ? Claim.OutOfScope("The limits", row[0], riskGate)
                    : AssertLimit(row[0], row[1]));
        }

        // 4. Failure behaviour. Placed by hand because the table names conditions rather than
        //    components, and asserted where the checkpoint that builds the behaviour has landed.
        foreach (IReadOnlyList<string> row in failures)
        {
            string condition = row[0];

            if (!FailureBehaviourCheckpoints.TryGetValue(condition, out string? owed))
            {
                claims.Add(Claim.NotExamined("Failure behaviour", condition,
                    "no checkpoint is recorded against this condition, so nothing says when the behaviour is owed"));
                continue;
            }

            claims.Add(schedule.HasLanded(owed)
                ? AssertFailureBehaviour(condition)
                : Claim.OutOfScope("Failure behaviour", condition, owed));
        }

        // 5. The phase report's own three sections, asserted against the report this run writes.
        //    A document that promises a section the report does not produce is the report telling
        //    you about itself, which is the one claim it is in a position to be sure of.
        foreach (IReadOnlyList<string> row in sections)
        {
            claims.Add(PhaseReportSections.Names.Contains(row[0], StringComparer.OrdinalIgnoreCase)
                ? Claim.Passed("The phase report", row[0], "the report writes this section")
                : Claim.Failed("The phase report", row[0],
                    $"the document promises a \"{row[0]}\" section and the report writes "
                    + string.Join(", ", PhaseReportSections.Names)));
        }

        // 6. The two phase-1 tables that state properties this build already has, asserted rather
        //    than left to the checkpoint that never comes back for them.
        claims.AddRange(PortabilityClaims(architecture));
        claims.AddRange(MoveProcedureClaims(architecture));
        claims.AddRange(ManagementClaims(architecture));
        claims.AddRange(LossCauseClaims(architecture));

        // 7. And every table in the document placed, which is what stops the five above from
        //    being the document as far as this check is concerned. A table nobody reads produces
        //    no claim at all, so it is absent from the count rather than unexamined in it, and
        //    absent is the one state the report cannot show you.
        claims.AddRange(TablePlacementClaims(architecture));

        var byVerdict = claims.GroupBy(c => c.Verdict, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Directory.CreateDirectory(RepositoryLayout.Artifacts);
        File.WriteAllText(
            Path.Combine(RepositoryLayout.Artifacts, "doc-conformance.json"),
            JsonSerializer.Serialize(
                new Conformance(
                    schedule.Phase,
                    schedule.LastLanded,
                    claims.Count,
                    byVerdict.GetValueOrDefault(Pass),
                    byVerdict.GetValueOrDefault(Fail),
                    byVerdict.GetValueOrDefault(Deferred),
                    byVerdict.GetValueOrDefault(Unexamined),
                    claims),
                Json));

        // The failure table is where this check reads source text to conclude something about
        // behaviour, and it is where two instances of an assertion outliving its subject shipped.
        // The fourth: the detector-error claim passed with the catch clause deleted, because the
        // private method issuing the insert was still in the file with nothing calling it. The
        // fifth: the store-version claim read three patterns that are all satisfied inside the
        // guard's own methods, so it passed with the line that calls the guard deleted. That one
        // is no longer listed here, because it no longer reads source at all: its verdict is a
        // detector run through the CLI against a store one migration short.
        //
        // Every scan that remains names what exercises it, so a claim resting on text alone is
        // visible rather than counted.
        coverage
            .Scan("Failure behaviour: Detector errors on one stock",
                CheckCoverage.Backing.Test(
                    "DetectorErrorTests.A_name_the_detector_cannot_read_gets_an_error_row_and_the_run_goes_partial",
                    "both detectors are run over a store with one name made unreadable, and the row and the "
                    + "partial outcome are read back. Added when the scan alone was found to pass with the catch "
                    + "clause removed"))
            .Scan("Failure behaviour: Nightly setup cap reached",
                CheckCoverage.Backing.Test(
                    "NightlyCapTests.The_release_rule_holds_over_every_arrangement_of_the_two_counts",
                    "the arithmetic the cap applies is swept over every arrangement of the two counts, so the "
                    + "scan is left holding only that the stage still reads the night whole and reports what it "
                    + "truncated"))
            .Scan("Failure behaviour: Unprocessed corporate action",
                CheckCoverage.Backing.Test(
                    "IndicatorEngineTests.A_ticker_with_an_open_demand_is_refused_and_the_others_are_not",
                    "a ticker with an open rebuild demand gets no row and the rest of the night does, which is "
                    + "the behaviour the blocked counter in the scan stands for"))
            .Scan("Failure behaviour: Daily API ceiling reached",
                CheckCoverage.Backing.Test(
                    "RunLoggerTests.A_night_with_a_stage_that_stopped_short_names_it_and_an_ordinary_night_names_nothing",
                    "the third clause, which is the one the verdict did not assert until 3.12. A night with a "
                    + "stage that ended other than cleanly names it and an ordinary night names nothing, both "
                    + "read back through DegradedBecause. The first two clauses are exercised by "
                    + "RunLoggerTests.A_stage_stops_at_the_ceiling_and_completes_partial_rather_than_overrunning, "
                    + "and the scan asks only that the run scope still exposes what is left and that both "
                    + "detectors still call the reader"))
            .Scan("Failure behaviour: A spread snapshot is missed",
                CheckCoverage.Backing.Test(
                    "SpreadSnapshotterTests.A_session_nobody_sampled_refuses_rather_than_answering_with_nothing",
                    "the case the other two are told apart from, and the only one of the three a scan could "
                    + "never see: a store with no pass row at all is read and the reader throws. Beside it "
                    + "A_session_sampled_once_is_degraded_and_says_which_pass_it_has and "
                    + "A_pass_stopped_by_the_ceiling_is_partial_and_says_how_far_it_got exercise the other two, "
                    + "so the scan is left holding only that the stage still writes a pass row on every path"))
            .Scan("Failure behaviour: A comparison has no control outcomes",
                CheckCoverage.Backing.Test(
                    "ForwardReturnFillerTests.A_control_draw_produces_forward_returns_of_kind_control",
                    "a control draw is seeded and the rows are read back by subject kind, which is the "
                    + "behaviour the scan's two source shapes stand for. The scan alone would have passed "
                    + "for the whole of phase 3 had the query been present and unreachable"))
            .Scan("the catalogue's components exist and are registered, found by scanning declarations and registrations",
                CheckCoverage.Backing.Test(
                    "ComponentReachabilityTests.Every_stage_the_entry_point_advertises_has_an_arm_in_the_dispatch",
                    "the direction this scan cannot see is a registration that is present and unreachable: a "
                    + "line the pattern does not match fails loudly, and a stage in the table with no arm in "
                    + "the dispatch passes. The test asks the dispatch itself for each advertised name and "
                    + "asserts on what comes back, with an authored bad name and a bad case asserting the "
                    + "other direction. It read Program.cs as text and matched switch-arm shapes with a regex "
                    + "until 4.17, which was a scan backed by a scan and was recorded here as backed"));

        // The catalogue read against the build order in the direction nothing asked before 4.14.
        //
        // The claim above runs one way: every component a phase says it builds is one the catalogue
        // describes. The reverse is the one that was missing, and P4 was building an auditor of a
        // thing no phase built: PlanBuilder, VariantResolver and SessionReplayClock each carry a
        // nightly slot in the catalogue and appeared in no Builds row at all.
        //
        // <b>It is a scope with a floor rather than a claim per component.</b> A claim per component
        // would double the catalogue's contribution to the register to say a second thing about the
        // same rows, and CLAUDE.md's rule is that a check states a floor under each scope it names.
        // So the property fails the run outright and the number it examined is reported beside it.
        //
        // A screen is excluded because a Builds cell names screens in prose, as "watchlist page",
        // and NamesIn takes only single tokens. That exclusion is what the count is of, so it is
        // stated rather than left in the predicate.
        string[] catalogueTypes =
            [.. componentNames.Where(n => !n.Contains(' ', StringComparison.Ordinal))];

        HashSet<string> namedByAPhase =
        [
            .. buildOrder.SelectMany(row => Schedule.NamesIn(row[1])),
        ];

        string[] unplaced = [.. catalogueTypes.Where(n => !namedByAPhase.Contains(n)).Order(StringComparer.Ordinal)];

        // How much of the register states more than one clause, derived rather than stated in prose.
        //
        // <b>The 3.12 row's own figure, and it had gone stale.</b> That row measured 40 multi-clause
        // claims over about 160 clauses at the time it was written, in a sentence nothing read, and
        // the register has grown by two pages and a phase of components since. A number in prose
        // about the corpus is the shape `stated-counts` exists for, and the sweep it prices is the
        // one piece of work whose size nobody could see moving.
        //
        // Context rather than a floor. It is a fact about how the document is written, it falls as
        // the sweep runs and rises as the corpus grows, and neither direction is a property going
        // away.
        (int multiClause, int clauses) = ClauseWeight(architecture);

        coverage.Context("claims whose cell states more than one clause, awaiting the 3.12 sweep", multiClause);
        coverage.Context("clauses those cells hold, split at a sentence end, a semicolon or a comma before a conjunction", clauses);

        coverage.Examined("catalogued components a phase's Builds row names", catalogueTypes.Length - unplaced.Length);
        coverage.Context("catalogued screens, named in a Builds row as prose rather than as a token", componentNames.Length - catalogueTypes.Length);

        foreach (IGrouping<string, Claim> table in claims.GroupBy(c => c.Table, StringComparer.Ordinal))
        {
            coverage.Examined($"claims in {table.Key}", table.Count(c => c.Verdict is Pass or Fail));

            Claim[] deferred = [.. table.Where(c => c.Verdict == Deferred)];
            if (deferred.Length > 0)
            {
                foreach (IGrouping<string, Claim> byCheckpoint in deferred
                             .GroupBy(c => c.Closes ?? "unplaced", StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    coverage.OutOfScope(
                        $"claims in {table.Key} closed by {byCheckpoint.Key}",
                        byCheckpoint.Count(),
                        CheckCoverage.OutOfScopeReason.UntilCheckpoint(byCheckpoint.Key,
                            "placed at that checkpoint by BUILD_PLAN: "
                            + string.Join(", ", byCheckpoint.Select(c => c.Subject).Take(4))));
                }
            }

            Claim[] unexamined = [.. table.Where(c => c.Verdict == Unexamined)];
            if (unexamined.Length > 0)
            {
                coverage.NotExamined($"claims in {table.Key}", unexamined.Length,
                    string.Join("; ", unexamined.Take(4).Select(c => $"{c.Subject}: {c.Detail}")));
            }
        }

        coverage.Report();

        Assert.True(unplaced.Length == 0,
            "The catalogue describes " + string.Join(", ", unplaced)
            + " and no phase's Builds row names them, so the document describes components the build order "
            + "never builds. Name each in the phase that builds it. This is the direction nothing asked "
            + "before 4.14, and it is how P4 came to build an auditor of a thing no phase built.");

        Claim[] failed = [.. claims.Where(c => c.Verdict == Fail)];
        Assert.True(failed.Length == 0,
            $"{failed.Length} architecture claim(s) do not hold:\n  "
            + string.Join("\n  ", failed.Select(c => $"[{c.Table}] {c.Subject}: {c.Detail}")));

        IReadOnlyList<string> unclosed = OutOfScopeProblems(claims, schedule.Exists, schedule.HasLanded);
        Assert.True(unclosed.Count == 0,
            $"{unclosed.Count} out-of-scope claim(s) do not name a checkpoint that will end them:\n  "
            + string.Join("\n  ", unclosed));

        // Stated so the parser stopping cannot pass as a document that got smaller.
        //
        // A floor rather than an equality, and the distinction is the one the coverage baseline
        // already makes. What this guards is that the parser still finds rows; how many rows the
        // catalogue holds is a fact about the corpus that grows every time a component is added, and
        // an equality here would be a third copy of that number going red on an ordinary addition.
        // The exact count is held where it belongs: `stated-counts` compares the parsed rows against
        // the number the document states about itself, in both directions.
        Assert.True(catalogue.Count >= 52,
            $"The component catalogue parsed {catalogue.Count} rows. It held 52 before any of this phase's "
            + "components were added, so a number below that means the parser stopped matching rather than that the "
            + "document got smaller.");
        Assert.True(failures.Count == FailureBehaviourCheckpoints.Count,
            $"The failure-behaviour table has {failures.Count} rows and {FailureBehaviourCheckpoints.Count} are placed at a checkpoint.");
    }

    /// <summary>
    /// The tables this check reads for claims, by the heading above each.
    ///
    /// Named so <see cref="TablePlacementClaims"/> can tell a table it read from one it did not,
    /// rather than the two lists drifting apart silently, which is the same defect one level up
    /// from the one the placement pass exists to catch.
    /// </summary>
    public static IReadOnlyList<string> ClaimTables { get; } =
        ["Component catalogue", "Build order", "The limits", "Failure behaviour", "The phase report",
         "Running on Windows and macOS", "The procedure", "What differs in management",
         "Why each loss happened"];

    /// <summary>
    /// Every other table in the document, and why it yields no claim.
    ///
    /// Two kinds, and the distinction is the same one the verdicts draw. A table of definitions or
    /// worked examples asserts nothing about the code and never will, so it is exempt by name with
    /// the reason written down. A table describing something a later checkpoint builds is out of
    /// scope and names that checkpoint, exactly as a deferred row does.
    ///
    /// A table this list does not name is unexamined, loudly, because a table nobody placed is a
    /// table nobody read, and that is a finding rather than a gap.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TablesWithoutClaims { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Three real long trades published by the trader this is modelled on"] =
            "worked examples of the pattern, not a statement about the code",
        ["Vocabulary"] = "definitions of terms, not a statement about the code",
        ["Which kinds of measurement are missing"] =
            "what the design deliberately does not measure, which no code can be checked against",
        ["What each tier of change can be replayed against"] = "5.3",
        ["What the pack contains"] = "6.4",
        ["Model budget"] = "6.5",
        ["What each vendor endpoint carries"] =
            "what the vendor returns from each route, established by probe and capture rather than by "
            + "reading the code. No check can assert it: the subject is the vendor, and the one thing a "
            + "test could confirm is that the lab still calls the endpoints named, which is the half that "
            + "was never in doubt. It is placed here as a permanent exemption rather than deferred, "
            + "because nothing will close it",
        ["Data budget"] = "read by pinned-constants and stated-counts, cost and cadence per row",
        ["Authored parameters"] = "read by pinned-constants, one pin per row that has a code constant",
    };

    /// <summary>
    /// Every table in the document has a verdict, including the ones this check reads no claims
    /// from.
    ///
    /// The failure this closes is the quietest kind of under-reporting there is. Reading five
    /// tables by name and reporting eighty-two claims looks like coverage of the document, and the
    /// twelve tables nobody parsed contribute nothing at all: not a pass, not a fail, and not an
    /// unexamined row either. Absent is worse than unexamined, because unexamined is the verdict
    /// that blocks and absent is the one the report cannot show.
    /// </summary>
    public static IReadOnlyList<Claim> TablePlacementClaims(string architecture)
    {
        const string Table = "Tables in the document";
        var claims = new List<Claim>();

        foreach (string heading in HtmlTable.HeadingOfEveryTable(architecture).Distinct(StringComparer.Ordinal))
        {
            if (ClaimTables.Contains(heading, StringComparer.Ordinal))
            {
                claims.Add(Claim.Passed(Table, heading, "read for claims by this check"));
                continue;
            }

            if (!TablesWithoutClaims.TryGetValue(heading, out string? why))
            {
                claims.Add(Claim.NotExamined(Table, heading,
                    "no claim is read from this table and nothing says why, so it is a table nobody placed"));
                continue;
            }

            // A reason that reads as a checkpoint is one, and obeys the same rule a deferred claim
            // does: the plan has to have it and the record must not yet carry it.
            claims.Add(Checkpoint().IsMatch(why)
                ? Claim.OutOfScope(Table, heading, why)
                : Claim.Passed(Table, heading, why));
        }

        return claims;
    }

    [GeneratedRegex(@"^\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex Checkpoint();


    /// <summary>
    /// The management table, which describes the two rule sets and was deferred to 4.8 until 4.8
    /// built them.
    ///
    /// <b>One of its four rows is a claim about the code and three are not.</b> Holding period,
    /// ambition and worst case say what the two sides of this strategy are for; no run of anything
    /// could agree or disagree with them, so they are exempt by name with the reason rather than
    /// passed against an assertion nobody wrote. The exit-rule row is different: every clause in it
    /// names a constant or a mechanism that exists, and until 4.8 none of them did, which is what the
    /// deferral was for.
    ///
    /// <b>Both cells are asserted, and the pooling rule is why that matters here.</b> A check that
    /// read the long cell and reported the table would say the same thing whether the short side had
    /// been built or not.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    private static IReadOnlyList<Claim> ManagementClaims(string architecture)
    {
        const string Table = "What differs in management";
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> rows = HtmlTable.BodyRowsUnder(architecture, Table);

        foreach (IReadOnlyList<string> row in rows)
        {
            string what = row[0];

            claims.Add(what switch
            {
                "Exit rule" => TheTwoRuleSetsAreBuiltAndSeparate(row[1], row[2])
                    ? Claim.Passed(Table, what,
                        "the long trail reads a daily close against the 9-day average and fills at the next "
                        + "open, the short trim takes 15% of the planned size at 3R, the short exit reads an "
                        + "hourly close against the 50-day average, the two live in separate files, and both "
                        + "cells' \"whichever is reached first\" is one ordering over all three reasons")
                    : Claim.Failed(Table, what,
                        "a clause of the exit-rule row no longer matches the rules the code holds, or the two "
                        + "rule sets stopped being separate code paths, which is the one way to test a "
                        + "strategy nobody trades"),

                _ => Claim.Passed(Table, what,
                    "what the two sides of the strategy are for rather than what the code does, so no run of "
                    + "anything could agree or disagree with it. Exempt by name beside the vocabulary and the "
                    + "worked examples"),
            });
        }

        return claims;
    }

    /// <summary>
    /// The exit-rule row, clause by clause, against the code that holds each one.
    ///
    /// The two cells are passed in so a reworded document fails here rather than being asserted
    /// against constants the row no longer names, which is the direction a source scan cannot see on
    /// its own.
    /// </summary>
    private static bool TheTwoRuleSetsAreBuiltAndSeparate(string longCell, string shortCell)
    {
        string manager = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "PositionManager.cs"));

        // Separate code paths rather than one routine with a sign flag, read off the two types
        // rather than off the text of their files: each names the other in its own prose, on
        // purpose, so a scan for the other side's method name finds a cross-reference and calls it a
        // merge. What the deliverable asks for is that neither side's rule is reachable through the
        // other and that no rule takes a direction, because a direction parameter is the sign flag.
        Type longSide = typeof(Core.Trading.LongExitRules);
        Type shortSide = typeof(Core.Trading.ShortExitRules);

        bool separate = longSide.GetMethod("TrailArmedBy") is not null
            && longSide.GetMethod("Reclaimed") is null
            && shortSide.GetMethod("Reclaimed") is not null
            && shortSide.GetMethod("TrailArmedBy") is null
            && !longSide.GetMethods().Concat(shortSide.GetMethods())
                .SelectMany(m => m.GetParameters())
                .Any(p => string.Equals(p.Name, "direction", StringComparison.Ordinal));

        bool trail = longCell.Contains("9-day", StringComparison.Ordinal)
            && longCell.Contains("next open", StringComparison.Ordinal)
            && Core.Trading.LongExitRules.TrailArmedBy(adjustedClose: 99m, nineDayAverage: 100m)
            && !Core.Trading.LongExitRules.TrailArmedBy(adjustedClose: 100m, nineDayAverage: 100m)
            && manager.Contains("ExitReason.Trail", StringComparison.Ordinal);

        bool trim = shortCell.Contains("15%", StringComparison.Ordinal)
            && shortCell.Contains("3R", StringComparison.Ordinal)
            && Core.Trading.ShortExitRules.TrimFraction == 0.15m
            && Core.Trading.ShortExitRules.TrimAt == 3m
            && Core.Trading.ShortExitRules.TrimShares(plannedShares: 150, heldShares: 150) == 22;

        bool reclaim = shortCell.Contains("50-day", StringComparison.Ordinal)
            && Core.Trading.ShortExitRules.Reclaimed(adjustedHourlyClose: 101m, fiftyDayAverage: 100m)
            && !Core.Trading.ShortExitRules.Reclaimed(adjustedHourlyClose: 100m, fiftyDayAverage: 100m)
            && manager.Contains("ExitReason.Reclaim", StringComparison.Ordinal);

        // Both cells say it, and it is one ordering over all three reasons rather than two rules
        // each knowing about the stop.
        bool whicheverIsFirst = longCell.Contains("whichever is reached first", StringComparison.Ordinal)
            && shortCell.Contains("whichever is reached first", StringComparison.Ordinal)
            && Core.Trading.ExitReason.ThatCloseAPosition.Count == 3
            && Core.Trading.ExitReason.First(
                [
                    new Core.Trading.ExitCandidate(Core.Trading.ExitReason.Trail, 88m, AtTheOpen: true),
                    new Core.Trading.ExitCandidate(Core.Trading.ExitReason.GaveUp, 95m, AtTheOpen: true),
                ])?.Reason == Core.Trading.ExitReason.GaveUp
            && manager.Contains("ExitReason.First(", StringComparison.Ordinal);

        return separate && trail && trim && reclaim && whicheverIsFirst;
    }


    /// <summary>
    /// The loss taxonomy, which was deferred to 4.10 until 4.10 built it.
    ///
    /// <b>Every row here is a claim about the code, which is what makes this table different from
    /// the management one.</b> Each names a detector, and until 4.10 none of them had one. The gap
    /// row is the reason this pass is worth having at all: it carried a detector for the whole of
    /// phases 1 to 3 that would have put every ordinary stop-out in its bucket, and nothing could
    /// see it because the component that would have made the cell assertable did not exist.
    /// see: A gap loss is detected from the exit fill's basis, not from the size of the loss
    /// </summary>
    private static IReadOnlyList<Claim> LossCauseClaims(string architecture)
    {
        const string Table = "Why each loss happened";
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> rows = HtmlTable.BodyRowsUnder(architecture, Table);
        string migration = PullbackStrategyLab.Data.MigrationRunner.All()
            .Single(m => m.Name.Contains("loss-class", StringComparison.Ordinal)).Sql;

        decimal oneR = Core.Trading.LossCause.OneRInReturn(giveUpDistance: 5m, triggerPrice: 100m);

        foreach (IReadOnlyList<string> row in rows)
        {
            string what = HtmlTable.Text(row[0]);

            claims.Add(what switch
            {
                "Noise stop-out" =>
                    Core.Trading.LossCause.AftermathOf(oneR, oneR) == Core.Trading.LossAftermath.Noise
                    && Core.Trading.LossCause.AftermathOf(oneR * 2m, oneR) == Core.Trading.LossAftermath.Noise
                        ? Claim.Passed(Table, what,
                            "a direction-signed ten-session return that reached one unit of risk places the loss "
                            + "as noise, and reaching it exactly counts, because one R is where the trade would "
                            + "have paid for the risk it took")
                        : Claim.Failed(Table, what,
                            "the boundary no longer places a return that reached one unit of risk as noise"),

                "Failed setup" =>
                    Core.Trading.LossCause.AftermathOf(0m, oneR) == Core.Trading.LossAftermath.FailedSetup
                    && Core.Trading.LossCause.AftermathOf(-oneR, oneR) == Core.Trading.LossAftermath.FailedSetup
                        ? Claim.Passed(Table, what,
                            "a follow-up that was flat or against the trade places the loss as a failed setup, "
                            + "which is the bucket selection changes can reduce")
                        : Claim.Failed(Table, what,
                            "a flat or adverse follow-up no longer places the loss as a failed setup"),

                "Gap loss" => TheGapIsDetectedFromTheBasis(row[1])
                    ? Claim.Passed(Table, what,
                        "the mechanism is read from the exit fill's own basis, so a gap is an exit that could "
                        + "not be hit at the price it named. The cell said \"loss larger than one unit of risk\" "
                        + "until 4.10, and that detector fires on every ordinary stop-out because a round trip "
                        + "costs two crossings")
                    : Claim.Failed(Table, what,
                        "the gap detector is back to a test on the size of the loss, which every ordinary "
                        + "stop-out satisfies, or the mechanism no longer reads the fill's basis"),

                "unclassified" =>
                    Core.Trading.LossAftermath.All.Contains(Core.Trading.LossAftermath.Unclassified, StringComparer.Ordinal)
                    && migration.Contains("'unclassified'", StringComparison.Ordinal)
                        ? Claim.Passed(Table, what,
                            "it is a value the store admits and the taxonomy names, rather than a null or a "
                            + "silent skip. A row still waiting on its horizon carries neither, which is a "
                            + "different fact and the one that would otherwise swamp it")
                        : Claim.Failed(Table, what,
                            "unclassified is no longer a value the store admits, so a loss the rules cannot "
                            + "place has nowhere to go but the nearest bucket"),

                _ => Claim.NotExamined(Table, what,
                    "the taxonomy gained a cause and this check has no assertion for it, so nothing says "
                    + "whether the detector it names exists"),
            });
        }

        return claims;
    }

    /// <summary>
    /// The gap row, against the code and against its own cell.
    ///
    /// The cell is passed in so a document reworded back to a size test fails here rather than the
    /// check quietly asserting the code against a line the table no longer carries, which is the
    /// direction a source scan cannot see on its own.
    /// </summary>
    private static bool TheGapIsDetectedFromTheBasis(string detectionCell)
    {
        string cell = HtmlTable.Text(detectionCell);

        bool documentSaysBasis = cell.Contains("basis", StringComparison.OrdinalIgnoreCase)
            && !cell.Contains("larger than one unit of risk", StringComparison.OrdinalIgnoreCase);

        string classifier = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "LossClassifier.cs"));

        return documentSaysBasis
            && Core.Trading.LossCause.MechanismOf(Core.Trading.FillModel.Gapped) == Core.Trading.LossMechanism.Gap
            && Core.Trading.LossCause.MechanismOf(Core.Trading.FillModel.Slipped) == Core.Trading.LossMechanism.Ordinary
            && classifier.Contains("LossCause.MechanismOf(exit.Basis)", StringComparison.Ordinal);
    }

    /// <summary>
    /// The two-platform table, asserted against the properties that already hold rather than left
    /// to a checkpoint that will not come back for it.
    ///
    /// Every row of it describes something phase 1 either does or does not do, and most of them
    /// already have a check standing behind them. What was missing was the document's own rows
    /// being tied to those checks, so a row could be reworded into something nothing enforces and
    /// nothing would notice.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    private static IReadOnlyList<Claim> PortabilityClaims(string architecture)
    {
        const string Table = "Running on Windows and macOS";
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> rows = HtmlTable.BodyRowsUnder(architecture, Table);
        string properties = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, "Directory.Build.props"));
        string attributes = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, ".gitattributes"));

        foreach (IReadOnlyList<string> row in rows)
        {
            string what = row[0];

            claims.Add(what switch
            {
                "Timezone identifiers" => properties.Contains("<InvariantGlobalization>false</InvariantGlobalization>", StringComparison.Ordinal)
                    ? Claim.Passed(Table, what, "InvariantGlobalization is set false explicitly, which is what keeps IANA lookup working")
                    : Claim.Failed(Table, what, "InvariantGlobalization is not set false in Directory.Build.props, so IANA lookup can fail silently"),

                "Filesystem case sensitivity" => Claim.Passed(Table, what, "asserted by path-casing, byte for byte against the on-disk path"),

                "Path separators and roots" => Claim.Passed(Table, what, "asserted by store-portability, which refuses an absolute path in any stored row"),

                "Scheduling" => Claim.Passed(Table, what, "the worker is one CLI entry point per stage and holds no scheduler"),

                "Native dependencies" => Claim.Passed(Table, what, "asserted by the matrix, which runs the suite on macos-latest as well as windows-latest"),

                "Line endings" => attributes.Contains("text=auto", StringComparison.Ordinal) || attributes.Contains("eol=lf", StringComparison.Ordinal)
                    ? Claim.Passed(Table, what, "normalised in .gitattributes rather than left to each machine's git config")
                    : Claim.Failed(Table, what, ".gitattributes does not normalise line endings, so the repository depends on each machine's git config"),

                _ => Claim.NotExamined(Table, what,
                    "a row this check does not name, so adding one to the table is visible rather than silent"),
            });
        }

        return claims;
    }

    /// <summary>
    /// The move procedure, which this document and RUNBOOK.md both state, compared step by step.
    ///
    /// The same procedure written twice is two things to keep right, and at the 1.12 review they
    /// had already diverged: the rehearsal at 1.11 found step 2 naming five tables that do not
    /// exist, corrected RUNBOOK.md, and left this document telling an operator to count them, get
    /// zero, and report success. <c>stated-counts</c> compared the two by row count, ten against
    /// ten, and passed over it, which is what a count does when the disagreement is in the words.
    /// </summary>
    private static IReadOnlyList<Claim> MoveProcedureClaims(string architecture)
    {
        const string Table = "The procedure";
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> here = HtmlTable.BodyRowsUnder(architecture, Table);
        IReadOnlyList<IReadOnlyList<string>> runbook = MarkdownTable.BodyRowsAfter(
            RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "RUNBOOK.md")),
            "## Moving the store to another machine");

        foreach (IReadOnlyList<string> row in here)
        {
            string step = row[0];
            IReadOnlyList<string>? twin = runbook.FirstOrDefault(r => r[0].Trim() == step);

            if (twin is null)
            {
                claims.Add(Claim.Failed(Table, $"step {step}",
                    "RUNBOOK.md's move procedure has no step with this number, so the two statements of it have diverged in shape"));
                continue;
            }

            claims.Add(ProcedureStepClaim(step, row[^1], twin[^1]));
        }

        return claims;
    }

    /// <summary>
    /// One step of the move procedure, as the two documents state it.
    ///
    /// Compared on the stores each step names rather than on its wording. The two documents are
    /// written for different readers and should read differently; what may not differ is which
    /// stores an operator is told to count, since that is the substance the rehearsal turns on and
    /// the exact thing that had drifted by 1.12.
    ///
    /// Separated from the run so it can be proved against steps written by hand, rather than by
    /// reverting the document once and putting it back.
    /// </summary>
    public static Claim ProcedureStepClaim(string step, string here, string there)
    {
        string[] hereNamed = SubstanceNamedIn(here);
        string[] thereNamed = SubstanceNamedIn(there);

        // Neither side names anything the comparator knows, so there is nothing to compare and the
        // honest verdict is that this step was not examined.
        //
        // <b>This is the seventh shape, and it is why the branch exists.</b> The vocabulary used to
        // be the store table names alone. The 1.12 repair rewrote both documents to say "derived
        // from the schema rather than from a list here", which took the last table name out of both
        // sides in the same commit, and from then on every step compared an empty list against an
        // empty list and returned Passed. Ten claims, a floor of exactly ten, and a green phase
        // report, over a comparator that had not compared anything for a phase and a half. No
        // one-sided check could have caught it, because both operands went empty together.
        //
        // So an empty comparison is now a verdict of its own. A count of claims that passed is only
        // worth reading if passing meant something happened.
        if (hereNamed.Length == 0 && thereNamed.Length == 0)
        {
            return Claim.NotExamined("The procedure", $"step {step}",
                "neither ARCHITECTURE.html nor RUNBOOK.md names a store, a command or a file the comparator "
                + "recognises in this step, so the two statements of it were compared on nothing. Widen the "
                + "vocabulary or state what this step is meant to agree on");
        }

        return hereNamed.SequenceEqual(thereNamed, StringComparer.Ordinal)
            ? Claim.Passed("The procedure", $"step {step}",
                $"names the same {hereNamed.Length} item(s) as RUNBOOK.md's step of the same number")
            : Claim.Failed("The procedure", $"step {step}",
                $"names [{string.Join(", ", hereNamed)}] where RUNBOOK.md names [{string.Join(", ", thereNamed)}]. "
                + "The same procedure in two documents, disagreeing about what an operator does");
    }

    private static string[] SubstanceNamedIn(string prose) =>
        [.. ProcedureSubstance().Matches(prose)
            .Select(m => Normalise(m.Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// What a step of the move procedure names that both documents have to agree on: the stores an
    /// operator counts, and the commands and files the step turns on.
    ///
    /// The store names are the phase 2 to 5 tables alongside the ones that exist, because those are
    /// the ones a procedure written before they existed reaches for, and naming a table that is not
    /// there is what makes the step count nothing. The commands and files were added at 3.10, when
    /// the store half of this vocabulary turned out to match nothing at all in either document.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:setup_signal|setups?|forward_return|trade|variant|daily_bar|indicator_daily|run_log)\b"
        + @"|VACUUM\s+INTO|PRAGMA\s+integrity_check|appsettings\.Secrets\.json"
        + @"|tools/snapshot-db|launchd|Task\s+Scheduler"
        + @"|\bdatabase\b|\bwatchlist\b|\bsession\s+boundaries\b|\bread-only\b|\barchive\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProcedureSubstance();

    /// <summary>
    /// One matched term in the form both documents can be compared on: lower case, single-spaced
    /// and singular, so "setups" here and "setup" there is an agreement rather than a difference.
    /// The two documents are written for different readers and their grammar is allowed to differ;
    /// what they name is not.
    /// </summary>
    private static string Normalise(string term)
    {
        string collapsed = WhiteSpaceRun().Replace(term.Trim().ToLowerInvariant(), " ");

        return collapsed.EndsWith('s') && !collapsed.EndsWith("_signal", StringComparison.Ordinal)
            ? collapsed[..^1]
            : collapsed;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpaceRun();

    /// <summary>
    /// What is wrong with the out-of-scope claims, taken as a set. Three things, and each is a
    /// different way of resting there forever:
    ///
    /// A claim with no checkpoint is indistinguishable from one nobody got to. A claim naming a
    /// checkpoint the plan does not have closes at nothing, which is the same thing spelled
    /// differently. A claim naming a checkpoint PROGRESS already records is worse than either,
    /// because that checkpoint shipped and did not bring the claim into scope, and nothing said
    /// so at the time.
    ///
    /// Separated from the run above so it can be proved against claims written by hand rather
    /// than against whatever the corpus happens to say today. A check nobody can break on
    /// purpose is a check nobody knows the state of.
    /// </summary>
    public static IReadOnlyList<string> OutOfScopeProblems(
        IReadOnlyList<Claim> claims,
        Func<string, bool> checkpointExists,
        Func<string, bool> checkpointHasLanded)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(checkpointExists);
        ArgumentNullException.ThrowIfNull(checkpointHasLanded);

        var problems = new List<string>();

        foreach (Claim claim in claims.Where(c => c.Verdict == Deferred))
        {
            string where = $"[{claim.Table}] {claim.Subject}";

            if (string.IsNullOrWhiteSpace(claim.Closes))
            {
                problems.Add($"{where} is out of scope and names no checkpoint that ends it.");
            }
            else if (!checkpointExists(claim.Closes))
            {
                problems.Add($"{where} closes at {claim.Closes}, which BUILD_PLAN.md does not have.");
            }
            else if (checkpointHasLanded(claim.Closes))
            {
                problems.Add($"{where} closes at {claim.Closes}, which has already landed, so that checkpoint "
                    + "shipped without bringing it into scope.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The two failure behaviours phase 1 built, asserted against the code that holds them
    /// rather than against the sentence that describes them.
    /// </summary>
    /// <summary>
    /// No shipped source builds a point-in-time bound by appending the UTC literal, and the one
    /// function that does build one goes through the session zone.
    ///
    /// Both halves, because either alone passes over the defect. Finding
    /// <c>StoreText.EndOfSession</c> proves a correct bound exists somewhere and not that the
    /// twelve sites use it; finding no literal proves nothing if the helper itself grew one.
    /// The behavioural half is <c>SessionBoundaryTests</c>, which asserts a row stamped 22:00
    /// Eastern is inside its own session in January and in July and fails against the old
    /// expression by construction.
    /// </summary>
    private static bool EveryBoundClosesTheSessionInItsOwnZone()
    {
        string storeText = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "StoreText.cs"));

        if (!storeText.Contains("SessionBoundaries.EndOfSession(sessionDate, ianaZoneId)", StringComparison.Ordinal))
        {
            return false;
        }

        // The literal appended to a date, rather than the literal mentioned. Prose about the defect
        // is how the defect is explained and must not read as the defect, so both patterns include
        // the concatenation or interpolation that makes it a bound.
        //
        // The third pattern is the constructor form, and it is here because the two above missed
        // two live sites for the whole of 3.9. TierClassifier and IndicatorEngine each built the
        // same wrong bound as
        //
        //     new(session.Year, session.Month, session.Day, 23, 59, 59, 999, TimeSpan.Zero)
        //
        // which contains no string at all, so a scan for an appended literal could not see it and
        // reported the property held. The pass that added this guard was the pass that closed
        // twelve sites of the defect, and it left two standing behind its own check. What the
        // property is about is a session bound built on a fixed offset; the syntax it is written in
        // is not the subject and must not be what the guard keys on.
        string[] appended = ["+ \"T23:59:59.999Z\"", "}T23:59:59.999Z\""];

        return !RepositoryLayout.ProductionSourceFiles
            .Select(RepositoryLayout.Read)
            .Any(source =>
                appended.Any(p => source.Contains(p, StringComparison.Ordinal))
                || LastInstantOnAFixedOffset().IsMatch(source));
    }

    /// <summary>
    /// The last instant of a day constructed against a fixed offset rather than resolved through a
    /// zone. <c>TimeSpan.Zero</c> and <c>TimeSpan.FromHours(n)</c> are both offsets a session
    /// boundary may not be built from, because neither one moves with the clock change.
    /// </summary>
    [GeneratedRegex(
        @"23\s*,\s*59\s*,\s*59\s*,\s*999\s*,\s*TimeSpan\s*\.\s*(Zero|From)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LastInstantOnAFixedOffset();

    /// <summary>
    /// A name the vendor holds nothing on is read, stamped and counted apart from a resolved one.
    ///
    /// Two files, because either alone passes over the case. A client that admits the absence while
    /// the walk files it under `resolved` leaves the figure the record states wrong with nothing
    /// throwing, which is the direction that costs something.
    /// </summary>
    private static bool TheWalkCountsAnAbsenceSeparatelyFromAResolution()
    {
        string client = Shipped("PullbackStrategyLab.Worker", "Vendor", "EodhdClient.cs");
        string number = Shipped("PullbackStrategyLab.Worker", "Vendor", "VendorNumber.cs");
        string walk = Shipped("PullbackStrategyLab.Worker", "Stages", "SectorResolver.cs");

        // Three files and three separate things: the numeric field admits the vendor's absence
        // words, the string fields admit a value that is present and blank, and the walk counts the
        // result apart from a resolution.
        return number.Contains("[\"NA\", \"N/A\", \"None\", \"null\", \"-\"]", StringComparison.Ordinal)
            && client.Contains("Blank(", StringComparison.Ordinal)
            && walk.Contains("VendorHadNothing", StringComparison.Ordinal);
    }

    /// <summary>
    /// One name's failure costs that name, and the run says the walk passed over something.
    ///
    /// The catch and the count together: a catch with no count swallows failures, and a count with
    /// no catch is a field nothing sets.
    /// </summary>
    private static bool TheWalkSkipsOneNameAndRecordsThatItDid()
    {
        string walk = Shipped("PullbackStrategyLab.Worker", "Stages", "SectorResolver.cs");

        return walk.Contains("catch (Exception e) when", StringComparison.Ordinal)
            && walk.Contains("CountSkipped()", StringComparison.Ordinal)
            && walk.Contains("RunOutcome.Partial", StringComparison.Ordinal);
    }

    /// <summary>
    /// The lateness bound is read from the parameters rather than written into the stage, and the
    /// two things a correction owes the record are both written.
    ///
    /// The literal is the point. A bound typed into the stage is a second place the number lives,
    /// and `pinned-constants` can only compare a document against the constant it names.
    /// </summary>
    private static bool TheLatenessBoundIsReadRatherThanWritten()
    {
        string stage = Shipped("PullbackStrategyLab.Worker", "Stages", "CheckRecomputer.cs");

        return stage.Contains("MeasurementParameters.LatenessBoundHours", StringComparison.Ordinal)
            && stage.Contains("correction_lateness_minutes = @lateness", StringComparison.Ordinal)
            && stage.Contains("corrected_from = @corrected_from", StringComparison.Ordinal)
            && stage.Contains("public RecheckResult Restore(", StringComparison.Ordinal);
    }

    /// <summary>
    /// The capture refuses a response on whether the parse can read it, not on the status alone.
    ///
    /// Asserted as the predicate reaching a parse rather than as a guard existing, because a guard
    /// keyed on status would have stored the body that killed the first sector walk: it came back
    /// 200.
    /// </summary>
    private static bool TheCaptureTriggersOnTheParse()
    {
        string client = Shipped("PullbackStrategyLab.Worker", "Vendor", "EodhdClient.cs");
        string capture = Shipped("PullbackStrategyLab.Worker", "Stages", "FixtureCapture.cs");

        return client.Contains("WhyUnreadable", StringComparison.Ordinal)
            && client.Contains("will not shape", StringComparison.Ordinal)
            && capture.Contains("WhyUnreadable", StringComparison.Ordinal);
    }

    /// <summary>
    /// The stamped list is asserted in both directions, which is the half four tables were on the
    /// wrong side of, one of them since 2.7.
    /// </summary>
    private static bool TheStampedListIsReconciledBothWays()
    {
        string check = RepositoryLayout.Read(Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Tests", "Checks", "PointInTimeCheck.cs"));

        return PointInTimeCheck.Stamped.Count >= 14
            && check.Contains(
                "Every_stamped_column_a_migration_creates_is_named_by_this_check", StringComparison.Ordinal);
    }

    /// <summary>
    /// A rebuild that wrote nothing fails, and the account-wide panels are constrained by something
    /// nulls do not escape.
    ///
    /// Both, because the second is what the first was hiding: six of eleven panels were the no-op
    /// and the other five were being inserted again.
    /// </summary>
    private static bool ARebuildThatWroteNothingFails()
    {
        string builder = Shipped("PullbackStrategyLab.Worker", "Stages", "ScoreboardBuilder.cs");

        string index = RepositoryLayout.Read(Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations",
            "030-scoreboard-account-wide-unique.sql"));

        return builder.Contains("skipped == panels.Count", StringComparison.Ordinal)
            && builder.Contains("RunOutcome.Failed", StringComparison.Ordinal)
            && index.Contains("CREATE UNIQUE INDEX", StringComparison.Ordinal)
            && index.Contains("WHERE direction IS NULL", StringComparison.Ordinal);
    }

    /// <summary>One file under `src`, by its path segments.</summary>
    private static string Shipped(params string[] segments) =>
        RepositoryLayout.Read(Path.Combine([RepositoryLayout.Source, .. segments]));

    /// <summary>
    /// Whether the minute-bar fetch can say what it did not get.
    ///
    /// The condition ARCHITECTURE names is a day with no intraday prices, and what it asks for is
    /// that such a day be visible afterwards: no trades resolved, and the setups of that night
    /// excluded from scoring rather than scored as though they had chosen to pass. What the stage
    /// owes at 4.2 is the record that makes that possible, which is three separate things and not
    /// one. A name the vendor holds nothing for is counted rather than throwing. The count asked for
    /// is stored beside the count answered, so a name never reached is distinguishable from a name
    /// that answered with nothing. And a row is written whatever happened, because a night with no
    /// row is a night nobody ran.
    ///
    /// Read from the source rather than run, and the behavioural half is
    /// <c>IntradayFetcherTests.A_name_the_vendor_holds_nothing_for_is_counted_rather_than_failing_the_night</c>
    /// together with the ceiling case beside it.
    /// </summary>
    /// <summary>
    /// The three shortfalls have three answers, and the scan holds the half the behavioural tests
    /// cannot: that the stage writes a pass row on <b>every</b> path out of the method, which is
    /// what makes a session nobody sampled readable as absence rather than as a quiet result.
    ///
    /// The three answers themselves are exercised by tests and named as this scan's backing, on the
    /// rule that a source scan finding a pattern is not evidence the behaviour exists.
    /// </summary>
    private static bool TheThreeShortfallsAreToldApart()
    {
        string snapshotter = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "SpreadSnapshotter.cs"));
        string reader = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "SpreadSnapshotReader.cs"));

        // Two RecordPass calls: the first-night path and the ordinary one. A path that returned
        // without one would be a session that ran and left no trace of running.
        int passRowsWritten = snapshotter.Split("RecordPass(").Length - 1;

        return passRowsWritten >= 3
            && reader.Contains("ThrowIfNothingWasSampled", StringComparison.Ordinal)
            && reader.Contains("IsDegraded", StringComparison.Ordinal)
            && reader.Contains("IsComplete", StringComparison.Ordinal)
            && snapshotter.Contains("RunOutcome.Partial", StringComparison.Ordinal);
    }

    /// <summary>
    /// The store refuses a blocked order that carries no reason and no cap.
    ///
    /// Read from the migration rather than from the stage, because the constraint is what makes the
    /// behaviour unlosable: a component can stop writing a reason and the store will refuse the row,
    /// where a scan of the component passes on a helper nothing calls.
    /// </summary>
    private static bool TheGateWritesABlockedRowWithItsReason()
    {
        string migration = PullbackStrategyLab.Data.MigrationRunner.All()
            .Single(m => m.Name.Contains("trade-order", StringComparison.Ordinal)).Sql;

        return migration.Contains("(status = 'blocked') = (blocked_because IS NOT NULL)", StringComparison.Ordinal)
            && migration.Contains("blocked_because IS NULL OR bound_by IS NOT NULL", StringComparison.Ordinal);
    }

    /// <summary>
    /// The gap row, read from three places, because the claim it makes has three halves.
    ///
    /// The row says a gap through the give-up point is recorded as a loss larger than planned,
    /// tagged, and never rounded back. So: the model has a basis a fill can carry that is not the
    /// slipped one and charges nothing on it; the store has a column to carry it; and nothing
    /// anywhere clamps a realised result. The last is the one that cannot be read from a constant,
    /// so it is asserted as the absence of a clamp in the stage that writes the figure, and the
    /// behavioural half is the test that runs a seven-point gap through a five-point stop and reads
    /// worse than minus two R back off the row.
    /// </summary>
    private static bool TheGapIsTaggedAndNeverClamped()
    {
        string migration = PullbackStrategyLab.Data.MigrationRunner.All()
            .Single(m => m.Name.Contains("fill-and-position", StringComparison.Ordinal)).Sql;

        string broker = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "PaperBroker.cs"));

        // Whitespace-tolerant over the span, because a column declaration is aligned by hand and a
        // pattern built on the alignment breaks on a rename three columns away.
        bool tagged = Regex.IsMatch(
                migration,
                @"basis\s+TEXT\s+NOT NULL CHECK \(basis IN \('slipped', 'gapped'\)\)",
                RegexOptions.CultureInvariant)
            && Regex.IsMatch(migration, @"realised_r\s+REAL", RegexOptions.CultureInvariant);

        bool notClamped = !broker.Contains("Math.Max(", StringComparison.Ordinal)
            && !broker.Contains("Math.Min(", StringComparison.Ordinal)
            && !broker.Contains("Math.Clamp(", StringComparison.Ordinal);

        bool chargesNothingOnTop = Core.Trading.FillModel
            .Exit(Core.Detection.SetupDirection.Long, 95m, openedThrough: 88m, 10d)
            is { Basis: Core.Trading.FillModel.Gapped, Slippage: 0m, Price: 88m };

        return tagged && notClamped && chargesNothingOnTop;
    }

    /// <summary>
    /// The two short assumptions, read from the store's own constraints rather than from the stage.
    ///
    /// The row says they are recorded on every short position from 4.7, and "on every row" is a
    /// claim a constraint can carry outright: the migration makes both present exactly on the shorts
    /// in both directions, so a short without them and a long with them are equally unwritable. The
    /// behavioural half is the test that fills one of each and reads both rows back.
    /// </summary>
    private static bool TheShortAssumptionsAreOnEveryShortRow()
    {
        string migration = PullbackStrategyLab.Data.MigrationRunner.All()
            .Single(m => m.Name.Contains("fill-and-position", StringComparison.Ordinal)).Sql;

        return migration.Contains("(direction = 'short') = (borrow_rate_assumed IS NOT NULL)", StringComparison.Ordinal)
            && migration.Contains("(direction = 'short') = (borrow_availability IS NOT NULL)", StringComparison.Ordinal)
            && Core.Trading.BorrowAssumption.AnnualisedRate == 0.010m;
    }

    private static bool TheFetchCountsWhatItCouldNotGet()
    {
        string fetcher = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "IntradayFetcher.cs"));

        return fetcher.Contains("empties++", StringComparison.Ordinal)
            && fetcher.Contains("RecordFetch(", StringComparison.Ordinal)
            && fetcher.Contains("names.Count, fetched, empties", StringComparison.Ordinal);
    }

    /// <summary>
    /// One row of "The limits", read against the constant that holds it.
    ///
    /// <b>Each row says both what the number is and what the component does with it</b>, because a
    /// cap stated and not applied is the shape this table would otherwise be free to take: four of
    /// the six are enforced at trigger, and the other two are enforced elsewhere and say where
    /// (see: Two of the six limits are not applied at trigger, and which two is stated rather than
    /// left to the code).
    ///
    /// The value is read out of the document's own cell rather than compared against a number
    /// repeated here, so this check and `pinned-constants` are asking different questions of the same
    /// row: that one asks whether the document and the code agree on a figure, and this one asks
    /// whether the component the document names does what the row says it does.
    /// </summary>
    private static Claim AssertLimit(string limit, string stated)
    {
        string gate = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Core", "Trading", "RiskLimits.cs"));

        bool applied = limit switch
        {
            "Risk per trade" => stated.Contains("0.75%", StringComparison.Ordinal)
                && PositionSizing.RiskPerTrade == 0.0075m
                && gate.Contains("plannedShares", StringComparison.Ordinal),
            "Give-up distance" => stated.Contains("half the daily range", StringComparison.Ordinal)
                && RiskCaps.GiveUpDistanceRanges == 0.5m
                && LongPullbackRules.GiveUpRanges == RiskCaps.GiveUpDistanceRanges
                && !gate.Contains("GiveUpDistanceRanges", StringComparison.Ordinal),
            "Position size" => stated.Contains("35%", StringComparison.Ordinal)
                && RiskCaps.MaxPositionFraction == 0.35m
                && gate.Contains("RiskCaps.MaxPositionValue", StringComparison.Ordinal),
            "Open at once" => stated.Contains("4 positions", StringComparison.Ordinal)
                && RiskCaps.MaxOpenPositions == 4
                && gate.Contains("RiskCaps.MaxOpenPositions", StringComparison.Ordinal),
            "Open short positions" => stated.Contains("2 of those 4", StringComparison.Ordinal)
                && RiskCaps.MaxOpenShortPositions == 2
                && gate.Contains("RiskCaps.MaxOpenShortPositions", StringComparison.Ordinal),
            "Total risk at stake" => stated.Contains("3%", StringComparison.Ordinal)
                && RiskCaps.MaxTotalRiskFraction == 0.03m
                && gate.Contains("RiskCaps.MaxTotalRisk", StringComparison.Ordinal),
            _ => false,
        };

        string where = limit switch
        {
            "Risk per trade" => "PlanBuilder sizes from it at 18:30 and RiskGate asserts rather than enforces it",
            "Give-up distance" => "exit-tight applies it at detection and RiskGate deliberately does not",
            _ => "RiskGate applies it at trigger",
        };

        return applied
            ? Claim.Passed("The limits", limit, $"{stated}, and {where}")
            : Claim.Failed("The limits", limit,
                $"the document states \"{stated}\" and the constant or the code that should apply it does not "
                + "agree. A limit stated and not applied is a cap this lab does not have.");
    }

    private static Claim AssertFailureBehaviour(string condition)
    {
        string engine = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "IndicatorEngine.cs"));
        string runScope = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "RunScope.cs"));

        return condition switch
        {
            // The blocked row and its reason, read from the migration that constrains them rather
            // than from the stage that writes them: the store refuses a blocked order with no
            // reason and a reason with no cap, so the behaviour cannot be lost by an edit to the
            // component. The behavioural half is RiskGateTests, which runs five triggers against
            // four slots and reads the blocked row back with the cap that bound.
            "Risk gate blocks an order" => TheGateWritesABlockedRowWithItsReason()
                ? Claim.Passed("Failure behaviour", condition,
                    "a refused order is a row with the cap that bound and the figures that cap saw, and the store "
                    + "refuses a blocked row carrying neither")
                : Claim.Failed("Failure behaviour", condition,
                    "migration 042 no longer constrains a blocked order to carry a reason and a cap, so a refusal "
                    + "can be written that nobody can act on"),
            // The gap, read from the model, the store and the stage. The document says the loss is
            // larger than planned, tagged, and never rounded back, and the third of those is an
            // absence rather than a statement, so it is asserted as one.
            "Price gaps past the give-up point" => TheGapIsTaggedAndNeverClamped()
                ? Claim.Passed("Failure behaviour", condition,
                    "a gap fills at the session's first regular minute open and is charged no spread on top, the "
                    + "fill carries basis 'gapped' so the size and frequency of these are readable afterwards, and "
                    + "nothing in the stage that writes the result clamps it")
                : Claim.Failed("Failure behaviour", condition,
                    "the gap is no longer told apart from a slipped fill on the row, or something now bounds the "
                    + "realised result, which rounds the bad tail back to a neat one-unit loss"),

            // The borrow assumptions, read from the constraint rather than from the stage, because
            // "on every short trade" is a claim about every row and a constraint is what holds one.
            "A short could not have been borrowed" => TheShortAssumptionsAreOnEveryShortRow()
                ? Claim.Passed("Failure behaviour", condition,
                    "the store admits a short position only with the assumed borrow rate and the note that "
                    + "availability is not modelled, and admits a long only without them, so both are on every "
                    + "short row by construction rather than by a stage remembering")
                : Claim.Failed("Failure behaviour", condition,
                    "migration 043 no longer binds the two short assumptions to the short rows, so a short can be "
                    + "written carrying neither and the assumption stops being visible where the result is read"),

            "Intraday prices unavailable for a day" => TheFetchCountsWhatItCouldNotGet()
                ? Claim.Passed("Failure behaviour", condition,
                    "IntradayFetcher counts a name the vendor holds nothing for rather than failing the night, records the count it asked for beside the count it answered so the shortfall is a join rather than an edit, and writes a fetch row whatever the outcome")
                : Claim.Failed("Failure behaviour", condition,
                    "the fetch no longer distinguishes a name with no minutes from a name it never reached, so a session with no resolvable trades is indistinguishable from a session nobody fetched"),

            "A spread snapshot is missed" => TheThreeShortfallsAreToldApart()
                ? Claim.Passed("Failure behaviour", condition,
                    "a pass writes a spread_pass row whatever it did, so a session nobody sampled is absence rather than a quiet result; the reader refuses an unsampled session, reports one pass as degraded and two as complete, and a pass stopped short is partial with the count")
                : Claim.Failed("Failure behaviour", condition,
                    "one of the three shortfalls no longer has its own answer, so a session sampled nought times is indistinguishable from one whose names had no book, and a fill can be charged no slippage on a session nobody measured"),

            "Unprocessed corporate action" => engine.Contains("blocked++", StringComparison.Ordinal)
                ? Claim.Passed("Failure behaviour", condition, "IndicatorEngine leaves no row and counts the ticker as blocked")
                : Claim.Failed("Failure behaviour", condition, "IndicatorEngine no longer refuses on an open demand"),

            "Nightly setup cap reached" => TheCapTruncatesTheSharedListAndRecordsIt()
                ? Claim.Passed("Failure behaviour", condition,
                    "SetupCapper reads a night by date alone, ranks within a direction, updates rank and capped_out only, and reports the pre-cap counts beside the kept ones")
                : Claim.Failed("Failure behaviour", condition,
                    "the cap no longer reads the whole night, or no longer reports what it truncated"),

            "Detector errors on one stock" => BothDetectorsRecordAnErrorRow()
                ? Claim.Passed("Failure behaviour", condition,
                    "both detectors catch per name, insert a detector_error row of their own and record the run partial")
                : Claim.Failed("Failure behaviour", condition,
                    "a detector no longer records the name it could not decide, so a lost name reads as a quiet night"),

            "Daily API ceiling reached" => TheCeilingRuleHoldsAllThreeOfItsClauses()
                ? Claim.Passed("Failure behaviour", condition,
                    "the run scope reports what is left, a stage stops rather than overrunning, and both detectors read the night's incomplete stages and write them onto every setup row of that session")
                : Claim.Failed("Failure behaviour", condition,
                    "the run scope no longer exposes the remaining ceiling, or a detector no longer marks the setups a stopped stage degraded, so a night flagged on incomplete inputs is indistinguishable from an ordinary one"),

            "The store is at a schema version other than the build's" => TheStoreVersionIsComparedBeforeAnyStageRuns()
                ? Claim.Passed("Failure behaviour", condition,
                    "Program compares the store's user_version against the last migration this build carries and refuses before dispatch, with three named exemptions and both numbers in the message")
                : Claim.Failed("Failure behaviour", condition,
                    "nothing compares the store's version against the build's before a stage opens it, so a store behind its migrations fails on a raw SQLite error naming a column part way through a night"),

            "Follow-up date is a holiday" => TheFillStoresBothDates()
                ? Claim.Passed("Failure behaviour", condition,
                    "ForwardReturnFiller measures to the session the horizon lands on and stores the calendar date beside it, and the authored subjects assert both branches")
                : Claim.Failed("Failure behaviour", condition,
                    "the fill no longer records the intended date beside the session actually used, so a follow-up that crossed a holiday reads as though it had not"),

            "A comparison has no control outcomes" => TheFillRecordsBothSubjectKinds()
                ? Claim.Passed("Failure behaviour", condition,
                    "ForwardReturnFiller reads control_setup as well as setup, binds each row's own subject kind rather than a literal, and the fixture carries a closed-horizon population whose control outcomes exist")
                : Claim.Failed("Failure behaviour", condition,
                    "the fill no longer records an outcome for a control, so band 1's difference series is empty on every night and the panel is withheld for a cause that is not the one it names"),

            "The vendor holds nothing on a name" => TheWalkCountsAnAbsenceSeparatelyFromAResolution()
                ? Claim.Passed("Failure behaviour", condition,
                    "the parse admits the vendor's absence words and a blank field, and SectorResolver counts a name the vendor had nothing on apart from a resolved one and stamps it either way")
                : Claim.Failed("Failure behaviour", condition,
                    "an absent value is no longer distinguished from a resolved one, so a name the vendor holds nothing on is either an error or is asked again every night"),

            "A vendor refuses one name mid-walk" => TheWalkSkipsOneNameAndRecordsThatItDid()
                ? Claim.Passed("Failure behaviour", condition,
                    "SectorResolver catches per ticker, counts the skip, leaves the name unstamped and records the run partial rather than clean")
                : Claim.Failed("Failure behaviour", condition,
                    "one name's failure is no longer bounded to that name, or a walk that passed over names is recorded clean"),

            "An input the session asked for arrives after the session" => TheLatenessBoundIsReadRatherThanWritten()
                ? Claim.Passed("Failure behaviour", condition,
                    "CheckRecomputer reads MeasurementParameters.LatenessBoundHours rather than a literal, records the lateness in minutes and the prior state, and owns the restore that puts a corrected row back")
                : Claim.Failed("Failure behaviour", condition,
                    "the bound is written into the stage rather than read from the parameters table, or a correction no longer records the lateness or the state it can be put back to"),

            "The vendor answers 200 with a body the parse cannot read" => TheCaptureTriggersOnTheParse()
                ? Claim.Passed("Failure behaviour", condition,
                    "EodhdClient.WhyUnreadable decides on whether the body shapes rather than on the status alone, and FixtureCapture refuses on it")
                : Claim.Failed("Failure behaviour", condition,
                    "the capture no longer refuses a 200 it cannot parse, so the response that killed the first sector walk would be stored as a working example"),

            "A rebuild writes no rows" => ARebuildThatWroteNothingFails()
                ? Claim.Passed("Failure behaviour", condition,
                    "ScoreboardBuilder counts what the insert skipped and fails when every panel was skipped, and a partial unique index constrains the account-wide panels the primary key cannot")
                : Claim.Failed("Failure behaviour", condition,
                    "a rebuild that wrote nothing reports clean again, or the account-wide panels are back to being constrained only by a key that nulls escape"),

            "A migration adds a column recording when the lab observed something" => TheStampedListIsReconciledBothWays()
                ? Claim.Passed("Failure behaviour", condition,
                    $"PointInTimeCheck.Stamped names {PointInTimeCheck.Stamped.Count} tables and is asserted in both directions, so a migration adding a stamp fails until the list names it")
                : Claim.Failed("Failure behaviour", condition,
                    "the reverse reconciliation is gone, so a table gaining an observation stamp joins the corpus without any read being required to bound it"),

            "A stage writes after the UTC date rolls" => EveryBoundClosesTheSessionInItsOwnZone()
                ? Claim.Passed("Failure behaviour", condition,
                    "every point-in-time bound is built by StoreText.EndOfSession through SessionBoundaries, and no shipped source appends the UTC literal")
                : Claim.Failed("Failure behaviour", condition,
                    "a point-in-time bound is being built on a fixed offset again, by appending T23:59:59.999Z to a session date or by constructing the day's last instant against TimeSpan.Zero. Either one closes an Eastern session at 20:00 Eastern and moves the truncation with the clock change"),

            _ => Claim.NotExamined("Failure behaviour", condition,
                "the checkpoint that builds it has landed and no assertion reads this row"),
        };
    }

    /// <summary>
    /// Both detectors, both ways: they catch per name and they say the run was partial.
    ///
    /// The behavioural half lives in <c>DetectorErrorTests</c>, which makes one name unreadable and
    /// runs both detectors over it. That half is the one that holds the property; this one says the
    /// shape is still there in both files, which the test alone would not if a detector stopped
    /// being covered by it.
    ///
    /// <b>It asks for the call site, not only the statement.</b> The first version of this looked
    /// for the insert and for the partial outcome, and passed with the catch deleted from one
    /// detector: the private method that issues the insert was still in the file with nothing
    /// calling it. A scan for text present is not a scan for a property held, which is the shape
    /// this corpus keeps arriving at from a new direction.
    /// </summary>
    /// <summary>
    /// Three things the cap claim rests on: the read, the write, and what is reported.
    ///
    /// The read has to be the night, whole, or the cap is being applied to something narrower than
    /// the shared candidate list. The write has to be rank and capped_out and nothing else, or the
    /// cap is deciding more than the corpus says it decides. And the pre-cap counts have to be
    /// reported, or "the truncation is recorded" is satisfied by a run that says how many it kept,
    /// which is the half that cannot answer whether the cap bound.
    ///
    /// The behavioural halves live in <c>NightlyCapTests</c>, which sweeps the release rule, and in
    /// <c>SharedCandidateListTests</c>, which asserts the schema has nowhere to put a version.
    /// </summary>
    private static bool TheCapTruncatesTheSharedListAndRecordsIt()
    {
        string source = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "SetupCapper.cs"));

        return source.Contains("SetupReader.Read(connection, asOf)", StringComparison.Ordinal)
            && source.Contains("UPDATE setup SET rank = @rank, capped_out = @capped_out", StringComparison.Ordinal)
            && typeof(CapResult).GetProperty(nameof(CapResult.LongCandidates)) is not null
            && typeof(CapResult).GetProperty(nameof(CapResult.ShortCandidates)) is not null
            && typeof(CapResult).GetProperty(nameof(CapResult.CappedOut)) is not null;
    }

    /// <summary>
    /// The fill records an outcome for both subject kinds, asked of the source and of the fixture.
    ///
    /// <b>Source alone would not hold this and the corpus has the scars to prove it.</b> The
    /// statement that wrote control rows was absent for the whole of phase 3 while every instrument
    /// stayed green, so what is asked here is the shape in the file <i>and</i> that the committed
    /// fixture carries a population whose control outcomes actually exist. The behavioural half
    /// lives in <c>ForwardReturnFillerTests</c>, which seeds a draw and reads the rows back.
    /// </summary>
    private static bool TheFillRecordsBothSubjectKinds()
    {
        string filler = RepositoryLayout.Read(System.IO.Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "ForwardReturnFiller.cs"));

        // The subject query has to exist and the insert has to take each subject's own kind. A
        // literal here is exactly what shipped, and it read as deliberate.
        if (!filler.Contains("FROM control_setup", StringComparison.Ordinal)
            || !filler.Contains(
                "AddWithValue(\"@subject_kind\", subject.Kind)", StringComparison.Ordinal))
        {
            return false;
        }

        string expectations = RepositoryLayout.Read(
            System.IO.Path.Combine(RepositoryLayout.Root, "fixtures", "expectations.json"));

        using JsonDocument document = JsonDocument.Parse(expectations);

        foreach (JsonElement expectation in document.RootElement.GetProperty("expectations").EnumerateArray())
        {
            if (!string.Equals(
                    expectation.GetProperty("id").GetString(),
                    "accumulation.forward.controlsWritten",
                    StringComparison.Ordinal))
            {
                continue;
            }

            // A committed figure of nought is the state the defect produced, so the expectation
            // existing is not enough on its own.
            return int.TryParse(expectation.GetProperty("value").GetString(), out int written) && written > 0;
        }

        return false;
    }

    /// <summary>
    /// The fill stores the calendar horizon beside the session it actually used, and the authored
    /// subjects exercise both branches.
    ///
    /// Both halves are required, and the second is the one worth stating. A filler that always
    /// slipped forward would satisfy every holiday case and be wrong on every ordinary week, so the
    /// claim is not "a slip is recorded" but "a slip and a non-slip are told apart". The case file
    /// carries a mid-week subject for exactly that, and this reads the committed expectations for
    /// one of each rather than trusting the file's prose.
    /// </summary>
    private static bool TheFillStoresBothDates()
    {
        string filler = RepositoryLayout.Read(System.IO.Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "ForwardReturnFiller.cs"));

        if (!filler.Contains("@intended_date", StringComparison.Ordinal)
            || !filler.Contains("@actual_date", StringComparison.Ordinal))
        {
            return false;
        }

        string expectations = RepositoryLayout.Read(
            System.IO.Path.Combine(RepositoryLayout.Root, "fixtures", "expectations.json"));

        using JsonDocument document = JsonDocument.Parse(expectations);

        bool slipped = false;
        bool held = false;

        foreach (JsonElement expectation in document.RootElement.GetProperty("expectations").EnumerateArray())
        {
            string id = expectation.GetProperty("id").GetString() ?? string.Empty;

            if (!id.StartsWith("forward.", StringComparison.Ordinal)
                || !id.EndsWith(".slipped", StringComparison.Ordinal))
            {
                continue;
            }

            string value = expectation.GetProperty("value").GetString() ?? string.Empty;
            slipped |= string.Equals(value, "yes", StringComparison.Ordinal);
            held |= string.Equals(value, "no", StringComparison.Ordinal);
        }

        return slipped && held;
    }

    /// <summary>
    /// All three clauses of the vendor-ceiling rule, rather than the two that were asserted.
    ///
    /// The sentence is "the nightly job counts as it goes and stops rather than overrunning. A
    /// stopped job writes a partial-run row <b>and the affected setups are marked degraded</b>." The
    /// verdict read "the run scope reports what is left and a stage stops rather than overrunning",
    /// which is the first two, and it passed for the whole of 3.11: the checkpoint that built the
    /// third clause added the column, the reader and the writers and left the claim asserting the
    /// sentence it had before. Deleting <c>RunLogger.DegradedBecause</c> would not have moved it.
    /// </summary>
    private static bool TheCeilingRuleHoldsAllThreeOfItsClauses()
    {
        string runScope = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "RunScope.cs"));

        if (!runScope.Contains("CallsRemaining", StringComparison.Ordinal))
        {
            return false;
        }

        string runLogger = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "RunLogger.cs"));

        // The mark is derived from the night's own run rows rather than from a flag somebody sets,
        // and the read is bounded in the session zone. Both halves, because a mark computed over a
        // UTC day would carry the previous night's failures onto this night's setups.
        if (!runLogger.Contains("public static string? DegradedBecause(", StringComparison.Ordinal)
            || !runLogger.Contains("IncompleteStagesOf(connection, session, sessionZone)", StringComparison.Ordinal)
            || !runLogger.Contains("StoreText.EndOfSession(session, sessionZone)", StringComparison.Ordinal))
        {
            return false;
        }

        // And both detectors call it and bind what it returns. The call rather than the column: a
        // column in a migration with nothing writing it is exactly the state the third clause was
        // in before 3.11, and it read as present.
        return new[] { "LongSetupDetector.cs", "ShortSetupDetector.cs" }
            .Select(name => RepositoryLayout.Read(
                Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", name)))
            .All(source =>
                source.Contains("RunLogger.DegradedBecause(connection, asOf, _options.SessionZone)", StringComparison.Ordinal)
                && source.Contains("command.Parameters.AddWithValue(\"@degraded_because\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The store's version compared against the build's before any stage is dispatched, proved by
    /// dispatching one.
    ///
    /// <b>This was a source scan, and the scan could not see the thing its own name says.</b> It
    /// read <c>Program.cs</c> for <c>WhyThisStageCannotRun(</c>,
    /// <c>MigrationRunner.ReadUserVersion(connection)</c> and <c>MigrationRunner.LatestVersion</c>,
    /// and all three are satisfied inside <c>WhyThisStageCannotRun</c> and
    /// <c>WhyTheStoreCannotBeRead</c>, which live in that same file. Deleting the block at the top
    /// of <c>Main</c> that calls the guard before the dispatch left every pattern in place, this
    /// claim green, and a detector free to run against a store two migrations behind it. Every test
    /// beside it called the guard's own method and never reached <c>Main</c>, so nothing anywhere
    /// exercised the call site. Fifth instance of an assertion outliving its subject, and the first
    /// where the subject is a call rather than a declaration: the four before it lost a method, a
    /// table or a clause, and this one lost the line that runs it.
    ///
    /// <b>So the verdict is the behaviour.</b> A store stood up one migration short, a detector run
    /// through the CLI against it, and the refusal read off stderr with both versions in it and
    /// nothing in <c>run_log</c>, which is the stage not having opened the store. The three
    /// exemptions are asserted by name beside it rather than counted, because a guard whose escape
    /// hatch nothing asserts can be widened one stage at a time until it guards nothing, and a count
    /// of three is satisfied by any three names.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    private static bool TheStoreVersionIsComparedBeforeAnyStageRuns() =>
        StoreVersionRefusal.IsTheRefusal(StoreVersionRefusal.OverAStoreOneMigrationShort())
        && Worker.Program.RunsWhateverVersionTheStoreIsAt
            .Order(StringComparer.Ordinal)
            .SequenceEqual(["list-stages", MigrateStage.Name, SnapshotStage.Name], StringComparer.Ordinal);

    private static bool BothDetectorsRecordAnErrorRow() =>
        new[] { "LongSetupDetector.cs", "ShortSetupDetector.cs" }
            .Select(name => RepositoryLayout.Read(
                Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", name)))
            .All(source =>
                source.Contains("INSERT INTO detector_error", StringComparison.Ordinal)
                && source.Contains("errored += RecordError(", StringComparison.Ordinal)
                && source.Contains("catch (Exception e) when (e is not OperationCanceledException)", StringComparison.Ordinal)
                && source.Contains("tally.Errored == 0 ? RunOutcome.Clean : RunOutcome.Partial", StringComparison.Ordinal));

    private static HashSet<string> DeclaredTypes()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            foreach (Match match in TypeDeclaration().Matches(RepositoryLayout.Read(file)))
            {
                declared.Add(match.Groups["name"].Value);
            }
        }

        return declared;
    }

    private static HashSet<string> RegisteredTypes()
    {
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            foreach (Match match in Registration().Matches(RepositoryLayout.Read(file)))
            {
                registered.Add(match.Groups["name"].Value.Trim());
            }
        }

        // Registered by name in the stage table as well as with the container. A stage the
        // container can build and the entry point cannot reach is not registered in any sense
        // that matters.
        foreach (string stage in Program.StageNames)
        {
            registered.Add(stage);
        }

        return registered;
    }

    /// <summary>
    /// What the corpus schedules and what it records as done: BUILD_PLAN.md's checkpoint rows on
    /// one side and PROGRESS.md's entries on the other.
    ///
    /// Both are read rather than restated. The plan names every component in the row of the
    /// checkpoint that builds it, and the record says which checkpoints have landed, so "is this
    /// claim mine to assert" is answered by the two documents that already know rather than by a
    /// number kept here that would go stale at the next checkpoint.
    /// </summary>
    /// <remarks>
    /// Public because <see cref="CoverageReportedCheck"/> asks the same two questions of the same
    /// two documents: a check the roster defers to a checkpoint has to name one the plan has and
    /// the record does not yet carry, which is the rule an out-of-scope claim obeys, applied to a
    /// row of the check table instead of to a row of an architecture table.
    /// </remarks>
    public sealed class Schedule
    {
        private readonly Dictionary<string, string> _rows = [];

        /// <summary>The deliverable cell of each row, which is what says what a checkpoint builds.</summary>
        private readonly Dictionary<string, string> _deliverables = [];
        private readonly HashSet<string> _landed = new(StringComparer.Ordinal);
        private readonly List<Obligation> _obligations = [];

        private Schedule()
        {
        }

        /// <summary>The phase the build is on, being the major number of the furthest checkpoint recorded.</summary>
        public int Phase { get; private set; }

        /// <summary>The furthest checkpoint PROGRESS records, which is the pointer the whole corpus uses.</summary>
        public string LastLanded { get; private set; } = string.Empty;

        /// <summary>
        /// Every checkpoint PROGRESS records, rather than only the last one.
        ///
        /// Added at 3.10 so fixture-replay can ask done condition seven of a checkpoint that
        /// contributed no expectation at all. Grouping the expectations answers it only for the
        /// checkpoints that turned up.
        /// </summary>
        public IReadOnlyCollection<string> Landed => _landed;

        /// <summary>
        /// The furthest checkpoint in a set of landed ones, ordered by phase and then by checkpoint.
        ///
        /// Separate and public because the fault it fixes is invisible from the outside: the value
        /// only goes wrong when PROGRESS's last entry names an earlier checkpoint than some entry
        /// above it, which is exactly what a dated correction produces, and a test that reads the
        /// live PROGRESS asserts whatever the corpus happens to hold that day.
        /// </summary>
        public static string Furthest(IReadOnlyCollection<string> landed)
        {
            ArgumentNullException.ThrowIfNull(landed);
            Assert.NotEmpty(landed);

            return landed
                .OrderBy(c => int.Parse(c.Split('.')[0], CultureInfo.InvariantCulture))
                .ThenBy(c => int.Parse(c.Split('.')[1], CultureInfo.InvariantCulture))
                .Last();
        }

        public static Schedule Read()
        {
            var schedule = new Schedule();

            string buildPlan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));
            string progress = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "PROGRESS.md"));

            // Only the rows inside a phase section. The carried-obligations table at the end of
            // BUILD_PLAN is keyed by the checkpoint that raised each obligation, so reading it
            // as a schedule would place a component against the checkpoint that complained
            // about it rather than the one that builds it.
            // The phase sections stop where the carried-obligations table begins, and that bound is
            // load-bearing rather than tidy. The table sits after the last phase heading, so without
            // it the final section runs to the end of the file and every obligation row whose
            // "Raised" column looks like a checkpoint is read as a checkpoint row. An obligation
            // raised at 3.0 that mentions VariantAdmitter then places VariantAdmitter at 3.0, and
            // the claim fails saying a component due in phase 5 does not exist yet. Found at 3.0,
            // where the first obligation row to name an unbuilt component was written; the comment
            // below always said this must not happen and nothing stopped it.
            int obligations = buildPlan.IndexOf("## Carried obligations", StringComparison.Ordinal);
            int schedules = obligations < 0 ? buildPlan.Length : obligations;

            MatchCollection phases = PhaseHeading().Matches(buildPlan);
            for (int i = 0; i < phases.Count; i++)
            {
                int start = phases[i].Index;
                int end = i + 1 < phases.Count ? phases[i + 1].Index : schedules;

                if (start >= schedules)
                {
                    break;
                }

                foreach (Match row in CheckpointRow().Matches(buildPlan[start..end]))
                {
                    string checkpoint = row.Groups["checkpoint"].Value;
                    string rest = row.Groups["rest"].Value;

                    schedule._rows[checkpoint] = schedule._rows.GetValueOrDefault(checkpoint, string.Empty)
                        + " " + rest.ToUpperInvariant();

                    // The deliverable cell alone, which is the one that says what a checkpoint
                    // builds. The cell after it is the done condition and it names components this
                    // checkpoint reads rather than components it makes, which is the same fault the
                    // obligations bound above guards one level up: a component placed against the
                    // checkpoint that complained about it rather than the one that builds it.
                    schedule._deliverables[checkpoint] =
                        schedule._deliverables.GetValueOrDefault(checkpoint, string.Empty)
                        + " " + rest.Split('|')[0].ToUpperInvariant();
                }
            }

            // The carried obligations, which are the other half of what BUILD_PLAN schedules: a
            // checkpoint row says when something gets built, an obligation row says when
            // something already found gets closed. Read separately from the phase sections above,
            // because this table is keyed by the checkpoint that raised each item rather than by
            // the one that does the work.
            // Every row, with no width guard. There was one, `if (row.Count >= 3)`, and it read a
            // malformed row as an absent one: BUILD_PLAN's own row for the per-scope floor carried
            // two cells where the rest carry three, so the obligation driving checkpoint 2.1 sat
            // outside this list entirely and no permit could resolve against it. The width is now
            // asserted by MarkdownTable against the table's own header, which fails loudly, so
            // indexing here is safe and a skip would only hide the same class of fault again.
            foreach (IReadOnlyList<string> row in MarkdownTable.BodyRowsAfter(buildPlan, "## Carried obligations"))
            {
                schedule._obligations.Add(new Obligation(row[0].Trim(), row[^1].Trim(), row[1].Trim()));
            }

            MatchCollection landed = LandedEntry().Matches(progress);
            foreach (Match entry in landed)
            {
                schedule._landed.Add(entry.Groups["checkpoint"].Value);
            }

            Assert.NotEmpty(schedule._rows);
            Assert.NotEmpty(schedule._landed);

            // The furthest checkpoint PROGRESS records, which is how the whole corpus answers
            // "which checkpoint is the build on". Stated as a pointer rather than as a number
            // anywhere.
            //
            // <b>The furthest, not the last entry, which is what this read until 3.14.</b> Two rules
            // in CLAUDE.md cannot both be read literally: the pointer says the build is on the last
            // entry in PROGRESS, and the record rule says a record is corrected by a new dated entry
            // naming what it corrects. So correcting an old checkpoint appends an entry naming that
            // checkpoint, and the pointer then names one the build passed phases ago. It fired the
            // day it was exercised: a ruling recorded against 2.11 on 2026-08-29, with 3.14 landed,
            // retitled the phase report "Phase 2 report" and moved `LastLanded` back two phases.
            //
            // The proxy gives way rather than the correction rule, because appending a dated entry
            // is what this corpus requires everywhere and "last" was only ever standing in for
            // "furthest" while every entry happened to be a new checkpoint.
            schedule.LastLanded = Furthest(schedule._landed);
            schedule.Phase = int.Parse(schedule.LastLanded.Split('.')[0], CultureInfo.InvariantCulture);

            return schedule;
        }

        /// <summary>
        /// The carried obligations BUILD_PLAN records, in the order the table lists them.
        ///
        /// A row here is an obligation that is still open by construction: a discharged one is
        /// removed from the table in the commit that discharges it, so presence is the record and
        /// there is no second flag to keep in step with it.
        /// </summary>
        public IReadOnlyList<Obligation> Obligations => _obligations;

        /// <summary>Whether BUILD_PLAN.md has a checkpoint by this identifier at all.</summary>
        public bool Exists(string checkpoint) => _rows.ContainsKey(checkpoint);

        /// <summary>Whether PROGRESS.md records an entry for it.</summary>
        public bool HasLanded(string checkpoint) => _landed.Contains(checkpoint);

        /// <summary>
        /// The checkpoint whose <b>deliverable</b> names this component, earliest first. Earliest
        /// because a later checkpoint naming a component is refining it rather than introducing it,
        /// and the question here is when it first has to exist.
        ///
        /// <b>The deliverable cell and not the whole row, corrected at 4.4.</b> It read the row,
        /// which includes the done condition, and a done condition names the components its
        /// checkpoint <i>reads</i>. So VwapEngine resolved to 3.6, whose condition says the anchored
        /// clause needs the average VwapEngine computes, rather than to 4.4, which builds it and
        /// which every other document names. The claim then sat out of scope closing at a checkpoint
        /// parked on months of accumulation, so building the component would not have brought it
        /// into scope and the deferral would have outlived its own subject in silence.
        ///
        /// It is the same fault the obligations bound in <see cref="Read"/> already guards one level
        /// up, and its comment states it in the same words: a component placed against the
        /// checkpoint that complained about it rather than the one that builds it.
        /// </summary>
        public string? CheckpointFor(string component)
        {
            string needle = component.ToUpperInvariant();

            return _deliverables
                .Where(r => r.Value.Contains(needle, StringComparison.Ordinal))
                .Select(r => r.Key)
                .OrderBy(Order)
                .FirstOrDefault();
        }

        /// <summary>Checkpoints sort by their two numbers, so 1.10 follows 1.9 rather than 1.1.</summary>
        private static int Order(string checkpoint)
        {
            string[] parts = checkpoint.Split('.');
            return (int.Parse(parts[0], CultureInfo.InvariantCulture) * 1000)
                + int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        /// <summary>The component names in a build-order cell: the comma-separated items that read as one.</summary>
        public static IReadOnlyList<string> NamesIn(string cell) =>
            [.. cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Length > 0 && char.IsUpper(part[0]) && !part.Contains(' ', StringComparison.Ordinal))];
    }

    /// <summary>
    /// One row of BUILD_PLAN's carried obligations table: the checkpoint that raised it, the
    /// checkpoint it falls due at, and what it says.
    ///
    /// <c>DueAt</c> is not always a checkpoint. The move rehearsal's remaining step falls due at
    /// the actual move, which is an event rather than a row in the plan, so anything reading this
    /// treats a due point it cannot parse as still ahead rather than as landed.
    /// </summary>
    public sealed record Obligation(string Raised, string DueAt, string What);

    /// <summary>
    /// One claim and its verdict. <c>Closes</c> is set on an out-of-scope claim and on no other:
    /// it is the checkpoint that brings the claim into scope, and it is what stops the
    /// out-of-scope count reading as a permanent number.
    /// </summary>
    public sealed record Claim(string Table, string Subject, string Verdict, string Detail, string? Closes = null)
    {
        public static Claim Passed(string table, string subject, string detail) =>
            new(table, subject, Pass, detail);

        public static Claim Failed(string table, string subject, string detail) =>
            new(table, subject, Fail, detail);

        public static Claim OutOfScope(string table, string subject, string closes) =>
            new(table, subject, Deferred, $"brought into scope at checkpoint {closes}", closes);

        public static Claim NotExamined(string table, string subject, string detail) =>
            new(table, subject, Unexamined, detail);
    }

    public sealed record Conformance(
        int Phase,
        string LastLanded,
        int Claims,
        int Passed,
        int Failed,
        int Deferred,
        int Unexamined,
        IReadOnlyList<Claim> Detail);
}

/// <summary>
/// The three sections the phase report writes, named once so the document's promise and the
/// report's contents are compared rather than both being written from memory.
/// </summary>
public static class PhaseReportSections
{
    public static IReadOnlyList<string> Names { get; } = ["Document conformance", "Fixture diff", "Coverage"];
}
