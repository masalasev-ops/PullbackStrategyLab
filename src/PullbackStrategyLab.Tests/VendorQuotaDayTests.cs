using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The vendor's quota day and the lab's session night, which are two quantities that shared one
/// expression until 4.3.
///
/// <b>What these tests carry is that the two windows have different edges</b>, and that each read
/// answers on its own. The phase 3 sign-off found <c>substr(started_at, 1, 10)</c> being used for a
/// session night, where it truncates the evening at 20:00 Eastern; 3.12 repaired that read and left
/// a correct use of an identical expression a few lines away, which no guard could tell apart. The
/// obligation was repointed to 4.3 because a second intraday job starts spending against the quota
/// day, and it is discharged by naming the quantity rather than by pattern-matching the syntax.
///
/// The two spends below are the whole of it: one inside the session and one after the UTC date has
/// rolled, on the same evening, landing in the days each belongs to.
/// </summary>
public sealed class VendorQuotaDayTests : IDisposable
{
    /// <summary>The session, and an instant inside it while the market is open.</summary>
    private static readonly DateOnly Session = new(2026, 8, 27);

    /// <summary>
    /// 15:45 Eastern on the session, which is 19:45 UTC on the same date. Inside the session and
    /// inside the quota day that shares its number.
    /// </summary>
    private static readonly DateTimeOffset InsideTheSession =
        SessionBoundaries.At(Session, new TimeOnly(15, 45), SessionBoundaries.UsEquities);

    /// <summary>
    /// 21:50 Eastern on the same session, which is 01:50 UTC on the <b>following</b> date. Still
    /// inside the lab's night, and in the next quota day. This is the instant the scoreboard was
    /// measured at on 2026-08-28, and it is the one that separates the two quantities.
    /// </summary>
    private static readonly DateTimeOffset AfterTheUtcDateRolls =
        SessionBoundaries.At(Session, new TimeOnly(21, 50), SessionBoundaries.UsEquities);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public VendorQuotaDayTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private RunLogger Logger(FixedClock clock) => new(
        clock,
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path, DailyCallCeiling = 5000 }));

    private void Spend(SqliteConnection connection, DateTimeOffset at, int calls)
    {
        var clock = new FixedClock(at);
        using RunScope run = Logger(clock).Begin(connection, "spreads");

        for (int call = 0; call < calls; call++)
        {
            run.CountCall();
        }

        run.Complete(RunOutcome.Clean);
    }

    [Fact]
    public void The_two_spends_of_one_evening_land_in_the_quota_days_they_belong_to()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Spend(connection, InsideTheSession, calls: 60);
        Spend(connection, AfterTheUtcDateRolls, calls: 7);

        // Two spends, one evening, two quota days. The second is not a mistake and the ceiling is
        // right to see it that way: the vendor's allowance reset between them.
        Assert.Equal(
            60,
            RunLogger.CallsUsedOn(connection, VendorQuotaDay.Containing(InsideTheSession)));
        Assert.Equal(
            7,
            RunLogger.CallsUsedOn(connection, VendorQuotaDay.Containing(AfterTheUtcDateRolls)));

        Assert.NotEqual(
            VendorQuotaDay.Containing(InsideTheSession),
            VendorQuotaDay.Containing(AfterTheUtcDateRolls));
    }

    [Fact]
    public void Both_spends_are_inside_the_one_session_the_lab_calls_that_night()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Spend(connection, InsideTheSession, calls: 60);
        Spend(connection, AfterTheUtcDateRolls, calls: 7);

        // The other half, and it is what makes the first test mean something. Both runs are inside
        // the session of the 27th, which the session-bounded read sees and the quota-day read splits.
        // Two correct answers to two different questions about one column.
        Assert.Empty(RunLogger.IncompleteStagesOf(connection, Session, SessionBoundaries.UsEquities));

        Spend(connection, AfterTheUtcDateRolls, calls: 0);
        using (RunScope failing = Logger(new FixedClock(AfterTheUtcDateRolls)).Begin(connection, "scoreboard"))
        {
            failing.Complete(RunOutcome.Partial);
        }

        Assert.Equal(
            ["scoreboard"],
            RunLogger.IncompleteStagesOf(connection, Session, SessionBoundaries.UsEquities));
    }

    [Fact]
    public void A_quota_day_is_bounded_at_utc_midnight_at_both_ends()
    {
        VendorQuotaDay day = VendorQuotaDay.OfUtcDate(new DateOnly(2026, 8, 28));

        Assert.Equal(new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero), day.Start);
        Assert.Equal(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero), day.End);

        Assert.True(day.Contains(day.Start));
        Assert.True(day.Contains(day.End.AddMilliseconds(-1)));

        // Exclusive at the top, unlike a session bound, so the two windows abut with nothing between
        // them and a stamp carrying more precision than the store's milliseconds cannot fall in a gap.
        Assert.False(day.Contains(day.End));
    }

    // The guard the obligation said becomes possible once the quantity is named lives in
    // `point-in-time` rather than here, as the scan "the run log's stamp is never truncated to a
    // date", backed by the first test above. A scan in a plain test file is a scan no check reports
    // the coverage of, which is the shape `coverage-reported` exists to list.
}
