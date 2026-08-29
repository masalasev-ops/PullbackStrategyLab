using PullbackStrategyLab.Core.Indicators;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// `held-floor` and `no-reclaim` compare each dip session against the average as at that session,
/// not against the average as at the setup date.
///
/// ARCHITECTURE says "No daily close below the 21-day average during the dip". The dip is a span,
/// the average is a series, and the chart draws it as one, so the document and the screen already
/// agreed and the code was the odd one out. Until 3.11 it took the as-of session's single value and
/// held it against every bar of the dip.
///
/// <b>The two comparisons disagree in opposite directions, which is why one case cannot show it.</b>
/// On a rising average the as-of value is the highest the line reached over the dip, so the scalar
/// form is stricter than the chart and drops a setup whose closes were above the line the whole way.
/// On a falling average it is the lowest, so the scalar form is looser and admits one whose closes
/// were below it. A test built on a rising average alone would pass against a scalar that always
/// read the minimum, and one built on a falling average alone would pass against the maximum.
///
/// Every assertion here fails against the scalar form, which is the point of writing them.
/// </summary>
public sealed class FloorSeriesTests
{
    /// <summary>
    /// A dip of four sessions after the extreme, with the closes held one unit clear of the average
    /// at every session. The average is handed in, so the case is about the comparison rather than
    /// about how an average is computed.
    /// </summary>
    private static (IReadOnlyList<PullbackGeometry.Bar> Bars, PullbackGeometry.Pullback Dip) Dip(
        IReadOnlyList<decimal> closes)
    {
        PullbackGeometry.Bar[] bars =
        [
            .. closes.Select(c => new PullbackGeometry.Bar(c, c + 1m, c - 1m, c, c + 1m, c - 1m)),
        ];

        // The extreme sits at index 0, so every later bar is a session of the dip.
        var dip = new PullbackGeometry.Pullback(
            ThrustIndex: 0,
            ExtremeIndex: 0,
            ThrustOrigin: closes[0],
            ThrustExtreme: closes[0],
            PullbackExtreme: closes[^1],
            PullbackBars: closes.Count - 1,
            RetraceDepth: 0.5m,
            Trigger: closes[0],
            Stop: closes[^1]);

        return (bars, dip);
    }

    private static decimal?[] Series(params decimal[] values) => [.. values.Select(v => (decimal?)v)];

    [Fact]
    public void A_rising_average_makes_the_scalar_form_stricter_than_the_chart()
    {
        // The dip runs 99, 100, 105 after the extreme and the average runs 98, 99, 103 under it.
        // Every session of the dip closed above its own average, so nothing is beyond the floor and
        // the chart shows the whole dip above the line.
        (IReadOnlyList<PullbackGeometry.Bar> bars, PullbackGeometry.Pullback dip) =
            Dip([100m, 99m, 100m, 105m]);

        decimal?[] rising = Series(95m, 98m, 99m, 103m);

        Assert.Equal(0, PullbackGeometry.ClosesBeyondFloor(bars, dip, rising, isLong: true));

        // The as-of value is the last of a rising series, which is the highest the line reached.
        // Held against every session it condemns the two that closed under 103, and both of those
        // closed above the line the chart draws. Stricter than the document, and it drops a setup.
        decimal?[] asOfOnly = Series(103m, 103m, 103m, 103m);

        Assert.Equal(2, PullbackGeometry.ClosesBeyondFloor(bars, dip, asOfOnly, isLong: true));
    }

    [Fact]
    public void A_falling_average_makes_the_scalar_form_looser_than_the_chart()
    {
        // The mirror, and the direction that admits a setup rather than dropping one. The dip runs
        // 95, 94, 93 and the average falls past it, 99, 97, 92, so the first two sessions closed
        // under their own average and the chart shows them under the line.
        (IReadOnlyList<PullbackGeometry.Bar> bars, PullbackGeometry.Pullback dip) =
            Dip([100m, 95m, 94m, 93m]);

        decimal?[] falling = Series(103m, 99m, 97m, 92m);

        Assert.Equal(2, PullbackGeometry.ClosesBeyondFloor(bars, dip, falling, isLong: true));

        // The as-of value is the last of a falling series, which is the lowest the line reached.
        // Held against every session it clears all three, so a dip the chart shows breaking the
        // average twice reads as never having broken it. Looser than the document, and it admits a
        // setup rather than dropping one, which is the direction that costs something.
        decimal?[] asOfOnly = Series(92m, 92m, 92m, 92m);

        Assert.Equal(0, PullbackGeometry.ClosesBeyondFloor(bars, dip, asOfOnly, isLong: true));
    }

    [Fact]
    public void The_short_side_disagrees_in_the_same_two_directions()
    {
        // no-reclaim is the mirror: a bounce may not close back above the average. The same two
        // shapes, read the other way, so the property is not a long-side accident.
        (IReadOnlyList<PullbackGeometry.Bar> bars, PullbackGeometry.Pullback bounce) =
            Dip([100m, 101m, 102m, 103m]);

        decimal?[] rising = Series(102m, 103m, 104m, 105m);

        // Every session closed below its own average, so nothing reclaimed it.
        Assert.Equal(0, PullbackGeometry.ClosesBeyondFloor(bars, bounce, rising, isLong: false));

        // Against the as-of value alone, which is the highest of a rising series, the same three
        // sessions still hold. The looseness appears where the series falls.
        (IReadOnlyList<PullbackGeometry.Bar> falling, PullbackGeometry.Pullback fallingBounce) =
            Dip([100m, 99m, 98m, 97m]);

        decimal?[] fallingAverage = Series(101m, 100m, 99m, 96m);

        // 99 closed above 100? No. 98 above 99? No. 97 above 96? Yes, one reclaim.
        Assert.Equal(1, PullbackGeometry.ClosesBeyondFloor(falling, fallingBounce, fallingAverage, isLong: false));

        // The as-of value alone is 96, and every session closed above it, so the scalar form counts
        // three reclaims where the series counts one.
        Assert.Equal(3, PullbackGeometry.ClosesBeyondFloor(
            falling, fallingBounce, Series(96m, 96m, 96m, 96m), isLong: false));
    }

    [Fact]
    public void A_session_with_no_average_yet_is_neither_held_nor_broken()
    {
        (IReadOnlyList<PullbackGeometry.Bar> bars, PullbackGeometry.Pullback dip) =
            Dip([100m, 90m, 102m, 110m]);

        // Two sessions inside the warm-up. Counting a null as a breach would fail a setup for the
        // age of its history rather than for its shape, and counting it as held would say the close
        // was above a line nobody has drawn.
        decimal?[] partial = [null, null, 103m, 103m];

        Assert.Equal(1, PullbackGeometry.ClosesBeyondFloor(bars, dip, partial, isLong: true));
    }

    [Fact]
    public void A_series_shorter_than_the_bars_does_not_throw()
    {
        (IReadOnlyList<PullbackGeometry.Bar> bars, PullbackGeometry.Pullback dip) =
            Dip([100m, 90m, 91m, 92m]);

        Assert.Equal(1, PullbackGeometry.ClosesBeyondFloor(bars, dip, Series(101m, 95m), isLong: true));
    }

}
