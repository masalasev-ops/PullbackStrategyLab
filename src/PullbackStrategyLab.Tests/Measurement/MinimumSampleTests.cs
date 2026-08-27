using PullbackStrategyLab.Core.Measurement;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// The sample-size arithmetic, exercised rather than read.
///
/// <b>The figure this replaces passed every check the corpus had.</b> 160 was pinned, cited, and
/// stated in three places that agreed with each other, and it was still an estimate: nothing had
/// measured the dispersion it turns on and nothing had stated the power it was sized for. A pinned
/// constant proves the documents agree with the code, never that either is right, so what is
/// asserted here is the arithmetic itself and the properties it has to have.
/// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
/// </summary>
public sealed class MinimumSampleTests
{
    /// <summary>
    /// The corpus's own inputs give the corpus's own figure.
    ///
    /// The one assertion that would fail if any of the four inputs moved without the constant moving
    /// with it, which is the shape of drift a pin cannot see: the documents and the code would still
    /// agree, on a number neither of them derives any more.
    /// </summary>
    [Fact]
    public void The_stated_inputs_give_the_stated_minimum()
    {
        Assert.Equal(262, MinimumSample.Of(0.099811d));
        Assert.Equal(
            MeasurementParameters.MinimumEffectiveObservations, MinimumSample.Of(0.099811d));
    }

    /// <summary>
    /// The sensitivity table the decision states, asserted rather than left as prose.
    ///
    /// <b>It exists so the ratified choice stays visible as a choice.</b> Both inputs are judgements
    /// and a later session will otherwise read them as conventional defaults, which is exactly what
    /// happened to the figure this replaces: 160 was this arithmetic at a power nobody had chosen,
    /// and nothing beside it said what moving that power would cost.
    /// </summary>
    [Theory]
    [InlineData(0.524401d, 154)]
    [InlineData(0.841621d, 196)]
    [InlineData(MinimumSample.ZBetaPower90, 262)]
    [InlineData(1.644854d, 324)]
    public void The_sensitivity_to_power_is_what_the_decision_tabulates(double zBeta, int expected)
    {
        Assert.Equal(
            expected,
            MinimumSample.Of(
                0.099811d, MeasurementParameters.DetectableDifference,
                MinimumSample.ZAlphaTwoSided95, zBeta));
    }

    /// <summary>
    /// The ratified sample is powered on what is worth trading, not on what the strategy claims,
    /// and the difference between those two is recorded rather than left to be derived.
    ///
    /// The claimed expectancy is about 0.55R on a 3% stop, or about 1.65 points. The sample detects
    /// two points at 90%; against 1.65 points the same sample carries about 76% power, and 90% there
    /// would need 385. That is not an objection to the ratification, which deliberately sizes on the
    /// smallest effect worth having. It is here so nobody reads "90% power" as 90% of finding the
    /// strategy's own claimed edge.
    /// </summary>
    [Fact]
    public void Ninety_percent_power_is_against_two_points_and_not_against_the_claimed_expectancy()
    {
        const double Claimed = 0.0165d;

        Assert.Equal(
            385,
            MinimumSample.Of(
                0.099811d, Claimed, MinimumSample.ZAlphaTwoSided95, MinimumSample.ZBetaPower90));

        Assert.True(
            MeasurementParameters.MinimumEffectiveObservations < 385,
            "the ratified sample is sized on the two points worth trading, so it must be the smaller");
    }

    /// <summary>
    /// The sample goes as the inverse square of the difference, which is what makes the two-point
    /// choice the lever it is.
    ///
    /// Halving what you ask to detect quadruples what you must accumulate. Asserted as a property
    /// over a sweep rather than at the four points the decision tabulates, because the decision
    /// invites a later session to move that input and the relation is what it should be moved
    /// against.
    /// </summary>
    [Theory]
    [InlineData(0.02d, 0.01d)]
    [InlineData(0.03d, 0.015d)]
    [InlineData(0.05d, 0.025d)]
    public void Halving_the_difference_worth_detecting_quadruples_the_sample(double wider, double half)
    {
        int atWider = MinimumSample.Of(0.1d, wider, MinimumSample.ZAlphaTwoSided95, MinimumSample.ZBetaPower90);
        int atHalf = MinimumSample.Of(0.1d, half, MinimumSample.ZAlphaTwoSided95, MinimumSample.ZBetaPower90);

        // Rounding up costs at most one observation on each side, so the ratio is four to within
        // that rather than exactly four.
        Assert.InRange(atHalf, (atWider * 4) - 4, (atWider * 4) + 4);
    }

    /// <summary>
    /// Rounding is up rather than to nearest, which is the direction that asks for more evidence.
    ///
    /// A fractional observation cannot be had, and rounding to nearest would be an authored step in
    /// a figure whose point is that no step in it is authored.
    /// </summary>
    [Fact]
    public void A_fractional_observation_rounds_up_and_never_to_nearest()
    {
        // 261.71 at the stated inputs, which rounds to nearest at 262 either way, so the case
        // that separates the two rules is the one below rather than this one.
        Assert.Equal(262, MinimumSample.Of(0.099811d));

        // And a hair below a whole number still costs the whole observation.
        Assert.Equal(2, MinimumSample.Of(0.02d / (MinimumSample.ZAlphaTwoSided95 + MinimumSample.ZBetaPower90) * 1.001d));
    }

