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
/// nights whose minute bars were never bought on the night, so none can stand as those nights'
/// evidence.
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
    /// The thirty-seven slots, in the order the night runs them.
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
        // The reconciliation is the second verb of this slot rather than a slot of its own, because
        // it reads what the first writes. `index-bars` refetches each tracker's whole history every
        // night, so once it has run the store knows whether the session before was one the market
        // held, whether or not that session's own night ran. The daily bulk cannot say so: it asks
        // for its own date and nothing else.
        new("index", "17:50", ["index-bars", "reconcile-night"]),
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

        // 21:45, after the scores and before the scoreboard, because it reads the rows the slot
        // above wrote and the ledger reads what this one wrote. It settles a version that has
        // reached the sample written at its creation and leaves every other version open with its
        // shortfall stated, which is what makes it a nightly slot rather than an act somebody
        // remembers: the night a version matures is a night nobody can predict.
        new("acceptance", "21:45", ["settle-variants"]),

        new("scoreboard", "21:50", ["scoreboard"]),

        // Saturday morning rather than a weeknight, and the report has to know that or it would
        // report a missing slot on every weekday of the lab's life. A false alarm every night is
        // how a guard gets suppressed, and a suppressed guard is a dead one.
        new("ceiling", "08:00", ["ceiling"], WeeklyOn: DayOfWeek.Saturday),

        // The second weekly slot, and it is Saturday morning for the same reason the first is: the
        // metric standardises every signal over a trailing window of setups rather than over a
        // night, so a night's worth of new rows cannot move it enough to be worth a run. It runs
        // after `ceiling` because both read the same closed-outcome population and the bound is the
        // cheaper of the two to look at first.
        new("twins", "08:10", ["twin-pairs"], WeeklyOn: DayOfWeek.Saturday),

        // The third weekly slot, and it runs last of the three because it reads what the other two
        // wrote: the pack's twin section is a reading of the twin run, and its ceiling section a
        // reading of the bound. Weekly rather than nightly because the researcher is asked weekly
        // and a pack nobody is shown is a pack cut for the store's benefit.
        //
        // It runs whether or not the evidence has moved, because a pack that only appears when
        // there is something to say would leave no record of the weeks there was not, and five of
        // its nine sections rest on outcomes that have not closed.
        new("pack", "08:20", ["build-pack"], WeeklyOn: DayOfWeek.Saturday),

        // The fourth weekly slot, and the only one that leaves the machine. It runs after `pack`
        // rather than instead of it, and it cuts the pack again for itself: the body is not stored,
        // only its digest, so a seat reading the earlier row would have to rebuild the document
        // anyway. Cutting it here means the digest filed on the proposal is the digest of what the
        // model actually read, and the two cuts agreeing is byte-stability observed in the running
        // lab rather than claimed of the build.
        //
        // Weekly because the evidence barely moves in a day and a nightly ask would produce an idea
        // whether or not there is one. It runs whether or not the evidence moved, because a week the
        // seat declined is a result and a week it was never asked is not.
        new("seat", "08:30", ["ask-researcher"], WeeklyOn: DayOfWeek.Saturday),

        // The fifth weekly slot, and it runs after the seat because it reads what the seat filed.
        // A rule change goes to the screen and a signal request is a build task, so this is where
        // the two kinds part; an abstention reaches a recorded state of its own, because a
        // considered decline and a week the seat could not be asked are opposite facts.
        //
        // It runs whether or not the seat answered, because a week with no answer still has to
        // leave the queue, and a screen that separated nothing is written down rather than skipped.
        new("registry", "08:40", ["screen-proposals"], WeeklyOn: DayOfWeek.Saturday),

        // The one slot a run report cannot see, named rather than left out. `snapshot-db` copies the
        // store and takes no RunLogger, so it writes no run entry, and a report that silently
        // omitted it would be reporting thirty-six slots under a heading saying thirty-seven. That is
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

    /// <summary>
    /// The slots a registered task fires on a day, which is a narrower list than <see cref="Due"/>
    /// on a Saturday and on a Sunday.
    ///
    /// <b>Two lists because they answer two questions.</b> <see cref="Due"/> is the population a
    /// morning report of a session is over, and it keeps the weekly slots on their own day beside the
    /// rest. The tasks themselves are registered weekdays for the nightly slots and Saturday for the
    /// weekly ones, so on a Saturday the nightly slots do not fire at all, and a reconciliation that
    /// took the report's list would record thirty-two slots as not having run on every Saturday of
    /// the lab's life for want of a reason that does not exist.
    /// </summary>
    public static IReadOnlyList<NightSlot> FiresOn(DayOfWeek on) => on switch
    {
        DayOfWeek.Sunday => [],
        DayOfWeek.Saturday => [.. Slots.Where(s => s.WeeklyOn == DayOfWeek.Saturday)],
        _ => [.. Slots.Where(s => s.WeeklyOn is null)],
    };

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
