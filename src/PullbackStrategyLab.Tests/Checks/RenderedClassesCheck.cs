using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every class a rendered page carries has a rule in `lab.css`.
///
/// <b>What a page says is checkable and what a page draws is not, which is the 5.5 obligation.</b>
/// `surface-claims` renders a page and reads the text that came out. It is the only check in this
/// corpus whose subject is a surface, and the thing it reads a surface for is words, so a drawn
/// element is outside it by construction: not deferred, not exempt, and counted nowhere as missing.
/// **Two instances are on the record and they are the two halves of the gap.** The minute picture on
/// the trade chart carried `chart minutes` from 4.11 while every rule for the shared chart was
/// written against `candles`, so no candle, no wick and none of the four level lines had a stroke
/// and the element drew an empty white box for two checkpoints; it was found at 5.5 by a session
/// adding a strip beside it, by looking. And `band0.degradedRuns` was said to read red above five
/// percent while the caption was a static string and nothing rendered anything red, which is the row
/// raised at 3.5. **The first is a page saying the right words over a picture nobody could see; the
/// second is a colour, and a colour is not a word.**
///
/// <b>5.5 priced two instruments and this checkpoint chooses rather than inherits.</b> The other was
/// a count of the elements a rendered page carries against what the view declares it draws. That one
/// asserts a number and catches an element that vanished; this one catches an element that is
/// present and invisible, which is the fault both recorded instances actually were. A count of
/// elements would have passed on both.
///
/// <b>It reads the rendered page rather than the template.</b> A class written into a `.cshtml` file
/// is a string in a file; a class on a rendered element is what a browser will look up. The two
/// differ wherever a class is composed at render time, which is exactly where this fault lives: the
/// chart's `minutes` came from an interpolation and the panel's `reads-badly` comes from a ternary
/// on a stored state.
///
/// <b>The reverse direction is reported and does not fail.</b> A rule in `lab.css` that no page
/// carries is either a state this fixture cannot produce or a rule nobody removed, and those are
/// different facts that a rendering of one stubbed store cannot tell apart. Counting them is worth
/// having; failing on them would make the check answerable by deleting rules.
/// </summary>
public sealed partial class RenderedClassesCheck : IClassFixture<WebApplicationFactory<LabApiClient>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<LabApiClient> _host;
    private readonly ITestOutputHelper _output;

    public RenderedClassesCheck(WebApplicationFactory<LabApiClient> host, ITestOutputHelper output)
    {
        _host = host;
        _output = output;
    }

    /// <summary>
    /// Classes a page carries that `lab.css` deliberately has no rule for, each with why.
    ///
    /// <b>Declared here rather than in a fixture, because the list is about the stylesheet and not
    /// about a claim anybody made.</b> Two shapes: a class that exists to be found by a check or a
    /// script rather than to be drawn, and a class the browser gives meaning to on its own.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Unstyled =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["muted"] = "styled through a shared rule on the element rather than on the class alone, "
                + "and present on so many elements that a rule of its own would be a second place the "
                + "same weight is set",

            // Section identifiers. Each names what a block of a page is about and none of them is
            // drawn by anything: the block's own appearance comes from `ledger`, `panels` or
            // `scoreboard-band` beside it. They are here so a page's structure can be read from its
            // markup and so a later rule can be written against one without renaming anything.
            ["watchlist"] = "names the morning screen's own block. Drawn by nothing on purpose",
            ["journal"] = "names the journal's own block. Drawn by nothing on purpose",
            ["register"] = "names the ledger's register of versions. Drawn by `ledger` beside it",
            ["versions"] = "names the register's table. Drawn by `ledger` beside it",
            ["differences"] = "names the ledger's difference-series block. Drawn by `ledger` beside it",
            ["twins"] = "names the ledger's twin-pair block. Drawn by `ledger` beside it",
            ["pairs"] = "names the twin-pair table. Drawn by `ledger` beside it",
            ["holdout"] = "names the ledger's holdout block. Drawn by `ledger` beside it",
            ["windows"] = "names the holdout window table. Drawn by `ledger` beside it",

            // Layout hooks inside a table or a card, where the element's own tag carries the
            // appearance and the class exists so a rule can be written against one later.
            ["row"] = "a row of a hand-built grid, drawn by the grid rather than by itself",
            ["col"] = "a column of the same grid",
            ["pair"] = "a label and its value together, drawn by the two inside it",
            ["detail"] = "a secondary line inside a cell, drawn by the cell",
        };

    [Fact]
    [Trait("check", "rendered-classes")]
    public async Task Every_class_a_rendered_page_carries_has_a_rule_in_the_stylesheet()
    {
        var coverage = new CheckCoverage("rendered-classes", _output);

        string stylesheet = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root, "src", "PullbackStrategyLab.Web", "wwwroot", "lab.css"));

        IReadOnlySet<string> styled = ClassesWithARule(stylesheet);

        Assert.True(
            styled.Count > 0,
            "lab.css yielded no class selectors at all, so this check would pass over every page by "
            + "matching nothing. That is the shape it exists to refuse one level up.");

        var carried = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string surface in Surfaces)
        {
            string html = await Render(surface);

            foreach (string name in ClassesOn(html))
            {
                if (!carried.TryGetValue(name, out List<string>? pages))
                {
                    pages = [];
                    carried[name] = pages;
                }

                pages.Add(surface);
            }
        }

        Assert.True(
            carried.Count > 0,
            "no rendered page carried a class at all, which means the render returned nothing and "
            + "this check compared nothing.");

        var missing = new List<string>();

        foreach ((string name, List<string> pages) in carried.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (styled.Contains(name) || Unstyled.ContainsKey(name))
            {
                continue;
            }

            missing.Add(
                $"{name} is carried by {string.Join(", ", pages.Distinct(StringComparer.Ordinal))} "
                + "and lab.css has no rule naming it, so the element it is on is drawn with whatever "
                + "the element's own default is. Add a rule, or declare it in Unstyled with why.");
        }

        coverage.NoSourceScan(
            "it renders the shipped pages and reads the classes that came out. The subject is what a "
            + "browser will look up, which is a property of the rendered page rather than of the "
            + "template: a class composed at render time is in the second and not in the first, and "
            + "that is exactly where both recorded instances of this fault lived");

        coverage.Examined("distinct classes the rendered pages carry", carried.Count);
        coverage.Examined("surfaces rendered", Surfaces.Length);
        coverage.Context("class selectors lab.css has a rule for", styled.Count);
        coverage.Context("classes declared as deliberately unstyled, each with why", Unstyled.Count);

        // The reverse read, reported and never failed. A rule no page carries is either a state this
        // fixture cannot produce or a rule nobody removed, and one rendering of one stubbed store
        // cannot tell those apart; failing on it would make the check answerable by deleting rules.
        coverage.Context(
            "class selectors no rendered page carried, which is a state this fixture cannot produce "
            + "or a rule nobody removed and is not decided here",
            styled.Count(name => !carried.ContainsKey(name)));

        coverage.Report();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} class(es) are carried by a rendered page and have no rule in lab.css:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The guard, proved against markup written here and run through the check's own extraction.
    ///
    /// A check whose only subject is the live pages is a check nobody can break on purpose, and the
    /// fault this one catches is invisible to everything else in the suite.
    /// </summary>
    [Fact]
    public void A_class_with_no_rule_is_caught_and_a_styled_one_is_not()
    {
        IReadOnlySet<string> styled = ClassesWithARule(
            ".panel { color: red; }\n.ledger.side.long { color: blue; }\ntable.ledger td.age { color: grey; }");

        Assert.Contains("panel", styled);
        Assert.Contains("ledger", styled);
        Assert.Contains("side", styled);
        Assert.Contains("long", styled);
        Assert.Contains("age", styled);
        Assert.DoesNotContain("minutes", styled);

        // The fault as it actually shipped: a chart element carrying `minutes` where every rule was
        // written against `candles`.
        IReadOnlyList<string> carried = ClassesOn(
            "<div class=\"chart minutes\"><span class='panel'>x</span></div>");

        Assert.Contains("chart", carried);
        Assert.Contains("minutes", carried);
        Assert.Contains("panel", carried);

        // And an element with no class at all yields nothing rather than an empty name, which would
        // match every rule and make the check pass over it.
        Assert.Empty(ClassesOn("<div class=\"\"><p>nothing</p></div>"));
    }

    /// <summary>
    /// The surfaces this reads.
    ///
    /// Every page the navigation names, plus the chart, which is reached for a ticker rather than
    /// browsed to and is where the recorded instance of this fault lived.
    /// </summary>
    private static readonly string[] Surfaces =
    [
        "/watchlist", "/setups", "/journal", "/scoreboard", "/research", "/packs",
        "/chart?trade=t-held",
    ];

    /// <summary>
    /// Every class name `lab.css` has a rule naming, read from the selectors rather than from a list.
    ///
    /// A compound selector yields each of its names, because a rule on `.ledger.side.long` is a rule
    /// naming all three and an element carrying any of them is drawn by something.
    /// </summary>
    private static IReadOnlySet<string> ClassesWithARule(string stylesheet)
    {
        ArgumentNullException.ThrowIfNull(stylesheet);

        // Declaration blocks and comments stripped first, so a class name inside a comment or a
        // property value is not read as a selector. Without it every word of this file's own prose
        // would count as styled and the check would pass over anything.
        string selectors = DeclarationBlock().Replace(Comment().Replace(stylesheet, " "), " ");

        return new HashSet<string>(
            ClassSelector().Matches(selectors).Select(m => m.Groups["name"].Value),
            StringComparer.Ordinal);
    }

    /// <summary>Every class name on every element of a rendered page, in the order they appear.</summary>
    private static IReadOnlyList<string> ClassesOn(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        return
        [
            .. ClassAttribute().Matches(html)
                .SelectMany(m => m.Groups["names"].Value
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        ];
    }

    /// <summary>
    /// One surface, rendered through the host with the read surface stubbed.
    ///
    /// Stubbed on the same grounds `surface-claims` gives: the Api and the pages are two hosts
    /// started separately, and a request reaching a port nobody is listening on tests the timeout
    /// instead of the page. The bodies are its own, so a page gaining a section that only renders
    /// over data gains coverage here at the same time.
    /// </summary>
    private async Task<string> Render(string path)
    {
        using HttpClient client = _host
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHttpClient<LabApiClient>()
                    .ConfigurePrimaryHttpMessageHandler(SurfaceClaimsCheck.Surfaces)))
            .CreateClient();

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{path} answered {(int)response.StatusCode}, so nothing was rendered to read classes off.");

        return await response.Content.ReadAsStringAsync();
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comment();

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationBlock();

    [GeneratedRegex(@"\.(?<name>[A-Za-z_][A-Za-z0-9_-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ClassSelector();

    [GeneratedRegex(@"class\s*=\s*[""'](?<names>[^""']*)[""']", RegexOptions.CultureInvariant)]
    private static partial Regex ClassAttribute();
}
