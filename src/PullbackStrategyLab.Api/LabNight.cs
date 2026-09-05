using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// Which of a session's slots ran, read from the store the night wrote rather than from the four
/// lists that declare them.
///
/// <b>The seventh failure shape, and this is the instrument the obligation named rather than
/// another check.</b> <c>slot-roster</c> reconciles the dispatcher's slot table, its own parameter
/// set, the worker's advertised stages and RUNBOOK's schedule in every direction, and all four
/// agreed while fifteen of the thirty-two slots had never run once. Whether a scheduled task exists
/// is a property of the machine, and every check in this corpus takes its subject from the source,
/// the documents, the fixture or a store it builds itself. A green report is a statement about the
/// build and never about the lab.
///
/// <b>So this is not a check and nothing in the verification harness calls it.</b> It is a figure a
/// person reads on the morning it happens, on the terms CLAUDE.md sets for a property about the
/// running system, and it belongs beside the morning queue because that is the screen somebody opens
/// the morning after. What it cost to learn: four flagged nights whose minute bars cannot be bought
/// back at any price, the two spread passes never taken at all, and the lab flagging nothing on the
/// night four stages died on a missing column.
///
/// <b>It reports the slot it cannot see rather than shortening its list.</b> <c>snapshot-db</c>
/// takes no run logger and leaves no run entry, so this has no answer about it, and a report of
/// thirty-one slots under a heading saying thirty-two is the under-reporting shape one level up.
/// </summary>
public static class LabNight
{
    public static NightResponseOfSlots Read(
        StoreConnectionFactory connections, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);

