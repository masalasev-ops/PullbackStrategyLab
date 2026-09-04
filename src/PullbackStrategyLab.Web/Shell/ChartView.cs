using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// One stock's window as the page renders it, and its own shape rather than the Api's type. The
/// two ends agree on a wire format rather than on an assembly.
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public sealed record ChartView(
    string Ticker,
    string AsOf,
    int Drawn,
    int Read,
    IReadOnlyList<Candle> Candles,
    IReadOnlyList<AverageLine> Averages,
    ChartReadoutView? Readout,
    string? Nothing)
{
    public static ChartView Empty(string ticker, string why) =>
        new(ticker, string.Empty, 0, 0, [], [], null, why);

    public bool HasBars => Candles.Count > 0;
}

/// <summary>
/// One trade's session as the page renders it: the minutes it happened in, and the four prices that
/// decided it drawn across them.
///
/// <b>Its own shape rather than <see cref="ChartView"/> widened.</b> A daily chart of a quarter and
/// a minute chart of one session answer different questions: a daily candle cannot show a trigger
/// reached at 10:00 and a stop reached at 14:00 on the same day, and a minute chart has no
/// fifty-day average to draw. One type carrying both would be a type where half the fields are null
/// on every instance.
///
/// <b><see cref="Nothing"/> is a reason rather than an error.</b> A trade whose minutes the fetch
/// never bought is an ordinary thing to ask for, and the sentence says which absence it is.
/// </summary>
public sealed record TradeChartView(
    string TradeId,
    string Ticker,
    string Direction,
    string ClosedSession,
    string OpenedSession,
    string ExitReason,
    IReadOnlyList<MinuteCandle> Candles,
    IReadOnlyList<TradeLevelLine> Levels,
    string? Nothing,
    IReadOnlyList<Candle> Daily,
    int HeldSessions,
    int SessionsWithNoMinutes,
    bool HeldPastItsOwnSession,
    string? MinutesAbsentBecause)
{
    public static TradeChartView Empty(string tradeId, string why) =>
        new(tradeId, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, [], [], why,
            [], 0, 0, false, null);

    public bool HasBars => Candles.Count > 0;

    /// <summary>Whether the daily strip has anything on it, which is the picture of the middle.</summary>
    public bool HasDaily => Daily.Count > 0;

    /// <summary>Whether the trade opened in a session other than the one drawn, which the page says.</summary>
    public bool OpenedEarlier =>
        !string.Equals(OpenedSession, ClosedSession, StringComparison.Ordinal);
}

/// <summary>One horizontal line, its price and what it is, drawn across the session.</summary>
public sealed record TradeLevelLine(string Name, string Price, string What);

/// <summary>
/// The figures the lab computed for this session and stored, beside the lines the page draws.
///
/// The last point of the drawn ema9 line and the stored ema9 are the same computation over the
/// same window, so where both exist they agree. The page shows the agreement rather than
/// asserting it, because a reader comparing this page against a chart they already trust is
/// comparing these numbers.
/// </summary>
public sealed record ChartReadoutView(
    string AsOf,
    decimal Ema9,
    decimal Ema21,
    decimal Ema50,
    decimal Atr14,
    decimal Adr20,
    decimal DollarVolumeMedian,
    decimal RangeAverage)
{
    public string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The daily range as a percentage, from the fraction the store holds.
    ///
    /// The store holds 0.068 and a reader thinks in 6.8%, and the conversion happens once, here,
    /// on the way to a screen. Storing the percentage instead is how a figure ends up an order
    /// of magnitude out in a comparison, which is why the fraction is what the column holds.
    /// </summary>
    public string Percent(decimal fraction) =>
        (fraction * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public string Money(decimal value) => "$" + (value / 1_000_000m).ToString("0.0", CultureInfo.InvariantCulture) + "M";
}
