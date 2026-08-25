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

    /// <summary>The windows the page offers. A quarter, half a year, a year, three years.</summary>
    public static IReadOnlyList<int> Windows { get; } = [60, 120, 250, 750];

    private readonly LabApiClient _api;

    public ChartModel(LabApiClient api) : base(api) => _api = api;

    [BindProperty(SupportsGet = true)]
    public string? Ticker { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Sessions { get; set; } = 60;

    public ChartView? Chart { get; private set; }

    public CandlestickGeometry Geometry { get; private set; } = CandlestickChart.Lay([], [], Width, Height);

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);
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
}
