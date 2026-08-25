using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every claim ARCHITECTURE.html makes in a table, asserted against the code, one verdict each.
///
/// Four verdicts, and the fourth is the point. <b>pass</b> and <b>fail</b> are what a test gives
/// you. <b>deferred</b> is a claim about a component the corpus itself places in a later phase,
/// which is not this phase's business and is counted separately so it can never be mistaken for
/// coverage. <b>unexamined</b> is a claim this phase should have been able to assert and could
/// not, and it is not a pass.
///
/// The difference between deferred and unexamined is the whole discipline. Collapsing them would
/// let forty later-phase rows hide one row nobody can check, which is exactly the failure
/// coverage-reported exists to prevent.
///
/// A component is placed in a phase by reading the corpus rather than by a list kept here: the
/// build order table first, and the build plan's phase sections where the build order describes
/// a component rather than naming it. A component neither document places is unexamined, loudly,
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

    [GeneratedRegex(@"\b(?:class|record|interface|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"(?:AddSingleton|AddScoped|AddTransient|AddHttpClient)<(?<name>[^,>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Registration();

    [GeneratedRegex("""^@page\s+"(?<route>[^"]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageRoute();

    /// <summary>
    /// The failure-behaviour table states conditions in prose, so it names no component a parser
    /// could follow. Each row is placed here by hand against the phase that builds the behaviour,
    /// and a row this list does not name is unexamined rather than skipped, which is what makes
    /// adding a row to that table visible.
    /// </summary>
    public static IReadOnlyDictionary<string, int> FailureBehaviourPhases { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Intraday prices unavailable for a day"] = 4,
        ["Price gaps past the give-up point"] = 4,
        ["A short could not have been borrowed"] = 4,
        ["Unprocessed corporate action"] = 1,
        ["Detector errors on one stock"] = 2,
        ["Nightly setup cap reached"] = 2,
        ["Daily API ceiling reached"] = 1,
        ["Two variants pick the same stock"] = 4,
        ["Risk gate blocks an order"] = 4,
        ["AI usage allowance exhausted"] = 6,
        ["Holdout windows exhausted"] = 5,
        ["Proposal cites the planted null signal"] = 6,
        ["Variant sample never accumulates"] = 5,
        ["Follow-up date is a holiday"] = 3,
        ["Someone edits the baseline"] = 5,
    };

    /// <summary>
    /// Which route answers for each screen in the catalogue.
    ///
    /// Recorded here because a screen has no class a catalogue name resolves to. Every one of
    /// these is a page a later phase fills, and the nav's own list is the five that are already
    /// reachable; a screen this list does not name is unexamined rather than skipped, so adding
    /// a screen to the catalogue is visible.
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
    private static IReadOnlyList<string> RoutedPages { get; } = RepositoryLayout.Root is var root
        ? [.. Directory.EnumerateFiles(Path.Combine(root, "src", "PullbackStrategyLab.Web", "Pages"), "*.cshtml", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Select(text => PageRoute().Match(text))
            .Where(m => m.Success)
            .Select(m => m.Groups["route"].Value)]
        : [];

    /// <summary>
    /// The limits are the risk caps, and RiskGate is the only thing that may apply them. They
    /// travel with it, which is why one entry places the whole table.
    /// see: RiskGate is the sole writer of orders, for both directions and every version
    /// </summary>
    public const string LimitsAreEnforcedBy = "RiskGate";

    [Fact]
    [Trait("check", "architecture-conformance")]
    public void Every_claim_the_architecture_makes_in_a_table_has_a_verdict()
    {
        var coverage = new CheckCoverage("architecture-conformance", _output);
        string architecture = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));
        string buildPlan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));

        int phase = CurrentPhase();
        var claims = new List<Claim>();

        IReadOnlyList<IReadOnlyList<string>> catalogue = HtmlTable.BodyRowsUnder(architecture, "Component catalogue");
        IReadOnlyList<IReadOnlyList<string>> buildOrder = HtmlTable.BodyRowsUnder(architecture, "Build order");
        IReadOnlyList<IReadOnlyList<string>> limits = HtmlTable.BodyRowsUnder(architecture, "The limits");
        IReadOnlyList<IReadOnlyList<string>> failures = HtmlTable.BodyRowsUnder(architecture, "Failure behaviour");
        IReadOnlyList<IReadOnlyList<string>> sections = HtmlTable.BodyRowsUnder(architecture, "The phase report");

        string[] componentNames = [.. catalogue.Select(r => r[0])];
        var places = new ComponentPlacement(buildOrder, buildPlan);
        HashSet<string> declared = DeclaredTypes();
        HashSet<string> registered = RegisteredTypes();

        // 1. The component catalogue. Every component named exists and is registered, or the
        //    corpus places it in a phase that has not run.
        foreach (string component in componentNames)
        {
            int? placed = places.PhaseOf(component);

            if (placed is null)
            {
                claims.Add(new Claim("Component catalogue", component, Unexamined,
                    "neither the build order nor the build plan places this component in a phase, so nothing says when it is owed"));
                continue;
            }

            if (placed > phase)
            {
                claims.Add(new Claim("Component catalogue", component, Deferred, $"built at phase {placed}"));
                continue;
            }

            if (component.Contains(' ', StringComparison.Ordinal))
            {
                // A screen rather than a type, asserted against a Razor page that answers a
                // route rather than against a class name. A page has no class a catalogue name
                // resolves to, and asserting one would be asserting a naming convention.
                claims.Add(Screens.TryGetValue(component, out string? route)
                    ? RoutedPages.Contains(route, StringComparer.Ordinal)
                        ? new Claim("Component catalogue", component, Pass, $"a page answers {route}")
                        : new Claim("Component catalogue", component, Fail,
                            $"phase {placed} has run and no page declares the route {route}")
                    : new Claim("Component catalogue", component, Unexamined,
                        "a screen with no route recorded against it, so nothing says what would answer for it"));
                continue;
            }

            if (!declared.Contains(component))
            {
                claims.Add(new Claim("Component catalogue", component, Fail,
                    $"phase {placed} has run and no type named {component} is declared in the source"));
                continue;
            }

            claims.Add(registered.Contains(component)
                ? new Claim("Component catalogue", component, Pass, "declared and registered")
                : new Claim("Component catalogue", component, Fail,
                    $"{component} is declared and is not registered with the container, so nothing can resolve it"));
        }

        // 2. The build order, read the other way: every component a phase says it builds is a
        //    component the catalogue names. A phase that builds something the catalogue does not
        //    list is a component with no description.
        foreach (IReadOnlyList<string> row in buildOrder)
        {
            string[] missing = ComponentPlacement.NamesIn(row[1])
                .Where(n => !componentNames.Contains(n, StringComparer.Ordinal))
                .ToArray();

            claims.Add(missing.Length == 0
                ? new Claim("Build order", row[0], Pass, "every component it names is in the catalogue")
                : new Claim("Build order", row[0], Fail,
                    "names components the catalogue does not describe: " + string.Join(", ", missing)));
        }

        // 3. The limits. Risk caps, enforced by the one component that may open a position.
        int? riskGate = places.PhaseOf(LimitsAreEnforcedBy);
        foreach (IReadOnlyList<string> row in limits)
        {
            claims.Add(riskGate is null
                ? new Claim("The limits", row[0], Unexamined, $"nothing places {LimitsAreEnforcedBy}, which is what applies these")
                : riskGate > phase
                    ? new Claim("The limits", row[0], Deferred, $"{LimitsAreEnforcedBy} is built at phase {riskGate}")
                    : new Claim("The limits", row[0], Unexamined, $"{LimitsAreEnforcedBy} exists and no assertion reads this row yet"));
        }

        // 4. Failure behaviour. Placed by hand because the table names conditions rather than
        //    components, and asserted where this phase built the behaviour.
        foreach (IReadOnlyList<string> row in failures)
        {
            string condition = row[0];

            if (!FailureBehaviourPhases.TryGetValue(condition, out int owed))
            {
                claims.Add(new Claim("Failure behaviour", condition, Unexamined,
                    "no phase is recorded against this condition, so nothing says when the behaviour is owed"));
                continue;
            }

            if (owed > phase)
            {
                claims.Add(new Claim("Failure behaviour", condition, Deferred, $"the behaviour is built at phase {owed}"));
                continue;
            }

            claims.Add(AssertFailureBehaviour(condition));
        }

        // 5. The phase report's own three sections, asserted against the report this run writes.
        //    A document that promises a section the report does not produce is the report telling
        //    you about itself, which is the one claim it is in a position to be sure of.
        foreach (IReadOnlyList<string> row in sections)
        {
            claims.Add(PhaseReportSections.Names.Contains(row[0], StringComparer.OrdinalIgnoreCase)
                ? new Claim("The phase report", row[0], Pass, "the report writes this section")
                : new Claim("The phase report", row[0], Fail,
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
                    phase,
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

            int deferred = table.Count(c => c.Verdict == Deferred);
            if (deferred > 0)
            {
                coverage.OutOfScope($"claims in {table.Key}", deferred, "the corpus places them in a later phase");
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

        // Stated so the parser stopping cannot pass as a document that got smaller.
        Assert.True(catalogue.Count == 52, $"The component catalogue parsed {catalogue.Count} rows and states 52.");
        Assert.True(failures.Count == FailureBehaviourPhases.Count,
            $"The failure-behaviour table has {failures.Count} rows and {FailureBehaviourPhases.Count} are placed in a phase.");
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
                ? new Claim("Failure behaviour", condition, Pass, "IndicatorEngine leaves no row and counts the ticker as blocked")
                : new Claim("Failure behaviour", condition, Fail, "IndicatorEngine no longer refuses on an open demand"),

            "Daily API ceiling reached" => runScope.Contains("CallsRemaining", StringComparison.Ordinal)
                ? new Claim("Failure behaviour", condition, Pass, "the run scope reports what is left and a stage stops rather than overrunning")
                : new Claim("Failure behaviour", condition, Fail, "the run scope no longer exposes the remaining ceiling"),

            _ => new Claim("Failure behaviour", condition, Unexamined,
                "placed in this phase and no assertion reads it"),
        };
    }

    /// <summary>
    /// Which phase the build is on, read from the last entry in PROGRESS rather than stated here.
    /// A number in a second place is a number that goes stale the moment a checkpoint lands.
    /// </summary>
    public static int CurrentPhase()
    {
        string progress = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "PROGRESS.md"));
        MatchCollection entries = Regex.Matches(progress, @"^## (?<checkpoint>\d+)\.\d+ ", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        Assert.True(entries.Count > 0, "PROGRESS records no checkpoint, so nothing says which phase the build is on.");

        return int.Parse(entries[^1].Groups["checkpoint"].Value, System.Globalization.CultureInfo.InvariantCulture);
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
    /// Where the corpus says each component is built. The build order table first, because that
    /// is the document describing the system; the build plan's phase sections after it, because
    /// the build order describes some components rather than naming them and the plan names
    /// every one.
    /// </summary>
    private sealed class ComponentPlacement
    {
        private readonly Dictionary<int, string> _byPhase = [];

        public ComponentPlacement(IReadOnlyList<IReadOnlyList<string>> buildOrder, string buildPlan)
        {
            foreach (IReadOnlyList<string> row in buildOrder)
            {
                int phase = int.Parse(new string([.. row[0].Where(char.IsDigit)]), System.Globalization.CultureInfo.InvariantCulture);
                _byPhase[phase] = string.Join(" ", row.Skip(1)).ToUpperInvariant();
            }

            MatchCollection headings = PhaseHeading().Matches(buildPlan);
            for (int i = 0; i < headings.Count; i++)
            {
                int phase = int.Parse(headings[i].Groups["phase"].Value, System.Globalization.CultureInfo.InvariantCulture);
                int start = headings[i].Index;
                int end = i + 1 < headings.Count ? headings[i + 1].Index : buildPlan.Length;

                _byPhase[phase] = _byPhase.GetValueOrDefault(phase, string.Empty)
                    + " " + buildPlan[start..end].ToUpperInvariant();
            }
        }

        public int? PhaseOf(string component)
        {
            string needle = component.ToUpperInvariant();
            foreach (int phase in _byPhase.Keys.Order())
            {
                if (_byPhase[phase].Contains(needle, StringComparison.Ordinal))
                {
                    return phase;
                }
            }

            return null;
        }

        /// <summary>The component names in a build-order cell: the comma-separated items that read as one.</summary>
        public static IReadOnlyList<string> NamesIn(string cell) =>
            [.. cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part.Length > 0 && char.IsUpper(part[0]) && !part.Contains(' ', StringComparison.Ordinal))];
    }

    public sealed record Claim(string Table, string Subject, string Verdict, string Detail);

    public sealed record Conformance(
        int Phase,
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
