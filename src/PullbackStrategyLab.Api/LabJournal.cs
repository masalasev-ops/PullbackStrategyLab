using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the trade journal reads: every closed trade, with the plan held against it and the cause of
/// every loss.
///
/// <b>It reads what the three stages wrote and computes nothing.</b> A read surface that recomputed
/// a result, a difference or a cause would be a second implementation of arithmetic the phase turns
/// on, and the two would eventually disagree with the page as the last place anybody looked. That is
/// the ruling the scoreboard's read already took.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>Long and short come back as separate lists.</b> Not one list with a direction column: any
/// figure that would require adding a long result to a short result is not displayed at all, and the
/// shape of the wire is what makes that easy rather than remembered. The two expectancies in the
/// band are computed per side and the page never adds them.
/// see: Long and short are never pooled into one figure
///
/// <b>The one figure here that is not about a trade is what the caps could not see.</b>
/// <c>manage_run.closed_in_their_own_session</c> is the size of an approximation rather than a
/// result: RiskGate reads the book as it stood coming into the session, so a position opened and
/// closed inside one still occupied a slot. The decision to leave the gate where it is rests on that
/// cost being countable rather than argued, and a figure nobody reads is one nobody reviews the
/// choice against.
/// see: RiskGate reads the book as it stood coming into the session, and what that costs is counted
/// </summary>
public static class LabJournal
{
    public static JournalResponse Read(StoreConnectionFactory connections, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connections);

        if (!connections.StoreExists)
        {
            return JournalResponse.Empty(asOf, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        IReadOnlyList<StoredTrade> trades = TradeReader.AllClosed(connection, asOf);

        if (trades.Count == 0)
        {
            return JournalResponse.Empty(asOf, "no trade has closed yet");
        }

        Dictionary<string, StoredPlanAudit> audits = TradeReader
            .AuditsOf(connection, [.. trades.Select(t => t.TradeId)], asOf)
            .ToDictionary(a => a.TradeId, StringComparer.Ordinal);

        Dictionary<string, StoredLossClass> causes = LossClassReader
            .All(connection, asOf)
            .ToDictionary(l => l.TradeId, StringComparer.Ordinal);

        var longSide = new List<TradeResponse>();
        var shortSide = new List<TradeResponse>();

        foreach (StoredTrade trade in trades)
        {
            audits.TryGetValue(trade.TradeId, out StoredPlanAudit? audit);
            causes.TryGetValue(trade.TradeId, out StoredLossClass? cause);

            TradeResponse row = Row(trade, audit, cause);

            (string.Equals(trade.Direction, "short", StringComparison.Ordinal) ? shortSide : longSide)
                .Add(row);
        }

        return new JournalResponse(
            asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            null,
            Expectancy(longSide),
            Expectancy(shortSide),
            longSide,
            shortSide,
            SlotsTheCapsCouldNotSee(connection, asOf));
    }

    /// <summary>
    /// The mean result in R over one side, or null where that side has closed nothing.
    ///
    /// Null rather than nought, on the terms every withheld panel already stands on: a mean of
    /// nought over no trades reads as a strategy that breaks even and means that nothing has
    /// happened. The count travels beside it so the figure is never read without its denominator.
    /// </summary>
    private static double? Expectancy(IReadOnlyList<TradeResponse> side) =>
        side.Count == 0 ? null : side.Average(t => t.ResultR);

    private static TradeResponse Row(
        StoredTrade trade, StoredPlanAudit? audit, StoredLossClass? cause) =>
        new(
            trade.TradeId,
            trade.Ticker,
            trade.Direction,
            trade.OpenedSession.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            trade.ClosedSession.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StoreText.PriceToStorageText(trade.EntryPrice),
            StoreText.PriceToStorageText(trade.ExitPrice),
            trade.ExitReason,
            trade.ResultR,
            trade.HeldSessions,
            trade.Shares,
            trade.TrimmedShares,

            // Two figures the risk decision asked to be visible side by side rather than assumed
            // away. The plan's intention is on the audit and what the position actually risked is on
            // the trade, and the gap between them is the entry slippage.
            audit is null ? null : StoreText.PriceToStorageText(audit.RiskIntended),
            StoreText.PriceToStorageText(trade.RiskRealised),

            // The two unmodelled short assumptions, present exactly on the shorts, so a person
            // reading one row learns what it assumed without being told to go and read a table.
            trade.BorrowRateAssumed is null ? null : StoreText.PriceToStorageText(trade.BorrowRateAssumed.Value),
            trade.BorrowCost is null ? null : StoreText.PriceToStorageText(trade.BorrowCost.Value),
            trade.BorrowRateAssumed is null ? null : BorrowAssumption.AvailabilityIsNotModelled,

            audit?.EntryDifferenceBasisPoints,
            audit?.ExitDifferenceBasisPoints,
            audit?.EntryBasis,
            audit?.ExitBasis,
            audit is null ? null : StoreText.PriceToStorageText(audit.PlannedGiveUp),
            audit?.PlannedShares,
            audit?.ExecutedShares,
            audit?.ReducedBecause,

            cause?.Mechanism,
            cause?.Aftermath,
            cause?.AftermathBecause);

    /// <summary>
    /// How many positions closed in the session they opened in, summed over every night the store
    /// holds, which is the size of the approximation the caps make.
    ///
    /// Bounded on the run row's own instant, which is the one place this read touches an operational
    /// table. It is exempt from the point-in-time list as a run row, and the figure it carries is a
    /// count of positions rather than a measurement of the market.
    /// </summary>
    private static int SlotsTheCapsCouldNotSee(SqliteConnection connection, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(closed_in_their_own_session), 0)
              FROM manage_run
             WHERE observed_at <= @observed_before
            """;

        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// One day's journal on the wire: two lists, two expectancies, and the one figure that is about the
/// caps rather than about a trade.
/// </summary>
public sealed record JournalResponse(
    string AsOf,
    string? Absent,
    double? LongExpectancyR,
    double? ShortExpectancyR,
    IReadOnlyList<TradeResponse> Long,
    IReadOnlyList<TradeResponse> Short,
    int SlotsTheCapsCouldNotSee)
{
    public static JournalResponse Empty(DateOnly asOf, string why) =>
        new(asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), why, null, null, [], [], 0);
}

/// <summary>
/// One closed trade, with its audit and its cause beside it.
///
/// Every nullable field here is null for a stated reason rather than because a value was missing:
/// the audit fields where nothing has audited the trade yet, the borrow fields on every long, and
/// the cause fields on a trade that did not lose. <see cref="Aftermath"/> is null on a loss whose
/// ten-session horizon has not closed, which is a different fact from <c>unclassified</c>.
/// </summary>
public sealed record TradeResponse(
    string TradeId,
    string Ticker,
    string Direction,
    string OpenedSession,
    string ClosedSession,
    string EntryPrice,
    string ExitPrice,
    string ExitReason,
    double ResultR,
    int HeldSessions,
    int Shares,
    int TrimmedShares,
    string? RiskIntended,
    string RiskRealised,
    string? BorrowRateAssumed,
    string? BorrowCost,
    string? BorrowAvailability,
    double? EntryDifferenceBasisPoints,
    double? ExitDifferenceBasisPoints,
    string? EntryBasis,
    string? ExitBasis,
    string? PlannedGiveUp,
    int? PlannedShares,
    int? ExecutedShares,
    string? ReducedBecause,
    string? LossMechanism,
    string? Aftermath,
    string? AftermathBecause);
