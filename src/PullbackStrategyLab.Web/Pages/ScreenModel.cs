using Microsoft.AspNetCore.Mvc.RazorPages;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// What every screen shares: the status band's contents, and which nav entry it is.
///
/// The band is read once per page load and handed to the layout through ViewData, so no page
/// has to remember to fetch it and no page can render the shell without it.
/// </summary>
public abstract class ScreenModel : PageModel
{
    private readonly LabApiClient _api;

    protected ScreenModel(LabApiClient api) => _api = api;

    /// <summary>The nav entry this page is, resolved from the path so the two cannot drift apart.</summary>
    public NavigationItem Item => Navigation.Items
        .FirstOrDefault(i => string.Equals(i.Path, Request.Path.Value, StringComparison.OrdinalIgnoreCase))
        ?? Navigation.Items[0];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        LabStatusView status = await _api.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        ViewData["Status"] = status;
        ViewData["Title"] = Item.Title;
    }
}