        string date = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!connections.StoreExists)
        {
            return NightResponseOfSlots.Empty(date, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        // The last run of each stage inside the session's own day, which is where every slot of a
        // session lands: the two spread passes fire inside it and the rest after the close.
        Dictionary<string, StageRun> ran = RunLogger
            .StagesOn(connection, asOf, sessionZone)
            .ToDictionary(r => r.Stage, StringComparer.Ordinal);

        var slots = new List<SlotResponse>();

        // The slots that session was due, which on any day but Saturday is the list less the weekly
        // ceiling recomputation. Taking the whole list every day would report a missing slot on
        // every weekday the lab has ever run, and a report that cries wolf nightly is one nobody
        // reads by the second week.
        foreach (NightSlot slot in NightlySchedule.Due(asOf.DayOfWeek))
        {
            IReadOnlyList<StageResponse> stages =
                [.. slot.Stages.Select(stage => Stage(stage, slot, ran))];

            slots.Add(new SlotResponse(
                slot.Slot,
                slot.At,
                slot.InsideTheSession,
                slot.LeavesNoRunEntry,
                stages,
                Bought(connection, slot, asOf, sessionZone)));
        }

        // Every stage that ran and belongs to no slot of this session. Counted rather than dropped:
        // a stage run by hand is how a night gets repaired, and a report that showed only the
        // declared list would say nothing about the repair.
        IReadOnlyList<string> unscheduled =
            [.. ran.Keys
                .Where(stage => !NightlySchedule.Due(asOf.DayOfWeek).Any(s => s.Stages.Contains(stage, StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal)];

        return new NightResponseOfSlots(
            date,
            null,
            slots,
            slots.Count(s => s.Ran),
            slots.Count(s => s.NeverRan),
            slots.Count(s => s.NotClean),
            slots.Count(s => s.IsUnobservable),
            unscheduled);
    }

    /// <summary>
    /// What the one unrecoverable buy of the night actually bought, or null for every other slot.
    ///
    /// <b>The row existed and nothing read it.</b> `intraday_fetch` recorded 92 names asked, 92
    /// answered with nothing and 0 bars written on the night of 2026-09-04, and
    /// <c>IntradayBarReader.LatestFetch</c> was the only reader of that table and had no caller
    /// outside the suite. So the honest counts were written down every night and reached nobody,
    /// which is the sixth failure shape from the other end: not an answer a surface dropped, but an
    /// answer no surface ever asked for.
    ///
    /// It is the intraday slot alone rather than a figure per slot. Every other stage's outcome
    /// says what it did; this is the one whose "clean" was compatible with having bought nothing,
    /// and it is the one whose miss cannot be bought back at any price.
    /// </summary>
    private static FetchResponse? Bought(
        SqliteConnection connection, NightSlot slot, DateOnly asOf, string sessionZone)
    {
        if (!string.Equals(slot.Slot, IntradaySlot, StringComparison.Ordinal))
        {
            return null;
        }

        StoredIntradayFetch? fetch = IntradayBarReader.LatestFetch(connection, asOf, asOf, sessionZone);

        return fetch is null
            ? null
            : new FetchResponse(
                fetch.Requested,
                fetch.Fetched,
                fetch.Empty,
                fetch.BarsWritten,
                fetch.Stored,
                fetch.WindowSessions,
                ScanSpans.AnchorWindowSessions);
    }

    /// <summary>
    /// The slot whose buy is unrecoverable, named once here rather than spelled at each use.
    ///
    /// It matches <c>NightlySchedule</c>'s own slot name, which <c>slot-roster</c> reconciles
    /// against the dispatcher, the worker's advertised stages and RUNBOOK in every direction.
    /// </summary>
    public const string IntradaySlot = "intraday";

    private static StageResponse Stage(
        string stage, NightSlot slot, IReadOnlyDictionary<string, StageRun> ran)
    {
        if (slot.LeavesNoRunEntry is not null)
        {
            return new StageResponse(stage, null, null, null, 0, slot.LeavesNoRunEntry);
        }

        if (!ran.TryGetValue(stage, out StageRun? run))
        {
            return new StageResponse(stage, null, null, null, 0, null);
        }

        return new StageResponse(
            stage,
            run.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),

            // Null on a stage that began and has not finished, which is a state and not a missing
            // value. A report folding that into "did not run" would be wrong on the one morning the
            // distinction decides what to rerun.
            run.EndedAt?.ToString("HH:mm", CultureInfo.InvariantCulture),
            run.Outcome,
            run.CallsUsed,
            null);
    }
}

/// <summary>
/// What one session's slots did, as the read surface answers it.
///
/// The four counts are separate because they are four different mornings. Every slot ran cleanly;
/// a slot never fired; a slot fired and did not end cleanly; and a slot nothing here can see. Adding
/// the last into either of the others is the shape this whole report exists to refuse.
/// </summary>
public sealed record NightResponseOfSlots(
    string AsOf,
    string? Absent,
    IReadOnlyList<SlotResponse> Slots,
    int Ran,
    int NeverRan,
    int NotClean,
    int Unobservable,
    IReadOnlyList<string> Unscheduled)
{
    public static NightResponseOfSlots Empty(string asOf, string why) =>
        new(asOf, why, [], 0, 0, 0, 0, []);
}

/// <summary>One slot of the night, with each stage it runs.</summary>
public sealed record SlotResponse(
    string Slot,
    string At,
    bool InsideTheSession,
    string? Unobservable,
    IReadOnlyList<StageResponse> Stages,
    FetchResponse? Bought = null)
{
    /// <summary>Whether every stage of the slot ran and ended cleanly.</summary>
    public bool Ran =>
        Unobservable is null && Stages.All(s => string.Equals(s.Outcome, "clean", StringComparison.Ordinal));

    /// <summary>Whether no stage of the slot left a run entry at all, which is the state that lost four nights.</summary>
    public bool NeverRan =>
        Unobservable is null && Stages.All(s => s.StartedAt is null);

    /// <summary>Whether the slot ran and something about it was not clean, which includes a run still open.</summary>
    public bool NotClean =>
        Unobservable is null && !NeverRan && !Ran;

    /// <summary>Whether nothing in the store can say what this slot did.</summary>
    public bool IsUnobservable => Unobservable is not null;
}

/// <summary>
/// One stage of one slot on one session.
///
/// <paramref name="Unobservable"/> is why the store cannot say, on the one stage where it cannot.
/// It is a third answer beside "ran" and "did not", and folding it into either would be a report
/// that under-states its own scope.
/// </summary>
public sealed record StageResponse(
    string Stage,
    string? StartedAt,
    string? EndedAt,
    string? Outcome,
    int CallsUsed,
    string? Unobservable);

/// <summary>
/// What the night's unrecoverable buy actually bought, read from the row the fetch wrote.
///
/// <b>Five numbers rather than a verdict, because the verdict is the thing that was wrong.</b> The
/// stage's outcome now turns on <paramref name="Stored"/>, and this reports the counts it turned on
/// so a reader can see the arithmetic rather than take the word for it.
///
/// <paramref name="WindowSessions"/> against <paramref name="WindowAsks"/> is the second half, and
/// it is on the screen for a reason no other slot has: short's twenty-session count starts on the
/// first night the fetch runs at the full anchor window, so a night at a narrower one is a permanent
/// forfeit rather than a delay, and the morning it happens is the only time anyone can act on it.
/// </summary>
public sealed record FetchResponse(
    int Requested,
    int Fetched,
    int Empty,
    int BarsWritten,
    int Stored,
    int WindowSessions,
    int WindowAsks);
