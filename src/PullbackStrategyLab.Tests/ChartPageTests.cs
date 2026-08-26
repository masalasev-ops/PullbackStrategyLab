using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The chart page, asked for through the host.
///
/// The read surface is answered from a stub routed by path, so the page's three states are all
/// tests: no ticker asked for, a ticker the store has nothing on, and a window with bars and a
/// readout behind it.
/// </summary>
public sealed class ChartPageTests : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private const string Status = """
        { "store": "ready", "schemaVersion": 10, "session": "2026-08-25", "lastRun": null,
          "universeMembers": 2070, "barsStored": 1482108, "callsUsed": 0, "dailyCallCeiling": 5000,
          "marketMood": null, "positionsOpen": null, "shortPositionsOpen": null, "riskAtStake": null }
        """;

    /// <summary>Three sessions and one average, which is enough to render every part of the page.</summary>
    private const string Chart = """
        {
          "ticker": "IESC", "asOf": "2026-08-25", "requested": 60, "drawn": 3, "read": 153,
          "bars": [
            { "date": "2026-08-20", "open": 340.0, "high": 345.0, "low": 338.0, "close": 344.0, "volume": 200000 },
            { "date": "2026-08-21", "open": 344.0, "high": 350.0, "low": 343.0, "close": 349.0, "volume": 210000 },
            { "date": "2026-08-24", "open": 349.0, "high": 351.0, "low": 323.0, "close": 324.12, "volume": 207308 }
          ],
          "averages": [
            { "name": "ema9", "period": 9, "values": [352.1, 352.5, 352.9966] },
            { "name": "ema21", "period": 21, "values": [353.0, 353.1, 353.2321] },
            { "name": "ema50", "period": 50, "values": [343.1, 343.2, 343.3746] }
          ],
          "readout": {
            "asOf": "2026-08-25", "ema9": 352.9966, "ema21": 353.2321, "ema50": 343.3746,
            "atr14": 24.1364, "adr20": 0.0670, "dollarVolumeMedian": 204580994.64, "rangeAverage": 23.3959
          },
          "nothing": null
        }
        """;

    private const string NothingThere = """
        { "ticker": "ZZZZ", "asOf": "", "requested": 60, "drawn": 0, "read": 0, "bars": [],
          "averages": [], "readout": null, "nothing": "no stored bars for ZZZZ on or before 2026-08-25" }
        """;

    private readonly WebApplicationFactory<LabApiClient> _host;

    public ChartPageTests(WebApplicationFactory<LabApiClient> host) => _host = host;

    private HttpClient Client(string chartBody)
    {
        var handler = new StubHandler(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string body = path.StartsWith("/chart", StringComparison.Ordinal) ? chartBody : Status;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        return _host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>().ConfigurePrimaryHttpMessageHandler(() => handler)))
            .CreateClient();
    }

    [Fact]
    public async Task With_no_ticker_the_page_offers_the_form_and_draws_nothing()
    {
        using HttpClient client = Client(Chart);
        string html = await client.GetStringAsync("/chart");

        Assert.Contains("name=\"ticker\"", html, StringComparison.Ordinal);
        Assert.Contains("no bars for this window", html, StringComparison.Ordinal);

        // The component's own empty state rather than an empty box, which would read as a stock
        // that did not move.
        Assert.DoesNotContain("class=\"body\" x=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_with_bars_draws_candles_the_three_averages_and_the_readout()
    {
        using HttpClient client = Client(Chart);
        string html = await client.GetStringAsync("/chart/IESC");

        Assert.Contains("<rect class=\"body\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"average a1\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"average a2\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"average a3\"", html, StringComparison.Ordinal);

        // The readout the lab stored, beside the lines drawn from the same computation. Two
        // decimals, which is what a charting platform shows for a stock at this price and is
        // therefore the unit the comparison is actually made in.
        Assert.Contains("EMA 9</b>353.00", html, StringComparison.Ordinal);
        Assert.Contains("EMA 50</b>343.37", html, StringComparison.Ordinal);

        // The daily range is a fraction in the store and a percentage on a screen, converted
        // once on the way out. 0.067 shown as 6.7% rather than as 0.07.
        Assert.Contains("6.70%", html, StringComparison.Ordinal);
        Assert.Contains("$204.6M", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticker_the_store_has_nothing_on_says_so_and_still_renders()
    {
        using HttpClient client = Client(NothingThere);
        using HttpResponseMessage response = await client.GetAsync("/chart/ZZZZ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("no stored bars for ZZZZ", html, StringComparison.Ordinal);
        Assert.Contains("class=\"band\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chart_page_carries_no_script()
    {
        using HttpClient client = Client(Chart);
        string html = await client.GetStringAsync("/chart/IESC");

        // A chart that needs JavaScript to appear is a chart that does not appear in a saved
        // page or in a print.
        // see: Pages are server-rendered with no build step, and any script is local rather than fetched
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
    }
}
