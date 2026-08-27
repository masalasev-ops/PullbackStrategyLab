using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The gallery, asked for through the host.
///
/// The read surface is answered from a stub routed by path, so every state of the page is a test:
/// a night with setups on both sides, a night with nothing, and a filter that hid everything.
///
/// <b>The page's function is the record it leaves, and the paging is a convenience on top of it.</b>
/// So the form posts are asserted separately from the script, and the page is asserted to work with
/// the script removed: an agreement that could only be recorded by a keystroke would be a page that
/// stops working the day something in that script throws.
/// </summary>
public sealed class SetupsPageTests : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private const string Status = """
        { "store": "ready", "schemaVersion": 14, "session": "2026-08-24", "lastRun": null,
          "universeMembers": 2070, "barsStored": 1482108, "callsUsed": 0, "dailyCallCeiling": 5000,
          "marketMood": null, "positionsOpen": null, "shortPositionsOpen": null, "riskAtStake": null }
        """;

    /// <summary>One setup a side, which is enough to render every part of the page.</summary>
    private const string Night = """
        {
          "asOf": "2026-08-24", "failedCheck": null, "flagged": 2,
          "long": [
            { "setupId": "2026-08-24-HOOD-long", "ticker": "HOOD", "direction": "long",
              "rank": 1, "cappedOut": false, "passedAll": false,
              "triggerPrice": 118.50, "stopPrice": 112.25, "stopDistanceRanges": 0.31,
              "agreement": null, "agreementNote": null,
              "checks": [
                { "name": "tradable", "passed": true, "value": 204580994.64, "note": null },
                { "name": "exit-tight", "passed": false, "value": null, "note": "no stop or no daily range for the session" }
              ],
              "candles": [
                { "date": "2026-08-21", "open": 110.0, "high": 115.0, "low": 109.0, "close": 114.0 },
                { "date": "2026-08-24", "open": 114.0, "high": 119.0, "low": 113.0, "close": 118.0 }
              ] }
          ],
          "short": [
            { "setupId": "2026-08-24-INTC-short", "ticker": "INTC", "direction": "short",
              "rank": null, "cappedOut": null, "passedAll": false,
              "triggerPrice": 85.14, "stopPrice": 85.14, "stopDistanceRanges": 0.0,
              "agreement": "disagree", "agreementNote": "the bounce has not stalled",
              "checks": [
                { "name": "downtrend", "passed": true, "value": null, "note": "falling" },
                { "name": "averages-squeezing", "passed": false, "value": 1.2276, "note": null },
                { "name": "reached-ceiling", "passed": false, "value": 2.0193,
                  "note": "21-day and 50-day only; the anchored clause arrives at 4.4" },
                { "name": "tradable-shortable", "passed": true, "value": 9849921234.0, "note": null }
              ],
              "candles": [
                { "date": "2026-08-21", "open": 90.0, "high": 91.0, "low": 86.0, "close": 87.0 },
                { "date": "2026-08-24", "open": 87.0, "high": 88.0, "low": 85.14, "close": 87.26 }
              ] }
          ],
          "checkNames": ["averages-squeezing", "downtrend", "exit-tight", "reached-ceiling", "tradable", "tradable-shortable"],
          "nothing": null
        }
        """;

    private const string NothingFlagged = """
        { "asOf": "2026-08-24", "failedCheck": null, "flagged": 0, "long": [], "short": [],
          "checkNames": [], "nothing": "no setups were flagged on 2026-08-24" }
        """;

    private const string FilteredToNothing = """
        { "asOf": "2026-08-24", "failedCheck": "held-floor", "flagged": 2, "long": [], "short": [],
          "checkNames": ["exit-tight", "held-floor"], "nothing": null }
        """;

    private readonly WebApplicationFactory<LabApiClient> _host;

    public SetupsPageTests(WebApplicationFactory<LabApiClient> host) => _host = host;

    private static StubHandler Handler(string setupsBody, HttpStatusCode agreementStatus = HttpStatusCode.OK) =>
        new(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/agreement", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(agreementStatus)
                {
                    Content = new StringContent(
                        """{ "setupId": "x", "recorded": true, "why": null }""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                };
            }

            string body = path.StartsWith("/setups", StringComparison.Ordinal) ? setupsBody : Status;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });

    /// <summary>
    /// The antiforgery token the form carries, read out of the rendered page.
    ///
    /// The tag helper puts it there and the host validates it, so a test that posted without one
    /// would be testing a page nobody can use. Reading it back is also the assertion that it is
    /// there: a form that lost its token would fail here rather than in a browser.
    /// </summary>
    private static string Token(string html)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            html,
            """name="__RequestVerificationToken"[^>]*value="(?<token>[^"]+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.True(match.Success, "The gallery's form carries no antiforgery token.");
        return match.Groups["token"].Value;
    }

    private HttpClient Client(StubHandler handler) =>
        _host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler)))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_night_draws_both_sides_apart_with_every_check_on_each_card()
    {
        using HttpClient client = Client(Handler(Night));
        string html = await client.GetStringAsync("/setups");

        // Two headings, so the two sides are two lists and not one list with a column. A pooled
        // gallery is one careless loop away and this is what stands between.
        Assert.Contains(">Long <span class=\"count\">1</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Short <span class=\"count\">1</span>", html, StringComparison.Ordinal);

        Assert.Contains("HOOD", html, StringComparison.Ordinal);
        Assert.Contains("INTC", html, StringComparison.Ordinal);

        // Every check, passed and failed alike. A gallery showing only failures could not be
        // disagreed with on a pass.
        Assert.Contains("<li class=\"pass\">", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"fail\">", html, StringComparison.Ordinal);
        Assert.Contains("tradable", html, StringComparison.Ordinal);
        Assert.Contains("averages-squeezing", html, StringComparison.Ordinal);

        // The candles come from the one shared component, laid small.
        Assert.Contains("<rect class=\"body\"", html, StringComparison.Ordinal);

        // A check handed nothing shows what was absent rather than a blank, so a reader deciding
        // whether they agree can tell it apart from a threshold that was tested and missed.
        Assert.Contains("no stop or no daily range for the session", html, StringComparison.Ordinal);

        // An unranked setup says so. A blank cell reads as a zero, which is the top of the ranking.
        Assert.Contains("rank unranked", html, StringComparison.Ordinal);
        Assert.Contains("rank 1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_that_has_both_a_number_and_a_caveat_shows_both()
    {
        using HttpClient client = Client(Handler(Night));
        string html = await client.GetStringAsync("/setups");

        // The regression this closes. `Reading` fell back to the note only when there was no value,
        // so a check carrying both showed the number and swallowed the caveat. The two notes that
        // matter most both carry a value: `reached-ceiling` runs two of its three clauses until 4.4,
        // and a calibration `tradable-shortable` runs three of its four. ARCHITECTURE says the setup
        // record states that narrowing outright rather than leaving it to be inferred from a passing
        // verdict, and the screen is where it is read.
        Assert.Contains("21-day and 50-day only; the anchored clause arrives at 4.4", html, StringComparison.Ordinal);
        Assert.Contains("class=\"caveat\"", html, StringComparison.Ordinal);

        // And the number still shows beside it rather than being replaced by it.
        Assert.Contains("2.02 daily range(s) to the nearer average", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_on_a_card_says_what_it_is_and_what_it_was_tested_against()
    {
        using HttpClient client = Client(Handler(Night));
        string html = await client.GetStringAsync("/setups");

        // The defect the gallery review found: the card read `tradable-shortable 9849921234`, which
        // is a median daily turnover in dollars tested against a fifty million dollar floor, and none
        // of that was recoverable from the digits.
        Assert.DoesNotContain(">9849921234<", html, StringComparison.Ordinal);
        Assert.Contains("$9.85bn median daily turnover", html, StringComparison.Ordinal);
        Assert.Contains("floor $50m", html, StringComparison.Ordinal);
        Assert.Contains("class=\"against\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agreement_already_recorded_is_shown_beside_its_note()
    {
        using HttpClient client = Client(Handler(Night));
        string html = await client.GetStringAsync("/setups");

        Assert.Contains("the bounce has not stalled", html, StringComparison.Ordinal);
        Assert.Contains(">disagree</span>", html, StringComparison.Ordinal);

        // And the one nobody has looked at says that, rather than reading as an absence of opinion
        // that could be mistaken for agreement.
        Assert.Contains("not looked at", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_an_agreement_posts_it_and_re_reads_the_night()
    {
        StubHandler handler = Handler(Night);
        using HttpClient client = Client(handler);

        string token = Token(await client.GetStringAsync("/setups"));

        using HttpResponseMessage response = await client.PostAsync("/setups", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("setupId", "2026-08-24-HOOD-long"),
            new KeyValuePair<string, string>("agreement", "agree"),
            new KeyValuePair<string, string>("note", "the dip is the right shape"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(handler.Asked, p => p.EndsWith("/agreement", StringComparison.Ordinal));

        // The night is re-read after the post rather than the page rendering what it believes it
        // just wrote, so what the screen shows is what the store holds.
        Assert.Contains(handler.Asked, p => p.StartsWith("/setups/", StringComparison.Ordinal)
            && !p.EndsWith("/agreement", StringComparison.Ordinal));

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("HOOD", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agreement_the_read_surface_refuses_is_said_out_loud_and_the_night_still_renders()
    {
        using HttpClient client = Client(Handler(Night, HttpStatusCode.BadRequest));

        string token = Token(await client.GetStringAsync("/setups"));

        using HttpResponseMessage response = await client.PostAsync("/setups", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("setupId", "nonsense"),
            new KeyValuePair<string, string>("agreement", "agree"),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        ]));

        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The agreement was not recorded", html, StringComparison.Ordinal);
        Assert.Contains("HOOD", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_night_with_nothing_flagged_says_so()
    {
        using HttpClient client = Client(Handler(NothingFlagged));
        string html = await client.GetStringAsync("/setups");

        Assert.Contains("no setups were flagged on 2026-08-24", html, StringComparison.Ordinal);
        Assert.Contains("class=\"band\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_that_hid_everything_says_it_hid_it_rather_than_showing_an_empty_night()
    {
        using HttpClient client = Client(Handler(FilteredToNothing));
        string html = await client.GetStringAsync("/setups?failed=held-floor");

        // The distinction the page has to make: two setups were flagged and the filter left none.
        // "Nothing was flagged" and "nothing failed this check" are different nights.
        Assert.Contains("2 setup(s) were flagged", html, StringComparison.Ordinal);
        Assert.Contains("none of them failed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_only_script_is_local_and_the_recording_works_without_it()
    {
        using HttpClient client = Client(Handler(Night));
        string html = await client.GetStringAsync("/setups");

        // Local and unbundled, permitted by name for exactly this page.
        // see: Pages are server-rendered with no build step, and any script is local rather than fetched
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);

        // And the function the page exists for is a form post, which is what makes the script a
        // convenience rather than the mechanism.
        Assert.Contains("<form method=\"post\" class=\"agree\">", html, StringComparison.Ordinal);
        Assert.Contains("name=\"agreement\" value=\"agree\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"agreement\" value=\"disagree\"", html, StringComparison.Ordinal);
    }
}
