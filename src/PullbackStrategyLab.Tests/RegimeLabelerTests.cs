using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The market mood: two scores summed, and a label that gates nothing.
///
/// The scoring is arithmetic and is tested as arithmetic. The property that needs a test of its own
/// is the one a value cannot show: that nothing in the lab branches on the label. A mood that
/// filters is an untested assumption baked into the baseline, and it would be invisible in every
/// figure this stage produces.
/// </summary>
public sealed class RegimeLabelerTests
{
    // ---- the two scores ----------------------------------------------------------------------

    [Theory]
    [InlineData(3, 3, 1)]
    [InlineData(0, 3, -1)]
    [InlineData(1, 3, 0)]
    [InlineData(2, 3, 0)]
    public void The_index_score_is_all_none_or_neither(int above, int measured, int expected) =>
        Assert.Equal(expected, RegimeLabeler.IndexScore(above, measured));

    [Fact]
    public void No_tracker_measurable_scores_zero_rather_than_minus_one()
    {
        // "None of nothing was above" is not the same statement as "none of three was above".
        // Scoring it -1 would read a missing feed as a falling market, which is the shape of error
        // that turns an outage into a signal.
        Assert.Equal(0, RegimeLabeler.IndexScore(above: 0, measured: 0));
        Assert.Equal(-1, RegimeLabeler.IndexScore(above: 0, measured: 3));
    }

    [Theory]
    [InlineData(9, 5, 1)]
    [InlineData(3, 5, -1)]
    [InlineData(5, 5, 0)]
    [InlineData(6, 5, 0)]
    public void The_breadth_score_turns_on_the_ratio(int rising, int falling, int expected) =>
        Assert.Equal(expected, RegimeLabeler.BreadthScore(rising, falling));

    [Fact]
    public void The_breadth_thresholds_are_exclusive_at_both_ends()
    {
        // 1.5 exactly is not above 1.5, and the ratio either side decides it. Stated because a
        // boundary written with the wrong comparison is the commonest way a threshold is off by
        // one case and never noticed.
        Assert.Equal(0, RegimeLabeler.BreadthScore(3, 2));
        Assert.Equal(1, RegimeLabeler.BreadthScore(31, 20));
        Assert.Equal(0, RegimeLabeler.BreadthScore(67, 100));
        Assert.Equal(-1, RegimeLabeler.BreadthScore(66, 100));
    }

    [Fact]
    public void Nothing_laddering_either_way_scores_zero_and_no_falling_names_scores_plus_one()
    {
        // Two different undefined ratios and they mean different things. Every name that laddered
        // laddered upward is the strongest reading of the score there is; nothing laddering at all
        // is no reading.
        Assert.Equal(0, RegimeLabeler.BreadthScore(0, 0));
        Assert.Equal(1, RegimeLabeler.BreadthScore(40, 0));
    }

    // ---- the label ---------------------------------------------------------------------------

    [Fact]
    public void Neither_extreme_is_reachable_without_both_scores_agreeing()
    {
        // The three-state form buffers itself, which is the property the design leans on: the label
        // cannot go from risk-on to risk-off without passing through mixed.
        Assert.Equal(RegimeLabeler.RiskOn, RegimeLabeler.LabelFor(1, 1));
        Assert.Equal(RegimeLabeler.RiskOff, RegimeLabeler.LabelFor(-1, -1));

        foreach ((int index, int breadth) in new[] { (1, 0), (0, 1), (0, 0), (1, -1), (-1, 1), (0, -1), (-1, 0) })
        {
            Assert.Equal(RegimeLabeler.Mixed, RegimeLabeler.LabelFor(index, breadth));
        }
    }

    // ---- the label filters nothing -------------------------------------------------------------

    [Fact]
    public void No_shipped_component_branches_on_the_market_mood()
    {
        // The property the whole design rests on, and the one no figure can show. The label is
        // recorded against every setup and gates nothing in the baseline, which is what keeps it
        // available as a clean experiment: baking it in now would be an untested assumption and
        // adding it later as a version is a measurement.
        //
        // Read from the shipped source with comments stripped, so a comment explaining the rule is
        // not read as the code breaking it. The vectorizer is exempt by name: freezing the label
        // onto a setup is recording it, which is exactly what the decision asks for.
        var offenders = new List<string>();

        foreach (string file in RepositoryLayout.SourceFiles.Where(IsShippedCode))
        {
            if (Path.GetFileName(file) is "RegimeLabeler.cs" or "RegimeReader.cs" or "SignalVectorizer.cs")
            {
                continue;
            }

            string source = CSharpSource.WithoutComments(File.ReadAllText(file));

            foreach (string label in new[] { RegimeLabeler.RiskOn, RegimeLabeler.RiskOff })
            {
                if (source.Contains($"\"{label}\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{RepositoryLayout.Relative(file)} names \"{label}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} shipped file(s) branch on the market mood, which filters nothing in the "
            + "baseline: " + string.Join(", ", offenders));
    }

    private static bool IsShippedCode(string file) =>
        !file.Contains($"{Path.DirectorySeparatorChar}PullbackStrategyLab.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
