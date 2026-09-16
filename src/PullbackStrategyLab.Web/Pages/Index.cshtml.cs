using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The front page: what the experiment is, how far it has got, and what would prove it wrong.
///
/// <b>It was a developer's front door until 7.20</b>, listing the screens with the build checkpoint
/// that fills each beside them and rendering the shared chart component's empty state. That is a
/// page about how the lab is built, shown to somebody who does not yet know what it does. What it
/// never said is what the lab is for, whether it is working, or that the gallery's long list is
/// mostly names it rejected.
///
/// <b>The figures are per direction and the page states no total.</b> This is the surface where the
/// pooling rule is easiest to break, because one number reads better than two
/// (see: Long and short are never pooled into one figure).
/// see: A surface a person opens is a screen, and the navigation names the way in apart from the research screens
/// </summary>
public sealed class IndexModel : ScreenModel
{
    private readonly LabApiClient _api;

    public IndexModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The date being read. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public ExperimentView Experiment { get; private set; } =
        ExperimentView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(session, "yyyy-MM-dd", out DateOnly asOf))
        {
            Experiment = ExperimentView.Empty(session, "the store records no session yet");
            return;
        }

        Experiment = await _api.ReadExperimentAsync(asOf, cancellationToken).ConfigureAwait(false);
    }
}
