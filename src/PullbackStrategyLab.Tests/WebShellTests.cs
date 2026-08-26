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
/// Every screen is reachable and renders its empty state.
///
/// Asserted by asking the host for the page rather than by reading the .cshtml. A route that
/// does not resolve and a view that does not compile both look perfectly fine in the source,
/// and "openable" is a done condition that has to be worth something without a person opening
/// anything.
///
/// The read surface is answered from a stub rather than from a socket. The Api and the pages
/// are two hosts started separately, so both states of the band are ordinary states of the
/// machine and both are worth a test; leaving the requests to reach a port nobody is listening
/// on would test the timeout instead of the page.
/// </summary>
public sealed class WebShellTests : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    /// <summary>A store with rows in it, as the read surface would answer.</summary>
    private const string StatusBody = """
        {
          "store": "ready",
          "schemaVersion": 10,
          "session": "2026-08-24",
          "lastRun": { "stage": "indicators", "startedAt": "2026-08-25T22:00:00.000Z", "endedAt": "2026-08-25T22:00:04.000Z", "outcome": "clean", "callsUsed": 0 },
          "universeMembers": 2070,
          "barsStored": 1482108,
          "callsUsed": 690,
          "dailyCallCeiling": 5000,
          "marketMood": null,
          "positionsOpen": null,
          "shortPositionsOpen": null,
          "riskAtStake": null
        }
        """;

    private readonly WebApplicationFactory<LabApiClient> _host;

    public WebShellTests(WebApplicationFactory<LabApiClient> host) => _host = host;

    public static TheoryData<string> EveryScreen()
    {
        var paths = new TheoryData<string> { "/" };
        foreach (NavigationItem item in Navigation.Items)
        {
            paths.Add(item.Path);
        }

        return paths;
    }

    private HttpClient Client(StubHandler readSurface) =>
        _host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>().ConfigurePrimaryHttpMessageHandler(() => readSurface)))
            .CreateClient();

    private HttpClient Reading() => Client(StubHandler.Json(StatusBody));

    [Theory]
    [MemberData(nameof(EveryScreen))]
    public async Task Every_screen_is_reachable_and_renders_the_shell(string path)
    {
        using HttpClient client = Reading();
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync();

        // The whole shell, on every page: the mark, the five-item nav rendered from one list,
        // and the status band.
        Assert.Contains("PullbackStrategyLab", html, StringComparison.Ordinal);
        Assert.Contains("paper trading only", html, StringComparison.Ordinal);
        Assert.Contains("class=\"band\"", html, StringComparison.Ordinal);

        foreach (NavigationItem item in Navigation.Items)
        {
            Assert.Contains($"href=\"{item.Path}\"", html, StringComparison.Ordinal);
            Assert.Contains($">{item.Title}<", html, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EveryScreen))]
    public async Task No_screen_fetches_anything_from_another_host(string path)
    {
        using HttpClient client = Reading();
        string html = await client.GetStringAsync(path);

        // A page that fetches from anywhere is a page that does not render on a machine with no
        // network, and the whole lab is meant to run on a laptop. The rule is that nothing is
        // fetched, not that nothing is scripted: the gallery carries a local block for keyboard
        // paging, permitted by name, and what makes it permitted is that it arrives with the page.
        // see: Pages are server-rendered with no build step, and any script is local rather than fetched
        Assert.DoesNotContain("<script src", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("//cdn", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("integrity=", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A screen that has landed says nothing about waiting for a checkpoint.
    ///
    /// The counterpart of the test below, and the pair is what keeps the two honest: an empty state
    /// left in place after its checkpoint landed reads as a page nobody built, and a page that
    /// dropped its empty state before landing reads as one that is finished.
    /// </summary>
    [Fact]
    public async Task A_screen_whose_checkpoint_has_landed_no_longer_says_it_is_waiting()
    {
        using HttpClient client = Reading();

        foreach (NavigationItem item in Navigation.Items.Where(i => Landed.Contains(i.Path, StringComparer.Ordinal)))
        {
            string html = await client.GetStringAsync(item.Path);
            Assert.DoesNotContain("Nothing here yet", html, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The nav paths whose page is built. Named rather than derived, because "is it built" is not a
    /// property of the source that anything here can read: an empty state is a perfectly ordinary
    /// page, and the difference is whether a checkpoint says it should still be one.
    /// </summary>
    private static IReadOnlyList<string> Landed { get; } = ["/setups"];

    [Theory]
    [MemberData(nameof(EveryScreen))]
    public async Task No_screen_invents_a_row(string path)
    {
        using HttpClient client = Reading();
        string html = await client.GetStringAsync(path);

        // The tickers the mockup uses. A page carrying one of these is a page showing sample
        // data, which reads as a working screen and is the one thing that cannot be told apart
        // from the real one later.
        foreach (string invented in new[] { "SMCI", "CRWV", ">XYZ<", ">ABC<" })
        {
            Assert.DoesNotContain(invented, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_screen_says_which_checkpoint_fills_it()
    {
        using HttpClient client = Reading();

        foreach (NavigationItem item in Navigation.Items.Where(i => !Landed.Contains(i.Path, StringComparer.Ordinal)))
        {
            string html = await client.GetStringAsync(item.Path);

            Assert.Contains("Nothing here yet", html, StringComparison.Ordinal);
            Assert.Contains($"checkpoint {item.ArrivesAt}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_band_shows_what_the_store_holds_and_a_dash_for_what_it_does_not()
    {
        using HttpClient client = Reading();
        string html = await client.GetStringAsync("/");

        Assert.Contains("2026-08-24", html, StringComparison.Ordinal);
        // The middot arrives encoded: the default HTML encoder escapes everything outside basic
        // Latin, which is correct and is not what this test is about.
        Assert.Contains("indicators", html, StringComparison.Ordinal);
        Assert.Contains("clean", html, StringComparison.Ordinal);
        Assert.Contains("690 of 5,000", html, StringComparison.Ordinal);

        // Market mood and the position caps do not exist before phases 2 and 4. The checkpoint
        // that fills each, never a zero: a zero reads as "none open" where the truth is
        // "positions are not a thing yet".
        Assert.Contains("not until 2.5", html, StringComparison.Ordinal);
        Assert.Contains("not until 4.7", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The read surface is not answering", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_band_says_the_read_surface_is_down_rather_than_failing_to_render()
    {
        using HttpClient client = Client(StubHandler.NotListening());
        using HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The read surface is not answering", html, StringComparison.Ordinal);
        Assert.Contains("class=\"band\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_surface_that_answers_badly_is_reported_rather_than_thrown()
    {
        using HttpClient client = Client(StubHandler.Status(HttpStatusCode.InternalServerError));
        using HttpResponseMessage response = await client.GetAsync("/scoreboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("answered 500", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shared_chart_renders_its_empty_state_rather_than_an_empty_box()
    {
        using HttpClient client = Reading();
        string html = await client.GetStringAsync("/");

        Assert.Contains("class=\"candles\"", html, StringComparison.Ordinal);
        Assert.Contains("no bars for this window", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stylesheet_is_served_from_this_host()
    {
        using HttpClient client = Reading();
        using HttpResponseMessage response = await client.GetAsync("/lab.css");

        // The host sets its content root to where the binary sits, so the web root has to be
        // copied beside it. Without that the sheet 404s, every page renders unstyled and
        // nothing fails: the pages are all still there and all still reachable.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".band", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_no_screen_claims_is_not_found()
    {
        using HttpClient client = Reading();
        using HttpResponseMessage response = await client.GetAsync("/positions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_navigation_holds_five_screens_and_no_two_share_a_path()
    {
        // Five, matching the screens the architecture describes and the mockup's own tab strip.
        Assert.Equal(5, Navigation.Items.Count);
        Assert.Equal(5, Navigation.Items.Select(i => i.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Navigation.Items, i => Assert.StartsWith("/", i.Path, StringComparison.Ordinal));
    }
}
