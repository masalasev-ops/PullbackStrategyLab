using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No cited name in a spec resolves to a decision under "Previously decided".
///
/// Separate from decision-resolves on purpose. A citation to a superseded decision resolves
/// perfectly well and is exactly wrong: the reader follows it, finds reasoning that was
/// deliberately replaced, and acts on it.
///
/// The records are exempt, for the same reason stated-counts exempts them. A PROGRESS entry
/// says what was true on a date and a CHANGELOG entry names the decision that authorised an
/// edit at the time it was made. Both are history, and a citation inside history is correct
/// history: the alternative is rewriting a dated entry every time a decision is replaced,
/// which is the one thing an append-only record must never do. The exemption is counted and
/// reported rather than applied quietly, because a check that narrows its own scope in silence
/// is the failure mode the coverage line exists to catch.
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

        List<Citation> inRecords = Corpus.Citations.Where(c => IsRecord(c.File)).ToList();
        List<Citation> inSpecs = Corpus.Citations.Except(inRecords).ToList();
        List<Citation> offenders = inSpecs.Where(c => superseded.Contains(c.Name)).ToList();

        coverage
            .Examined("citations checked against the superseded list", inSpecs.Count)
            .Examined("names under Previously decided", superseded.Count)
            .OutOfScope("citations inside a record", inRecords.Count,
                CheckCoverage.OutOfScopeReason.ByDesign(
                    "a dated entry names what authorised it at the time, and correcting that would rewrite history "
                    + "rather than the corpus. Nothing closes it, because a record is meant to say what was true then"))
            .Examined("citations inside a record that name a superseded decision",
                inRecords.Count(c => superseded.Contains(c.Name)));

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
        Assert.True(inSpecs.Count > 0,
            "No citations outside the records were found at all, which means the record filter is matching every "
            + "file. A check that examines nothing passes forever.");

        Assert.True(Corpus.DecisionNames.Count > 0,
            "No current decision names were parsed from DECISIONS.md, which means the split at "
            + $"\"{Corpus.PreviouslyDecidedHeading}\" is reading the whole file as superseded.");
    }

    /// <summary>
    /// The two append-only records. Named here rather than pattern-matched, because a file that
    /// stopped matching a pattern would leave the check quietly wider or quietly narrower and
    /// nothing would say which.
    /// </summary>
    private static bool IsRecord(string file) =>
        file.EndsWith("PROGRESS.md", StringComparison.Ordinal)
        || file.EndsWith("CHANGELOG.md", StringComparison.Ordinal);
}
