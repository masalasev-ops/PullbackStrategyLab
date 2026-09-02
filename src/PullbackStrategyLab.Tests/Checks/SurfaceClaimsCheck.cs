using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// <b>The stub bodies are authored, and until 4.11 that was a hole rather than a design.</b> Every
/// page here is rendered with the read surface stubbed, which is right: what is under test is
/// whether the page carries a note it was handed, so the note has to be handed to it. But a claim
/// whose text originates in a <i>producer</i> rather than in the template then proved only that the
/// template does not swallow a string, because the same words were written twice by hand, once into
/// the claim and once into the stub. If `ScoreboardBuilder`'s wording moved and neither copy did,
/// this check stayed green over a page carrying different words. Raised at 3.5 while adding exactly
/// such a claim, whose text was reconciled against the stage's own sentence by hand.
///
/// <b>So every claim naming text declares where that text comes from.</b> `ProducedBy` is either
/// <see cref="ThePage"/>, meaning the words are the template's own and rendering them is the whole
/// reconciliation, or <c>Type.Member</c> naming a constant in the shipped source, which is resolved
/// and compared. A claim naming text and declaring neither fails, on the same grounds
/// `coverage-reported` fails a check that declares neither a scan nor `NoSourceScan`: a declaration
/// that can be forgotten is one the next claim will forget.
///
/// <b>The stub is not interpolated from those constants, and that is deliberate.</b> It would be the
/// same property held twice. A producer whose wording moves fails the comparison below, the claim's
/// text is then corrected, and the stub still holding the old words fails the render on the next
/// line. Two mechanisms for one property is how one of them stops being read.
///
/// <b>Deliberately narrow, and not UI testing.</b> The subject is a declared list of corpus
/// sentences and the exact text each requires. It says nothing about whether a page is readable,
/// well laid out, or any good. A claim whose surface arrives later names the checkpoint that builds
/// it and is counted out of scope, so the number falls as checkpoints land rather than resting.
/// </summary>
public sealed partial class SurfaceClaimsCheck : IClassFixture<WebApplicationFactory<LabApiClient>>
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
        string? ProducedBy,
        string? ArrivesAt,
        string Why);

    /// <summary>What <c>ProducedBy</c> says where the text is the surface's own words.</summary>
    public const string ThePage = "the page";

    private sealed record ClaimFile(string Tier, IReadOnlyList<Claim> Claims);

    /// <summary>
    /// The declared claims, read from the committed file. Exposed so the proof beside this check can
    /// run the reverse read with them and without them, which is the whole of what it proves.
    /// </summary>
    internal static IReadOnlyList<Claim> DeclaredClaims() =>
        JsonSerializer.Deserialize<ClaimFile>(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "fixtures", "surface-claims.json")), Json)
            ?.Claims ?? [];

    /// <summary>
    /// The documents the reverse read covers, and the sentence shapes it treats as a claim about a
    /// surface.
    ///
    /// <b>Narrow on purpose.</b> A sentence claims something reaches a person only if it names a
    /// surface and says something appears on it, so both halves are required. A pattern on the verb
    /// alone matches every sentence in the corpus about what a store records, and a check that
    /// matched everything would be answered by an exemption list rather than by a claim file.
    /// </summary>
    private static readonly string[] Corpus =
    [
        "docs/ARCHITECTURE.html", "docs/SCHEMA.md", "docs/BUILD_PLAN.md",
        "CLAUDE.md", "docs/DECISIONS.md", "docs/RUNBOOK.md",
    ];

    /// <summary>
    /// A surface named, and something said to appear on it within thirty characters of naming it.
    ///
    /// <b>The proximity is what makes this a pattern rather than a sieve.</b> Asking only that a
    /// sentence contain a surface word somewhere and a visibility verb somewhere matched thirty-four
    /// sentences, of which half were prose that happened to hold both: "the one thing that was
    /// prose" and "belongs to a check" are not claims about a screen. A list of exemptions that long
    /// would be the check being answered by its exemptions, which is the shape it exists to refuse.
    /// </summary>
    [GeneratedRegex(@"(screen|page|band|panel|gallery|card|watchlist|scoreboard|journal)[^.]{0,30}?\b(shows|show|renders|render|displays|is shown|are shown)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaimsSomethingAppearsOnASurface();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Sentences();

    /// <summary>
    /// Sentences the pattern matches that are not claims about a surface, each with the reason.
    ///
    /// Keyed by a fragment that identifies the sentence rather than by the whole of it, so an
    /// editorial change to the words does not silently drop the exemption and re-admit the sentence
    /// under a name nobody reads. A fragment that stops matching turns the sentence back into an
    /// undeclared claim, which is the direction this list should fail in.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExemptSentences { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["figures shown are illustrative rather than measured"] =
                "a caption on a mockup saying its own figures are not real, which is the opposite of a claim that "
                + "something appears on a live page. Both screen descriptions carry it as of 4.14",
            ["Every sentence claiming this page shows something is declared"] =
                "5.5's done condition, which instructs the checkpoint to declare its claims rather than making "
                + "one. It names no quantity and no page element, so there is nothing a rendered response could "
                + "be read for, and declaring it would put a claim in the file whose subject is the file. Caught "
                + "by the reverse read on the pass that wrote it, which is that read working on the session's "
                + "own prose for the second time after the 4.1 instance recorded above",
            ["It is the page you would show someone"] =
                "what the scoreboard is for, in a sentence about a person's use of it rather than about its contents",
            ["not in a page somebody looks at"] =
                "the name of a decision, appearing wherever that decision is cited. A citation is not a claim",
            ["The watchlist has no share count column and the plan now carries a size"] =
                "a statement that a column is absent, which is the one shape this check cannot assert against a "
                + "rendered page: the claim file holds text a page must carry, and there is no text to look for. "
                + "It replaces the exemption 4.16 made dead, which read that a plan is a store row rather than a "
                + "surface and that 4.16 had to settle which screen the count appears on. It did settle it, the "
                + "row was rewritten around the answer, and the sentence the exemption named no longer exists. "
                + "What is owed is the column itself, which is a row due at 4.11",
            ["this scoreboard also shows decile curves and win rates"] =
                "the reason a resampling scheme was not adopted, which is an argument about a statistic rather "
                + "than a statement that a page carries one",
            ["the journal that shows the pair to a person at 4.11"] =
                "already deferred as a declared claim under its own name, and this is the same sentence read "
                + "from the decision that states it rather than from the document the claim names",
            ["reports what the page would show"] =
                "what the publish-watchlist stage prints in a log, which is a statement about a stage and not "
                + "about a rendered page. Written at 4.1, in RUNBOOK and again in BUILD_PLAN's row, and the key "
                + "is the fragment the two share: the first version of this entry quoted one of them whole and "
                + "the check caught the other on the same day, which is the reverse read working on this "
                + "session's own prose",
            ["the scoreboard's loss-share panel shows the four causes"] =
                "the loss-share panel is band 2's and the fifth category it names arrives with LossClassifier at "
                + "4.10, so the surface cannot carry it yet. It is deferred rather than exempt in spirit, and it "
                + "is here rather than in the claim file because the sentence is about what the taxonomy holds "
                + "rather than about what the page draws",
            ["on every short position"] =
                "the two unmodelled short assumptions, being the assumed borrow rate and the note that "
                + "availability is not modelled at all. The sentence became a present tense at 4.7, which is "
                + "the checkpoint that first writes a position, and what it claims is that the assumption is "
                + "on the row rather than that a page draws it: the store is the subject, so the assertion "
                + "that belongs to it is a behavioural one over a written row, which "
                + "PaperBrokerTests.A_short_position_carries_the_two_unmodelled_assumptions_and_a_long_carries_neither "
                + "is, backed by a migration CHECK that refuses a short without them and a long with them. The "
                + "surface half is the journal and the sentence says so: it arrives at 4.11 and becomes a "
                + "declared claim there. Both the short-checks prose and the failure table carry the fragment",
        };

    /// <summary>
    /// Every corpus sentence that claims something reaches a surface, read back against the claim
    /// file. Returns how many were examined and which are declared nowhere.
    ///
    /// A sentence counts as declared when a claim's own sentence appears inside it, which is the
    /// relation the claim file already has to the corpus: a claim quotes the phrase it is about.
    /// </summary>
    internal static (int Examined, string[] Undeclared) CorpusSentencesClaimingVisibility(
        IReadOnlyList<Claim> claims)
    {
        var undeclared = new List<string>();
        int examined = 0;

        foreach (string document in Corpus)
        {
            string text = Markup().Replace(
                RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, document)), " ");

            foreach (string sentence in Sentences().Split(text))
            {
                string one = string.Join(' ', sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

                if (one.Length == 0 || !ClaimsSomethingAppearsOnASurface().IsMatch(one))
                {
                    continue;
                }

                examined++;

                bool declared = claims.Any(c => one.Contains(
                    string.Join(' ', c.Sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                    StringComparison.OrdinalIgnoreCase));

                bool exempt = ExemptSentences.Keys.Any(k => one.Contains(k, StringComparison.Ordinal));

                if (!declared && !exempt)
                {
                    undeclared.Add($"{document}: {(one.Length > 150 ? one[..150] + "..." : one)}");
                }
            }
        }

        return (examined, [.. undeclared]);
    }

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

        // The 3.5 obligation, discharged at 4.11: the claim's text against the member that emits it.
        //
        // A claim naming a producer is reconciled against the value that member holds today, so a
        // sentence moving in the shipped source fails here instead of leaving a green check over a
        // page carrying different words. A claim naming the page is not reconciled against anything
        // else, because there is nothing else: the render above is the whole of it.
        Claim[] fromAProducer =
            [.. live.Where(c => c.MustCarry is not null && c.ProducedBy is not (null or ThePage))];

        failures.AddRange(live.Where(c => c.MustCarry is not null && c.ProducedBy is null).Select(c =>
            $"{c.Name}: names the text \"{c.MustCarry}\" and declares no producer. Name the member that "
            + $"emits it, as Type.Member, or \"{ThePage}\" where the words are the template's own."));

        foreach (Claim claim in fromAProducer)
        {
            failures.AddRange(Drifted(claim, Emitted(claim.ProducedBy!)));
        }

        // The direction that was missing until 4.1, raised at 3.12 and named at 3.7 before that.
        //
        // Everything above reconciles the claim file against the pages: a declared claim whose
        // surface has stopped carrying it fails. Nothing reconciled the corpus against the claim
        // file, so a sentence claiming something is shown was guarded only if somebody remembered
        // to declare it. Both of the claims 3.11 exercised were true of the pages and held by
        // nothing, and 3.12 added them by reading the commits rather than by anything failing.
        // That is the defect this check exists for, one level up: an assertion whose subject set is
        // whatever a person put in it.
        (int candidates, string[] undeclared) = CorpusSentencesClaimingVisibility(file.Claims);

        coverage
            .Examined("claims of visibility declared in the corpus", file.Claims.Length())
            .Examined("of those whose surface exists and was rendered and read", live.Length)
            .Examined("corpus sentences claiming visibility, read back against the claim file", candidates)
            // The claims whose text is a producer's rather than the template's, which is the
            // population the 3.5 reconciliation governs. Floored, and low: a claim legitimately
            // moves to the page when a sentence stops being a producer's, so a floor at today's
            // figure would fire on that rather than on the guard going away. What it has to catch
            // is the count reaching nothing, which is the resolver having stopped resolving.
            .Examined("claims whose text is reconciled against the member that emits it", fromAProducer.Length)
            .Context("surfaces rendered", rendered.Count)
            .Context("sentences exempted from the reverse read, each with its reason", ExemptSentences.Count)
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

        Assert.True(undeclared.Length == 0,
            $"{undeclared.Length} corpus sentence(s) claim something reaches a surface and are declared in "
            + "no claim in fixtures/surface-claims.json, so nothing asserts them against the page they are "
            + "about: " + string.Join(" | ", undeclared)
            + " Declare each with the surface it is about, or name it in ExemptSentences with the reason "
            + "it is not a claim about a surface.");

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
    /// The value a named member of the shipped source holds today.
    ///
    /// <b>Resolved rather than copied, which is the whole of the 3.5 repair.</b> The name is
    /// <c>Type.Member</c>, matched against the loaded assemblies by type name, because the claim file
    /// is read by a person and a fully qualified name with its namespace would be noise on every row.
    /// A name that resolves to nothing throws rather than returning null: a producer that has been
    /// renamed is exactly the case this exists for, and answering "no text" would let it pass as a
    /// claim whose surface carries nothing.
    /// </summary>
    public static string Emitted(string producedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producedBy);

        string[] parts = producedBy.Split('.');

        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                $"\"{producedBy}\" is not a producer. Name one as Type.Member, or say \"{ThePage}\".");
        }

        Type[] types =
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("PullbackStrategyLab", StringComparison.Ordinal) == true)
                .SelectMany(SafeTypes)
                .Where(t => string.Equals(t.Name, parts[0], StringComparison.Ordinal)),
        ];

        if (types.Length == 0)
        {
            throw new InvalidOperationException(
                $"No type named {parts[0]} is loaded, so the claim naming {producedBy} resolves to nothing. "
                + "A renamed producer is what this reconciliation exists to catch.");
        }

        foreach (Type type in types)
        {
            object? value =
                type.GetField(parts[1], BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                ?? type.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            if (value is string text)
            {
                return text;
            }
        }

        throw new InvalidOperationException(
            $"{parts[0]} has no public static string called {parts[1]}, so the claim naming {producedBy} "
            + "resolves to nothing.");
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    /// <summary>
    /// Whether a claim's text and the sentence its producer emits have come apart.
    ///
    /// <b>One contains the other, in whichever direction.</b> A claim sometimes names the whole of a
    /// producer's sentence and sometimes a phrase inside it, and the page sometimes wraps the
    /// producer's words in its own, as "over every flagged setup" does around a population. Demanding
    /// equality would make the claim file a copy of the source with extra steps; demanding a fixed
    /// direction would reject one of those two shapes for no reason. What matters is that the shorter
    /// of the two is wholly inside the longer, because that is exactly the condition under which the
    /// claim is still about the sentence the producer emits. A wording change that breaks it fails
    /// here, which is the thing that was done by hand until 4.11.
    /// </summary>
    public static IReadOnlyList<string> Drifted(Claim claim, string emitted)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(emitted);

        string carried = claim.MustCarry ?? string.Empty;

        if (carried.Contains(emitted, StringComparison.Ordinal)
            || emitted.Contains(carried, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            $"{claim.Name}: the claim names \"{carried}\" and {claim.ProducedBy} now emits \"{emitted}\", "
            + "so the claim and the sentence it is about have come apart. The page may still render the "
            + "producer's words and this check would go on looking for the old ones.",
        ];
    }

    /// <summary>
    /// The 3.5 reconciliation, proved against a claim and a sentence written here.
    ///
    /// <b>Both shapes and the failure, because the corpus holds only the two that pass.</b> A run
    /// over the live claim file exercises a claim naming the whole of a producer's sentence and one
    /// naming a phrase inside it, and never the drift, so the clause that fails would be filtering an
    /// empty list and would read exactly as it reads when it holds.
    ///
    /// The resolver is exercised against a real member rather than a stub, because a resolver that
    /// answered from a table written here would prove nothing about the one the run uses.
    /// </summary>
    [Fact]
    public void A_claim_whose_producer_has_been_reworded_is_caught()
    {
        var claim = new Claim(
            Name: "a-claim",
            Sentence: "the caveat is shown beside the reading",
            StatedIn: "ARCHITECTURE.html",
            Surface: "/setups",
            MustCarry: "the anchored clause arrives at 4.4",
            ProducedBy: "ShortPullbackRules.ClausesRun",
            ArrivesAt: null,
            Why: "a proof, not a run");

        // The member the claim names, read out of the shipped source rather than copied here.
        string emitted = Emitted(claim.ProducedBy!);
        Assert.Contains(claim.MustCarry!, emitted, StringComparison.Ordinal);

        // A phrase inside the producer's sentence, and the producer's sentence inside a claim that
        // wraps it. Both are shapes the corpus holds and neither is drift.
        Assert.Empty(Drifted(claim, emitted));
        Assert.Empty(Drifted(claim with { MustCarry = "over " + emitted }, emitted));

        // And the one the corpus never holds: the producer reworded and the claim left behind.
        string failure = Assert.Single(Drifted(claim, "21-day and 50-day only; the third clause arrives later"));
        Assert.Contains("have come apart", failure, StringComparison.Ordinal);

        // A producer that has been renamed resolves to nothing rather than to no text, because no
        // text reads as a page carrying nothing and passes every comparison above.
        Assert.Throws<InvalidOperationException>(() => Emitted("ShortPullbackRules.AClauseNobodyDeclared"));
        Assert.Throws<InvalidOperationException>(() => Emitted("ATypeNobodyDeclared.ClausesRun"));
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
            ProducedBy: "ShortPullbackRules.ClausesRun",
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
                _ when path.StartsWith("/journal", StringComparison.Ordinal) => Trades,
                _ => Status,
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        });

    /// <summary>
    /// A journal carrying one trade a side, because the two claims it answers are about the two
    /// sides separately.
    ///
    /// <b>A short and a long, and the short is the one that matters.</b> The borrow claim is that
    /// both unmodelled assumptions are recorded on every short trade, and a body with no short in it
    /// would let the page satisfy that claim by never having to render one. The long is here so the
    /// other half is exercised too: a page that printed the borrow sentence on every row would
    /// satisfy the claim and be wrong, because a long carries neither assumption and a cost of
    /// nought on it reads as a long that borrowed for free.
    ///
    /// <b>The risk pair is on both.</b> The decision it answers is that the realised risk is recorded
    /// beside the intended risk on every position, and "every" is what a one-row body cannot show.
    ///
    /// <b>The loss on the short has a mechanism and no aftermath.</b> That is the ordinary state of
    /// every loss for its first ten sessions, and the page has to be able to say it is waiting rather
    /// than say it is unclassified.
    /// </summary>
    private const string Trades = """
        {
          "asOf": "2026-08-24", "absent": null,
          "longExpectancyR": 0.48, "shortExpectancyR": 0.11,
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
              "borrowAvailability": "borrow availability is not in the price feed: the market-capitalisation floor of tradable-shortable stands in for it, so a short nobody would have lent is recorded here as though it filled",
              "entryDifferenceBasisPoints": 10.0, "exitDifferenceBasisPoints": 10.0,
              "entryBasis": "slipped", "exitBasis": "slipped",
              "plannedGiveUp": "105.00", "plannedShares": 196, "executedShares": 150,
              "reducedBecause": "total-risk",
              "lossMechanism": "ordinary", "aftermath": null, "aftermathBecause": null }
          ]
        }
        """;

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
          "long": [
            { "setupId": "2026-08-24-AAPL-long", "ticker": "AAPL", "direction": "long",
              "rank": 1, "cappedOut": false, "passedAll": false,
              "triggerPrice": 37.67, "stopPrice": 36.42, "stopDistanceRanges": 0.50,
              "agreement": null, "agreementNote": null, "degradedBecause": null,
              "checks": [
                { "name": "tradable", "passed": false, "value": 9849921234.0, "note": null,
                  "failedClauses": ["price"] },
                { "name": "moves-enough", "passed": true, "value": 0.068, "note": null,
                  "failedClauses": [] }
              ],
              "candles": [] }
          ],
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
    /// <b>And the case the old trigger got wrong: evidence far above the minimum, on five
    /// sessions.</b> 3.6 fires on twenty sessions AND 262 effective observations, and the page
    /// compared the effective count alone before rendering the sentence of the whole condition. A
    /// panel at 900 observations over 5 sessions would have announced the project's own decision
    /// point on a reading the bootstrap refused to give an interval to. It is here rather than in
    /// the view tests alone because the claim is about what a person reads, and the two panels that
    /// disagree have to be on one page for the page to be the thing asserted over.
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
    ///
    /// <b>The three loss-cause panels arrived at 4.10 and the two populations among them are the
    /// point.</b> The gap share is over every classified loss and the other two are over the losses
    /// whose horizon has closed, so the page has to be able to render two denominators inside one
    /// band. A fixture carrying only one of them would satisfy the sentence about failed setups
    /// while leaving the page unable to tell a reader that the panel above it counted a different
    /// set, which is the population claim's own failure mode arriving one band lower down.
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
              "withheldBecause": null, "sessions": 214, "minimumSessions": 20 },
            { "name": "band1.vsLoose", "direction": "long", "figure": "withheld",
              "low": null, "high": null, "rows": 240, "effective": 31,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": "only 14 session(s) carry a pair and a block bootstrap needs 20, which is a shortage of sessions rather than of evidence",
              "sessions": 14, "minimumSessions": 20 },
            { "name": "band2.lossCause.failedSetup", "direction": "long", "figure": "0.4100",
              "low": null, "high": null, "rows": 44, "effective": null,
              "population": "every loss whose horizon has closed", "minimum": null,
              "withheldBecause": null },
            { "name": "band2.lossCause.noise", "direction": "long", "figure": "0.3600",
              "low": null, "high": null, "rows": 44, "effective": null,
              "population": "every loss whose horizon has closed", "minimum": null,
              "withheldBecause": null },
            { "name": "band2.lossCause.gap", "direction": "long", "figure": "0.0900",
              "low": null, "high": null, "rows": 61, "effective": null,
              "population": "every classified loss", "minimum": null,
              "withheldBecause": null }
          ],
          "short": [
            { "name": "band1.vsLoose", "direction": "short", "figure": "withheld",
              "low": null, "high": null, "rows": 0, "effective": 0,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": "144 setup outcome(s) have closed and no control outcome has, so no pair exists. That is a shortage of control outcomes rather than of time, and waiting does not fix it",
              "sessions": 0, "minimumSessions": 20 },
            { "name": "band1.vsTight", "direction": "short", "figure": "withheld",
              "low": null, "high": null, "rows": 1740, "effective": 900,
              "population": "every flagged setup", "minimum": 262,
              "withheldBecause": "only 5 session(s) carry a pair and a block bootstrap needs 20, which is a shortage of sessions rather than of evidence",
              "sessions": 5, "minimumSessions": 20 },
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
