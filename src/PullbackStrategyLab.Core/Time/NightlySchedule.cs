namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// What a complete night runs, slot by slot, so a morning can be told which of them did.
///
/// <b>This is a fifth declaration of the slots and it is here for the one reason the other four
/// cannot serve.</b> <c>tools/nightly.ps1</c> declares the slot table, its own parameter set
/// accepts the names, the worker advertises its stages and RUNBOOK schedules them, and
/// <c>slot-roster</c> reconciles those four in every direction. All four agreed on 2026-09-03 while
/// fifteen of the thirty-two slots had never run once, because whether a scheduled task exists is a
/// property of the machine and every check in this corpus takes its subject from the source, the
/// documents, the fixture or a store it builds itself. What that cost is on the record: four flagged
/// nights whose minute bars cannot be bought back at any price.
///
/// <b>So the instrument is a read of the live store rather than a fifth thing to reconcile.</b>
/// <c>run_log</c> records which stages ran for a session and this list declares which were meant to,
/// and the report comparing them is a figure a person reads on the morning it happens. The four
/// lists above are in the Worker and in tools, and the read surface may reference neither; this one
/// is in Core because Core is what the read surface and the Worker share. <c>slot-roster</c> holds
/// it to the other four in both directions, so it is one fact in a fifth place rather than a fifth
/// fact.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
///
/// <b>Times are local to the session zone and are declarations rather than measurements.</b> They
/// are what RUNBOOK's schedule says and what the registered tasks were written from, and they are
/// here so a report can order what is missing and say when it should have fired. Nothing computes
/// anything from them.
/// </summary>
public static class NightlySchedule
{
    /// <summary>
    /// The thirty-two slots, in the order the night runs them.
    ///
    /// <b>Order is the declaration and not a sort.</b> Two slots share 17:20 and two share 18:28, so
    /// ordering by time alone would put them in whichever order a comparer happened to choose, and
    /// the sequence is the thing an operator reruns by.
    /// </summary>
    public static IReadOnlyList<NightSlot> Slots { get; } =
    [
        new("spread-open", "10:15", ["spreads"], InsideTheSession: true),
        new("spread-close", "15:45", ["spreads"], InsideTheSession: true),
        new("universe", "17:15", ["universe-build"]),
        new("actions", "17:20", ["actions"]),
        new("bars", "17:30", ["daily-bars"]),
        new("rebuild", "17:45", ["backfill"]),
        new("index", "17:50", ["index-bars"]),
        new("indicators", "18:00", ["indicators"]),
        new("scans", "18:10", ["scans", "tiers"]),
        new("sectors", "18:12", ["sectors"]),
        new("regime", "18:15", ["clusters", "regime"]),
        new("detect", "18:20", ["detect-long", "detect-short"]),
        new("seal", "18:25", ["vectorize", "journal"]),
        new("controls", "18:26", ["controls"]),
        new("cap", "18:28", ["cap"]),
        new("versions", "18:28", ["resolve-variants"]),
        new("plans", "18:30", ["plans"]),
        new("watchlist", "18:40", ["publish-watchlist"]),
        new("intraday", "20:30", ["intraday-bars"]),
        new("vwap", "21:00", ["vwap"]),
        new("resolve", "21:05", ["resolve-triggers"]),
        new("orders", "21:10", ["orders"]),
        new("fills", "21:15", ["fills"]),
        new("manage", "21:20", ["manage"]),
        new("trades", "21:25", ["trades"]),
        new("audit", "21:26", ["audit"]),
        new("forward", "21:30", ["forward-returns"]),
        new("losses", "21:35", ["losses"]),
        new("scores", "21:40", ["score-variants"]),
        new("scoreboard", "21:50", ["scoreboard"]),

        // Saturday morning rather than a weeknight, and the report has to know that or it would
        // report a missing slot on every weekday of the lab's life. A false alarm every night is
        // how a guard gets suppressed, and a suppressed guard is a dead one.
        new("ceiling", "08:00", ["ceiling"], WeeklyOn: DayOfWeek.Saturday),

        // The one slot a run report cannot see, named rather than left out. `snapshot-db` copies the
        // store and takes no RunLogger, so it writes no run entry, and a report that silently
        // omitted it would be reporting thirty-one slots under a heading saying thirty-two. That is
        // the under-reporting shape: a check that narrows its own scope and goes on passing.
        new("snapshot", "22:00", ["snapshot-db"],
            LeavesNoRunEntry:
                "snapshot-db copies the store and takes no run logger, so nothing it does reaches "
                + "run_log and this report cannot say whether it ran. The night's own log is where "
                + "that is written"),
    ];

    /// <summary>
    /// The slots a session of <paramref name="on"/> was meant to run, which is the population a
    /// report of that session is over.
    ///
    /// <b>The weekly slot is in it only on its own day.</b> Every other slot runs on every session,
    /// so this returns the whole list on a Saturday and the list less the weekly slot on any other
    /// day. A report that took the whole list every day would name a missing slot on every weekday
    /// the lab has ever run.
    /// </summary>
    public static IReadOnlyList<NightSlot> Due(DayOfWeek on) =>
        [.. Slots.Where(s => s.WeeklyOn is null || s.WeeklyOn == on)];

    /// <summary>The stages a run report expects to find, which is every stage of every slot that logs one.</summary>
    public static IReadOnlyList<string> ObservableStages { get; } =
        [.. Slots.Where(s => s.LeavesNoRunEntry is null).SelectMany(s => s.Stages).Distinct(StringComparer.Ordinal)];

    /// <summary>The slot one stage belongs to, or null where no slot runs it.</summary>
    public static NightSlot? SlotOf(string stage) =>
        Slots.FirstOrDefault(s => s.Stages.Contains(stage, StringComparer.Ordinal));
}

/// <summary>
/// One slot of the night: when it fires, and the stages it runs in order.
/// </summary>
/// <param name="Slot">The name <c>tools/nightly.ps1</c> dispatches by, which is what an operator reruns.</param>
/// <param name="At">The local time in the session zone, as RUNBOOK's schedule states it.</param>
/// <param name="Stages">
/// The stages the slot runs, in order. Two stages in one slot means the second reads what the first
/// wrote, which is why they are a slot rather than two entries a minute apart.
/// </param>
/// <param name="InsideTheSession">
/// Whether the slot fires while the market is open. The two spread passes do, and they are the only
/// ones: a quote has no history to buy back, so a pass that does not fire is a sample that never
/// existed rather than one bought late.
/// </param>
/// <param name="WeeklyOn">
/// The one day of the week this slot fires on, or null where it fires on every session. Only the
/// ceiling recomputation is weekly, and it is Saturday morning rather than a weeknight.
/// </param>
/// <param name="LeavesNoRunEntry">
/// Why <c>run_log</c> cannot see this slot, on the one slot where it cannot, and null on every other.
/// Present rather than absent so a report says which slot it has no answer for instead of shortening
/// its own list.
/// </param>
public sealed record NightSlot(
    string Slot,
    string At,
    IReadOnlyList<string> Stages,
    bool InsideTheSession = false,
    DayOfWeek? WeeklyOn = null,
    string? LeavesNoRunEntry = null);
