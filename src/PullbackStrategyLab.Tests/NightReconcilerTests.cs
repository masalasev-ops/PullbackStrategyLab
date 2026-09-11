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
/// The next night's reconciliation of a night's slots against <c>run_log</c> and the night's log.
///
/// <b>Every log line here is rendered from <c>tools/nightly.ps1</c>'s own format strings</b>, through
/// <see cref="SlotScriptLines"/>, so a script whose wording moves turns these red rather than leaving
/// them green over a line nothing writes.
///
/// Population: stores these tests build and migrate, and authored nights. Nothing here reads the live
/// store or a real night's log.
/// </summary>
public sealed class NightReconcilerTests : IDisposable
{
    private const string Zone = SessionBoundaries.UsEquities;

    /// <summary>A Monday. 2026-08-24, the golden fixture's own session.</summary>
    private static readonly DateOnly Monday = new(2026, 8, 24);

    /// <summary>Labor Day 2026, the first market holiday the lab ran through.</summary>
    private static readonly DateOnly LaborDay = new(2026, 9, 7);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly PullbackStrategyLabPaths _paths;

    public NightReconcilerTests()
    {
        _paths = new PullbackStrategyLabPaths(_root.Path);
        _connections = new StoreConnectionFactory(_paths);
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// 7.1's done condition, over a store this test built: a weekday on which the guard refused every
    /// slot reconciles to thirty-two refused rows and none missing.
    ///
    /// Thirty-two is stated in advance and derived from RUNBOOK's schedule rather than from the code:
    /// thirty-seven slots, five of them Saturday's, so a Monday fires thirty-two.
    /// </summary>
    [Fact]
    public void A_weekday_the_guard_refused_reconciles_to_thirty_two_refused_rows_and_none_missing()
    {
        NightReconciliation night = Reconciler(Monday.AddDays(1))
            .Reconcile([Monday], _ => RefusedNight(Monday)).Nights.Single();

        Assert.Equal(32, night.Due);
        Assert.Equal(32, night.Written);
        Assert.Equal(0, night.Missing);
        Assert.Equal(0, night.Ran);
        Assert.Equal(32, night.WrittenBecause[DidNotRunBecause.RefusedByTheTreeGuard]);

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyDictionary<string, string> recorded = RunLogger.DidNotRunOn(connection, Monday);

        Assert.Equal(32, recorded.Count);
        Assert.All(recorded.Values, because => Assert.Equal(DidNotRunBecause.RefusedByTheTreeGuard, because));
        Assert.Contains("spread-open", recorded.Keys);
        Assert.Contains("snapshot", recorded.Keys);
    }

    /// <summary>
    /// A night reconciled twice records each fact once, which is what lets every night look back a
    /// week and catch up a night this stage did not run on.
    /// </summary>
    [Fact]
    public void Reconciling_the_same_night_twice_writes_each_slot_once()
    {
        Reconciler(Monday.AddDays(1)).Reconcile([Monday], _ => RefusedNight(Monday));
        NightReconciliation again = Reconciler(Monday.AddDays(2))
            .Reconcile([Monday], _ => RefusedNight(Monday)).Nights.Single();

        Assert.Equal(0, again.Written);
        Assert.Equal(32, again.AlreadyRecorded);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Equal(32, RunLogger.DidNotRunOn(connection, Monday).Count);
    }

    /// <summary>
    /// The parser reads the structured line exactly as the script spells it, and ignores one that
    /// names a different session.
    /// </summary>
    [Fact]
    public void The_structured_line_is_read_as_the_script_writes_it()
    {
        Assert.Equal("  did-not-run slot={0} session={1} because={2}", SlotScriptLines.DidNotRunFormat);

        NightReconciler.NightLog log = NightReconciler.NightLog.Parse(
            [
                SlotScriptLines.DidNotRunLine(Monday, "universe", "17:15", DidNotRunBecause.PausedByTheOperator),
                SlotScriptLines.DidNotRunLine(Monday.AddDays(-1), "bars", "17:30", DidNotRunBecause.PausedByTheOperator),
            ],
            Monday);

        Assert.Equal(DidNotRunBecause.PausedByTheOperator, log.Reasons["universe"]);
        Assert.False(log.Reasons.ContainsKey("bars"));
    }

    /// <summary>
    /// A night from before 7.1 carries only the guard's older wording, which names no slot, and it is
    /// read as a refusal of the slot that started before it. Those nights are the ones the operator
    /// reconciles by naming the date.
    /// </summary>
    [Fact]
    public void A_night_from_before_the_structured_line_is_read_from_the_guard_s_own_wording()
    {
        IEnumerable<string> lines = NightlySchedule.FiresOn(Monday.DayOfWeek).SelectMany(slot => new[]
        {
            SlotScriptLines.StartingLine(Monday, slot.Slot, slot.At),
            SlotScriptLines.RefusingLine(Monday, slot.At, "phase-6-5-researcher-seat"),
        });

        NightReconciliation night = Reconciler(Monday.AddDays(1)).Reconcile([Monday], _ => [.. lines]).Nights.Single();

        Assert.Equal(32, night.WrittenBecause[DidNotRunBecause.RefusedByTheTreeGuard]);
        Assert.Equal(0, night.Missing);
    }

    /// <summary>
    /// Two slots sharing a minute, both refused in the older wording, take a refusal each and never
    /// hand one to a slot that was not refused.
    /// </summary>
    [Fact]
    public void Two_slots_sharing_a_minute_each_take_their_own_refusal()
    {
        NightReconciler.NightLog log = NightReconciler.NightLog.Parse(
            [
                SlotScriptLines.StartingLine(Monday, "universe", "17:15"),
                SlotScriptLines.StartingLine(Monday, "cap", "18:28"),
                SlotScriptLines.StartingLine(Monday, "versions", "18:28"),
                SlotScriptLines.RefusingLine(Monday, "18:28", "a-branch"),
                SlotScriptLines.RefusingLine(Monday, "18:28", "a-branch"),
            ],
            Monday);

        Assert.Equal(DidNotRunBecause.RefusedByTheTreeGuard, log.Reasons["cap"]);
        Assert.Equal(DidNotRunBecause.RefusedByTheTreeGuard, log.Reasons["versions"]);
        Assert.False(log.Reasons.ContainsKey("universe"));
        Assert.Contains("universe", log.Started);
    }

    /// <summary>
    /// A slot that ran is not recorded as not having run, whatever the log says about the rest.
    /// </summary>
    [Fact]
    public void A_slot_whose_stage_ran_gets_no_row()
    {
        RunStage("universe-build", Monday, new TimeOnly(17, 15));

        NightReconciliation night = Reconciler(Monday.AddDays(1))
            .Reconcile([Monday], _ => RefusedNight(Monday)).Nights.Single();

        Assert.Equal(1, night.Ran);
        Assert.Equal(31, night.Written);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.False(RunLogger.DidNotRunOn(connection, Monday).ContainsKey("universe"));
    }

    /// <summary>
    /// A slot that fired and reached nothing is a fault, and the one slot that never writes a run
    /// entry is counted as unseeable instead, because on that slot a clean run looks the same.
    /// </summary>
    [Fact]
    public void A_slot_that_fired_and_reached_nothing_is_a_fault_except_the_one_that_never_writes_a_run()
    {
        NightReconciliation night = Reconciler(Monday.AddDays(1)).Reconcile(
            [Monday],
            _ =>
            [
                SlotScriptLines.StartingLine(Monday, "universe", "17:15"),
                SlotScriptLines.StartingLine(Monday, "snapshot", "22:00"),
            ]).Nights.Single();

        Assert.Equal(1, night.WrittenBecause[DidNotRunBecause.Fault]);
        Assert.Equal(1, night.Unobservable);
        Assert.Equal(30, night.Missing);
        Assert.Equal(MarketDay.NotYetKnown, night.Market);
    }

    /// <summary>
    /// The holiday trap, both halves. A weekday the index history places as closed, because every
    /// tracker holds a later session and none holds it, is market closed. The same weekday before any
    /// later session is stored cannot be told from a lost night, and nothing is written.
    /// </summary>
    [Fact]
    public void A_holiday_is_market_closed_once_the_index_history_places_it_and_unwritten_before()
    {
        SeedIndexBars(new DateOnly(2026, 9, 4));

        NightReconciliation early = Reconciler(LaborDay.AddDays(1)).Reconcile([LaborDay], _ => []).Nights.Single();

        Assert.Equal(MarketDay.NotYetKnown, early.Market);
        Assert.Equal(0, early.Written);
        Assert.Equal(32, early.Missing);

        SeedIndexBars(new DateOnly(2026, 9, 8));

        NightReconciliation placed = Reconciler(LaborDay.AddDays(1).AddDays(1)).Reconcile([LaborDay], _ => []).Nights.Single();

        Assert.Equal(MarketDay.NotHeld, placed.Market);
        Assert.Equal(32, placed.WrittenBecause[DidNotRunBecause.MarketClosed]);
        Assert.Equal(0, placed.Missing);
    }

    /// <summary>
    /// A refusal on a holiday is recorded as the refusal, because that is what happened to the slot.
    /// 2026-09-07 is that night: the guard refused thirty-one slots on Labor Day.
    /// </summary>
    [Fact]
    public void A_refusal_on_a_holiday_is_recorded_as_the_refusal()
    {
        SeedIndexBars(new DateOnly(2026, 9, 4));
        SeedIndexBars(new DateOnly(2026, 9, 8));

        NightReconciliation night = Reconciler(LaborDay.AddDays(2))
            .Reconcile([LaborDay], _ => RefusedNight(LaborDay)).Nights.Single();

        Assert.Equal(MarketDay.NotHeld, night.Market);
        Assert.Equal(32, night.WrittenBecause[DidNotRunBecause.RefusedByTheTreeGuard]);
        Assert.False(night.WrittenBecause.ContainsKey(DidNotRunBecause.MarketClosed));
    }

    /// <summary>
    /// A weekday a tracker holds a bar for was held, so a slot with nothing to say for it stays
    /// missing rather than being written as closed.
    /// </summary>
    [Fact]
    public void A_session_the_index_history_holds_is_never_market_closed()
    {
        SeedIndexBars(Monday);
        SeedIndexBars(Monday.AddDays(1));

        NightReconciliation night = Reconciler(Monday.AddDays(2)).Reconcile([Monday], _ => []).Nights.Single();

        Assert.Equal(MarketDay.Held, night.Market);
        Assert.Equal(0, night.Written);
        Assert.Equal(32, night.Missing);
    }

    /// <summary>
    /// A Saturday fires the five weekly slots and nothing else, and is no session the market could
    /// have held.
    /// </summary>
    [Fact]
    public void A_saturday_reconciles_the_five_weekly_slots_alone()
    {
        DateOnly saturday = new(2026, 9, 12);

        NightReconciliation night = Reconciler(saturday.AddDays(2)).Reconcile(
            [saturday],
            _ => [.. NightlySchedule.FiresOn(DayOfWeek.Saturday).SelectMany(s => SlotScriptLines.Refused(saturday, s.Slot, s.At))])
            .Nights.Single();

        Assert.Equal(5, night.Due);
        Assert.Equal(5, night.WrittenBecause[DidNotRunBecause.RefusedByTheTreeGuard]);
        Assert.Equal(MarketDay.NotASession, night.Market);
    }

    /// <summary>A night that has not finished is refused, because its later slots are still ahead of it.</summary>
    [Fact]
    public void A_session_that_is_not_before_today_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Reconciler(Monday).Reconcile([Monday], _ => []));
    }

    /// <summary>
    /// The readers that bound on the instant leave a did-not-run row out. Each is written the next
    /// night, so without the exclusion the next night would read the stage that wrote them as having
    /// ended other than cleanly and mark its own setups degraded for a night that is not its own.
    /// </summary>
    [Fact]
    public void The_readers_bounded_on_the_instant_leave_a_did_not_run_row_out()
    {
        DateOnly next = Monday.AddDays(1);
        Reconciler(next).Reconcile([Monday], _ => RefusedNight(Monday));

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(RunLogger.IncompleteStagesOf(connection, next, Zone));
        Assert.Null(RunLogger.DegradedBecause(connection, next, Zone));

        Data.StageRun own = Assert.Single(RunLogger.StagesOn(connection, next, Zone));
        Assert.Equal(NightReconciler.Name, own.Stage);
        Assert.Equal("clean", own.Outcome);
    }

    /// <summary>The store refuses a reason outside the four, and so does the one writer.</summary>
    [Fact]
    public void A_reason_outside_the_four_is_refused_by_the_writer()
    {
        RunLogger logger = Logger(Monday.AddDays(1));
        using SqliteConnection connection = _connections.OpenWrite();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            logger.RecordDidNotRun(connection, NightReconciler.Name, "universe", Monday, "machine-off"));
        Assert.Throws<ArgumentOutOfRangeException>(() => DidNotRunBecause.Reads("machine-off"));
    }

    private NightReconciler Reconciler(DateOnly today) =>
        new(_connections, _paths, Logger(today), Clock(today), Options.Create(new PullbackStrategyLabOptions()));

    private static FixedClock Clock(DateOnly today) =>
        new(new DateTimeOffset(today.Year, today.Month, today.Day, 22, 0, 0, TimeSpan.Zero));

    private static RunLogger Logger(DateOnly today) =>
        new(Clock(today), Options.Create(new PullbackStrategyLabOptions()));

    private static IReadOnlyList<string> RefusedNight(DateOnly session) =>
        [.. NightlySchedule.FiresOn(session.DayOfWeek).SelectMany(s => SlotScriptLines.Refused(session, s.Slot, s.At))];

    private void RunStage(string stage, DateOnly session, TimeOnly at)
    {
        var clock = new FixedClock(SessionBoundaries.At(session, at, Zone));
        var logger = new RunLogger(clock, Options.Create(new PullbackStrategyLabOptions()));

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = logger.Begin(connection, stage);
        run.Complete(RunOutcome.Clean);
    }

    private void SeedIndexBars(DateOnly barDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        foreach (string symbol in new PullbackStrategyLabOptions().IndexSymbols)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO index_bar (symbol, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@symbol, @bar_date, '1', '1', '1', '1', '1', 1, @observed_at);
                """;
            command.Parameters.AddWithValue("@symbol", symbol);
            command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
            command.Parameters.AddWithValue(
                "@observed_at", StoreText.TimestampToStorageText(SessionBoundaries.At(barDate, new TimeOnly(17, 50), Zone)));
            command.ExecuteNonQuery();
        }
    }
}
