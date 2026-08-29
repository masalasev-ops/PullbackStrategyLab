using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Assembles the phase report from the parts the checks wrote, and says whether the phase is
/// green.
///
/// It asserts nothing itself, and that is deliberate. The checks are where assertions live; a
/// second implementation of the same claim inside the reporter would be a second place to keep
/// right, and the two would disagree eventually. What this adds is the verdict over the whole
/// set, in one exit code and one page: a part that did not run is not a part that passed.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
///
/// Green means every claim passed, every expectation held, and nothing is listed as unexamined.
/// Out of scope is not unexamined: the corpus placed those in a later phase or exempted them by
/// name, and they are shown separately so they can never be read as coverage.
/// </summary>
public sealed class PhaseReportStage
{
    public const string Name = "phase-report";

    /// <summary>The suite's own exit code, folded in by tools/verify-phase so a red suite cannot leave a green report.</summary>
    public const string SuiteFlag = "--suite";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IClock _clock;

    public PhaseReportStage(IClock clock) => _clock = clock;

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string root = RepositoryRoot();
        string artifacts = Path.Combine(root, "artifacts");

        int suite = 0;
        int flag = Array.IndexOf(args, SuiteFlag);
        if (flag >= 0 && flag + 1 < args.Length)
        {
            suite = int.Parse(args[flag + 1], CultureInfo.InvariantCulture);
        }

        Report report = Assemble(root, artifacts, suite, _clock.UtcNow);

        Report? written = WriteReport(report, artifacts, ReadHead(root));
        if (written is null)
        {
            return 2;
        }

        report = written;

        Console.WriteLine($"{Name}: phase {report.Phase}, {report.Claims.Total} claim(s), {report.Expectations.Total} expectation(s)"
            + (report.Expectations.Voided > 0 ? $", {report.Expectations.Voided} of them void" : string.Empty));
        Console.WriteLine($"{Name}: {report.Claims.Passed} passed, {report.Claims.Failed} failed, {report.Claims.OutOfScope} out of scope, {report.Claims.Unexamined} unexamined");
        Console.WriteLine($"{Name}: coverage examined {report.Coverage.Sum(c => c.Examined)}, unexamined {report.Coverage.Sum(c => c.Unexamined)}");
        Console.WriteLine($"{Name}: inputs {string.Join(", ", (report.Inputs?.Tiers ?? []).Select(t => $"{t.Tier} {t.Count}"))}");
        Console.WriteLine($"{Name}: expectations changed since the last commit: {report.ExpectationsChangedSinceHead}");
        Console.WriteLine($"{Name}: {report.Commit}, working tree {(report.TreeClean ? "clean" : "dirty")}, generated {report.GeneratedAt}");
        Console.WriteLine($"{Name}: artifacts/phase-report.html");
        Console.WriteLine($"{Name}: {(report.Green ? "GREEN" : "NOT GREEN")}");

        foreach (string reason in report.Reasons)
        {
            Console.WriteLine($"{Name}:   {reason}");
        }

