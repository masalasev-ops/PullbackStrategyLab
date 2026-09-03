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
/// <b>The session drawn is the one the trade closed in.</b> A trade held four sessions has four
/// sessions of minutes, and drawing all of them would put three gaps in the middle of a picture
/// whose x-axis is a clock. The close is the session the exit happened in and is where three of the
/// four levels are, so it is the one a person opens this for; the entry level is drawn on it too and
/// says which session it was reached in, which is how a reader knows the picture is not the whole
/// trade.
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

        if (bars.Count == 0)
        {
            return TradeChartResponse.Empty(
                tradeId,
                $"the store holds no minute of {trade.ClosedSession:yyyy-MM-dd} for {trade.Ticker}, "
                + "so there is nothing to draw the levels across");
        }

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
            null);
    }
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
    string? Nothing)
{
    public static TradeChartResponse Empty(string tradeId, string why) =>
        new(tradeId, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], [], why);
}

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
