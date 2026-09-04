using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Which of a session's slots ran, read from the store the night wrote.
///
/// <b>This is the instrument the 5.2 obligation named, and it is not a check.</b> Four lists declare
/// the slots and <c>slot-roster</c> reconciles them in every direction, and all four agreed while
/// fifteen of the thirty-two had never fired once: whether a scheduled task exists is a property of
/// the machine, and every check in this corpus takes its subject from the source, the documents, the
/// fixture or a store it builds itself. What that cost is on the record, four flagged nights of
/// minute bars nobody can buy back.
///
/// <b>The cases here are authored and the population is a store this test wrote.</b> Nothing here
/// reads the live store, which would make the result depend on last night. What is asserted is that
/// the read tells the four states apart, and the live store is where a person reads the answer.
/// </summary>
public sealed class LabNightTests : IDisposable
{
    private const string Zone = "America/New_York";

    /// <summary>An ordinary weekday, so the weekly ceiling slot is not among the slots due.</summary>
    private static readonly DateOnly Weekday = new(2026, 9, 3);

    /// <summary>A Saturday, which is the one day the ceiling slot is due.</summary>
    private static readonly DateOnly Saturday = new(2026, 9, 5);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public LabNightTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A night on which nothing ran reports every slot as never having fired, and names the ones
    /// whose input cannot be bought back.
    ///
    /// <b>This is the night of 2026-09-03 in miniature.</b> Fifteen slots had never fired and the
    /// four lists that declare them all agreed, so nothing said so; this report says so on the
    /// morning screen.
    ///
    /// Population: a migrated store with an empty run log, read for a weekday.
    /// </summary>
    [Fact]
    public void A_night_on_which_nothing_ran_names_every_slot_that_never_fired()
    {
        NightResponseOfSlots night = LabNight.Read(_connections, Weekday, Zone);

        Assert.Equal(0, night.Ran);
        Assert.Equal(0, night.NotClean);
        Assert.Equal(1, night.Unobservable);
        Assert.Equal(night.Slots.Count - 1, night.NeverRan);

        SlotResponse intraday = night.Slots.Single(s => s.Slot == "intraday");
        Assert.True(intraday.NeverRan);
        Assert.Equal("20:30", intraday.At);
        Assert.Equal(["intraday-bars"], intraday.Stages.Select(s => s.Stage));
    }

    /// <summary>
    /// A stage that ran cleanly and one that did not are two states, and a stage still running is a
    /// third.
    ///
    /// <b>The third is why the report reads `started_at` rather than `ended_at`.</b> A stage that
    /// began and never finished is a different morning from one that never began, and a predicate on
    /// the end alone folds them together on the one morning the distinction decides what to rerun.
    ///
    /// Population: three authored run entries on one weekday, one clean, one failed and one open.
    /// </summary>
    [Fact]
    public void A_clean_slot_a_failed_slot_and_a_slot_still_running_are_three_states()
    {
        SeedRun("universe-build", Weekday, "17:15", ended: "17:18", outcome: "clean");
        SeedRun("sectors", Weekday, "18:12", ended: "18:14", outcome: "failed");
        SeedRun("intraday-bars", Weekday, "20:30", ended: null, outcome: null);

        NightResponseOfSlots night = LabNight.Read(_connections, Weekday, Zone);

        Assert.True(night.Slots.Single(s => s.Slot == "universe").Ran);
        Assert.True(night.Slots.Single(s => s.Slot == "sectors").NotClean);

        SlotResponse intraday = night.Slots.Single(s => s.Slot == "intraday");
        Assert.False(intraday.NeverRan);
        Assert.True(intraday.NotClean);
        Assert.Null(intraday.Stages.Single().EndedAt);
    }

    /// <summary>
    /// A slot running two stages is clean only when both are, because the second reads what the
    /// first wrote.
    ///
    /// Population: one authored weekday with `scans` clean and `tiers` never run.
    /// </summary>
    [Fact]
    public void A_slot_of_two_stages_is_not_clean_when_only_one_of_them_ran()
    {
        SeedRun("scans", Weekday, "18:10", ended: "18:11", outcome: "clean");

        SlotResponse scans = LabNight.Read(_connections, Weekday, Zone).Slots.Single(s => s.Slot == "scans");

        Assert.False(scans.Ran);
        Assert.False(scans.NeverRan);
        Assert.True(scans.NotClean);
    }

    /// <summary>
    /// The slot the store cannot see is reported as its own state rather than folded into either of
    /// the other two.
    ///
    /// <b>Folding it in is the under-reporting shape, one level up from a check.</b>
    /// <c>snapshot-db</c> takes no run logger, so a report that dropped it would say thirty-one
    /// under a heading meaning thirty-two and would read as complete.
    ///
    /// Population: a migrated store with an empty run log, read for a weekday.
    /// </summary>
    [Fact]
    public void The_slot_that_leaves_no_run_entry_is_reported_rather_than_dropped()
    {
        NightResponseOfSlots night = LabNight.Read(_connections, Weekday, Zone);

        SlotResponse snapshot = night.Slots.Single(s => s.Slot == "snapshot");

        Assert.True(snapshot.IsUnobservable);
        Assert.False(snapshot.Ran);
        Assert.False(snapshot.NeverRan);
        Assert.False(snapshot.NotClean);
        Assert.Contains("run_log", snapshot.Unobservable!, StringComparison.Ordinal);

        // The four counts cover the whole list exactly once, which is what makes the fourth a
        // reported state rather than a rounding.
        Assert.Equal(
            night.Slots.Count,
            night.Ran + night.NeverRan + night.NotClean + night.Unobservable);
    }