        return report.Green ? 0 : 1;
    }

    private static Report Assemble(string root, string artifacts, int suite, DateTimeOffset generatedAt)
    {
        var reasons = new List<string>();

        Conformance? conformance = ReadPart<Conformance>(Path.Combine(artifacts, "doc-conformance.json"));
        FixtureDiff? fixture = ReadPart<FixtureDiff>(Path.Combine(artifacts, "fixture-diff.json"));
        InputTiers? inputs = ReadPart<InputTiers>(Path.Combine(artifacts, "input-tiers.json"));

        if (conformance is null)
        {
            reasons.Add("The document-conformance part is missing, so no architecture claim has a verdict.");
        }

        if (fixture is null)
        {
            reasons.Add("The fixture-diff part is missing, so the pipeline was not run over the fixture.");
        }

        if (inputs is null)
        {
            reasons.Add("The input-tier part is missing, so nothing says where the fixture's inputs came from.");
        }
        else if (inputs.EndpointsWithNoCapturedInput.Count > 0)
        {
            reasons.Add(
                $"{inputs.EndpointsWithNoCapturedInput.Count} endpoint(s) rest on authored evidence alone: "
                + string.Join(", ", inputs.EndpointsWithNoCapturedInput));
        }

        var coverage = new List<CheckCoverageRecord>();
        string checksDirectory = Path.Combine(artifacts, "checks");

        if (Directory.Exists(checksDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(checksDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                CheckCoverageRecord? record = ReadPart<CheckCoverageRecord>(file);
                if (record is not null)
                {
                    coverage.Add(record);
                }
            }
        }

        if (coverage.Count == 0)
        {
            reasons.Add("No check wrote a coverage record, so nothing states what was examined.");
        }

        // A check that stopped running is invisible to everything above: the coverage section is
        // assembled from the files that are there, so a vanished check leaves one fewer row and
        // every count still adds up. dotnet test exits zero when a filter matches no test, so the
        // CI step that was supposed to run it passes by running nothing, and nothing anywhere says
        // the check is gone. The roster is what the run is measured against, rather than the run
        // being measured against itself.
        Expected? expected = ReadPart<Expected>(Path.Combine(artifacts, "expected-checks.json"));

        if (expected is null)
        {
            reasons.Add(
                "The check roster is missing, so nothing says which checks this run owed a coverage record. "
                + "coverage-reported writes it, so its absence means that check did not run either.");
        }
        else
        {
            string[] silent =
            [
                .. expected.Checks
                    .Where(name => !coverage.Any(c => string.Equals(c.Check, name, StringComparison.Ordinal)))
                    .Order(StringComparer.Ordinal)
            ];

            if (silent.Length > 0)
            {
                reasons.Add(
                    $"{silent.Length} check(s) the roster says run left no coverage record: {string.Join(", ", silent)}. "
                    + "A check that did not run is not a check that passed.");
            }
        }

        var claims = new ClaimSummary(
            conformance?.Claims ?? 0,
            conformance?.Passed ?? 0,
            conformance?.Failed ?? 0,
            conformance?.Deferred ?? 0,
            conformance?.Unexamined ?? 0);

        // A voided row is neither matched nor differed, and counting it as differed is what turned
        // this report red at 3.11 over an expectation whose subject had deliberately been removed.
        // `fixture-replay` has always excluded void from its failures, so the check was green and
        // the report that reads the same file was not: two counts of the same rows, disagreeing.
        // Counted separately rather than folded into matched, because a fixture quietly voiding its
        // way to green is the failure the tier machinery exists to make visible.
        var expectations = new FixtureSummary(
            fixture?.Rows.Count ?? 0,
            fixture?.Rows.Count(r => r.Verdict == "matched") ?? 0,
            fixture?.Rows.Count(r => r.Verdict is not ("matched" or "void")) ?? 0,
            fixture?.Rows.Count(r => r.Verdict == "void") ?? 0,
            fixture?.ByTier ?? [],
            fixture?.Unexpected ?? []);

        int unexaminedInCoverage = coverage.Sum(c => c.Unexamined);

        if (claims.Failed > 0)
        {
            reasons.Add($"{claims.Failed} architecture claim(s) failed.");
        }

        if (claims.Unexamined > 0)
        {
            reasons.Add($"{claims.Unexamined} architecture claim(s) are unexamined, which is not a pass.");
        }

        int unclosed = (conformance?.Detail ?? [])
            .Count(c => c.Verdict == "deferred" && string.IsNullOrWhiteSpace(c.Closes));

        if (unclosed > 0)
        {
            reasons.Add($"{unclosed} out-of-scope claim(s) name no checkpoint that ends them, so they rest there forever.");
        }

        // Out of scope is accounted the way unexamined is, and the reason is that it is the one
        // bucket where scope narrows without anything going red. At 42% of the claims it is larger
        // than the passing set, so a claim moving quietly into it is a claim nobody answers for, and
        // nothing before 3.8 would have noticed fifty becoming sixty.
        int unreasoned = (conformance?.Detail ?? [])
            .Count(c => c.Verdict == "deferred" && string.IsNullOrWhiteSpace(c.Detail));

        if (unreasoned > 0)
        {
            reasons.Add(
                $"{unreasoned} out-of-scope claim(s) state no reason, so nothing says why they are not asserted.");
        }

        int ceiling = OutOfScopeCeiling(root);

        if (ceiling < 0)
        {
            reasons.Add(
                "ARCHITECTURE.html states no out-of-scope ceiling, so the one bucket that narrows without going red "
                + "is unbounded.");
        }
        else if (claims.OutOfScope > ceiling)
        {
            reasons.Add(
                $"{claims.OutOfScope} claim(s) are out of scope against a ceiling of {ceiling} stated in "
                + "ARCHITECTURE.html. Either a checkpoint that should have closed some has not, or the ceiling is "
                + "owed an edit saying why the set grew.");
        }

        if (expectations.Differed > 0)
        {
            reasons.Add($"{expectations.Differed} fixture expectation(s) did not hold.");
        }

        if (expectations.Unexpected.Count > 0)
        {
            reasons.Add($"{expectations.Unexpected.Count} figure(s) the replay produced have no expectation.");
        }

        if (unexaminedInCoverage > 0)
        {
            reasons.Add($"{unexaminedInCoverage} item(s) across the checks are unexamined.");
        }

        // A fixture with nothing but frozen values can only say the code still agrees with
        // itself, which is regression detection rather than verification.
        int independent = expectations.ByTier
            .Where(t => t.Tier is "DERIVED" or "CONFIRMED")
            .Sum(t => t.Total);

        if (fixture is not null && independent == 0)
        {
            reasons.Add("No expectation is DERIVED or CONFIRMED, so the fixture verifies nothing.");
        }

        if (suite != 0)
        {
            reasons.Add($"The test suite exited {suite}.");
        }

        return new Report(
            conformance?.Phase ?? 0,
            conformance?.LastLanded ?? "nothing recorded",
            generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Unstamped,
            true,
            reasons.Count == 0,
            reasons,
            claims,
            expectations,
            independent,
            ExpectationsChangedSinceHead(root),
            inputs,
            fixture,
            conformance?.Detail ?? [],
            coverage);
    }

    /// <summary>
    /// How many expectations changed since the last commit, beside how many passed.
    ///
    /// The two numbers only mean something together. A green diff over expectations edited in
    /// the same commit is a green diff over the output of the code it is checking, and nothing
    /// about the run itself distinguishes that from a green diff over expectations nobody
    /// touched. Read from git rather than tracked here, because git already knows.
    /// </summary>
    private static string ExpectationsChangedSinceHead(string root)
    {
        const string Path_ = "fixtures/expectations.json";

        try
        {
            var start = new ProcessStartInfo("git", $"show HEAD:{Path_}")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using Process? git = Process.Start(start);
            if (git is null)
            {
                return "unknown, git did not start";
            }

            string committed = git.StandardOutput.ReadToEnd();
            git.WaitForExit();

            if (git.ExitCode != 0)
            {
                return "all of them, no committed expectations to compare against";
            }

            string current = File.ReadAllText(System.IO.Path.Combine(root, "fixtures", "expectations.json"));

            var before = Read(committed);
            var after = Read(current);

            int changed = after.Count(a => !before.TryGetValue(a.Key, out string? was) || was != a.Value)
                + before.Count(b => !after.ContainsKey(b.Key));

            return changed.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            return $"unknown, {e.GetType().Name}";
        }

        static Dictionary<string, string> Read(string text)
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.GetProperty("expectations").EnumerateArray()
                .ToDictionary(
                    e => e.GetProperty("id").GetString() ?? string.Empty,
                    e => (e.GetProperty("tier").GetString() ?? string.Empty) + "=" + (e.GetProperty("value").GetString() ?? string.Empty),
                    StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The out-of-scope ceiling the architecture document states, or -1 where it states none.
    ///
    /// Read rather than written into this file. A literal here would be a number the corpus does not
    /// know about and could not reconcile, which is the shape pinned-constants exists for one level
    /// down. Mutating the figure in the document moves the verdict, which is what makes it a claim
    /// rather than a comment.
    /// </summary>
    private static int OutOfScopeCeiling(string root)
    {
        string file = Path.Combine(root, "docs", "ARCHITECTURE.html");

        if (!File.Exists(file))
        {
            return -1;
        }

        Match match = Regex.Match(
            File.ReadAllText(file),
            "[Oo]ut of scope carries a ceiling of (?<ceiling>[0-9]+)");

        return match.Success
            ? int.Parse(match.Groups["ceiling"].Value, CultureInfo.InvariantCulture)
            : -1;
    }
    private static T? ReadPart<T>(string file)
        where T : class
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The repository, found by walking up to the solution file. The report reads the corpus and
    /// writes beside it, so it needs the repository rather than the build output.
    /// </summary>
    /// <summary>What Assemble puts in the commit field, which the write replaces or refuses.</summary>
    public const string Unstamped = "unstamped";

    /// <summary>The commit a report was produced at, and whether anything was uncommitted with it.</summary>
    public sealed record Head(string Sha, bool Clean);

    /// <summary>
    /// Why a report may not be written, or null when it may.
    ///
    /// <b>An artifact that cannot say where it came from is the thing this refuses.</b> Every phase
    /// sign-off in this project quotes <c>artifacts/phase-report.json</c>, and until 3.12 the file
    /// carried no commit at all: a run that did not happen left the previous run's report in place,
    /// reading as current, and nothing on the page or in the JSON distinguished the two. Writing a
    /// report with a placeholder where the sha goes would be the same fault with an extra step, so
    /// the stage writes nothing and exits non-zero instead.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    public static string? WhyTheReportCannotBeWritten(Head? head) => head is null
        ? "the HEAD commit could not be read, so a report written here could not say which tree "
          + "produced it. A phase report is quoted at every sign-off and an undatable one is worse "
          + "than none, because it reads exactly like a current one. Run this from inside the "
          + "repository, with git on the path."
        : null;

    /// <summary>
    /// Stamps the report with the commit that produced it and writes both files, or writes neither.
    ///
    /// Returns the stamped report, or null when it refused. Both files go through here so there is
    /// one guard rather than one per file, and the stamp is applied at the write rather than in
    /// <c>Assemble</c> so that assembling a report and writing one cannot disagree about it.
    /// </summary>
    public static Report? WriteReport(Report report, string artifacts, Head? head)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifacts);

        string? refusal = WhyTheReportCannotBeWritten(head);
        if (refusal is not null)
        {
            Console.Error.WriteLine($"{Name}: {refusal}");
            return null;
        }

        Report stamped = report with { Commit = head!.Sha, TreeClean = head.Clean };

        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "phase-report.json"), JsonSerializer.Serialize(stamped, Json));
        File.WriteAllText(Path.Combine(artifacts, "phase-report.html"), Html(stamped));
        return stamped;
    }

    /// <summary>
    /// The commit HEAD is at and whether the working tree is clean, or null if either cannot be had.
    ///
    /// Read from git rather than tracked anywhere, on the same grounds as the expectations
    /// comparison above: git already knows. A tree with uncommitted changes still produces a report
    /// and the report says so, because refusing there would refuse every run made while working.
    /// The sha is checked for shape rather than taken on trust, so a git that answers something
    /// other than a commit is a refusal rather than a stamp nobody can resolve.
    /// </summary>
    public static Head? ReadHead(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string? sha = Git(root, "rev-parse HEAD")?.Trim();
        if (sha is null || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
        {
            return null;
        }

        string? status = Git(root, "status --porcelain");
        return status is null ? null : new Head(sha, status.Trim().Length == 0);
    }

    /// <summary>One git command's standard output, or null if it did not start or did not succeed.</summary>
    private static string? Git(string root, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using Process? git = Process.Start(start);
            if (git is null)
            {
                return null;
            }

            string output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // git is not on the path. A refusal rather than a throw, because the caller's job is
            // to say why no report was written and a stack trace does not say it.
            return null;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PullbackStrategyLab.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find PullbackStrategyLab.sln above the binary. The phase report reads the corpus from the "
            + "repository, so it cannot run from a published output that sits outside it.");
    }

    private static string Html(Report report)
    {
        var page = new StringBuilder();

        page.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        page.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        page.Append(CultureInfo.InvariantCulture, $"<title>Phase {report.Phase} report</title>");
        page.Append("<style>").Append(Style).Append("</style></head><body>");

        page.Append(CultureInfo.InvariantCulture,
            $"<h1>Phase {report.Phase} report <span class=\"{(report.Green ? "green" : "red")}\">{(report.Green ? "green" : "not green")}</span></h1>");
        page.Append(CultureInfo.InvariantCulture,
            $"<p class=\"when\">Generated {E(report.GeneratedAt)} at commit {E(report.Commit)}, "
            + $"working tree {(report.TreeClean ? "clean" : "dirty")}</p>");

        if (report.Reasons.Count > 0)
        {
            page.Append("<ul class=\"reasons\">");
            foreach (string reason in report.Reasons)
            {
                page.Append(CultureInfo.InvariantCulture, $"<li>{E(reason)}</li>");
            }

            page.Append("</ul>");
        }

        // Section one.
        page.Append("<h2>Document conformance</h2>");
        page.Append(CultureInfo.InvariantCulture,
            $"<p>{report.Claims.Total} claim(s): <b>{report.Claims.Passed}</b> pass, <b class=\"{(report.Claims.Failed > 0 ? "red" : string.Empty)}\">{report.Claims.Failed}</b> fail, "
            + $"{report.Claims.OutOfScope} placed in a later phase, <b class=\"{(report.Claims.Unexamined > 0 ? "red" : string.Empty)}\">{report.Claims.Unexamined}</b> unexamined. "
            + $"Unexamined is not a pass.</p>");

        Claim[] outOfScope = [.. report.ClaimDetail.Where(c => c.Verdict == "deferred")];
        if (outOfScope.Length > 0)
        {
            page.Append("<h3>Out of scope, by the checkpoint that ends it</h3>");
            page.Append(CultureInfo.InvariantCulture,
                $"<p>The last checkpoint recorded is {E(report.LastLanded)}. Every row here closes at a checkpoint "
                + $"ahead of it, so this count falls as they land rather than resting as a permanent number.</p>");
            page.Append("<table><tr><th>Closes at</th><th>Claims</th><th>Which</th></tr>");

            foreach (IGrouping<string, Claim> group in outOfScope
                .GroupBy(c => c.Closes ?? "nothing", StringComparer.Ordinal)
                .OrderBy(g => Checkpoint(g.Key)))
            {
                string which = string.Join(", ", group.Select(c => c.Subject).Order(StringComparer.Ordinal));
                page.Append(CultureInfo.InvariantCulture,
                    $"<tr><td class=\"{(group.Key == "nothing" ? "red" : string.Empty)}\">{E(group.Key)}</td>"
                    + $"<td>{group.Count()}</td><td>{E(which)}</td></tr>");
            }

            page.Append("</table>");
        }

        page.Append("<h3>Every claim</h3>");
        page.Append("<table><tr><th>Table</th><th>Subject</th><th>Verdict</th><th>Detail</th></tr>");
        foreach (Claim claim in report.ClaimDetail.OrderBy(c => Rank(c.Verdict)).ThenBy(c => c.Table, StringComparer.Ordinal))
        {
            page.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(claim.Table)}</td><td>{E(claim.Subject)}</td>"
                + $"<td class=\"v {E(claim.Verdict)}\">{E(claim.Verdict)}</td><td>{E(claim.Detail)}</td></tr>");
        }

        page.Append("</table>");

        // Section two.
        page.Append("<h2>Fixture diff</h2>");

        if (report.Fixture is null)
        {
            page.Append("<p class=\"red\">No fixture diff was produced.</p>");
        }
        else
        {
            page.Append(CultureInfo.InvariantCulture,
                $"<p>As of {E(report.Fixture.AsOf)}, inputs {E(report.Fixture.InputTier)}: "
                + $"{report.Fixture.CapturedResponses} captured response(s), {report.Fixture.ResponsesServed} read by the replay, "
                + $"screened over {report.Fixture.ScreeningSessions} session(s). "
                + $"Expectations changed since the last commit: <b>{E(report.ExpectationsChangedSinceHead)}</b>.</p>");

            if (report.Inputs is not null)
            {
                page.Append("<h3>Inputs, by tier</h3>");
                page.Append("<table><tr><th>Tier</th><th>Count</th><th>What</th><th>What it can say</th></tr>");
                foreach (InputTier tier in report.Inputs.Tiers)
                {
                    page.Append(CultureInfo.InvariantCulture,
                        $"<tr><td>{E(tier.Tier)}</td><td>{tier.Count}</td><td>{E(tier.What)}</td><td>{E(tier.WhatItCanSay)}</td></tr>");
                }

                page.Append("</table>");
            }

            page.Append("<h3>Expectations, by tier</h3>");
            page.Append("<table><tr><th>Tier</th><th>Total</th><th>Matched</th><th>Differed</th><th>Missing</th><th>What it can say</th></tr>");
            foreach (TierBreakdown tier in report.Expectations.ByTier)
            {
                page.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{E(tier.Tier)}</td><td>{tier.Total}</td><td>{tier.Matched}</td>"
                    + $"<td class=\"{(tier.Differed > 0 ? "red" : string.Empty)}\">{tier.Differed}</td>"
                    + $"<td class=\"{(tier.Missing > 0 ? "red" : string.Empty)}\">{tier.Missing}</td><td>{E(Meaning(tier.Tier))}</td></tr>");
            }

            page.Append("</table>");

            page.Append("<h3>Stages</h3><table><tr><th>Stage</th><th>Calls</th><th>Rows</th><th>Outcome</th></tr>");
            foreach (StageRun stage in report.Fixture.Stages)
            {
                page.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{E(stage.Stage)}</td><td>{stage.CallsUsed}</td><td>{stage.RowsWritten}</td><td>{E(stage.Outcome)}</td></tr>");
            }

            page.Append("</table>");

            DiffRow[] broken = [.. report.Fixture.Rows.Where(r => r.Verdict != "matched")];
            if (broken.Length > 0)
            {
                page.Append("<h3 class=\"red\">Expectations that did not hold</h3>");
                page.Append("<table><tr><th>Id</th><th>Tier</th><th>Checkpoint</th><th>Expected</th><th>Actual</th></tr>");
                foreach (DiffRow row in broken)
                {
                    page.Append(CultureInfo.InvariantCulture,
                        $"<tr><td>{E(row.Id)}</td><td>{E(row.Tier)}</td><td>{E(row.Checkpoint)}</td>"
                        + $"<td>{E(row.Expected)}</td><td class=\"red\">{E(row.Actual ?? "nothing")}</td></tr>");
                }

                page.Append("</table>");
            }
        }

        // Section three.
        page.Append("<h2>Coverage</h2>");
        page.Append("<p>What each check examined, not only whether it passed.</p>");
        page.Append("<table><tr><th>Check</th><th>Examined</th><th>Not owed yet</th><th>Unexamined</th><th>Why</th></tr>");

        foreach (CheckCoverageRecord record in report.Coverage)
        {
            string why = string.Join("; ", record.UnexaminedDetail.Select(u => $"{u.What} — {u.Why}"));
            page.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(record.Check)}</td><td>{record.Examined}</td><td>{record.OutOfScope}</td>"
                + $"<td class=\"{(record.Unexamined > 0 ? "red" : string.Empty)}\">{record.Unexamined}</td><td>{E(why)}</td></tr>");
        }

        page.Append("</table>");

        // Section four. What the checks conclude by reading the source, and what exercises it.
        //
        // A source scan that finds a pattern is not evidence the behaviour exists, and this corpus
        // has now shipped four assertions that survived the removal of their own subject. The list
        // is here rather than in a check because an unbacked scan is scheduled work rather than a
        // condition on the next commit, so it is reported and does not turn the report red.
        Scan[] scans = [.. report.Coverage.SelectMany(c => c.Scans.Select(s => s with { What = $"{c.Check}: {s.What}" }))];

        int byTest = scans.Count(s => s.BackedByTest is not null);
        int byJob = scans.Count(s => s.BackedByJob is not null);
        int byNothing = scans.Length - byTest - byJob;

        page.Append("<h2>Source-scan assertions</h2>");
        page.Append(CultureInfo.InvariantCulture,
            $"<p>{scans.Length} assertion(s) made by reading the shipped source. "
            + $"<b>{byTest}</b> are exercised by a behavioural test, <b>{byJob}</b> by a CI job, and "
            + $"<b class=\"{(byNothing > 0 ? "deferred" : string.Empty)}\">{byNothing}</b> by nothing. "
            + $"An unbacked scan is reported here rather than failing the run; the three are counted apart so "
            + $"the third growing is visible rather than absorbed.</p>");

        page.Append("<table><tr><th>Assertion</th><th>Exercised by</th><th>Why</th></tr>");
        foreach (Scan scan in scans.OrderBy(s => s.BackedByTest is null && s.BackedByJob is null ? 0 : 1)
                     .ThenBy(s => s.What, StringComparer.Ordinal))
        {
            string by = scan.BackedByTest ?? (scan.BackedByJob is string job ? $"the {job} job" : "nothing");
            string cell = scan.BackedByTest is null && scan.BackedByJob is null ? " class=\"deferred\"" : string.Empty;
            page.Append(CultureInfo.InvariantCulture,
                $"<tr><td>{E(scan.What)}</td><td{cell}>{E(by)}</td><td>{E(scan.Why)}</td></tr>");
        }

        page.Append("</table>");

        string[] noScan = [.. report.Coverage.Where(c => c.NoSourceScan is not null).Select(c => c.Check)];
        if (noScan.Length > 0)
        {
            page.Append(CultureInfo.InvariantCulture,
                $"<p>{noScan.Length} check(s) concluded nothing about behaviour by reading source and said so: "
                + $"{E(string.Join(", ", noScan.Order(StringComparer.Ordinal)))}.</p>");
        }

        page.Append("</body></html>");
        return page.ToString();
    }

    /// <summary>Checkpoints sort by their two numbers, so 1.10 follows 1.9 rather than 1.1.</summary>
    private static int Checkpoint(string identifier)
    {
        string[] parts = identifier.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int phase)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int step)
                ? (phase * 1000) + step
                : int.MaxValue;
    }

    private static int Rank(string verdict) => verdict switch
    {
        "fail" => 0,
        "unexamined" => 1,
        "pass" => 2,
        _ => 3,
    };

    private static string Meaning(string tier) => tier switch
    {
        "DERIVED" => "produced by a second implementation, so a difference means one of them is wrong",
        "CONFIRMED" => "read off a platform outside this project, so it can say the definition itself is right",
        "FROZEN" => "produced by this code, so it can only say the code has not changed",
        _ => "no tier recorded, which is itself a defect",
    };

    /// <summary>
    /// The five characters that would otherwise close a tag or an attribute. Written here rather
    /// than pulled from a web stack, because the Worker is a console application and a page it
    /// writes should not decide which assemblies it ships.
    /// </summary>
    private static string E(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    private const string Style = """
        :root { color-scheme: light dark; }
        body { font: 15px/1.55 -apple-system, Segoe UI, system-ui, sans-serif; margin: 2.2rem auto; max-width: 62rem; padding: 0 1.2rem; }
        h1 { font-size: 1.5rem; margin-bottom: .2rem; }
        h2 { font-size: 1.15rem; margin-top: 2.2rem; border-bottom: 1px solid #8884; padding-bottom: .3rem; }
        h3 { font-size: 1rem; margin-top: 1.4rem; }
        p.when { color: #8a8a8a; margin-top: 0; }
        table { border-collapse: collapse; width: 100%; margin: .8rem 0; font-size: 13.5px; }
        th, td { text-align: left; vertical-align: top; padding: .3rem .5rem; border-bottom: 1px solid #8883; }
        th { font-weight: 600; }
        td.v { font-variant: small-caps; letter-spacing: .02em; }
        td.pass { color: #167c3c; }
        td.fail, .red { color: #b3261e; font-weight: 600; }
        td.deferred, td.unexamined { color: #8a6d00; }
        .green { color: #167c3c; }
        ul.reasons { background: #b3261e14; border-left: 3px solid #b3261e; padding: .6rem 1.4rem; }
        """;

    public sealed record Report(
        int Phase,
        string LastLanded,
        string GeneratedAt,
        string Commit,
        bool TreeClean,
        bool Green,
        IReadOnlyList<string> Reasons,
        ClaimSummary Claims,
        FixtureSummary Expectations,
        int IndependentExpectations,
        string ExpectationsChangedSinceHead,
        InputTiers? Inputs,
        FixtureDiff? Fixture,
        IReadOnlyList<Claim> ClaimDetail,
        IReadOnlyList<CheckCoverageRecord> Coverage);

    public sealed record ClaimSummary(int Total, int Passed, int Failed, int OutOfScope, int Unexamined);

    public sealed record FixtureSummary(
        int Total,
        int Matched,
        int Differed,
        int Voided,
        IReadOnlyList<TierBreakdown> ByTier,
        IReadOnlyList<string> Unexpected);

    public sealed record Conformance(
        int Phase,
        string LastLanded,
        int Claims,
        int Passed,
        int Failed,
        int Deferred,
        int Unexamined,
        IReadOnlyList<Claim> Detail);

    /// <summary>
    /// One claim. <c>Closes</c> is the checkpoint that brings an out-of-scope claim into scope,
    /// and it is what stops the out-of-scope count reading as a permanent number. Whether that
    /// checkpoint exists and is still ahead is the conformance check's assertion, not this
    /// reporter's: a second implementation of it here would be a second place to keep right.
    /// </summary>
    public sealed record Claim(string Table, string Subject, string Verdict, string Detail, string? Closes = null);

    public sealed record FixtureDiff(
        string AsOf,
        string InputTier,
        int CapturedResponses,
        int ResponsesServed,
        IReadOnlyList<string> AskedOutsideTheFixture,
        IReadOnlyList<string> AskedOnAnUncoveredEndpoint,
        int ScreeningSessions,
        IReadOnlyList<StageRun> Stages,
        IReadOnlyList<TierBreakdown> ByTier,
        IReadOnlyList<string> Unexpected,
        IReadOnlyList<DiffRow> Rows);

    public sealed record StageRun(string Stage, int CallsUsed, int RowsWritten, string Outcome);

    public sealed record TierBreakdown(string Tier, int Total, int Matched, int Differed, int Missing);

    public sealed record DiffRow(
        string Id,
        string Tier,
        string Checkpoint,
        string Expected,
        string? Actual,
        string Verdict,
        string ProducedBy);

    public sealed record CheckCoverageRecord(
        string Check,
        int Examined,
        int Context,
        int Unexamined,
        int OutOfScope,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Gap> UnexaminedDetail,
        IReadOnlyList<Gap> OutOfScopeDetail,
        IReadOnlyList<Scan> Scans,
        string? NoSourceScan)
    {
        /// <summary>Empty rather than null for a record written before the scans existed.</summary>
        public IReadOnlyList<Scan> Scans { get; init; } = Scans ?? [];
    }

    /// <summary>One assertion a check makes by reading the shipped source, and what exercises it.</summary>
    public sealed record Scan(string What, string? BackedByTest, string? BackedByJob, string Why);

    /// <summary>
    /// The checks the roster in CLAUDE.md says run on every CI run, written by coverage-reported
    /// where this stage can read them. What the run is measured against, so a check that did not
    /// run is a missing row rather than a smaller report.
    /// </summary>
    public sealed record Expected(IReadOnlyList<string> Checks);

    public sealed record InputTier(string Tier, int Count, string What, string WhatItCanSay);

    public sealed record InputTiers(IReadOnlyList<InputTier> Tiers, IReadOnlyList<string> EndpointsWithNoCapturedInput);

    public sealed record Scope(string What, int Count, bool IsContext);

    public sealed record Gap(string What, int Count, string Why);
}
