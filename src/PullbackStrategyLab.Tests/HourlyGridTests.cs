using PullbackStrategyLab.Core.Time;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The hourly grid the short exit reads, and the half hour it refuses.
///
/// The rule is that an hourly bar closes back above the 50-day average. The store holds minutes, so
/// the bars are aggregated, and the answer moves with where the boundaries fall. This is the test of
/// the boundary rather than of the exit, which arrives at 4.8.
/// see: The hourly grid anchors to the session open, and the closing stub is not an hourly bar
/// </summary>
public sealed class HourlyGridTests
{
    private static readonly DateOnly Session = new(2026, 8, 25);

    [Fact]
    public void A_bar_opening_at_the_last_complete_hour_can_satisfy_the_close_rule()
    {
        // 14:30 opens the sixth and last complete hourly bar, which closes at 15:30. The rule reads
        // its close.
        Assert.True(HourlyGrid.IsHourlyClose(new TimeOnly(14, 30)));
    }

    [Fact]
    public void The_closing_stub_cannot_satisfy_the_close_rule()
    {
        // 15:30 to 16:00 is thirty minutes. A level held for thirty minutes has not been held for an
        // hour, and reading this as an hourly close would fire the exit on a bar the rule never
        // described. It is the last opening inside the session and it is not on the grid.
        Assert.False(HourlyGrid.IsHourlyClose(new TimeOnly(15, 30)));
        Assert.Equal(new TimeOnly(15, 30), HourlyGrid.StubOpen);
        Assert.True(HourlyGrid.HasStub);
        Assert.Equal(30, HourlyGrid.StubMinutes);
    }

    [Fact]
    public void The_grid_anchors_to_the_open_and_the_stub_is_at_the_end()
    {
        // The whole content of the decision, stated as the list it produces. Anchored to the clock
        // the first entry would be 09:30 to 10:00 and the six complete bars would start at 10:00,
        // which puts the shortest and noisiest bar of the session first.
        Assert.Equal(
            [
                new TimeOnly(9, 30), new TimeOnly(10, 30), new TimeOnly(11, 30),
                new TimeOnly(12, 30), new TimeOnly(13, 30), new TimeOnly(14, 30),
            ],
            HourlyGrid.Opens);

        Assert.Equal(6, HourlyGrid.CompleteBars);
        Assert.Equal(SessionBoundaries.RegularSessionOpen, HourlyGrid.Opens[0]);
    }

    [Fact]
    public void The_grid_is_derived_from_the_session_and_states_neither_boundary_again()
    {
        // Both figures are arithmetic over the two shipped constants rather than numbers written
        // beside them. If the exchange moved either boundary the grid would follow, and a grid that
        // restated 09:30 would not.
        Assert.Equal(SessionBoundaries.RegularSessionMinutes / 60, HourlyGrid.CompleteBars);
        Assert.Equal(SessionBoundaries.RegularSessionMinutes % 60, HourlyGrid.StubMinutes);
        Assert.Equal(
            SessionBoundaries.RegularSessionMinutes,
            (HourlyGrid.CompleteBars * 60) + HourlyGrid.StubMinutes);
    }

    [Fact]
    public void A_time_that_is_not_a_boundary_is_refused_rather_than_rounded()
    {
        // A caller handing in a minute rather than an hour boundary gets nothing back. Rounding to
        // the nearest grid line would answer a question about 10:47 with an answer about 10:30.
        Assert.False(HourlyGrid.IsHourlyClose(new TimeOnly(10, 47)));
        Assert.False(HourlyGrid.IsHourlyClose(new TimeOnly(10, 0)));
        Assert.False(HourlyGrid.IsHourlyClose(SessionBoundaries.RegularSessionClose));
    }

    [Fact]
    public void The_same_question_about_an_instant_resolves_through_the_session_zone()
    {
        DateTimeOffset lastComplete = SessionBoundaries.At(
            Session, new TimeOnly(14, 30), SessionBoundaries.UsEquities);
        DateTimeOffset stub = SessionBoundaries.At(
            Session, new TimeOnly(15, 30), SessionBoundaries.UsEquities);

        Assert.True(HourlyGrid.IsHourlyClose(lastComplete, Session, SessionBoundaries.UsEquities));
        Assert.False(HourlyGrid.IsHourlyClose(stub, Session, SessionBoundaries.UsEquities));
    }

    [Fact]
    public void A_minute_inside_the_stub_belongs_to_no_hourly_bar()
    {
        // The index answer and the close answer have to agree about the stub, or a consumer walking
        // minutes into buckets would put the last thirty in a seventh bar that does not exist.
        DateTimeOffset inStub = SessionBoundaries.At(
            Session, new TimeOnly(15, 47), SessionBoundaries.UsEquities);
        DateTimeOffset inLast = SessionBoundaries.At(
            Session, new TimeOnly(14, 47), SessionBoundaries.UsEquities);
        DateTimeOffset preMarket = SessionBoundaries.At(
            Session, new TimeOnly(8, 15), SessionBoundaries.UsEquities);

        Assert.Null(HourlyGrid.BarIndexOf(inStub, Session, SessionBoundaries.UsEquities));
        Assert.Equal(5, HourlyGrid.BarIndexOf(inLast, Session, SessionBoundaries.UsEquities));
        Assert.Null(HourlyGrid.BarIndexOf(preMarket, Session, SessionBoundaries.UsEquities));
    }
}
