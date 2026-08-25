using System.Diagnostics;
using System.Globalization;
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

        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "phase-report.json"), JsonSerializer.Serialize(report, Json));
        File.WriteAllText(Path.Combine(artifacts, "phase-report.html"), Html(report));

        Console.WriteLine($"{Name}: phase {report.Phase}, {report.Claims.Total} claim(s), {report.Expectations.Total} expectation(s)");
        Console.WriteLine($"{Name}: {report.Claims.Passed} passed, {report.Claims.Failed} failed, {report.Claims.OutOfScope} out of scope, {report.Claims.Unexamined} unexamined");
        Console.WriteLine($"{Name}: coverage examined {report.Coverage.Sum(c => c.Examined)}, unexamined {report.Coverage.Sum(c => c.Unexamined)}");
        Console.WriteLine($"{Name}: inputs {string.Join(", ", (report.Inputs?.Tiers ?? []).Select(t => $"{t.Tier} {t.Count}"))}");
        Console.WriteLine($"{Name}: expectations changed since the last commit: {report.ExpectationsChangedSinceHead}");
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

        var claims = new ClaimSummary(
            conformance?.Claims ?? 0,
            conformance?.Passed ?? 0,
            conformance?.Failed ?? 0,
            conformance?.Deferred ?? 0,
            conformance?.Unexamined ?? 0);

        var expectations = new FixtureSummary(
            fixture?.Rows.Count ?? 0,
            fixture?.Rows.Count(r => r.Verdict == "matched") ?? 0,
            fixture?.Rows.Count(r => r.Verdict != "matched") ?? 0,
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
            generatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
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
        page.Append(CultureInfo.InvariantCulture, $"<p class=\"when\">Generated {E(report.GeneratedAt)}</p>");

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

        page.Append("</table></body></html>");
        return page.ToString();
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
        string GeneratedAt,
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
        IReadOnlyList<TierBreakdown> ByTier,
        IReadOnlyList<string> Unexpected);

    public sealed record Conformance(
        int Phase,
        int Claims,
        int Passed,
        int Failed,
        int Deferred,
        int Unexamined,
        IReadOnlyList<Claim> Detail);

    public sealed record Claim(string Table, string Subject, string Verdict, string Detail);

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
        int Unexamined,
        int OutOfScope,
        IReadOnlyList<Scope> ExaminedDetail,
        IReadOnlyList<Gap> UnexaminedDetail,
        IReadOnlyList<Gap> OutOfScopeDetail);

    public sealed record InputTier(string Tier, int Count, string What, string WhatItCanSay);

    public sealed record InputTiers(IReadOnlyList<InputTier> Tiers, IReadOnlyList<string> EndpointsWithNoCapturedInput);

    public sealed record Scope(string What, int Count);

    public sealed record Gap(string What, int Count, string Why);
}
