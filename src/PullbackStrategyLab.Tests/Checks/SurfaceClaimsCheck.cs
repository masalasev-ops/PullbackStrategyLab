using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every corpus claim that something is visible is asserted against the surface a person reads it on.
///
/// <b>The sixth defect shape, closed.</b> Four early shapes were a check asserting less than its
/// label; the fifth was a figure over the wrong population. This one is neither: the instrument is
/// correct, it asserts exactly what it says, and <b>its answer is discarded downstream</b>.
/// `reached-ceiling` recorded that it ran two of its three clauses, `check-completeness` confirmed
/// the result was present, and ARCHITECTURE said the narrowing is stated outright rather than left to
/// be inferred from a passing verdict. Every one of those was true of the store. The gallery dropped
/// the note whenever a value sat beside it, so the sentence was false of the screen, which is the
/// only place it was ever about. Nothing upstream was wrong, so nothing upstream could have caught it.
///
/// <b>It renders the page and reads what came out.</b> That is the whole difference from every other
/// check here, all of which read source, the store, or a document. A claim about what a person can
/// see cannot be verified against what a machine can read, and until this existed it was.
///
/// <b>Deliberately narrow, and not UI testing.</b> The subject is a declared list of corpus
/// sentences and the exact text each requires. It says nothing about whether a page is readable,
/// well laid out, or any good. A claim whose surface arrives later names the checkpoint that builds
/// it and is counted out of scope, so the number falls as checkpoints land rather than resting.
/// </summary>
public sealed class SurfaceClaimsCheck : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<LabApiClient> _host;
    private readonly ITestOutputHelper _output;

    public SurfaceClaimsCheck(WebApplicationFactory<LabApiClient> host, ITestOutputHelper output)
    {
        _host = host;
        _output = output;
    }

    /// <summary>One declared claim: a sentence, where it is stated, and what its surface must carry.</summary>
    public sealed record Claim(
        string Name,
        string Sentence,
        string StatedIn,
        string Surface,
        string? MustCarry,
        string? ArrivesAt,
        string Why);

    private sealed record ClaimFile(string Tier, IReadOnlyList<Claim> Claims);

    [Fact]
    [Trait("check", "surface-claims")]
    public async Task Every_claim_of_visibility_holds_on_the_surface_that_carries_it()
    {
        var coverage = new CheckCoverage("surface-claims", _output);

        ClaimFile file = JsonSerializer.Deserialize<ClaimFile>(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", "surface-claims.json")), Json)
            ?? throw new InvalidOperationException("surface-claims.json did not parse.");

        Claim[] live = [.. file.Claims.Where(c => c.ArrivesAt is null)];
        Claim[] deferred = [.. file.Claims.Where(c => c.ArrivesAt is not null)];

        var rendered = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (Claim claim in live)
        {
            if (!rendered.TryGetValue(claim.Surface, out string? html))
            {
                html = await Render(claim.Surface);
                rendered[claim.Surface] = html;
            }

            failures.AddRange(Missing(claim, html));
        }

        coverage
            .Examined("claims of visibility declared in the corpus", file.Claims.Length())
            .Examined("of those whose surface exists and was rendered and read", live.Length)
            .Context("surfaces rendered", rendered.Count)
            .NoSourceScan(
                "it renders each page through the host and compares what came back against the text the "
                + "corpus claims is on it. Neither side is the shipped source: one is a rendered response and "
                + "the other is an authored claim, so nothing here concludes anything by reading code. It "
                + "declared a source-scan assertion until the phase 3 review, backed by a test that compared "
                + "two of its own string constants and called nothing in this class, which is the shape "
                + "CLAUDE.md names worse than no backing at all because it reads as covered");

        foreach (Claim claim in deferred)
        {
            coverage.OutOfScope(
                $"claim on a surface that arrives later: {claim.Name}", 1,
                CheckCoverage.OutOfScopeReason.UntilCheckpoint(
                    claim.ArrivesAt!,
                    $"{claim.StatedIn} says \"{claim.Sentence}\", and {claim.Surface} does not exist yet"));
        }

        coverage.Report();

        Assert.True(live.Length > 0,
            "No live claims were read at all, which means the claim file stopped parsing and this "
            + "check is asserting over an empty list. Every empty list holds.");

        Assert.True(failures.Count == 0,
            $"{failures.Count} claim(s) of visibility are false of the surface that carries them:\n  "
            + string.Join("\n  ", failures));
    }

    /// <summary>
    /// What a rendered page fails to carry, or nothing where it carries everything the claim names.
    ///
    /// Separated from the run so the comparison can be proved against a page body written by hand.
    /// It was inline until the phase 3 review, which is why the test named as its backing could
    /// only compare two literals of its own: there was nothing to call.
    /// </summary>
    public static IReadOnlyList<string> Missing(Claim claim, string html)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(html);

        // The claim states the text its surface must carry, and this is the assertion the whole
        // check exists to make: not that the value is in the store, not that a result was
        // recorded, but that the string is in what came back from the page.
        if (claim.MustCarry is null || html.Contains(claim.MustCarry, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            $"{claim.Name}: {claim.Surface} does not carry \"{claim.MustCarry}\". "
            + $"{claim.StatedIn} says \"{claim.Sentence}\", and it is not true of the page.",
        ];
    }

    /// <summary>
    /// The guard, proved against a page body written here and run through the check's own
    /// comparison.
    ///
    /// A check whose only subject is the live pages is a check nobody can break on purpose, and this
    /// one exists because the fault it catches is invisible to everything else in the suite.
    ///
    /// <b>It called nothing in this class until the phase 3 review.</b> It declared two constants
    /// and asserted that one contained a substring and the other did not, which is a property of
    /// `string.Contains` and holds however this check behaves. Deleting the comparison left it
    /// green.
    /// </summary>
    [Fact]
    public void A_surface_that_drops_a_claim_is_caught()
    {
        var claim = new Claim(
            Name: "a-claim",
            Sentence: "the caveat is shown beside the reading",
            StatedIn: "ARCHITECTURE.html",
            Surface: "/setups",
            MustCarry: "the anchored clause arrives at 4.4",
            ArrivesAt: null,
            Why: "a proof, not a run");

        Assert.Empty(Missing(claim, "<span class=\"caveat\">the anchored clause arrives at 4.4</span>"));

        string failure = Assert.Single(Missing(claim, "<span class=\"reading\">2.0193</span>"));
        Assert.Contains("does not carry", failure, StringComparison.Ordinal);
        Assert.Contains("the anchored clause arrives at 4.4", failure, StringComparison.Ordinal);

        // A claim naming no text is not a claim about the page's words, and is not a failure.
        Assert.Empty(Missing(claim with { MustCarry = null }, "anything at all"));
    }

    /// <summary>
    /// One surface, rendered through the host with the read surface stubbed.
    ///
    /// Stubbed rather than pointed at a socket, on the same grounds the shell tests give: the Api and
    /// the pages are two hosts started separately, and a request reaching a port nobody is listening
    /// on tests the timeout instead of the page.
    /// </summary>
    private async Task<string> Render(string path)
    {
        using HttpClient client = _host
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => Surfaces())))
            .CreateClient();

        return await client.GetStringAsync(path);
    }

    /// <summary>
    /// The read surface, answering each route with a body that carries the things the claims are
    /// about.
    ///
    /// <b>The bodies are authored and that is the point.</b> What is under test is whether the page
    /// carries a note it was handed, so the note has to be handed to it. A body drawn from the live
    /// store would test whichever notes that store happened to hold today.
    /// </summary>
    private static StubHandler Surfaces() =>
        new(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            string body = path switch
            {
                _ when path.StartsWith("/setups", StringComparison.Ordinal) => Night,
                _ when path.StartsWith("/scoreboard", StringComparison.Ordinal) => Panels,
                _ => Status,
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });

    private const string Status = """
        { "store": "ready", "schemaVersion": 20, "schemaVersionExpected": 32,
          "session": "2026-08-24", "lastRun": null,
          "universeMembers": 2070, "barsStored": 1482108, "callsUsed": 0, "dailyCallCeiling": 5000,
          "marketMood": null, "positionsOpen": null, "shortPositionsOpen": null, "riskAtStake": null }
        """;

    /// <summary>
    /// A night carrying the two notes the gallery review found dropped, and a check handed nothing.
    ///
    /// `reached-ceiling` carries its narrowing beside a value, which is exactly the shape the screen
    /// used to swallow: the fallback showed the note only when there was no value, so a check with
    /// both lost the note entirely.
    /// </summary>
    private const string Night = """
        {
          "asOf": "2026-08-24", "failedCheck": null, "flagged": 1,
          "long": [],
          "short": [
            { "setupId": "2026-08-24-INTC-short", "ticker": "INTC", "direction": "short",
              "rank": null, "cappedOut": null, "passedAll": false,
              "triggerPrice": 85.14, "stopPrice": 85.14, "stopDistanceRanges": 0.0,
              "agreement": null, "agreementNote": null, "degradedBecause": "sectors",
              "checks": [
                { "name": "reached-ceiling", "passed": false, "value": 2.0193,
                  "note": "21-day and 50-day only; the anchored clause arrives at 4.4" },
                { "name": "tradable-shortable", "passed": true, "value": 9849921234.0,
                  "note": "turnover, price and listing age only; no market capitalisation was resolved" },
                { "name": "exit-tight", "passed": false, "value": null,
                  "note": "no stop or no daily range for the session" }
              ],
              "candles": [
                { "date": "2026-08-24", "open": 85.0, "high": 86.0, "low": 84.0, "close": 85.5 }
              ] }
          ]
        }
        """;

    /// <summary>
    /// A scoreboard with one panel of each shape: an interval that does not clear zero, a plain
    /// count, and a decile over the other population.
    ///
    /// The interval matters most. A page that only ever rendered a clearing interval would satisfy a
    /// claim about showing results and say nothing about showing non-results, and the claim is that
    /// each panel carries the condition under which it reads badly.
    ///
    /// The two populations matter nearly as much. Both appear here, because a page rendering only
    /// one of them would carry the string the population claim looks for while still being unable to
    /// tell a reader that the panel below it counted something else.
    ///
    /// And both sides of the minimum appear, with panels below it and above. A page rendering only
    /// the reached case would carry the words a claim about the trigger looks for while being unable
    /// to tell a reader that the panel beside it is not an answer yet, which is the state the panel
    /// will be in for every night of the wait.
    ///
    /// The withheld panels name which shortage is blocking them. The first names a shortage of
    /// sessions rather than of evidence, because that is a distinction the page could not previously
    /// draw: withholding is settled by the session axis and the minimum by how much information the
    /// rows carry, and the two can disagree outright.
    ///
    /// The second names a shortage of control outcomes, which is the reason the panel could not give
    /// for the whole of phase 3. ForwardReturnFiller wrote no control outcome at all, so band 1 was
    /// empty on every night, and the panel said no horizon had closed while the store held thirty
    /// nights of closed horizons. A diagnostic that points away from the defect sends a reader to
    /// wait for something that has already happened, which is worse than one that says nothing.
    ///
    /// <b>This claim holds that the page renders the words, and nothing more than that.</b> That the
    /// stage produces them is held by the fixture, at `accumulation.starved.withheldBecause`, over a
    /// population with every control outcome deleted. Neither substitutes for the other: the sixth
    /// defect shape is exactly the gap between a producer that is right and a surface that drops it.
    /// </summary>
    private const string Panels = """
        {
          "asOf": "2026-08-24", "absent": null,
          "health": [
            { "name": "band0.nightsRecorded", "direction": null, "figure": "214",
              "low": null, "high": null, "rows": 214, "effective": null,
              "population": "every flagged setup", "minimum": null,
              "withheldBecause": null }
          ],
          "long": [
            { "name": "band1.vsTight", "direction": "long", "figure": "0.0110",
              "low": "-0.0030", "high": "0.0250", "rows": 3180, "effective": 412,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": null },
            { "name": "band1.vsLoose", "direction": "long", "figure": "withheld",
              "low": null, "high": null, "rows": 240, "effective": 31,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": "only 14 session(s) carry a pair and a block bootstrap needs 20, which is a shortage of sessions rather than of evidence" }
          ],
          "short": [
            { "name": "band1.vsLoose", "direction": "short", "figure": "withheld",
              "low": null, "high": null, "rows": 0, "effective": 0,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": "144 setup outcome(s) have closed and no control outcome has, so no pair exists. That is a shortage of control outcomes rather than of time, and waiting does not fix it" },
            { "name": "band2.decile1", "direction": "short", "figure": "0.0290",
              "low": null, "high": null, "rows": 1120, "effective": null,
              "population": "capped candidates only", "minimum": null,
              "withheldBecause": null }
          ]
        }
        """;
}

/// <summary>A count that reads the same way for a list and an array, so the scope names one thing.</summary>
internal static class ClaimCounting
{
    public static int Length<T>(this IReadOnlyList<T> items) => items.Count;
}
