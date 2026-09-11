namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// Why a scheduled slot did not run, as the four answers a <c>run_log</c> row can give.
///
/// <b>Until 7.1 a slot that did not run left nothing at all.</b> On 2026-09-07 thirty-one of the
/// thirty-two slots due refused on the tree guard and <c>run_log</c> held no row for the date, so
/// the store could not tell a refused night from a night the machine was off, a night the market
/// was closed, or a night nobody scheduled. The guard exits before the worker starts, and the one
/// declared writer of the table is inside the worker, so the refusal is written to the night's log
/// and the next night's reconciliation reads it back and writes the row.
///
/// <b>One vocabulary in Core, because three things have to agree about it.</b> The migration's
/// CHECK constraint holds the stored form, <c>tools/nightly.ps1</c> writes two of the four into the
/// night's log, and the read surface says each one in words. <c>slot-roster</c> reconciles all three
/// against <see cref="Reasons"/> in both directions, so a fifth reason added in one place fails
/// rather than being stored by one and read as nothing by another.
///
/// <b>A slot whose reason nothing can establish gets no row.</b> That is the fifth case and it is
/// deliberately not a word here: a night whose index history has not yet been ingested past it could
/// be a holiday or a lost night, and writing either would be a guess stored as a record.
/// </summary>
public static class DidNotRunBecause
{
    /// <summary>
    /// The tree guard in <c>tools/nightly.ps1</c> refused the slot because the checkout it runs from
    /// was not on <c>main</c>. Written by the script, read back from the night's log.
    /// </summary>
    public const string RefusedByTheTreeGuard = "refused-by-tree-guard";

    /// <summary>
    /// The market held no session that day, so there was nothing for the slot to run over. Never
    /// written by the script: it is derived by the reconciliation from the index history, which is
    /// refetched whole every night and so covers the session before whether or not that session's
    /// own night ran.
    /// </summary>
    public const string MarketClosed = "market-closed";

    /// <summary>
    /// The operator had paused the lab with the pause file under the data root. Written by the
    /// script, read back from the night's log.
    /// </summary>
    public const string PausedByTheOperator = "paused-by-operator";

    /// <summary>
    /// The slot fired and none of its stages reached the store: the build failed, the store refused
    /// the build's schema, or the script stopped before a stage opened a run. Derived by the
    /// reconciliation from the slot's own starting line with nothing after it in <c>run_log</c>.
    /// </summary>
    public const string Fault = "fault";

    /// <summary>The four, in the order a morning reads them.</summary>
    public static IReadOnlyList<string> Reasons { get; } =
        [RefusedByTheTreeGuard, MarketClosed, PausedByTheOperator, Fault];

    /// <summary>What the morning screen says for a refused slot.</summary>
    public const string RefusedByTheTreeGuardReads =
        "the tree guard refused it, because the checkout the schedule runs from was not on main";

    /// <summary>What the morning screen says for a slot on a session the market did not hold.</summary>
    public const string MarketClosedReads =
        "the market held no session that day, read from the index history the next night ingested";

    /// <summary>What the morning screen says for a paused slot.</summary>
    public const string PausedByTheOperatorReads =
        "the operator had paused the lab, so the slot fired and ran nothing";

    /// <summary>What the morning screen says for a slot that fired and reached nothing.</summary>
    public const string FaultReads =
        "it fired and none of its stages reached the store, so the night's log says why";

    /// <summary>
    /// The sentence for one stored reason. Throws on a reason outside the vocabulary rather than
    /// rendering it raw, because a word the screen has no sentence for is a fifth reason somebody
    /// stored without adding it here.
    /// </summary>
    public static string Reads(string reason) => reason switch
    {
        RefusedByTheTreeGuard => RefusedByTheTreeGuardReads,
        MarketClosed => MarketClosedReads,
        PausedByTheOperator => PausedByTheOperatorReads,
        Fault => FaultReads,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "not one of the four reasons a slot can have not run for"),
    };

    /// <summary>
    /// The reasons <c>tools/nightly.ps1</c> writes into the night's log itself. The other two are
    /// derived by the reconciliation, and <c>slot-roster</c> holds the script to exactly these.
    /// </summary>
    public static IReadOnlyList<string> WrittenByTheSlotScript { get; } =
        [RefusedByTheTreeGuard, PausedByTheOperator];
}
