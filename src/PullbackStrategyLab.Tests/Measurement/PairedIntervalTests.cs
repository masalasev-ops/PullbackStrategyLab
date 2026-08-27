using PullbackStrategyLab.Core.Measurement;
using Xunit;

namespace PullbackStrategyLab.Tests.Measurement;

/// <summary>
/// The interval, asserted as a property rather than as a value.
///
/// <b>Every value assertion this class could make already existed and every one of them passed.</b>
/// The scheme these tests replace produced five committed `DERIVED` interval expectations, matched
/// by an independent restatement in `tools/derive-indicators.py`, over five authored series written
/// to exercise the exact failure it had. It was still not a bootstrap: the restatement hard-coded
/// the same two strides, so what agreed was the transcription of an algorithm, never that the
/// algorithm was the one the decision names. A second implementation of the wrong thing agrees with
/// the first.
///
/// So what is held here is what the scheme has to be true of, not what it happens to return:
/// asking for more draws has to buy more resamples, an interval must not clear zero far more often
/// than its own confidence claims, and a series that cannot disperse must be withheld rather than
/// given an interval of no width.
/// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
/// </summary>
public sealed class PairedIntervalTests
{
    private const int Block = MeasurementParameters.BootstrapBlockSessions;

    private const int Draws = MeasurementParameters.BootstrapDraws;

    private static readonly DateOnly Start = new(2026, 1, 5);

    /// <summary>
    /// Ten thousand draws must buy materially more resamples than the night count does.
    ///
    /// <b>This is the assertion that would have failed on the day the last scheme shipped.</b> Its
    /// starts were <c>(draw * 7919 + block * 104729) mod N</c>, so every draw was draw nought
    /// rotated by the same amount and the resample space had one point per night in it however many
    /// draws were asked for. Ten thousand draws was bit-identical to N draws, and nothing said so,
    /// because the only thing anybody compared was the pair of bounds it returned.
    ///
    /// Ten times is below what a real scheme gives, which is between twenty-two and a hundred and
    /// sixty-six times over the five committed scenarios that produce an interval. The margin is
    /// deliberate and it is not large on the tightest of them: the assertion has to survive a change
    /// of generator, and it still sits an order of magnitude above what a rotation returns, which is
    /// exactly one whatever the series.
    /// </summary>
    [Fact]
    public void Asking_for_more_draws_buys_more_resamples()
    {
        IReadOnlyList<PairedInterval.Night> series = Wobbling(40, 0.002d, 0.02d, 7);

        int atNightCount = PairedInterval.DistinctResampleMeans(series, Block, series.Count);
        int atFullDraws = PairedInterval.DistinctResampleMeans(series, Block, Draws);

        Assert.True(
            atFullDraws > atNightCount * 10,
            $"{Draws} draws produced {atFullDraws} distinct resample means against {atNightCount} "
            + $"at {series.Count}. A scheme whose draws are one lattice rotated answers with the "
            + "night count whatever it is asked for.");
    }

    /// <summary>
    /// And the interval itself must move when the draw count does, which is the same property read
    /// through the surface that is actually stored.
    ///
    /// Held separately from the count above because a later session could keep the count honest and
    /// still collapse the interval, and because this is the form the defect took in the store: two
    /// runs at different draw counts wrote the same two numbers.
    /// </summary>
    [Fact]
    public void The_interval_is_not_the_same_at_every_draw_count()
    {
        IReadOnlyList<PairedInterval.Night> series = Wobbling(40, 0.002d, 0.02d, 11);

        PairedInterval.Estimate? few = PairedInterval.Of(series, Block, series.Count);
        PairedInterval.Estimate? many = PairedInterval.Of(series, Block, Draws);

        Assert.NotNull(few);
        Assert.NotNull(many);
        Assert.True(
            few.Low != many.Low || few.High != many.High,
            "The interval was identical at two draw counts, which is what a rotation returns.");
    }

