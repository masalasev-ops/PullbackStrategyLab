namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// Whether a trade-chain stage can still see what the stage before it wrote.
///
/// <b>The chain has a hard edge at local midnight and until 6.10 it had no answer for what lies
/// past it.</b> Every reader in the chain is pinned at <c>observed_at &lt;= EndOfSession(sessionDate)</c>:
/// <c>TriggerResolutionReader.ForLiveSession</c> for the gate, <c>TradeOrderReader.ForLiveSession</c>
/// for the broker and the same shape in the resolver. So a rerun after 23:59:59.999 of the session's
/// own day in the trading zone writes rows the next stage can never see. The gate then recorded
/// <c>clean</c> saying no plan resting in this session was touched, which is a clean run over a read
/// it could not make rather than a refusal.
///
/// <b>Two closing moves were available and this is the cheaper one.</b> The other is a lateness
/// stamp, which is what <c>recheck</c> and the sector walk carry: a column recording how late an
/// answer arrived, bounded, so a late row is attributed to the session it was fetched for and a
/// later reader can exclude it. That is the right shape where the late answer is still worth
/// something, and here it is not: the chain's rows are not answers the session asked a vendor for,
/// they are a session's own decisions, and a decision taken a day late is not a late decision but a
/// different one. So the stage refuses, which is what a point-in-time read already does when it
/// cannot answer, and the refusal is recorded rather than thrown so it reaches the morning screen.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
/// see: The trade chain refuses past its window rather than carrying a lateness stamp
///
/// <b>It has cost nothing and no instance is claimed.</b> <c>trade_plan</c> holds nought rows and
/// has on every night the lab has run, so the three slots the branch guard refused on the evening of
/// 2026-09-04 had nothing to do. A mechanism capable of producing a fault is not evidence that it
/// produced one, and this one is armed for the first night that has a plan.
/// </summary>
public static class TradeChainWindow
{
    /// <summary>
    /// Why a stage cannot run for <paramref name="sessionDate"/> at <paramref name="now"/>, or null
    /// where it can.
    ///
    /// The instant is passed in rather than read, because nothing outside the clock reads the
    /// machine's own time and this is a pure function of the two.
    /// </summary>
    public static string? Closed(DateTimeOffset now, DateOnly sessionDate, string sessionZone)
    {
        DateTimeOffset edge = SessionBoundaries.EndOfSession(sessionDate, sessionZone);

        if (now <= edge)
        {
            return null;
        }

        return $"the repair window for the session of {sessionDate:yyyy-MM-dd} closed at {edge:yyyy-MM-dd HH:mm:ss}Z, "
            + "which is local midnight of that session in the trading zone. Every reader in the trade chain is "
            + "bounded at that instant, so a row written now is one the next stage can never see, and a run that "
            + "reported clean would be reporting a read it could not make. Rerun nothing: the session is closed "
            + "and what it did not do it did not do.";
    }
}
