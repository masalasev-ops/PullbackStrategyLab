using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
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

        foreach (IGrouping<string, Claim> table in claims.GroupBy(c => c.Table, StringComparer.Ordinal))
        {
            coverage.Examined($"claims in {table.Key}", table.Count(c => c.Verdict is Pass or Fail));

            Claim[] deferred = [.. table.Where(c => c.Verdict == Deferred)];
            if (deferred.Length > 0)
            {
                coverage.OutOfScope($"claims in {table.Key}", deferred.Length,
                    "closed by " + string.Join(", ",
                        deferred.Select(c => c.Closes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
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
        Assert.True(catalogue.Count == 52, $"The component catalogue parsed {catalogue.Count} rows and states 52.");
        Assert.True(failures.Count == FailureBehaviourCheckpoints.Count,
            $"The failure-behaviour table has {failures.Count} rows and {FailureBehaviourCheckpoints.Count} are placed at a checkpoint.");
    }

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

            "Daily API ceiling reached" => runScope.Contains("CallsRemaining", StringComparison.Ordinal)
                ? Claim.Passed("Failure behaviour", condition, "the run scope reports what is left and a stage stops rather than overrunning")
                : Claim.Failed("Failure behaviour", condition, "the run scope no longer exposes the remaining ceiling"),

            _ => Claim.NotExamined("Failure behaviour", condition,
                "the checkpoint that builds it has landed and no assertion reads this row"),
        };
    }

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
    private sealed class Schedule
    {
        private readonly Dictionary<string, string> _rows = [];
        private readonly HashSet<string> _landed = new(StringComparer.Ordinal);

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
            MatchCollection phases = PhaseHeading().Matches(buildPlan);
            for (int i = 0; i < phases.Count; i++)
            {
                int start = phases[i].Index;
                int end = i + 1 < phases.Count ? phases[i + 1].Index : buildPlan.Length;

                foreach (Match row in CheckpointRow().Matches(buildPlan[start..end]))
                {
                    string checkpoint = row.Groups["checkpoint"].Value;
                    schedule._rows[checkpoint] = schedule._rows.GetValueOrDefault(checkpoint, string.Empty)
                        + " " + row.Groups["rest"].Value.ToUpperInvariant();
                }
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
