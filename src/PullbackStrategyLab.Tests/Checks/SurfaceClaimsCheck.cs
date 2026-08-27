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

            // The claim states the text its surface must carry, and this is the assertion the whole
            // check exists to make: not that the value is in the store, not that a result was
            // recorded, but that the string is in what came back from the page.
            if (claim.MustCarry is not null
                && !html.Contains(claim.MustCarry, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{claim.Name}: {claim.Surface} does not carry \"{claim.MustCarry}\". "
                    + $"{claim.StatedIn} says \"{claim.Sentence}\", and it is not true of the page.");
            }
        }

        coverage
            .Examined("claims of visibility declared in the corpus", file.Claims.Length())
            .Examined("of those whose surface exists and was rendered and read", live.Length)
            .Context("surfaces rendered", rendered.Count)
            .Scan(
                "that a corpus sentence asserting visibility is true of the rendered page",
                CheckCoverage.Backing.Test(
                    $"{nameof(SurfaceClaimsCheck)}.{nameof(A_surface_that_drops_a_claim_is_caught)}",
                    "the comparison is run against a page body written by hand, so the guard is "
                    + "proved against a case rather than against whatever the pages happen to render"));

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
    /// The guard, proved against a page body written here.
    ///
    /// A check whose only subject is the live pages is a check nobody can break on purpose, and this
    /// one exists because the fault it catches is invisible to everything else in the suite.
    /// </summary>
    [Fact]
    public void A_surface_that_drops_a_claim_is_caught()
    {
        const string CarriesIt = "<span class=\"caveat\">the anchored clause arrives at 4.4</span>";
        const string DropsIt = "<span class=\"reading\">2.0193</span>";

        Assert.Contains("the anchored clause arrives at 4.4", CarriesIt, StringComparison.Ordinal);
        Assert.DoesNotContain("the anchored clause arrives at 4.4", DropsIt, StringComparison.Ordinal);
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
        { "store": "ready", "schemaVersion": 20, "session": "2026-08-24", "lastRun": null,
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
              "agreement": null, "agreementNote": null,
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
    /// And both sides of the minimum appear, one panel below it and one above. A page rendering only
    /// the reached case would carry the words a claim about the trigger looks for while being unable
    /// to tell a reader that the panel beside it is not an answer yet, which is the state the panel
    /// will be in for every night of the wait.
    /// </summary>
    private const string Panels = """
        {
          "asOf": "2026-08-24", "absent": null,
          "health": [
            { "name": "band0.nightsRecorded", "direction": null, "figure": "214",
              "low": null, "high": null, "rows": 214, "effective": null,
              "population": "every flagged setup", "minimum": null }
          ],
          "long": [
            { "name": "band1.vsTight", "direction": "long", "figure": "0.0110",
              "low": "-0.0030", "high": "0.0250", "rows": 3180, "effective": 412,
              "population": "every flagged setup", "minimum": 196 },
            { "name": "band1.vsLoose", "direction": "long", "figure": "withheld",
              "low": null, "high": null, "rows": 240, "effective": 31,
              "population": "every flagged setup", "minimum": 196 }
          ],
          "short": [
            { "name": "band2.decile1", "direction": "short", "figure": "0.0290",
              "low": null, "high": null, "rows": 1120, "effective": null,
              "population": "capped candidates only", "minimum": null }
          ]
        }
        """;
}

/// <summary>A count that reads the same way for a list and an array, so the scope names one thing.</summary>
internal static class ClaimCounting
{
    public static int Length<T>(this IReadOnlyList<T> items) => items.Count;
}
