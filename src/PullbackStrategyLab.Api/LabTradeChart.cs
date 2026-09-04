using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// One trade's session, minute by minute, with the four prices that decided it drawn across it.
///
/// <b>Minutes rather than sessions, and that is the whole reason it is a second read.</b>
/// <see cref="LabChart"/> draws a quarter of daily bars with the three averages on them, which is
/// what a pullback is read against. A trade happened inside one session, and a daily candle cannot
/// show a trigger reached at 10:00 and a stop reached at 14:00 on the same day. The two answer
/// different questions and neither is a widening of the other.
///
/// <b>Four levels, and every one of them is a price something already recorded.</b> The trigger and
/// the give-up point come from the plan, and the two fills come from <c>fill</c>. Nothing here
/// recomputes a price: a read surface that derived a fill would be a second implementation of the
/// fill model, and the two would eventually disagree with the picture as the last place anybody
/// looked.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>The minute picture is the session the trade closed in, and a daily strip runs beside it from
/// the session it opened in.</b> The obligation raised at 4.11 was that a position held past its own
/// session has a middle nothing draws: the minute chart is one session, the trail exit in particular
/// is decided on a daily close, and three of four held sessions were drawn by nothing at all. The
/// row named two possible answers, a multi-session minute strip with the breaks marked or a daily
/// chart beside the minute one, and said the choice was about what a person reads.
///
/// <b>It turned out not to be, and the store settled it.</b> <c>intraday-bars</c> fetches minutes
/// for the names flagged on the evening before a session, which is the session a plan is live in and
/// therefore the session an entry fills in. A later session of the same name carries minutes only
/// where the detector flagged it again that evening, which is not something a held position causes.
/// So the sessions in the middle of a hold are the ones the store is most likely to hold no minute
/// of, the multi-session minute strip has nothing to draw across them, and the daily strip is not a
/// preference between two pictures but the only picture of the middle there is.
///
/// <b>How many of them is counted from the store rather than derived from the hold.</b> A trade held
/// four sessions does not have three sessions of missing minutes as a matter of arithmetic: it has
/// however many of those sessions the fetch never covered, and a name flagged again in the middle of
/// its own hold has one fewer. Deriving the figure would be the population defect this corpus keeps
/// finding, on a number a person reads beside a picture.
///
/// <b>The closing session usually does have minutes, and the read no longer depends on it.</b> An
/// exit fill is priced from the session's minutes, so a session the lab was blind on postpones the
/// fill rather than closing the position, which is what <c>armed_sessions_waited</c> counts. Until
/// 5.5 this read answered a trade whose closing session held no minute with an empty response and a
/// reason, so the whole page went blank rather than drawing the strip; the minute picture is now
/// absent with its reason while the daily strip and the four levels are still drawn.
/// </summary>
public static class LabTradeChart
{
    public static TradeChartResponse Read(
        StoreConnectionFactory connections, string tradeId, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(tradeId);

        if (!connections.StoreExists)
        {
            return TradeChartResponse.Empty(tradeId, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        StoredTrade? trade = TradeReader.AllClosed(connection, asOf, sessionZone)
            .FirstOrDefault(t => string.Equals(t.TradeId, tradeId, StringComparison.Ordinal));

        if (trade is null)
        {
            return TradeChartResponse.Empty(tradeId, "no closed trade with that identifier, as at this date");
        }

        IReadOnlyList<StoredTradePlan> plans =
            TradePlanReader.ForSetups(connection, [trade.SetupId], asOf, sessionZone);

        IReadOnlyList<StoredIntradayBar> bars = IntradayBarReader.ReadSession(
            connection, [trade.Ticker], trade.ClosedSession, asOf, sessionZone);

        // The daily strip, from the session the trade opened in to the session it closed in, with a
        // little either side so the exit is not against the frame. This is the middle the minute
        // picture cannot have: nothing fetches minutes for a session after the one a plan was live
        // in, so on a held trade these bars are the only drawing of the sessions between the ends.
        //
        // The window ends at the later of the trade and the read's own date, never past it: a strip
        // reaching five sessions beyond the exit is drawing sessions the lab may not have had, and
        // the observation bound alone would not stop a bar dated after the as-of from being in it.
        DateOnly windowEnd = DateOnly.FromDayNumber(Math.Min(
            trade.ClosedSession.AddDays(ContextSessions).DayNumber, asOf.DayNumber));

        IReadOnlyList<StoredDailyBar> daily = DailyBarReader.Read(
            connection, trade.Ticker, windowEnd,
            trade.HeldSessions + (2 * ContextSessions) + 1,
            SessionBoundaries.EndOfSession(asOf, sessionZone));

        // Both absent is the one case there is nothing to draw at all, and it is a different
        // sentence from either being absent on its own.
        if (bars.Count == 0 && daily.Count == 0)
        {
            return TradeChartResponse.Empty(
                tradeId,
                $"the store holds neither a minute nor a daily bar of {trade.Ticker} around "
                + $"{trade.ClosedSession:yyyy-MM-dd}, so there is nothing to draw the levels across");
        }

        // Why the minute picture is missing, on exactly the trades it is missing on.
        string? minutesAbsent = bars.Count > 0
            ? null
            : trade.ClosedSession > trade.OpenedSession
                ? MinutesAreNotCapturedAfterTheEntrySession
                : $"the store holds no minute of {trade.ClosedSession:yyyy-MM-dd} for {trade.Ticker}";

        // How much of the hold nothing can draw a minute of, counted rather than derived. The
        // sessions of the hold are the daily bars inside it, which is what the store says a session
        // is; the ones with minutes are what the fetch reached. Subtracting one from the hold length
        // would be an estimate, and a name flagged again during its own hold makes it the wrong one.
        var withMinutes = new HashSet<DateOnly>(
            IntradayBarReader.SessionsHeld(
                connection, trade.Ticker, trade.OpenedSession, trade.ClosedSession, asOf, sessionZone));

        int blind = daily
            .Count(b => b.BarDate >= trade.OpenedSession
                && b.BarDate <= trade.ClosedSession
                && !withMinutes.Contains(b.BarDate));

        var levels = new List<TradeLevel>();

        if (plans.Count > 0)
        {
            levels.Add(new TradeLevel(
                "trigger", StoreText.PriceToStorageText(plans[0].TriggerPrice),
                "the price the plan committed to before the session opened"));
            levels.Add(new TradeLevel(
                "give-up", StoreText.PriceToStorageText(plans[0].GiveUpPrice),
                "the resting instruction the plan carried from 18:30, live from the moment the entry filled"));
        }

        levels.Add(new TradeLevel(
            "fill", StoreText.PriceToStorageText(trade.EntryPrice),
            $"what the entry actually got, in the session of {trade.OpenedSession:yyyy-MM-dd}"));
        levels.Add(new TradeLevel(
            "exit", StoreText.PriceToStorageText(trade.ExitPrice),
            $"what the exit got, on the {trade.ExitReason} rule"));

        return new TradeChartResponse(
            tradeId,
            trade.Ticker,
            trade.Direction,
            trade.ClosedSession.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            trade.OpenedSession.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            trade.ExitReason,
            [.. bars.Select(b => new TradeChartBar(
                b.OpenedAt.ToString("HH:mm", CultureInfo.InvariantCulture),
                b.Open, b.High, b.Low, b.Close, b.Volume))],
            levels,
            null,
            [.. daily.Select(b => new TradeChartDay(
                b.BarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                b.Open, b.High, b.Low, b.Close,
                b.BarDate == trade.OpenedSession,
                b.BarDate == trade.ClosedSession))],
            trade.HeldSessions,
            blind,
            trade.ClosedSession > trade.OpenedSession,
            minutesAbsent);
    }

    /// <summary>
    /// How many sessions of context the daily strip carries either side of the trade.
    ///
    /// Small on purpose. The strip is a picture of one position rather than of the stock, and a
    /// quarter of bars around a four-session hold would make the hold the part nobody could see. The
    /// stock's own window is what <see cref="LabChart"/> draws and it is one click away.
    /// </summary>
    public const int ContextSessions = 5;

    /// <summary>
    /// Why a held trade's closing session has no minutes, which is a fact about what the lab buys
    /// rather than about this trade.
    /// </summary>
    public const string MinutesAreNotCapturedAfterTheEntrySession =
        "minute bars are bought for the session a plan is live in, and for a later session of the "
        + "same name only where the detector flagged it again that evening. This position closed in "
        + "a session the store holds no minute of, and the vendor sells no minute history to buy one "
        + "back, so the daily strip is the whole picture of how it ended";
}

/// <summary>
/// One trade's session as the read surface answers it.
///
/// <paramref name="Nothing"/> is a reason rather than an error: a trade whose minutes the fetch
/// never bought is an ordinary thing to ask for, and the sentence says which of the two absences it
/// is.
/// </summary>
public sealed record TradeChartResponse(
    string TradeId,
    string Ticker,
    string Direction,
    string ClosedSession,
    string OpenedSession,
    string ExitReason,
    IReadOnlyList<TradeChartBar> Bars,
    IReadOnlyList<TradeLevel> Levels,
    string? Nothing,
    IReadOnlyList<TradeChartDay> Daily,
    int HeldSessions,
    int SessionsWithNoMinutes,
    bool HeldPastItsOwnSession,
    string? MinutesAbsentBecause)
{
    public static TradeChartResponse Empty(string tradeId, string why) =>
        new(tradeId, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], [], why,
            [], 0, 0, false, null);
}

/// <summary>
/// One session of the daily strip, which is the picture of the middle a held position has and the
/// minute chart cannot.
///
/// The two flags say which of these bars are the ends of the trade, so the page marks them rather
/// than a reader counting along the axis.
/// </summary>
public sealed record TradeChartDay(
    string Date, decimal Open, decimal High, decimal Low, decimal Close, bool Opened, bool Closed);

/// <summary>One minute of the session. Prices are decimal on the wire as they are in the store.</summary>
public sealed record TradeChartBar(
    string At, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>
/// One horizontal line and what it is.
///
/// The price is text on the wire for the reason every price in this store is: it is a decimal and a
/// double would round it on the way past.
/// </summary>
public sealed record TradeLevel(string Name, string Price, string What);
