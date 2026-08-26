using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>The four bands. Empty until forward returns exist, which is checkpoint 3.5.</summary>
public sealed class ScoreboardModel : ScreenModel
{
    public ScoreboardModel(LabApiClient api) : base(api)
    {
    }
}