    /// <summary>
    /// Under a true null the interval must not clear zero far more often than its own confidence
    /// claims.
    ///
    /// <b>This is the property the whole class exists for and the one nothing held.</b> Band 1 reads
    /// green when the lower bound clears zero, so an interval that clears it four times too often
    /// does not produce a slightly optimistic number: it produces the answer to the project's
    /// central question, wrongly, with a confidence figure printed beside it.
    ///
    /// Over three hundred authored null series, all three schemes seeing the same series, at forty
    /// nights: the scheme this replaces cleared zero 46.0% of the time independent and 71.3% at an
    /// AR(1) of 0.7. A percentile interval over correctly drawn blocks is much better and still not
    /// good enough, at 12.3% and 24.0%. Studentising holds 3.7% to 7.7% over independent nights and
    /// an AR(1) up to 0.7, from twenty to a hundred nights.
    ///
    /// <b>It does not hold at an AR(1) of 0.9, which is why the grid stops at 0.7 and says so.</b>
    /// There it reads 7.0% to 24.0%, because correlation at 0.9 runs well past ten sessions and no
    /// block of ten absorbs it. That is a limit of the block length, and the case that matters is
    /// held separately below: the process a ten-session label actually creates is a moving average of
    /// order nine, whose correlation cuts off inside the block.
    ///
    /// <b>The rotation's rate is erratic as well as high, which is its own argument.</b> It reads
    /// 8.7% at thirty independent nights and 46.0% at forty, because what it returns depends on how
    /// the lattice happens to land on the series length. A scheme whose confidence is a function of
    /// how many nights have accumulated is not a confidence figure at all.
    ///
    /// The ceiling here is 15% rather than something near 5% because two hundred trials put three
    /// standard errors at about four and a half points, and a test that fails once a month is a test
    /// that gets deleted. It is still nowhere near reachable by any of the schemes above that this
    /// one replaces.
    ///
    /// The floor is not decoration. An interval made arbitrarily wide would pass a ceiling on its
    /// own and would be useless, so the same assertion holds that the instrument can still say
    /// something.
    /// </summary>
    [Theory]
    [InlineData(0.0d, 21)]
    [InlineData(0.7d, 22)]
    public void Under_a_true_null_it_clears_zero_about_as_often_as_it_claims(double carryOver, int seed)
    {
        const int Trials = 200;
        const int Nights = 40;

        int cleared = 0;

        for (int trial = 0; trial < Trials; trial++)
        {
            // A fresh series with a true mean of nought. Not centred afterwards: centring would make
            // the estimate exactly zero and no interval could ever clear it, which is a test that
            // passes by construction.
            IReadOnlyList<PairedInterval.Night> series =
                Wobbling(Nights, 0d, 0.02d, seed + (trial * 7919), carryOver);

            PairedInterval.Estimate? estimate = PairedInterval.Of(series, Block, 1_000);

            if (estimate is not null && (estimate.Low > 0m || estimate.High < 0m))
            {
                cleared++;
            }
        }

        double rate = 100d * cleared / Trials;

        Assert.True(
            rate <= 15d,
            $"cleared zero in {rate:0.0}% of {Trials} null series at a carry-over of {carryOver}, "
            + "against a nominal 5%. An interval that clears zero this often answers band 1's "
            + "question by itself.");

        Assert.True(
            rate >= 0.5d,
            $"cleared zero in {rate:0.0}% of {Trials} null series, which is an interval so wide it "
            + "could never say anything rather than one that is calibrated.");
    }

    /// <summary>
    /// And it holds against the dependence a ten-session overlapping label actually creates.
    ///
    /// <b>This is the case the whole design is aimed at, and the AR(1) grid above is not it.</b> Each
    /// night's paired difference is measured over a window of ten sessions, so adjacent nights share
    /// nine of their ten and the series is a moving average of order nine. Its correlation is high at
    /// lag one and cuts off completely at lag ten, which is exactly what a block of ten is chosen to
    /// absorb. An AR(1) never cuts off, so at 0.9 it defeats any block length and says nothing about
    /// whether the block was chosen well.
    ///
    /// Measured at 3.0% to 11.7% from twenty to two hundred and forty nights. The ceiling here is 18%
    /// rather than 15% because the measured value at sixty nights is 11.7% and two hundred trials put
    /// three standard errors at about six points.
    /// </summary>
    [Fact]
    public void It_holds_against_the_overlap_a_ten_session_label_creates()
    {
        const int Trials = 200;
        const int Nights = 40;

        int cleared = 0;

        for (int trial = 0; trial < Trials; trial++)
        {
            IReadOnlyList<PairedInterval.Night> series = Overlapping(Nights, 41 + (trial * 7919));

            PairedInterval.Estimate? estimate = PairedInterval.Of(series, Block, 1_000);

            if (estimate is not null && (estimate.Low > 0m || estimate.High < 0m))
            {
                cleared++;
            }
        }

        double rate = 100d * cleared / Trials;

        Assert.True(
            rate <= 18d,
            $"cleared zero in {rate:0.0}% of {Trials} null series carrying the overlap a "
            + $"{MeasurementParameters.ScoringHorizonSessions}-session label creates, against a "
            + "nominal 5%. The block length is chosen to absorb exactly this dependence.");

        Assert.True(rate >= 0.5d, $"cleared zero in {rate:0.0}%, which is an interval that can never say anything.");
    }

