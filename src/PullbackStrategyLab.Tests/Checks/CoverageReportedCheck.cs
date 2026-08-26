using System.Text.Json;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// The check roster in CLAUDE.md, reconciled against the checks that exist, the checks
/// <c>tools/ci.*</c> invokes, and the checkpoints the corpus schedules.
///
/// This is the property CLAUDE.md calls the one that matters most and easiest to lose, and it
/// had no implementation until the 1.12 review went looking for it. Under-reporting is
/// survivorship: a check that errors loudly gets fixed because it blocks, while a check that
/// silently narrows its own scope keeps passing forever.
///
/// <b>A check that stops running is the sharpest form of that.</b> <c>dotnet test --filter</c>
/// exits zero when the filter matches no test, printing "No test matches the given testcase
/// filter" and nothing else, so a renamed or deleted check leaves a CI step that passes by
/// running nothing at all. The phase report then assembles its coverage section from whatever
/// files are in <c>artifacts/checks</c>, so the vanished check leaves one fewer row and the
/// report still says green. That was reproduced by hand at the 1.12 review before this was
/// written: deleting one coverage record and re-running the report changed one summary number
/// nobody compares and left the verdict GREEN.
///
/// Two things close it, and both are needed because they fail in different places. This check
/// reconciles the roster statically, so a name that stops matching is caught at the source. The
/// phase report requires a coverage record from every check the roster says runs, so a check
/// that is present in the source and does not run in the run being reported turns the report
/// red rather than shrinking it.
///
/// The roster is read from CLAUDE.md rather than restated here. A list kept in the check would
/// be a second place to keep right, and the two would disagree eventually, which is the defect
/// this exists to catch wearing a different hat.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed partial class CoverageReportedCheck
{
    /// <summary>What the Runs column says about a check that runs on every CI run.</summary>
    public const string EveryRun = "every CI run";

    /// <summary>What it says about the one check whose runner is the workflow matrix.</summary>
    public const string TheMatrix = "the matrix";

    /// <summary>Where the phase report reads the roster of checks that owe it a coverage record.</summary>
    public const string RosterFile = "expected-checks.json";

    private readonly ITestOutputHelper _output;

    public CoverageReportedCheck(ITestOutputHelper output) => _output = output;

    [GeneratedRegex("""\[Trait\("check",\s*"(?<name>[^"]+)"\)\]""", RegexOptions.CultureInvariant)]
    private static partial Regex TraitDeclaration();

    [GeneratedRegex(""""new CheckCoverage\("(?<name>[^"]+)"""", RegexOptions.CultureInvariant)]
    private static partial Regex CoverageConstruction();

    [GeneratedRegex(@"^(?:Invoke-Step|step)\s+'(?<name>check-[^']+)'", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CheckStep();

    [GeneratedRegex(@"(?:Invoke-Check|run_check)\s+'(?<name>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex CheckInvocation();

    [Fact]
    [Trait("check", "coverage-reported")]
    public void Every_check_the_roster_declares_is_implemented_run_and_reporting()
    {
        var coverage = new CheckCoverage("coverage-reported", _output);

        IReadOnlyList<RosterRow> roster = Roster();
        IReadOnlyDictionary<string, string> implemented = ImplementedChecks();
        IReadOnlySet<string> reporting = ReportingChecks(implemented);
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();

        (IReadOnlyList<string> stepNames, IReadOnlyList<string> invoked) = CiSteps();

        RosterRow[] live = [.. roster.Where(r => r.Runs == EveryRun)];
        RosterRow[] matrix = [.. roster.Where(r => r.Runs == TheMatrix)];
        RosterRow[] scheduled = [.. roster.Where(r => r.Runs != EveryRun && r.Runs != TheMatrix)];

        IReadOnlyList<string> problems = Problems(
            roster, implemented, reporting, invoked, stepNames, schedule.Exists, schedule.HasLanded, Workflow());

        WriteRoster(live);

        coverage
            .Examined("checks declared in CLAUDE.md's roster", roster.Count)
            .Examined("of those declared to run on every CI run", live.Length)
            .Examined("checks implemented in the suite", implemented.Count)
            .Examined("checks tools/ci.* invokes as a named step", invoked.Count)
            .OutOfScope("checks deferred to a checkpoint that has not landed", scheduled.Length,
                "closed by " + string.Join(", ", scheduled.Select(r => r.Runs).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            .OutOfScope("checks whose runner is the workflow matrix", matrix.Length,
                "the runner set is asserted against the workflow rather than against a test")
            .Report();

        // Stated in advance rather than left self-validating. A parser that stopped matching would
        // otherwise report an empty roster and pass, which is this check's own failure mode.
        Assert.True(roster.Count >= 20,
            $"CLAUDE.md's Checks table parsed {roster.Count} rows. It has held at least twenty since 1.7, so the parser stopped matching.");
        Assert.True(implemented.Count >= 15,
            $"The suite parsed {implemented.Count} checks. It has held at least fifteen since 1.11, so the trait scan stopped matching.");
        Assert.True(stepNames.Count >= 15,
            $"tools/ci.* parsed {stepNames.Count} check steps. It has held at least fifteen since 1.11, so the step scan stopped matching.");

        Assert.True(problems.Count == 0,
            $"{problems.Count} problem(s) reconciling the check roster against the code and the CI scripts:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// What is wrong with the roster, taken as a set, against the checks that exist and the steps
    /// that invoke them.
    ///
    /// Separated from the run above so it can be proved against a roster written by hand rather
    /// than against whatever the corpus happens to say today. A check nobody can break on purpose
    /// is a check nobody knows the state of, and this one exists precisely because the failure it
    /// catches is silent: nothing about a passing run distinguishes a check that examined
    /// everything from one that was never invoked.
    /// </summary>
    public static IReadOnlyList<string> Problems(
        IReadOnlyList<RosterRow> roster,
        IReadOnlyDictionary<string, string> implemented,
        IReadOnlySet<string> reporting,
        IReadOnlyList<string> invoked,
        IReadOnlyList<string> stepNames,
        Func<string, bool> checkpointExists,
        Func<string, bool> hasLanded,
        string workflow)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(implemented);
        ArgumentNullException.ThrowIfNull(reporting);
        ArgumentNullException.ThrowIfNull(invoked);
        ArgumentNullException.ThrowIfNull(stepNames);
        ArgumentNullException.ThrowIfNull(checkpointExists);
        ArgumentNullException.ThrowIfNull(hasLanded);

        var problems = new List<string>();

        // 1. Every row of the roster is one of the three kinds, and each kind has to hold up.
        foreach (RosterRow row in roster.Where(r => r.Runs == EveryRun))
        {
            if (!implemented.ContainsKey(row.Name))
            {
                problems.Add(
                    $"{row.Name} is declared to run on every CI run and no test carries [Trait(\"check\", \"{row.Name}\")]. "
                    + "A declared check with no implementation is a property nobody wrote down.");
                continue;
            }

            if (!reporting.Contains(row.Name))
            {
                problems.Add(
                    $"{row.Name} is implemented in {implemented[row.Name]} and does not construct CheckCoverage(\"{row.Name}\") "
                    + "and call Report(). A check that states no scope cannot be told from one that examined nothing.");
            }

            if (!invoked.Contains(row.Name, StringComparer.Ordinal))
            {
                problems.Add(
                    $"{row.Name} is declared to run on every CI run and tools/ci.* invokes no such check. "
                    + "It runs in the suite and not as a named step, so a failure would not stop CI where the roster says it does.");
            }
        }

        foreach (RosterRow row in roster.Where(r => r.Runs != EveryRun && r.Runs != TheMatrix))
        {
            // The rule an out-of-scope architecture claim obeys, applied to a row of this table.
            // A checkpoint the plan does not have closes at nothing, and one the record already
            // carries is a checkpoint that shipped without bringing the check into being.
            if (!checkpointExists(row.Runs))
            {
                problems.Add(
                    $"{row.Name} is deferred to checkpoint {row.Runs} and BUILD_PLAN.md has no such checkpoint, "
                    + "so nothing will ever start it.");
            }
            else if (hasLanded(row.Runs))
            {
                problems.Add(
                    $"{row.Name} is deferred to checkpoint {row.Runs} and PROGRESS.md already records it. "
                    + "That checkpoint shipped without building the check and nothing said so at the time.");
            }

            if (implemented.ContainsKey(row.Name))
            {
                problems.Add(
                    $"{row.Name} is deferred to checkpoint {row.Runs} and is already implemented in {implemented[row.Name]}. "
                    + "A check that exists should say 'every CI run' and be invoked, rather than reading as future work.");
            }
        }

        foreach (RosterRow row in roster.Where(r => r.Runs == TheMatrix))
        {
            // Exempt from implementation by name, so the exemption is assertable rather than free.
            if (!workflow.Contains("windows-latest", StringComparison.Ordinal)
                || !workflow.Contains("macos-latest", StringComparison.Ordinal))
            {
                problems.Add(
                    $"{row.Name} is declared to run as the matrix and the workflow does not name both runners, "
                    + "so the row rests on a runner set that is no longer there.");
            }
        }

        // 2. The other direction, which is the one the corpus already argued for at 1.7: a check
        //    that runs and is not declared here is a property nobody wrote down.
        foreach ((string name, string file) in implemented)
        {
            if (!roster.Any(r => r.Name == name))
            {
                problems.Add(
                    $"{name} is implemented in {file} and CLAUDE.md's Checks table does not declare it. "
                    + "The phase report enumerates checks by name, so the two lists would disagree with nothing to reconcile them.");
            }
        }

        // 3. A CI step invoking a name no check carries is the silent-pass case itself: the filter
        //    matches nothing, dotnet test exits zero, and the step reads as green.
        foreach (string name in invoked)
        {
            if (!implemented.ContainsKey(name))
            {
                problems.Add(
                    $"tools/ci.* invokes check '{name}' and no test carries that trait. The filter would match no test, "
                    + "dotnet test would exit zero, and the step would pass by running nothing.");
            }
        }

        // 4. And the step name has to agree with what the step invokes, or the scrollback names one
        //    check while another one runs.
        foreach (string step in stepNames)
        {
            string named = step["check-".Length..];
            if (!invoked.Contains(named, StringComparer.Ordinal))
            {
                problems.Add(
                    $"CI step '{step}' does not invoke check '{named}'. The step name and the filter have diverged, "
                    + "so the run reports one check by name and exercises another.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The roster: CLAUDE.md's Checks table, read as rows of name, runs and what it asserts.
    /// </summary>
    public static IReadOnlyList<RosterRow> Roster()
    {
        string claude = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, "CLAUDE.md"));

        return
        [
            .. MarkdownTable.BodyRowsAfter(claude, "## Checks")
                .Where(row => row.Count >= 2)
                .Select(row => new RosterRow(Bare(row[0]), row[1].Trim()))
        ];
    }

    /// <summary>The name in the first cell, without the backticks the table writes it in.</summary>
    private static string Bare(string cell) => cell.Trim().Trim('`').Trim();

    /// <summary>Every check the suite implements, by the trait it carries, and the file it lives in.</summary>
    private static IReadOnlyDictionary<string, string> ImplementedChecks()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in RepositoryLayout.SourceFiles)
        {
            string source = CSharpSource.WithoutComments(RepositoryLayout.Read(file));
            foreach (Match match in TraitDeclaration().Matches(source))
            {
                found[match.Groups["name"].Value] = RepositoryLayout.Relative(file);
            }
        }

        return found;
    }

    /// <summary>
    /// The checks that state their own scope: the file carrying the trait also constructs a
    /// CheckCoverage under the same name and calls Report on it.
    /// </summary>
    private static IReadOnlySet<string> ReportingChecks(IReadOnlyDictionary<string, string> implemented)
    {
        var reporting = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string name, string relative) in implemented)
        {
            string source = CSharpSource.WithoutComments(
                RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, relative.Replace('/', Path.DirectorySeparatorChar))));

            bool constructs = CoverageConstruction().Matches(source).Any(m => m.Groups["name"].Value == name);
            if (constructs && source.Contains(".Report()", StringComparison.Ordinal))
            {
                reporting.Add(name);
            }
        }

        return reporting;
    }

    /// <summary>
    /// The check steps both CI scripts declare, and the check names those steps invoke. Read from
    /// both files and intersected on neither: ci-parity already asserts the two run the same steps
    /// in the same order, so anything present in one and not the other fails there with a better
    /// message than this check would give.
    /// </summary>
    private static (IReadOnlyList<string> Steps, IReadOnlyList<string> Invoked) CiSteps()
    {
        string powershell = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.ps1"));
        string shell = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "ci.sh"));

        string[] steps =
        [
            .. CheckStep().Matches(powershell).Select(m => m.Groups["name"].Value),
            .. CheckStep().Matches(shell).Select(m => m.Groups["name"].Value),
        ];

        string[] invoked =
        [
            .. CheckInvocation().Matches(powershell).Select(m => m.Groups["name"].Value),
            .. CheckInvocation().Matches(shell).Select(m => m.Groups["name"].Value),
        ];

        return ([.. steps.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. invoked.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static string Workflow()
    {
        string directory = Path.Combine(RepositoryLayout.Root, ".github", "workflows");
        return Directory.Exists(directory)
            ? string.Concat(Directory.EnumerateFiles(directory, "*.yml").Select(File.ReadAllText))
            : string.Empty;
    }

    /// <summary>
    /// The names the phase report will require a coverage record from, written where it can read
    /// them. Written by the check rather than restated in the reporter, so the roster has one
    /// source; the reporter treats a missing roster as a reason of its own, which is what stops
    /// this file being a single point of silent failure.
    /// </summary>
    private static void WriteRoster(IReadOnlyList<RosterRow> live)
    {
        Directory.CreateDirectory(RepositoryLayout.Artifacts);
        File.WriteAllText(
            Path.Combine(RepositoryLayout.Artifacts, RosterFile),
            JsonSerializer.Serialize(
                new Expected([.. live.Select(r => r.Name).Order(StringComparer.Ordinal)]),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>One row of the roster: the check's name, and what the Runs column says about it.</summary>
    public sealed record RosterRow(string Name, string Runs);

    private sealed record Expected(IReadOnlyList<string> Checks);
}
