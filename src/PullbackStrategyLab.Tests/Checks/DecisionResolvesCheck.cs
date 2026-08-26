using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every decision name cited in code or docs matches a bold decision name in DECISIONS.md
/// exactly, and no two decisions share a name.
///
/// It fails on a near-miss rather than ignoring it. Names invite paraphrase, and a
/// paraphrased citation silently stops resolving, which leaves a comment that looks like
/// a citation and points at nothing.
///
/// A name resolves whether it is current or superseded. This check answers "does the name
/// exist", and `no-superseded-citation` answers "is it the one still in force", which is a
/// different question with a different answer for the records: a dated PROGRESS entry names
/// what authorised it at the time and that citation stays correct after the decision is
/// replaced. Folding the two questions into this one check made a supersession break every
/// record that had ever cited the decision, which would leave a session choosing between
/// rewriting history and never superseding anything.
/// </summary>
public sealed class DecisionResolvesCheck
{
    private readonly ITestOutputHelper _output;

    public DecisionResolvesCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "decision-resolves")]
    public void Every_cited_decision_name_resolves_exactly()
    {
        var coverage = new CheckCoverage("decision-resolves", _output);
        var known = new HashSet<string>(
            Corpus.DecisionNames.Concat(Corpus.SupersededDecisionNames), StringComparer.Ordinal);

        List<string> duplicates = Corpus.DecisionNames
            .Concat(Corpus.SupersededDecisionNames)
            .GroupBy(n => n, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        var unresolved = new List<string>();
        foreach (Citation citation in Corpus.Citations)
        {
            if (known.Contains(citation.Name))
            {
                continue;
            }

            string nearest = Nearest(citation.Name);
            unresolved.Add($"{citation}\n      nearest decision: {nearest}");
        }

        coverage
            .Examined("decision names in DECISIONS.md", Corpus.DecisionNames.Count)
            .Examined("names under Previously decided, which also resolve", Corpus.SupersededDecisionNames.Count)
            .Examined("citations resolved", Corpus.Citations.Count)
            .Context("files read for citations", RepositoryLayout.CorpusFiles.Count + RepositoryLayout.SourceFiles.Count)
            .NoSourceScan(
                "a citation is the subject rather than evidence about a behaviour. It reads source files, but "
                + "what it asserts about them is that the names they cite resolve, and a citation deleted is the "
                + "thing itself going away rather than an assertion outliving it");
        coverage.Report();

        Assert.True(duplicates.Count == 0,
            "Two decisions share a name, so a citation to it resolves to whichever the reader finds first:\n  "
            + string.Join("\n  ", duplicates));

        Assert.True(unresolved.Count == 0,
            $"{unresolved.Count} citation(s) do not resolve to a decision name in DECISIONS.md:\n  "
            + string.Join("\n  ", unresolved));

        // A corpus this size with no citations at all would mean the parser stopped matching,
        // and a check that examines nothing passes forever.
        Assert.True(Corpus.Citations.Count >= 20,
            $"Only {Corpus.Citations.Count} citations were found. The corpus carried at least 20 before any code existed, " +
            "so a number this low means the citation parser stopped matching rather than that the citations went away.");
    }

    private static string Nearest(string name)
    {
        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in Corpus.DecisionNames.Concat(Corpus.SupersededDecisionNames))
        {
            int distance = Distance(name, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best is null ? "(none)" : $"\"{best}\" (distance {bestDistance})";
    }

    /// <summary>Levenshtein, so a failure names the decision the author probably meant.</summary>
    private static int Distance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
