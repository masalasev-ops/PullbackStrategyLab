using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The front door: what the lab is, which screens exist and when each is filled, and the shared
/// chart component rendering its empty state.
/// </summary>
public sealed class IndexModel : ScreenModel
{
    public IndexModel(LabApiClient api) : base(api)
    {
    }

    /// <summary>
    /// The shared component with nothing in it. There is no store behind the Web project and no
    /// bars to draw until 1.10, and it renders that rather than an empty box, which would read
    /// as a stock that did not move.
    /// </summary>
    public CandlestickGeometry Chart { get; } = CandlestickChart.Lay([], [], 720, 260);
}
