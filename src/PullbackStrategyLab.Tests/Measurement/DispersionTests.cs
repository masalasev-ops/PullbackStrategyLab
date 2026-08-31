using PullbackStrategyLab.Core.Measurement;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// Which of the two discounts spent a panel's rows, and why the answer is not "the panel was thin".
///
/// <b>A panel reading "262 needed, 65 held" is read as short of rows and it need not be short of
/// anything.</b> The effective count is the row count discounted twice, once for nights repeating
/// each other and once for pairs moving together inside a night, and a panel can hold thousands of
/// rows over fifty nights and still report sixty-five. The repair for a thin panel is more nights.
/// The repair for a panel whose pairs move together is not, and the two are indistinguishable in
/// the reported figure.
/// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
///
/// <b>The exposure is not a second reading of the figure.</b>
/// <see cref="PairedInterval.EffectiveObservations"/> returns
/// <see cref="PairedInterval.Dispersion.Effective"/> and computes nothing of its own, and the first
/// test below is what says so. A diagnostic that re-derived the number it explains could drift from
/// it and go on sounding right, which is the shape this corpus has shipped four times.
/// </summary>
public sealed class DispersionTests
{
    private const int Nights = 40;

    private static IReadOnlyList<PairedInterval.Night> Series(int pairs, decimal within, decimal swing)
    {
        var start = new DateOnly(2026, 1, 5);

        return
        [
            .. Enumerable.Range(0, Nights).Select(i => new PairedInterval.Night(
                start.AddDays(i), 0.004m + (swing * ((i % 7) - 3) / 3m), pairs, within)),
        ];
    }

    /// <summary>
    /// The reported figure is the exposed one, over every series shape this file builds.
    ///
    /// The whole guard on the extraction. Where these two can differ, the explanation describes a
    /// computation nobody runs.
    /// </summary>
    [Fact]
    public void The_effective_count_is_the_one_the_discounts_produce()
    {
        IReadOnlyList<PairedInterval.Night>[] shapes =
        [
            Series(80, 0.1m, 0.011m),
            Series(80, 0.001m, 0.011m),
            Series(5, 0.4m, 0.02m),
            Series(80, 0m, 0.011m),
            [.. Series(80, 0.1m, 0.011m).Take(2)],
            [.. Series(80, 0.1m, 0m)],
        ];

        foreach (IReadOnlyList<PairedInterval.Night> series in shapes)
        {
            Assert.Equal(
                PairedInterval.EffectiveObservations(series),
                PairedInterval.Disperse(series).Effective);
        }
    }

    /// <summary>
    /// Two panels with the same rows, the same nights and the same nightly means, and one is worth
    /// twenty times the other.
    ///
    /// <b>This is the shape the tight control set turned out to have.</b> The only difference
    /// between the two series is how far apart the pairs sat inside each night. Where they sat close
    /// together, the swing between nights is far larger than independent pairs could produce, the
    /// design effect carries that, and three thousand two hundred rows are worth a few dozen
    /// observations. Nothing about the row count, the night count or the means says so.
    /// </summary>
    [Fact]
    public void Pairs_that_move_together_within_a_night_spend_the_row_count()
    {
        IReadOnlyList<PairedInterval.Night> dispersed = Series(80, 0.4m, 0.011m);
        IReadOnlyList<PairedInterval.Night> together = Series(80, 0.01m, 0.011m);

        Assert.Equal(dispersed.Sum(n => n.Pairs), together.Sum(n => n.Pairs));
        Assert.Equal(dispersed.Count, together.Count);

        PairedInterval.Dispersion wide = PairedInterval.Disperse(dispersed);
        PairedInterval.Dispersion tight = PairedInterval.Disperse(together);

        // The two discounts are reported separately because only one of them moved. The nights
        // repeat each other to exactly the same degree in both series.
        Assert.Equal(wide.Serial, tight.Serial);
        Assert.Equal(wide.IndependentRows, tight.IndependentRows);

        Assert.NotNull(wide.Design);
        Assert.NotNull(tight.Design);
        // The dispersed series reports the floor of one: its nights vary less than independent
        // pairs alone would produce, so there is no clustering to charge for. The clustered one
        // reports forty times that over the same rows and the same nights.
        Assert.Equal(1m, wide.Design);
        Assert.True(tight.Design > wide.Design * 10,
            $"The clustered series reported a design effect of {tight.Design} against {wide.Design}, "
            + "so the two series are not separated by the discount this test is about.");

        Assert.True(tight.Effective * 20 < wide.Effective,
            $"{tight.Effective} against {wide.Effective} over the same {together.Sum(n => n.Pairs)} rows.");
    }

    /// <summary>
    /// A series too short for either discount to be measurable says so rather than reporting one.
    ///
    /// Null for the design effect rather than one, because a value of one reads as "measured, and it
    /// cost nothing" and the truth is that nothing was measured. The distinction is the whole reason
    /// the field is nullable.
    /// </summary>
    [Fact]
    public void A_series_too_short_to_measure_a_discount_reports_none()
    {
        PairedInterval.Dispersion two = PairedInterval.Disperse([.. Series(80, 0.1m, 0.011m).Take(2)]);

        Assert.Equal(2, two.Nights);
        Assert.Equal(160, two.Rows);
        Assert.Null(two.Design);
        Assert.Equal(2, two.Effective);
    }

    /// <summary>
    /// A series whose nights are identical is one observation, and the record says which discount
    /// took it.
    ///
    /// Forty nights of the same mean carry one reading however many pairs they hold, and the serial
    /// factor of nought is what says the nights repeating each other is the cause rather than the
    /// pairing.
    /// </summary>
    [Fact]
    public void A_series_that_never_moves_is_one_observation_and_names_the_discount()
    {
        PairedInterval.Dispersion flat = PairedInterval.Disperse(Series(80, 0.1m, 0m));

        Assert.Equal(1, flat.Effective);
        Assert.Equal(0m, flat.Serial);
        Assert.Null(flat.Design);
    }
}
