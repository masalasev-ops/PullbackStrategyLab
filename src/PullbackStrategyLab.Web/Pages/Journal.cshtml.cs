using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>Closed trades and their loss causes. Empty until anything trades, which is checkpoint 4.12.</summary>
public sealed class JournalModel : ScreenModel
{
    public JournalModel(LabApiClient api) : base(api)
    {
    }
}