    /// <summary>
    /// A dispersion of nought or less is refused rather than answered.
    ///
    /// Nought would give a minimum of nought observations, which reads as "no evidence needed" and
    /// is the most dangerous number this method could return.
    /// </summary>
    [Fact]
    public void A_dispersion_of_nothing_is_refused_rather_than_answered_with_nothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MinimumSample.Of(0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => MinimumSample.Of(-0.01d));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MinimumSample.Of(0.1d, 0d, MinimumSample.ZAlphaTwoSided95, MinimumSample.ZBetaPower90));
    }

    /// <summary>
    /// The market's own move cancels out of the cross-section, which is the property the whole
    /// measurement rests on.
    ///
    /// Two sessions with identical spreads about different means must give the same dispersion: the
    /// second session's market move is a constant added to every name in it, and a constant added to
    /// every name changes no variance. Written as a test because a measurement that quietly picked up
    /// the market move would be larger, would ask for more evidence, and would look conservative
    /// while being wrong.
    /// </summary>
    [Fact]
    public void A_session_wide_move_leaves_the_dispersion_where_it_was()
    {
        double[] spread = [-0.06, -0.02, 0.0, 0.01, 0.03, 0.04];
        var names = Enumerable.Range(0, 24).Select(i => spread[i % spread.Length]).ToList();

        var calm = new ForwardDispersion.Session(new DateOnly(2026, 1, 5), names);
        var lifted = new ForwardDispersion.Session(
            new DateOnly(2026, 1, 6), [.. names.Select(r => r + 0.25d)]);

        ForwardDispersion.Measured? one = ForwardDispersion.Of([calm], 20, 5, 24);
        ForwardDispersion.Measured? both = ForwardDispersion.Of([calm, lifted], 20, 5, 24);

        Assert.NotNull(one);
        Assert.NotNull(both);
        Assert.Equal(one.Idiosyncratic, both.Idiosyncratic);
        Assert.Equal(2, both.Sessions);
    }

    /// <summary>
    /// The paired difference disperses further than a single name, and by the stated factor.
    ///
    /// The control mean is an average of five residuals rather than a clean subtraction, so it
    /// carries noise of its own. A pairing treated as free would understate the dispersion, shrink
    /// the minimum, and fire the decision early.
    /// </summary>
    [Fact]
    public void The_control_mean_carries_noise_of_its_own_and_the_difference_is_wider()
    {
        double[] spread = [-0.06, -0.02, 0.0, 0.01, 0.03, 0.04];
        var session = new ForwardDispersion.Session(
            new DateOnly(2026, 1, 5),
            [.. Enumerable.Range(0, 24).Select(i => spread[i % spread.Length])]);

        ForwardDispersion.Measured? measured = ForwardDispersion.Of([session], 20, 5, 24);

        Assert.NotNull(measured);
        Assert.True(measured.PairedDifference > measured.Idiosyncratic);
        Assert.Equal(
            Math.Round(measured.Idiosyncratic * Math.Sqrt(1.2d), 6, MidpointRounding.AwayFromZero),
            measured.PairedDifference);
    }

    /// <summary>
    /// A session too thin to have a cross-section is dropped rather than pooled in.
    ///
    /// Its mean would be mostly one of its own names, so removing it removes part of the dispersion
    /// being measured. Too small is the direction that costs something, so the drop is asserted
    /// rather than assumed: a session of three names contributes nothing at all, and a run of only
    /// such sessions answers null rather than answering small.
    /// </summary>
    [Fact]
    public void A_session_with_no_cross_section_is_dropped_rather_than_measured()
    {
        var thin = new ForwardDispersion.Session(new DateOnly(2026, 1, 5), [0.1d, -0.1d, 0.02d]);

        Assert.Null(ForwardDispersion.Of([thin], 20, 5, 3));

        double[] spread = [-0.06, -0.02, 0.0, 0.01, 0.03, 0.04];
        var full = new ForwardDispersion.Session(
            new DateOnly(2026, 1, 6),
            [.. Enumerable.Range(0, 24).Select(i => spread[i % spread.Length])]);

        ForwardDispersion.Measured? alone = ForwardDispersion.Of([full], 20, 5, 24);
        ForwardDispersion.Measured? withThin = ForwardDispersion.Of([thin, full], 20, 5, 24);

        Assert.NotNull(alone);
        Assert.NotNull(withThin);
        Assert.Equal(alone.Idiosyncratic, withThin.Idiosyncratic);
        Assert.Equal(1, withThin.Sessions);
    }

    /// <summary>
    /// A forward return looks forward by exactly the horizon and stops where the series does.
    ///
    /// The last horizon's worth of sessions have no return at all, and inventing one for them by
    /// reaching for the final close would put a shorter horizon into the population under the same
    /// name.
    /// </summary>
    [Fact]
    public void The_last_sessions_have_no_forward_return_rather_than_a_shorter_one()
    {
        var series = Enumerable.Range(0, 15)
            .Select(i => (new DateOnly(2026, 1, 5).AddDays(i), 100m + i))
            .ToList();

        IReadOnlyList<(DateOnly Date, double Return)> returns = ForwardDispersion.Returns(series, 10);

        Assert.Equal(5, returns.Count);
        Assert.Equal(new DateOnly(2026, 1, 5), returns[0].Date);
        Assert.Equal(0.1d, returns[0].Return, 10);
    }
}
