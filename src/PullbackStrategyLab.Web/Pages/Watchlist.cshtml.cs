using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>The morning screen. Empty until the plans exist, which is checkpoint 4.1.</summary>
public sealed class WatchlistModel : ScreenModel
{
    public WatchlistModel(LabApiClient api) : base(api)
    {
    }
}
