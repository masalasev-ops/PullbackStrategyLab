using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using PullbackStrategyLab.Web.Shell;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The trade journal as a person reads it.
///
/// <b>Asserted against the rendered page rather than against the view.</b> The sixth failure shape
/// this corpus catalogues is a producer that is right and a surface that drops its answer, and every
/// figure on this page comes from a component that was already asserted correct at 4.7 through 4.10.
/// What is open is whether the page carries them.
///
/// The read surface is answered from a stub rather than from a socket, on the terms the shell tests
/// already set: the Api and the pages are two hosts started separately, so a request to a port
/// nobody is listening on would test the timeout instead of the page.
/// </summary>
public sealed class JournalPageTests : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private const string StatusBody = """
        {
          "store": "ready", "schemaVersion": 47, "session": "2026-08-24", "lastRun": null,
          "universeMembers": 2070, "barsStored": 1482108, "callsUsed": 690, "dailyCallCeiling": 5000,
          "marketMood": null, "positionsOpen": 2, "shortPositionsOpen": 1, "riskAtStake": 1.53
        }
        """;

    /// <summary>
    /// One trade a side, because every claim this page answers is about the two sides separately.
    ///
    /// The short is the one that carries the borrow assumptions, a trim, a reduction by a cap and a
    /// loss whose horizon has not closed. The long carries none of those, which is what makes the
    /// page's per-row branches assertable rather than only its per-page ones.
    /// </summary>
    private const string JournalBody = """
        {
          "asOf": "2026-08-24", "absent": null,
          "longExpectancyR": 9.8, "shortExpectancyR": -1.02,
          "slotsTheCapsCouldNotSee": 3,
          "long": [
            { "tradeId": "t-long", "ticker": "AAA", "direction": "long",
              "openedSession": "2026-08-20", "closedSession": "2026-08-24",
              "entryPrice": "100.10", "exitPrice": "126.40", "exitReason": "trail",
              "resultR": 9.8, "heldSessions": 4, "shares": 150, "trimmedShares": 0,
              "riskIntended": "750.00", "riskRealised": "765.00",
              "borrowRateAssumed": null, "borrowCost": null, "borrowAvailability": null,
              "entryDifferenceBasisPoints": 10.0, "exitDifferenceBasisPoints": 10.0,
              "entryBasis": "slipped", "exitBasis": "slipped",
              "plannedGiveUp": "95.00", "plannedShares": 150, "executedShares": 150,
              "reducedBecause": null,
              "lossMechanism": null, "aftermath": null, "aftermathBecause": null }
          ],
          "short": [
            { "tradeId": "t-short", "ticker": "BBB", "direction": "short",
              "openedSession": "2026-08-21", "closedSession": "2026-08-24",
              "entryPrice": "99.90", "exitPrice": "105.11", "exitReason": "give-up",
              "resultR": -1.02, "heldSessions": 3, "shares": 150, "trimmedShares": 22,
              "riskIntended": "750.00", "riskRealised": "765.00",
              "borrowRateAssumed": "0.010", "borrowCost": "1.23",
              "borrowAvailability": "borrow availability is not in the price feed",
              "entryDifferenceBasisPoints": 10.0, "exitDifferenceBasisPoints": 0.0,
              "entryBasis": "slipped", "exitBasis": "gapped",
              "plannedGiveUp": "105.00", "plannedShares": 196, "executedShares": 150,
              "reducedBecause": "total-risk",
              "lossMechanism": "gap", "aftermath": null, "aftermathBecause": null }
          ]
        }
        """;

    /// <summary>
    /// How a leading plus sign reaches the page.
    ///
    /// Razor's default HTML encoder escapes it, which is correct and is not what any of these tests
    /// is about. Named once here rather than written out at four call sites, so a reader is not left
    /// wondering whether the escape is the subject.
    /// </summary>
    private const string Plus = "&#x2B;";

    private readonly WebApplicationFactory<LabApiClient> _host;

    public JournalPageTests(WebApplicationFactory<LabApiClient> host) => _host = host;

    /// <summary>
    /// The page renders both sides, each figure a component produced, and the caps caption.
    ///
    /// This is the whole checkpoint on one surface: two panels, a result in R per trade, how long it
    /// was held, its exit rule, what the plan asked for against what happened, and the size of the
    /// approximation the caps make.
    /// </summary>
    [Fact]
    public async Task The_page_carries_both_sides_and_every_figure_the_stages_produced()
    {
        string html = await Render();

        Assert.Contains("AAA", html, StringComparison.Ordinal);
        Assert.Contains("BBB", html, StringComparison.Ordinal);
        Assert.Contains(Plus + "9.8R", html, StringComparison.Ordinal);
        Assert.Contains("-1.0R", html, StringComparison.Ordinal);
        Assert.Contains("trail", html, StringComparison.Ordinal);
        Assert.Contains("give-up", html, StringComparison.Ordinal);

        // The caps caption, which is the one figure on the page that is about an approximation
        // rather than about a trade.
        Assert.Contains("3 position(s) closed in the session they opened in", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two expectancies are two figures and the page has no total.
    ///
    /// A page that could add them would be one number away from the fault the pooling rule exists to
    /// stop, and a screen is where it is easiest to commit.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public async Task The_two_expectancies_are_two_figures_and_there_is_no_total()
    {
        string html = await Render();

        Assert.Contains("Long expectancy", html, StringComparison.Ordinal);
        Assert.Contains("Short expectancy", html, StringComparison.Ordinal);
        Assert.Contains(Plus + "9.80R over 1", html, StringComparison.Ordinal);
        Assert.Contains("-1.02R over 1", html, StringComparison.Ordinal);
        Assert.Contains("never added together", html, StringComparison.Ordinal);

        // The two sides are two blocks rather than one table with a direction column, which is what
        // makes a total awkward to write rather than merely against the rules.
        Assert.Contains("journal long", html, StringComparison.Ordinal);
        Assert.Contains("journal short", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A short carries both unmodelled assumptions and a long carries the sentence saying it carries
    /// neither.
    ///
    /// A blank cell on a long reads as a cost of nought, which is a claim about borrowing rather than
    /// the absence of the question.
    /// </summary>
    [Fact]
    public async Task A_short_carries_both_assumptions_and_a_long_says_it_carries_neither()
    {
        string html = await Render();

        Assert.Contains("borrow availability is not in the price feed", html, StringComparison.Ordinal);
        Assert.Contains("1.23 at 0.010 a year assumed", html, StringComparison.Ordinal);
        Assert.Contains("not a short, so no borrow is assumed", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The realised risk is beside the intended risk on every row, which is what "beside" is a claim
    /// about.
    /// </summary>
    [Fact]
    public async Task Every_row_carries_the_realised_risk_beside_the_intended_one()
    {
        string html = await Render();

        Assert.Contains("risk intended 750.00 beside risk realised 765.00", html, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(html, "risk intended"));
    }

    /// <summary>
    /// A gapped end is named rather than numbered, because the model charged nothing on it and the
    /// price moved anyway.
    ///
    /// A basis-point figure beside a slipped one would be two different quantities in one column,
    /// which is what the fill's basis is carried to prevent.
    /// </summary>
    [Fact]
    public async Task A_gapped_end_is_named_rather_than_numbered()
    {
        string html = await Render();

        Assert.Contains(Plus + "10.0bps in, gapped out", html, StringComparison.Ordinal);
        Assert.Contains(Plus + "10.0bps in, " + Plus + "10.0bps out", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loss whose horizon has not closed reads as waiting rather than as unclassified, which is the
    /// distinction the classifier keeps and the page has to keep with it.
    /// </summary>
    [Fact]
    public async Task A_loss_waiting_on_its_horizon_says_so_rather_than_reading_as_unclassified()
    {
        string html = await Render();

        Assert.Contains("gap, awaiting its ten-session horizon", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unclassified", html, StringComparison.Ordinal);
    }

    /// <summary>A trimmed short says what the trim took out of the position it opened with.</summary>
    [Fact]
    public async Task A_trimmed_short_says_what_the_trim_took()
    {
        string html = await Render();

        Assert.Contains("150 less 22 trimmed", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A store with no closed trade says so in words a built page uses, and never in the words an
    /// unbuilt one does.
    ///
    /// "Nothing has closed" and "this page is not built" are different facts. A built page reusing
    /// the empty-state sentence would report itself as unbuilt on the one surface a person reads.
    /// </summary>
    [Fact]
    public async Task A_lab_that_has_closed_nothing_says_so_without_claiming_to_be_unbuilt()
    {
        string html = await Render("""
            { "asOf": "2026-08-24", "absent": "no trade has closed yet",
              "longExpectancyR": null, "shortExpectancyR": null,
              "slotsTheCapsCouldNotSee": 0, "long": [], "short": [] }
            """);

        Assert.Contains("No closed trades", html, StringComparison.Ordinal);
        Assert.Contains("no trade has closed yet", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing here yet", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chart for one trade draws its session minute by minute with the four prices that decided
    /// it, which is the checkpoint's other half.
    ///
    /// A daily candle cannot show a trigger reached at 10:00 and a stop reached at 14:00 on the same
    /// day, so this is a different picture from the one the chart page draws for a window, and every
    /// level on it is a price a component already recorded rather than one this page derived.
    /// </summary>
    [Fact]
    public async Task The_chart_for_a_trade_draws_its_minutes_with_the_four_levels_on_them()
    {
        string html = await RenderChart(TradeChartBody);

        Assert.Contains("minute by minute", html, StringComparison.Ordinal);
        Assert.Contains("chart minutes", html, StringComparison.Ordinal);

        foreach (string level in new[] { "trigger", "give-up", "fill", "exit" })
        {
            Assert.Contains($"level {level}", html, StringComparison.Ordinal);
        }

        // The prices themselves, so a level line is checkable against the row it came from rather
        // than only present as a class name.
        Assert.Contains("100.00", html, StringComparison.Ordinal);
        Assert.Contains("95.00", html, StringComparison.Ordinal);
        Assert.Contains("100.10", html, StringComparison.Ordinal);
        Assert.Contains("94.90", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trade whose entry filled in an earlier session says so, because the picture is one session
    /// and the fill line is a price that session may never have reached.
    /// </summary>
    [Fact]
    public async Task A_trade_that_opened_earlier_says_which_session_the_fill_line_is_from()
    {
        string html = await RenderChart(TradeChartBody);

        Assert.Contains("The entry filled in the session of 2026-08-21", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trade the store holds no minute for says which absence it is rather than drawing an empty
    /// box, on the terms every other absent answer in this shell is a sentence.
    /// </summary>
    [Fact]
    public async Task A_trade_with_no_stored_minute_says_so_rather_than_drawing_nothing()
    {
        string html = await RenderChart("""
            { "tradeId": "t-short", "ticker": "", "direction": "", "closedSession": "",
              "openedSession": "", "exitReason": "", "bars": [], "levels": [],
              "nothing": "the store holds no minute of 2026-08-24 for BBB" }
            """);

        Assert.Contains("the store holds no minute of 2026-08-24 for BBB", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chart minutes", html, StringComparison.Ordinal);
    }

    /// <summary>The nav says the journal arrives at 4.11, which is the checkpoint that built it.</summary>
    [Fact]
    public void The_navigation_records_the_checkpoint_that_built_the_page()
    {
        NavigationItem journal = Navigation.Items.Single(i => i.Path == "/journal");

        Assert.Equal("4.11", journal.ArrivesAt);
    }

    /// <summary>
    /// One session of minutes with the four levels on it, and an entry from an earlier session.
    ///
    /// The entry in a different session is the case worth authoring: it is what makes the picture
    /// one session of a trade rather than the whole of it, and a page that did not say so would show
    /// a fill line at a price the drawn session never traded.
    /// </summary>
    private const string TradeChartBody = """
        {
          "tradeId": "t-short", "ticker": "BBB", "direction": "short",
          "closedSession": "2026-08-24", "openedSession": "2026-08-21", "exitReason": "give-up",
          "bars": [
            { "at": "09:30", "open": 99.0, "high": 100.5, "low": 98.5, "close": 100.0, "volume": 1000 },
            { "at": "09:31", "open": 100.0, "high": 105.5, "low": 99.5, "close": 105.0, "volume": 2000 }
          ],
          "levels": [
            { "name": "trigger", "price": "100.00", "what": "the price the plan committed to" },
            { "name": "give-up", "price": "95.00", "what": "the resting instruction the plan carried" },
            { "name": "fill", "price": "100.10", "what": "what the entry actually got" },
            { "name": "exit", "price": "94.90", "what": "what the exit got" }
          ],
          "nothing": null
        }
        """;

    private async Task<string> RenderChart(string body)
    {
        using HttpClient client = _host
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                (request.RequestUri?.AbsolutePath ?? string.Empty)
                                    .StartsWith("/chart/trade", StringComparison.Ordinal)
                                        ? body
                                        : StatusBody,
                                System.Text.Encoding.UTF8,
                                "application/json"),
                        }))))
            .CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/chart?trade=t-short");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> Render(string? journal = null)
    {
        using HttpClient client = _host
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => Routed(journal ?? JournalBody))))
            .CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/journal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private static StubHandler Routed(string journal) =>
        new(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                (request.RequestUri?.AbsolutePath ?? string.Empty).StartsWith("/journal", StringComparison.Ordinal)
                    ? journal
                    : StatusBody,
                System.Text.Encoding.UTF8,
                "application/json"),
        });

    private static int Occurrences(string haystack, string needle)
    {
        int found = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            found++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return found;
    }
}
