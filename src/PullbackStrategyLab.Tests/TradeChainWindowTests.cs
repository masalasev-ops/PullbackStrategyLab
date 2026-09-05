using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The repair window the trade chain has, and what it does past the edge of it.
///
/// <b>The chain has a hard edge at local midnight and had no answer for what lies past it.</b>
/// Every reader in it is pinned at <c>observed_at &lt;= EndOfSession(sessionDate)</c>, so a rerun
/// after 23:59:59.999 of the session's own day in the trading zone writes rows the next stage can
/// never see. `RiskGate` then recorded <c>clean</c> with "no plan resting in this session was
/// touched", which is a clean run over a read it could not make rather than a refusal.
///
/// <b>Authored, because nothing has ever happened here.</b> `trade_plan` holds nought rows and has
/// on every night the lab has run, so no instance of this fault is claimed and none exists. The
/// mechanism is armed for the first night that has a plan, and these are the cases that say what it
/// does when it fires.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
/// </summary>
public sealed class TradeChainWindowTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 8, 26);

    /// <summary>The last instant a stage may run for this session, which is what the readers bound on.</summary>
    private static readonly DateTimeOffset Edge =
        SessionBoundaries.EndOfSession(Session, SessionBoundaries.UsEquities);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public TradeChainWindowTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        using SqliteConnection seed = _connections.OpenWrite();
        TestVersions.SeedBaseline(seed);
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options_ =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    /// <summary>
    /// The window itself, at the instant it closes and one millisecond after.
    ///
    /// The boundary is inclusive because the readers' own bound is: a row observed at exactly
    /// <c>EndOfSession</c> is visible, so a stage running at exactly that instant can still be read
    /// by the one after it. An exclusive edge here would refuse a run the chain would have honoured.
    /// </summary>
    [Fact]
    public void The_window_closes_at_local_midnight_of_the_sessions_own_day_and_not_a_millisecond_before()
    {
        Assert.Null(TradeChainWindow.Closed(Edge, Session, SessionBoundaries.UsEquities));
        Assert.Null(TradeChainWindow.Closed(Edge.AddHours(-6), Session, SessionBoundaries.UsEquities));

        string? closed = TradeChainWindow.Closed(Edge.AddMilliseconds(1), Session, SessionBoundaries.UsEquities);

        Assert.NotNull(closed);
        Assert.Contains("repair window", closed, StringComparison.Ordinal);
        Assert.Contains("2026-08-26", closed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The edge moves with the clock change, because it is local midnight and not a fixed offset.
    ///
    /// The same property <c>EndOfSession</c> carries, asserted here because this is the first thing
    /// to turn a refusal on it. A fixed UTC edge would refuse an hour early or an hour late on the
    /// two nights of the year the zone moves, and both are a session that was still open.
    /// </summary>
    [Fact]
    public void The_edge_is_local_midnight_in_the_trading_zone_on_both_sides_of_the_clock_change()
    {
        var daylight = new DateOnly(2026, 8, 26);
        var standard = new DateOnly(2026, 12, 16);

        Assert.Equal(
            SessionBoundaries.EndOfSession(daylight, SessionBoundaries.UsEquities),
            LastOpenInstant(daylight));
        Assert.Equal(
            SessionBoundaries.EndOfSession(standard, SessionBoundaries.UsEquities),
            LastOpenInstant(standard));

        // And they are different clock readings in UTC, which is the whole reason the edge is
        // resolved through the zone rather than written down as an offset.
        Assert.NotEqual(
            SessionBoundaries.EndOfSession(daylight, SessionBoundaries.UsEquities).TimeOfDay,
            SessionBoundaries.EndOfSession(standard, SessionBoundaries.UsEquities).TimeOfDay);
    }

    /// <summary>The last instant at which the window is open for a session, found rather than assumed.</summary>
    private static DateTimeOffset LastOpenInstant(DateOnly session)
    {
        DateTimeOffset edge = SessionBoundaries.EndOfSession(session, SessionBoundaries.UsEquities);

        Assert.Null(TradeChainWindow.Closed(edge, session, SessionBoundaries.UsEquities));
        Assert.NotNull(TradeChainWindow.Closed(edge.AddMilliseconds(1), session, SessionBoundaries.UsEquities));

        return edge;
    }

    /// <summary>
    /// Each of the three trade-chain stages refuses past the edge, and the refusal is recorded as a
    /// failed run rather than thrown.
    ///
    /// <b>Recorded rather than thrown is the half that matters.</b> An exception would reach the
    /// operator's console and nothing else; a failed run reaches `run_log`, so the night's row says
    /// the slot did not end cleanly and the morning screen puts the slot in the ragged bucket. The
    /// fault being repaired is that a run said clean over a read it could not make, and a refusal
    /// nobody can see would be the same silence with a different cause.
    ///
    /// All three rather than the one the row was raised against, because the bound is the same
    /// bound in all three and a repair to the loudest of them is the shape 3.14 already took once.
    /// </summary>
    [Fact]
    public void Every_trade_chain_stage_refuses_past_the_edge_and_records_the_refusal()
    {
        var past = new FixedClock(Edge.AddMinutes(1));

        TriggerRunResult resolved = new TriggerResolver(_connections, new RunLogger(past, Options_), past, Options_)
            .Resolve(Session);
        OrderRunResult gated = new RiskGate(_connections, new RunLogger(past, Options_), past, Options_)
            .Apply(Session);
        FillRunResult filled = new PaperBroker(_connections, new RunLogger(past, Options_), past, Options_)
            .Fill(Session);

        foreach ((string stage, RunOutcome outcome, string? because) in new[]
        {
            (TriggerResolver.Name, resolved.Outcome, resolved.StoppedBecause),
            (RiskGate.Name, gated.Outcome, gated.StoppedBecause),
            (PaperBroker.Name, filled.Outcome, filled.StoppedBecause),
        })
        {
            Assert.True(outcome == RunOutcome.Failed, $"{stage} did not refuse, it reported {outcome}.");
            Assert.NotNull(because);
            Assert.Contains("repair window", because, StringComparison.Ordinal);

            // And it does not report the ordinary quiet answer, which is the sentence that was
            // false: "no plan was live in this session" over a read that could not have seen one.
            Assert.DoesNotContain(TriggerResolver.NoPlansResting, because, StringComparison.Ordinal);
            Assert.DoesNotContain(RiskGate.NoTriggers, because, StringComparison.Ordinal);
            Assert.DoesNotContain(PaperBroker.NothingToFill, because, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Inside the window the three stages run and report their ordinary quiet answers.
    ///
    /// The other side of the guard, and the one that stops it being a stage that never runs. A
    /// refusal that fired on every invocation would pass the test above and take the chain out
    /// entirely, which is the shape a guard asserted only in the direction it fires can take.
    /// </summary>
    [Fact]
    public void Inside_the_window_the_three_stages_run_and_report_the_ordinary_quiet_answer()
    {
        var inside = new FixedClock(SessionBoundaries.At(Session, new TimeOnly(21, 10), SessionBoundaries.UsEquities));

        TriggerRunResult resolved = new TriggerResolver(_connections, new RunLogger(inside, Options_), inside, Options_)
            .Resolve(Session);
        OrderRunResult gated = new RiskGate(_connections, new RunLogger(inside, Options_), inside, Options_)
            .Apply(Session);
        FillRunResult filled = new PaperBroker(_connections, new RunLogger(inside, Options_), inside, Options_)
            .Fill(Session);

        Assert.Equal(RunOutcome.Clean, resolved.Outcome);
        Assert.Equal(TriggerResolver.NoPlansResting, resolved.StoppedBecause);

        Assert.Equal(RunOutcome.Clean, gated.Outcome);
        Assert.Equal(RiskGate.NoTriggers, gated.StoppedBecause);

        Assert.Equal(RunOutcome.Clean, filled.Outcome);
        Assert.Equal(PaperBroker.NothingToFill, filled.StoppedBecause);
    }

    /// <summary>
    /// The refusal reaches the run log, which is what the morning screen and the night's row read.
    ///
    /// A refusal the store does not carry is a refusal only the console saw, and a console is not
    /// something this lab keeps.
    /// </summary>
    [Fact]
    public void The_refusal_is_in_the_run_log_so_the_night_reads_as_not_clean()
    {
        var past = new FixedClock(Edge.AddMinutes(1));

        new RiskGate(_connections, new RunLogger(past, Options_), past, Options_).Apply(Session);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT outcome FROM run_log WHERE stage = @stage ORDER BY started_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("@stage", RiskGate.Name);

        Assert.Equal("failed", command.ExecuteScalar() as string);
    }
}