    /// <summary>
    /// A series of nightly means each taken over the same ten daily shocks its own window covers, so
    /// adjacent nights share nine of ten and the correlation cuts off at lag ten.
    ///
    /// True mean of nought, and not centred afterwards, for the same reason the AR(1) series are not.
    /// </summary>
    private static IReadOnlyList<PairedInterval.Night> Overlapping(int nights, int seed)
    {
        int horizon = MeasurementParameters.ScoringHorizonSessions;
        var shocks = new List<double>(nights + horizon);
        ulong state = unchecked((ulong)seed) + 0x9E3779B97F4A7C15UL;

        for (int i = 0; i < nights + horizon; i++)
        {
            state = Next(state, out ulong first);
            state = Next(state, out ulong second);

            double u = ((first >> 11) + 0.5d) / (1UL << 53);
            double v = ((second >> 11) + 0.5d) / (1UL << 53);

            shocks.Add(0.02d * Math.Sqrt(-2d * Math.Log(u)) * Math.Cos(2d * Math.PI * v));
        }

        var series = new List<PairedInterval.Night>(nights);

        for (int i = 0; i < nights; i++)
        {
            double window = 0d;

            for (int j = 0; j < horizon; j++)
            {
                window += shocks[i + j];
            }

            series.Add(new PairedInterval.Night(
                Start.AddDays(i), (decimal)(window / horizon), 80, 0.1m));
        }

        return series;
    }

    /// <summary>
    /// A series whose blocks do not disperse is withheld, not given an interval of no width.
    ///
    /// <b>The first route to this failure, held permanently.</b> An interval of no width clears zero
    /// always, and it is reached by any scheme whose resamples all return the same mean. It shipped
    /// once at 3.5 from walking the block offsets in order, and it is reachable again from a series
    /// that simply repeats one value, which is a real state on a night where every pair returned the
    /// same difference.
    /// </summary>
    [Fact]
    public void A_series_that_cannot_disperse_is_withheld_rather_than_given_no_width()
    {
        IReadOnlyList<PairedInterval.Night> flat =
        [
            .. Enumerable.Range(0, 40)
                .Select(i => new PairedInterval.Night(Start.AddDays(i), 0.004m, 80, 0.1m)),
        ];

        Assert.Null(PairedInterval.Of(flat, Block, Draws));
    }

    /// <summary>
    /// The same series gives the same interval, every time and in any input order.
    ///
    /// The reproducibility the decision asks for, asserted rather than assumed. A fixed published
    /// seed buys nothing if the walk over the series depends on the order rows came back in.
    /// </summary>
    [Fact]
    public void The_same_series_gives_the_same_interval_in_any_order()
    {
        IReadOnlyList<PairedInterval.Night> series = Wobbling(40, 0.006d, 0.02d, 33);
        IReadOnlyList<PairedInterval.Night> shuffled = [.. series.Reverse()];

        PairedInterval.Estimate? first = PairedInterval.Of(series, Block, Draws);
        PairedInterval.Estimate? again = PairedInterval.Of(series, Block, Draws);
        PairedInterval.Estimate? reordered = PairedInterval.Of(shuffled, Block, Draws);

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.Equal(first, reordered);
    }

    /// <summary>
    /// Fewer nights than twice the block length produces nothing at all.
    ///
    /// The one thing that withholds an interval, and it is a shortage of sessions rather than of
    /// evidence. Held here so the two shortages stay distinguishable.
    /// </summary>
    [Fact]
    public void Fewer_than_two_blocks_of_sessions_produces_nothing()
    {
        Assert.Null(PairedInterval.Of(Wobbling(19, 0.02d, 0.005d, 5), Block, Draws));
        Assert.NotNull(PairedInterval.Of(Wobbling(20, 0.02d, 0.005d, 5), Block, Draws));
    }

    /// <summary>
    /// A series of nightly means around <paramref name="mean"/>, optionally carrying part of the
    /// night before it.
    ///
    /// Generated here rather than drawn from a framework, so the series is identical on both
    /// platforms and in any restatement. splitmix64 into Box-Muller, which is four lines of each and
    /// has no state a runtime could change underneath it.
    /// </summary>
    private static IReadOnlyList<PairedInterval.Night> Wobbling(
        int nights, double mean, double spread, int seed, double carryOver = 0d)
    {
        var series = new List<PairedInterval.Night>(nights);
        ulong state = unchecked((ulong)seed) + 0x9E3779B97F4A7C15UL;
        double carried = 0d;

        for (int i = 0; i < nights; i++)
        {
            state = Next(state, out ulong first);
            state = Next(state, out ulong second);

            double u = ((first >> 11) + 0.5d) / (1UL << 53);
            double v = ((second >> 11) + 0.5d) / (1UL << 53);
            double normal = Math.Sqrt(-2d * Math.Log(u)) * Math.Cos(2d * Math.PI * v);

            carried = (carryOver * carried) + (spread * normal);

            series.Add(new PairedInterval.Night(
                Start.AddDays(i), (decimal)(mean + carried), 80, 0.1m));
        }

        return series;
    }

    private static ulong Next(ulong state, out ulong value)
    {
        state += 0x9E3779B97F4A7C15UL;

        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        value = z ^ (z >> 31);

        return state;
    }
}
