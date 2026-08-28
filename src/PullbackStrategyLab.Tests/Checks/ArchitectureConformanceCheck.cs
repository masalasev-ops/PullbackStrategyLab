using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    /// The failure-behaviour table states conditions in prose, so it names no component a parser
    /// could follow. Each row is placed here by hand against the checkpoint that builds the
    /// behaviour, and a row this list does not name is unexamined rather than skipped, which is
    /// what makes adding a row to that table visible.
    /// </summary>
    public static IReadOnlyDictionary<string, string> FailureBehaviourCheckpoints { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Intraday prices unavailable for a day"] = "4.2",
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
        ["A stage writes after the UTC date rolls"] = "3.8",
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

            claims.Add(registered.Contains(component)
                ? Claim.Passed("Component catalogue", component, "declared and registered")
                : Claim.Failed("Component catalogue", component,
                    $"{component} is declared and is not registered with the container, so nothing can resolve it"));
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
        string? riskGate = schedule.CheckpointFor(LimitsAreEnforcedBy);
        foreach (IReadOnlyList<string> row in limits)
        {
            claims.Add(riskGate is null
                ? Claim.NotExamined("The limits", row[0], $"no checkpoint names {LimitsAreEnforcedBy}, which is what applies these")
                : !schedule.HasLanded(riskGate)
                    ? Claim.OutOfScope("The limits", row[0], riskGate)
                    : Claim.NotExamined("The limits", row[0], $"{LimitsAreEnforcedBy} exists and no assertion reads this row yet"));
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
        // behaviour, and it is where the fourth instance of an assertion outliving its subject
        // shipped: the detector-error claim passed with the catch clause deleted, because the
        // private method issuing the insert was still in the file with nothing calling it. Each
        // of the four names what exercises it, so a claim resting on text alone is visible.
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
                    "RunLoggerTests.A_stage_stops_at_the_ceiling_and_completes_partial_rather_than_overrunning",
                    "the stage is given a ceiling it reaches mid-run and stops, and the run entry says partial. "
                    + "The scan asks only that the run scope still exposes what is left"))
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
                    + "the dispatch passes. The test resolves each advertised name against the dispatch, which "
                    + "is exactly the gap between registered and reachable"));

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
         "Running on Windows and macOS", "The procedure"];

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
        ["What differs in management"] = "4.8",
        ["Why each loss happened"] = "4.10",
        ["What each tier of change can be replayed against"] = "5.3",
        ["What the pack contains"] = "6.4",
        ["Model budget"] = "6.5",
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
        string[] hereStores = StoresNamedIn(here);
        string[] thereStores = StoresNamedIn(there);

        return hereStores.SequenceEqual(thereStores, StringComparer.Ordinal)
            ? Claim.Passed("The procedure", $"step {step}", "states the same stores as RUNBOOK.md's step of the same number")
            : Claim.Failed("The procedure", $"step {step}",
                $"names stores [{string.Join(", ", hereStores)}] where RUNBOOK.md names [{string.Join(", ", thereStores)}]. "
                + "The same procedure in two documents, disagreeing about what an operator counts");
    }

    private static string[] StoresNamedIn(string prose) =>
        [.. StoreTable().Matches(prose).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A store named in prose. Deliberately the phase 2 to 5 table names alongside the ones that
    /// exist, because those are the ones a procedure written before they existed reaches for, and
    /// naming a table that is not there is what makes the step count nothing.
    /// </summary>
    [GeneratedRegex(@"\b(?:setup|setup_signal|forward_return|trade|variant|daily_bar|indicator_daily|run_log)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StoreTable();

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
        string[] appended = ["+ \"T23:59:59.999Z\"", "}T23:59:59.999Z\""];

        return !RepositoryLayout.ProductionSourceFiles
            .Select(RepositoryLayout.Read)
            .Any(source => appended.Any(p => source.Contains(p, StringComparison.Ordinal)));
    }

    private static Claim AssertFailureBehaviour(string condition)
    {
        string engine = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "IndicatorEngine.cs"));
        string runScope = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "RunScope.cs"));

        return condition switch
        {
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

            "Daily API ceiling reached" => runScope.Contains("CallsRemaining", StringComparison.Ordinal)
                ? Claim.Passed("Failure behaviour", condition, "the run scope reports what is left and a stage stops rather than overrunning")
                : Claim.Failed("Failure behaviour", condition, "the run scope no longer exposes the remaining ceiling"),

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

            "A stage writes after the UTC date rolls" => EveryBoundClosesTheSessionInItsOwnZone()
                ? Claim.Passed("Failure behaviour", condition,
                    "every point-in-time bound is built by StoreText.EndOfSession through SessionBoundaries, and no shipped source appends the UTC literal")
                : Claim.Failed("Failure behaviour", condition,
                    "a bound is being built by appending T23:59:59.999Z to a session date again, which closes an Eastern session at 20:00 Eastern and moves the truncation with the clock change"),

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
        private readonly HashSet<string> _landed = new(StringComparer.Ordinal);
        private readonly List<Obligation> _obligations = [];

        private Schedule()
        {
        }

        /// <summary>The phase the build is on, which is the major number of the last checkpoint recorded.</summary>
        public int Phase { get; private set; }

        /// <summary>The last checkpoint PROGRESS records, which is the pointer the whole corpus uses.</summary>
        public string LastLanded { get; private set; } = string.Empty;

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
                    schedule._rows[checkpoint] = schedule._rows.GetValueOrDefault(checkpoint, string.Empty)
                        + " " + row.Groups["rest"].Value.ToUpperInvariant();
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

            // The last entry in PROGRESS, which is how the whole corpus answers "which checkpoint
            // is the build on". Stated as a pointer rather than as a number anywhere.
            schedule.LastLanded = landed[^1].Groups["checkpoint"].Value;
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
        /// The checkpoint whose row names this component, earliest first. Earliest because a
        /// later checkpoint mentioning a component is refining it rather than introducing it,
        /// and the question here is when it first has to exist.
        /// </summary>
        public string? CheckpointFor(string component)
        {
            string needle = component.ToUpperInvariant();

            return _rows
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
