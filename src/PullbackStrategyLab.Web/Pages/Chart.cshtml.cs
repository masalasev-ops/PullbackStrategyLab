using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// One stock's chart: candles on the adjusted basis, the three averages, and the readout the
/// lab computed for the session.
///
/// The page this checkpoint exists for. The done condition is that you compare it against a
/// chart you already trust and agree, so everything on it is either read from the store or
/// computed from stored bars through the arithmetic the engine uses, and nothing is smoothed,
/// resampled or rounded on its way to the picture.
/// see: The averages are one implementation, computed nightly and drawn on demand
/// </summary>
public sealed class ChartModel : ScreenModel
{
    /// <summary>The box the chart is drawn in. Wide, because a quarter of sessions in a narrow box is a smear.</summary>
    public const int Width = 1040;

    public const int Height = 420;

    /// <summary>
    /// The windows the page offers. A quarter, half a year, a year, two years.
    ///
    /// The largest is what the read surface will serve. It offered 750 until 3.10, and the read
    /// surface clamps at 500, so selecting three years drew two and nothing on the page or on the
    /// wire said the window had been cut. A control that offers what the thing behind it refuses
    /// is a control that lies to whoever uses it.
    /// </summary>
    public static IReadOnlyList<int> Windows { get; } = [60, 120, 250, 500];

    private readonly LabApiClient _api;

    public ChartModel(LabApiClient api) : base(api) => _api = api;

    [BindProperty(SupportsGet = true)]
    public string? Ticker { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Sessions { get; set; } = 60;

    /// <summary>
    /// A trade to draw instead of a window, which switches the page from a calendar to a clock.
    ///
    /// The journal links here with one, because a trade happened inside one session and a daily
    /// candle cannot show a trigger reached at 10:00 and a stop reached at 14:00 on the same day.
    /// The two are one page rather than two because they are the same picture of the same store,
    /// and a second page would be a second candlestick component eventually.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? Trade { get; set; }

    public ChartView? Chart { get; private set; }

    public TradeChartView? TradeChart { get; private set; }

    public CandlestickGeometry Geometry { get; private set; } = CandlestickChart.Lay([], [], Width, Height);

    public MinuteChartGeometry MinuteGeometry { get; private set; } =
        CandlestickChart.LayMinutes([], [], Width, Height);

    /// <summary>
    /// The daily strip a trade is drawn on, from the session it opened in to the session it closed
    /// in, with the four levels across it.
    ///
    /// <b>This is the missing middle, and the obligation that asked for it is 4.11's.</b> A position
    /// held four sessions has three sessions the minute picture cannot show, and the trail exit in
    /// particular is decided on a daily close. The row named two possible answers and said the
    /// choice was a question about what a person reads; it turned out to be a question about what
    /// the store holds, because minutes are bought only for the session a plan is live in, so the
    /// multi-session minute strip has nothing to draw.
    ///
    /// <b>It is the same component and not a second one.</b> The daily geometry gained the levels
    /// the minute geometry already had, so the two pictures scale a price into a box by one
    /// implementation and cannot disagree about where a stop sits.
    /// </summary>
    public CandlestickGeometry TradeDailyGeometry { get; private set; } =
        CandlestickChart.Lay([], [], Width, DailyStripHeight);

    /// <summary>
    /// How tall the strip beside a trade is.
    ///
    /// Shorter than the minute picture, because it is a companion to it rather than a second chart:
    /// a strip as tall as the session would read as the main picture and put the minutes second.
    /// </summary>
    public const int DailyStripHeight = 240;

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(Trade))
        {
            TradeChart = await _api.ReadTradeChartAsync(Trade, cancellationToken).ConfigureAwait(false);
            ViewData["Title"] = string.IsNullOrWhiteSpace(TradeChart.Ticker) ? "Trade" : TradeChart.Ticker;

            IReadOnlyList<PriceLevel> levels =
                [.. TradeChart.Levels.Select(l => new PriceLevel(l.Name, Price(l.Price)))];

            MinuteGeometry = CandlestickChart.LayMinutes(TradeChart.Candles, levels, Width, Height);

            // The same four levels on the strip. A trade whose exit was decided on a daily close has
            // that close on this picture and on no other.
            TradeDailyGeometry = CandlestickChart.Lay(
                TradeChart.Daily, [], Width, DailyStripHeight, levels);

            return;
        }

        ViewData["Title"] = string.IsNullOrWhiteSpace(Ticker) ? "Chart" : Ticker.ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(Ticker))
        {
            // No ticker asked for is not an error. The page renders its form and the component's
            // own empty state, which says there are no bars rather than drawing an empty box.
            return;
        }

        Chart = await _api.ReadChartAsync(Ticker, Sessions, cancellationToken).ConfigureAwait(false);
        Geometry = CandlestickChart.Lay(Chart.Candles, Chart.Averages, Width, Height);
    }

    /// <summary>
    /// A level's price, from the text the wire carries it as.
    ///
    /// TryParse rather than Parse, on the terms the scoreboard's interval already stands on: this
    /// runs before the response begins, and a value this page did not write would otherwise take
    /// the whole picture down instead of dropping one line out of the scale.
    /// </summary>
    private static decimal Price(string text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal price)
            ? price
            : 0m;
}
