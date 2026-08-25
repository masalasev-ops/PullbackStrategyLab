using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>The setup gallery. Empty until the detectors flag something, which is checkpoint 2.9.</summary>
public sealed class SetupsModel : ScreenModel
{
    public SetupsModel(LabApiClient api) : base(api)
    {
    }
}
