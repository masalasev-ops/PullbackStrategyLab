using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No cited name resolves to a decision under "Previously decided".
///
/// Separate from decision-resolves on purpose. A citation to a superseded decision resolves
/// perfectly well and is exactly wrong: the reader follows it, finds reasoning that was
/// deliberately replaced, and acts on it.
/// </summary>
public sealed class NoSupersededCitationCheck
{
    private readonly ITestOutputHelper _output;

    public NoSupersededCitationCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "no-superseded-citation")]
    public void No_citation_resolves_to_a_superseded_decision()
    {
        var coverage = new CheckCoverage("no-superseded-citation", _output);
        var superseded = new HashSet<string>(Corpus.SupersededDecisionNames, StringComparer.Ordinal);

        List<Citation> offenders = Corpus.Citations.Where(c => superseded.Contains(c.Name)).ToList();

        coverage
            .Examined("citations checked against the superseded list", Corpus.Citations.Count)
            .Examined("names under Previously decided", superseded.Count);

        if (superseded.Count == 0)
        {
            // Stated rather than left implied. With nothing superseded there is nothing this
            // check can catch today, and a reader of the coverage line should see that rather
            // than read a green line as evidence.
            coverage.NotExamined(
                "citations that could resolve to a superseded decision",
                0,
                "nothing has been superseded yet, so the check has no work and passes vacuously");
        }

        coverage.Report();

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} citation(s) resolve to a decision under \"Previously decided\":\n  "
            + string.Join("\n  ", offenders));

        // Both lists come from the same parse of the same file. If the split stopped working,
        // every name would land on one side and this check would pass over nothing.
        Assert.True(Corpus.DecisionNames.Count > 0,
            "No current decision names were parsed from DECISIONS.md, which means the split at "
            + $"\"{Corpus.PreviouslyDecidedHeading}\" is reading the whole file as superseded.");
    }
}