    /// <summary>
    /// The weekly slot is due on its own day and on no other.
    ///
    /// <b>A report taking the whole list every day would name a missing slot on every weekday the
    /// lab has ever run.</b> That is a false alarm nightly, and a guard that cries wolf nightly is
    /// one nobody reads by the second week, which is how a suppressed guard becomes a dead one.
    ///
    /// Population: the declared schedule, read for a Thursday and for a Saturday.
    /// </summary>
    [Fact]
    public void The_weekly_ceiling_slot_is_due_on_saturday_and_on_no_other_day()
    {
        Assert.DoesNotContain(
            LabNight.Read(_connections, Weekday, Zone).Slots, s => s.Slot == "ceiling");

        Assert.Contains(
            LabNight.Read(_connections, Saturday, Zone).Slots, s => s.Slot == "ceiling");

        Assert.Equal(
            NightlySchedule.Slots.Count,
            LabNight.Read(_connections, Saturday, Zone).Slots.Count);
    }

    /// <summary>
    /// A stage that ran and belongs to no slot of the session is reported rather than dropped,
    /// because a stage run by hand is how a night gets repaired.
    ///
    /// Population: one authored weekday with `ceiling` run by hand, which is a Saturday slot.
    /// </summary>
    [Fact]
    public void A_stage_run_by_hand_outside_the_schedule_is_reported()
    {
        SeedRun("ceiling", Weekday, "09:00", ended: "09:01", outcome: "clean");

        Assert.Equal(["ceiling"], LabNight.Read(_connections, Weekday, Zone).Unscheduled);
    }

    /// <summary>
    /// A run of a different session is not this session's, which is what the day bound is for.
    ///
    /// Population: one authored run on 2026-09-02, read for 2026-09-03.
    /// </summary>
    [Fact]
    public void A_run_of_another_session_does_not_count_as_this_ones()
    {
        SeedRun("universe-build", Weekday.AddDays(-1), "17:15", ended: "17:18", outcome: "clean");

        Assert.True(LabNight.Read(_connections, Weekday, Zone).Slots.Single(s => s.Slot == "universe").NeverRan);
        Assert.True(LabNight.Read(_connections, Weekday.AddDays(-1), Zone).Slots.Single(s => s.Slot == "universe").Ran);
    }

    /// <summary>
    /// A stage rerun after a failure is a stage that ran, so the report reads the last run of it
    /// that session rather than the first.
    ///
    /// The runbook's answer to a failed stage is to rerun it for its own date, and a report still
    /// showing the failure afterwards would send an operator round the same loop.
    ///
    /// Population: two authored runs of one stage on one weekday, the first failed and the second
    /// clean.
    /// </summary>
    [Fact]
    public void A_stage_rerun_after_a_failure_reads_as_the_rerun()
    {
        SeedRun("sectors", Weekday, "18:12", ended: "18:14", outcome: "failed");
        SeedRun("sectors", Weekday, "19:30", ended: "19:32", outcome: "clean");

        Assert.True(LabNight.Read(_connections, Weekday, Zone).Slots.Single(s => s.Slot == "sectors").Ran);
    }

    /// <summary>
    /// A slot that runs its stage twice at the same instant is one row and not a crash.
    ///
    /// <b>This is a defect the fixture found rather than a case somebody imagined.</b> The read took
    /// the latest run of each stage through a correlated subquery on the start instant, which returns
    /// two rows where a stage ran twice at that instant. The `sectors` slot runs its stage twice by
    /// design and a fixed clock gives both passes the same timestamp, so the whole read threw on a
    /// duplicate key and the morning report answered nothing at all about any slot.
    ///
    /// Population: two authored runs of one stage, on one weekday, at one instant.
    /// </summary>
    [Fact]
    public void A_stage_that_ran_twice_at_the_same_instant_is_one_row()
    {
        SeedRun("sectors", Weekday, "18:12", ended: "18:13", outcome: "failed");
        SeedRun("sectors", Weekday, "18:12", ended: "18:14", outcome: "clean");

        NightResponseOfSlots night = LabNight.Read(_connections, Weekday, Zone);

        Assert.Single(night.Slots.Single(s => s.Slot == "sectors").Stages);
    }

    private void SeedRun(string stage, DateOnly session, string startedAt, string? ended, string? outcome)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, rows_written, calls_used, counts_against_ceiling)
            VALUES (@run_id, @stage, @started_at, @ended_at, @outcome, 0, 0, 1);
            """;

        // The instants are the session's own day in the trading zone, which is the window the read
        // bounds on. Written through the same boundary helper the reader uses, so a test cannot pass
        // by agreeing with itself about what a day is.
        DateTimeOffset started = SessionBoundaries.At(
            session, TimeOnly.ParseExact(startedAt, "HH:mm"), Zone);

        // Unique per call rather than per instant, because two runs of one stage at one instant is
        // exactly the case below and a key built from the instant could not express it.
        command.Parameters.AddWithValue(
            "@run_id", $"{stage}-{session:yyyyMMdd}-{startedAt}-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue("@started_at", StoreText.TimestampToStorageText(started));
        command.Parameters.AddWithValue(
            "@ended_at",
            ended is null
                ? DBNull.Value
                : StoreText.TimestampToStorageText(
                    SessionBoundaries.At(session, TimeOnly.ParseExact(ended, "HH:mm"), Zone)));
        command.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
