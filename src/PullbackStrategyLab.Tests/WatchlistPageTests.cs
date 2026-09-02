using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The morning screen as a person reads it, and the share count in particular.
///
/// <b>Why this file exists at 4.11 and not at 4.1.</b> The watchlist was built with no share count,
/// because sizing was RiskGate's and RiskGate did not exist, and the obligation raised at 4.16 says
/// what changed: PlanBuilder writes a size at 18:30 and this page publishes at 18:40, so the column
/// has a source ten minutes before the page runs and a screen that went on omitting it understates
/// what the lab committed to.
///
/// <b>Both dispositions, because only one of them is the ordinary case.</b> A planned row shows the
/// count. A row the plan stage refused, being one whose geometry is absent, whose trigger and give-up
/// point are the same price, or whose risk budget cannot buy one share, shows the words the gallery
/// uses for an absent quantity: a blank there reads as a figure the lab computed and got nothing for,
/// and a nought reads as a size the lab chose, and neither is true.
///
/// The read surface is answered from a stub rather than from a socket, on the terms every other page
/// test here sets.
/// </summary>
public sealed class WatchlistPageTests : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private const string StatusBody = """
        {
          "store": "ready", "schemaVersion": 47, "session": "2026-08-24", "lastRun": null,
          "universeMembers": 2070, "barsStored": 1482108, "callsUsed": 690, "dailyCallCeiling": 5000,
          "marketMood": null, "positionsOpen": 2, "shortPositionsOpen": 1, "riskAtStake": 1.53
        }
        """;

    /// <summary>
    /// Two rows a side, one planned and one not, which is what makes both branches of the column
    /// assertable rather than only the one a passing night happens to produce.
    ///
    /// The unplanned long failed a gate and the unplanned short has a trigger and a give-up point at
    /// the same price, so the two reasons a row reaches this screen without a plan are both present.
    /// A capped-out row is not a third: the cap cut it and this page drops it rather than greying it.
    /// </summary>
    private const string Night = """
        {
          "asOf": "2026-08-24", "failedCheck": null, "flagged": 4,
          "long": [
            { "setupId": "2026-08-24-HOOD-long", "ticker": "HOOD", "direction": "long",
              "rank": 1, "cappedOut": false, "passedAll": true,
              "triggerPrice": 118.50, "stopPrice": 112.25, "stopDistanceRanges": 0.31,
              "agreement": null, "agreementNote": null, "degradedBecause": null,
              "plannedShares": 120,
              "checks": [ { "name": "tradable", "passed": true, "value": 204580994.64, "note": null } ],
              "candles": [] },
            { "setupId": "2026-08-24-AAPL-long", "ticker": "AAPL", "direction": "long",
              "rank": 2, "cappedOut": false, "passedAll": false,
              "triggerPrice": 205.00, "stopPrice": 199.00, "stopDistanceRanges": 0.44,
              "agreement": null, "agreementNote": null, "degradedBecause": null,
              "plannedShares": null,
              "checks": [ { "name": "exit-tight", "passed": false, "value": null, "note": null } ],
              "candles": [] }
          ],
          "short": [
            { "setupId": "2026-08-24-INTC-short", "ticker": "INTC", "direction": "short",
              "rank": 1, "cappedOut": false, "passedAll": true,
              "triggerPrice": 85.14, "stopPrice": 88.20, "stopDistanceRanges": 0.52,
              "agreement": null, "agreementNote": null, "degradedBecause": null,
              "plannedShares": 245,
              "checks": [ { "name": "downtrend", "passed": true, "value": null, "note": "falling" } ],
              "candles": [] },
            { "setupId": "2026-08-24-XYZ-short", "ticker": "XYZ", "direction": "short",
              "rank": 2, "cappedOut": false, "passedAll": true,
              "triggerPrice": 40.00, "stopPrice": 40.00, "stopDistanceRanges": 0.00,
              "agreement": null, "agreementNote": null, "degradedBecause": null,
              "plannedShares": null,
              "checks": [ { "name": "downtrend", "passed": true, "value": null, "note": "falling" } ],
              "candles": [] }
          ],
          "checkNames": [ "downtrend", "exit-tight", "tradable" ],
          "nothing": null
        }
        """;

    private readonly WebApplicationFactory<LabApiClient> _host;

    public WatchlistPageTests(WebApplicationFactory<LabApiClient> host) => _host = host;

    /// <summary>
    /// The column exists on both panels and carries the size the plan committed to.
    ///
    /// <b>The header and a value, not one or the other.</b> A header over a column of absences is a
    /// column that renders and says nothing, which is what the page would have done had the size
    /// never been wired through the read surface, and it reads identically to a working one.
    /// </summary>
    [Fact]
    public async Task Each_panel_carries_a_share_count_column_with_the_size_the_plan_committed_to()
    {
        string html = await Render();

        Assert.Contains("<th>Shares</th>", html, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(html, "<th>Shares</th>"));

        Assert.Contains("<td>120</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>245</td>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row the plan stage wrote nothing for says so in words rather than in a blank or a nought.
    ///
    /// Both reasons are present: a row that failed a gate and a row whose trigger and give-up point
    /// are the same price, which is one of the three refusals PlanBuilder counts. Neither is a defect
    /// and neither is a size of nothing.
    /// </summary>
    [Fact]
    public async Task A_row_with_no_plan_says_so_rather_than_showing_a_blank_or_a_nought()
    {
        string html = await Render();

        Assert.Equal(2, Occurrences(html, "<td>not set</td>"));
        Assert.DoesNotContain("<td>0</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td></td>", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page says the count is the plan's intention rather than the size that will be placed.
    ///
    /// RiskGate may reduce it at the trigger or block the order outright, hours after anybody reads
    /// this screen, so a column presented as the size of the trade would be a screen claiming
    /// something the lab does not know yet.
    /// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
    /// </summary>
    [Fact]
    public async Task The_page_says_the_count_is_the_plans_intention_and_not_what_was_placed()
    {
        string html = await Render();

        Assert.Contains("The share count is the plan", html, StringComparison.Ordinal);
        Assert.Contains("intention, and there is no conflict banner yet", html, StringComparison.Ordinal);
        Assert.Contains("RiskGate may reduce it at the", html, StringComparison.Ordinal);

        // The sentence it replaced, which said the column had no source. A page carrying both would
        // be a page contradicting itself in two paragraphs.
        Assert.DoesNotContain("No share count", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sizing is RiskGate", html, StringComparison.Ordinal);
    }

    private async Task<string> Render()
    {
        using HttpClient client = _host
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(request =>
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                (request.RequestUri?.AbsolutePath ?? string.Empty)
                                    .StartsWith("/setups", StringComparison.Ordinal)
                                        ? Night
                                        : StatusBody,
                                System.Text.Encoding.UTF8,
                                "application/json"),
                        }))))
            .CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/watchlist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadAsStringAsync();
    }

    private static int Occurrences(string html, string what)
    {
        int found = 0;

        for (int at = html.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = html.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }
}
