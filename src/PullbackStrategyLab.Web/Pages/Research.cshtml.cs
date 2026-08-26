using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>Proposals, samples, targets and the holdout register. Empty until checkpoint 5.5.</summary>
public sealed class ResearchModel : ScreenModel
{
    public ResearchModel(LabApiClient api) : base(api)
    {
    }
}
