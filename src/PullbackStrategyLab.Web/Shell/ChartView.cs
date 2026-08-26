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
