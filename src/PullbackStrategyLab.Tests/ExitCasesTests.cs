using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A long and a short position each walked to its exit by the shipped stages, from 7.10, over
/// <see cref="ExitCases"/>' authored sessions and hand-derived figures. Long and short are asserted
/// apart and never added.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class ExitCasesTests
{
    /// <summary>
    /// The long trims at 3R on the first session and 5R on the second, arms its trail on the third
    /// close and sells the rest at the fourth session's open.
    /// </summary>
    [Fact]
    public void The_long_trims_twice_and_exits_on_the_trail_at_the_next_open()
    {
        using var cases = new ExitCases();
        cases.Run();

        StoredFill[] fills = [.. cases.Exits(ExitCases.Long)];
        Assert.Equal(["trim", "trim", "exit"], fills.Select(f => f.Leg));
        Assert.Equal([115.40m, 125.60m, 112m], fills.Select(f => f.RestingPrice));
        Assert.Equal([115.2846m, 125.4744m, 111.888m], fills.Select(f => f.Price));
        Assert.Equal([22, 22, 106], fills.Select(f => f.Shares));
        Assert.Equal([ExitCases.Sessions[0], ExitCases.Sessions[1], ExitCases.Sessions[3]], fills.Select(f => f.SessionDate));

        StoredPosition closed = cases.Position(ExitCases.Long, ExitCases.Sessions[3]);
        Assert.Equal(ExitReason.Trail, closed.ExitReason);
        Assert.Equal(ExitCases.Sessions[2], closed.ExitArmedSession);
        Assert.Equal(2141.826m, closed.RealisedPnl);
    }

    /// <summary>
    /// The short trims at 3R on the first session, reaches nothing on the second, trims at 5R on the
    /// third, which is its third session held, and buys the rest at the fourth session's open.
    /// </summary>
    [Fact]
    public void The_short_trims_twice_and_exits_on_the_hold_limit_at_the_next_open()
    {
        using var cases = new ExitCases();
        IReadOnlyList<ManageRunResult> nights = cases.Run();

        StoredFill[] fills = [.. cases.Exits(ExitCases.Short)];
        Assert.Equal(["trim", "trim", "exit"], fills.Select(f => f.Leg));
        Assert.Equal([42.30m, 37.20m, 37.5m], fills.Select(f => f.RestingPrice));
        Assert.Equal([42.3423m, 37.2372m, 37.5375m], fills.Select(f => f.Price));
        Assert.Equal([45, 45, 210], fills.Select(f => f.Shares));
        Assert.Equal([ExitCases.Sessions[0], ExitCases.Sessions[2], ExitCases.Sessions[3]], fills.Select(f => f.SessionDate));

        StoredPosition closed = cases.Position(ExitCases.Short, ExitCases.Sessions[3]);
        Assert.Equal(ExitReason.HoldLimit, closed.ExitReason);
        Assert.Equal(ExitCases.Sessions[2], closed.ExitArmedSession);
        Assert.Equal(3521.0475m, closed.RealisedPnl);

        Assert.Equal([2, 1, 1, 0], nights.Select(n => n.Trimmed));
        Assert.Equal([0, 0, 2, 0], nights.Select(n => n.ExitsArmed));
        Assert.Equal(1, nights[3].ClosedTrail);
        Assert.Equal(1, nights[3].ClosedHoldLimit);
    }

    /// <summary>The short as at its second session has taken one trim, and the second is not yet visible.</summary>
    [Fact]
    public void The_short_as_at_its_second_session_reads_one_trim()
    {
        using var cases = new ExitCases();
        cases.Run();

        StoredPosition between = cases.Position(ExitCases.Short, ExitCases.Sessions[1]);
        Assert.Equal(1, between.Trims);
        Assert.Equal(45, between.TrimmedShares);
        Assert.Equal(PositionStatus.Open, between.Status);
    }
}
