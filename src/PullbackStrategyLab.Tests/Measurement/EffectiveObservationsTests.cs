using PullbackStrategyLab.Core.Measurement;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// What a night is worth, which is the number checkpoint 3.6 fires on.
///
/// <b>It used to be capped at the night count and that threw away what the control draw bought.</b>
/// Same-night setups share a market factor, which is why an unpaired figure over forty names is
/// worth about one observation. The paired difference removes that factor by construction, so what
/// is left inside a night is each name's own move against its own controls. Counting the night as
/// one anyway would have made three months of accumulation look like sixty observations when it was
/// nearer six thousand, and 3.6 would have waited on a number that could not arrive.
///
/// <b>The pessimistic reading is now the limiting case rather than the assumption</b>, and that is
/// what these assert: a night that cannot say how its own pairs dispersed still counts as one, and a
/// night whose pairs all move together collapses back to about one however many pairs it holds.
/// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
/// </summary>
public sealed class EffectiveObservationsTests
{
    private const int Nights = 40;

    /// <summary>A series of nightly means with a stated pair count and within-night spread.</summary>
    private static IReadOnlyList<PairedInterval.Night> Series(
        int pairs, decimal within, Func<int, decimal> mean)
    {
        var start = new DateOnly(2026, 1, 5);

        return
        [
            .. Enumerable.Range(0, Nights)
                .Select(i => new PairedInterval.Night(start.AddDays(i), mean(i), pairs, within)),
        ];
    }

    /// <summary>A wobble that does not repeat itself, so the serial discount is out of the way.</summary>
    private static decimal Wobble(int i, decimal size) => size * ((i % 7) - 3) / 3m;

    /// <summary>
    /// A night that cannot say how its own pairs dispersed counts as one, however many it holds.
    ///
    /// This is the old behaviour, kept as the corner rather than deleted. An unknown is read the safe
    /// way: a night of eighty pairs that reports no spread between them is indistinguishable from a
    /// night of eighty pairs that all said the same thing.
    /// </summary>
    [Fact]
    public void A_night_that_cannot_say_how_its_pairs_dispersed_counts_as_one()
    {
        IReadOnlyList<PairedInterval.Night> silent =
            Series(80, 0m, i => 0.004m + Wobble(i, 0.002m));

        int effective = PairedInterval.EffectiveObservations(silent);

        Assert.True(
            effective <= Nights,
            $"a series saying nothing about its own pairs claimed {effective} of {Nights} nights");
    }

    /// <summary>
    /// A night whose pairs move apart is worth its pairs, which is the whole point of pairing.
    ///
    /// The same series with a spread recorded is worth two orders of magnitude more than the same
    /// series without one, and that difference is the control draw showing up in the count.
    /// </summary>
    [Fact]
    public void A_night_whose_pairs_move_apart_is_worth_its_pairs_and_not_its_night()
    {
        Func<int, decimal> mean = i => 0.004m + Wobble(i, 0.011m);

        int silent = PairedInterval.EffectiveObservations(Series(80, 0m, mean));
        int spoken = PairedInterval.EffectiveObservations(Series(80, 0.1m, mean));

        Assert.True(silent <= Nights);
        Assert.True(
            spoken > Nights * 10,
            $"eighty pairs a night moving apart were worth only {spoken}, against {Nights} nights");
    }

    /// <summary>
    /// A night whose pairs all move together collapses back to about one, and the design effect is
    /// what does it.
    ///
    /// The nightly means vary far more than within-night independence would allow, so the excess is
    /// clustering the matching failed to remove and the row count is divided by it. Without this the
    /// row count would be a free credit and the effective sample would be a row count wearing a
    /// different name.
    /// </summary>
    [Fact]
    public void A_night_whose_pairs_move_together_collapses_back_toward_the_night_count()
    {
        // Under independence a nightly mean disperses by within / sqrt(pairs), which is 0.1/9 here.
        // A wobble ten times that is clustering, and the row count should be divided by about a
        // hundred rather than credited in full.
        IReadOnlyList<PairedInterval.Night> clustered =
            Series(81, 0.1m, i => 0.004m + Wobble(i, 0.111m));

        int effective = PairedInterval.EffectiveObservations(clustered);

        Assert.True(
            effective < Nights * 81 / 10,
            $"a night whose pairs all moved together was still worth {effective} of {Nights * 81} rows");
    }

    /// <summary>
    /// A series that repeats itself is discounted for the overlap even when its pairs move apart.
    ///
    /// The two discounts multiply rather than one masking the other, which is the arrangement a
    /// reader of the panel is entitled to assume. A ten-day label overlapping its neighbour costs
    /// something whatever is happening inside a night.
    /// </summary>
    [Fact]
    public void The_overlap_across_nights_still_costs_when_the_pairs_inside_them_move_apart()
    {
        var start = new DateOnly(2026, 1, 5);
        decimal carried = 0.004m;
        var repeating = new List<PairedInterval.Night>();

        for (int i = 0; i < Nights; i++)
        {
            // Each night carries most of the night before it, which is what a ten-session label
            // sliding one session at a time does to a series.
            carried = (0.85m * carried) + (0.15m * (0.004m + Wobble(i, 0.02m)));
            repeating.Add(new PairedInterval.Night(start.AddDays(i), carried, 80, 0.1m));
        }

        int repeats = PairedInterval.EffectiveObservations(repeating);
        int independent = PairedInterval.EffectiveObservations(
            Series(80, 0.1m, i => 0.004m + Wobble(i, 0.011m)));

        Assert.True(
            repeats < independent,
            $"a repeating series was worth {repeats}, no less than the independent one's {independent}");
    }

    /// <summary>
    /// The first nights count as themselves rather than as a claim.
    ///
    /// Too short for either discount to be measurable, so a night is one observation. It is
    /// meaningless for the first fortnight and says so by climbing from nothing, which is more than
    /// a date on a calendar ever said.
    /// </summary>
    [Fact]
    public void The_first_nights_climb_from_nothing_rather_than_claiming_their_rows()
    {
        var start = new DateOnly(2026, 1, 5);

        Assert.Equal(0, PairedInterval.EffectiveObservations([]));

        for (int nights = 1; nights <= 2; nights++)
        {
            IReadOnlyList<PairedInterval.Night> few =
            [
                .. Enumerable.Range(0, nights).Select(i =>
                    new PairedInterval.Night(start.AddDays(i), 0.004m, 80, 0.1m)),
            ];

            Assert.Equal(nights, PairedInterval.EffectiveObservations(few));
        }
    }
}
