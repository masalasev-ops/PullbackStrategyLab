using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// A placeholder that says it is one. An empty page that says it is empty is honest, and
/// a page of invented rows is not.
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly LabApiClient _api;

    public IndexModel(LabApiClient api)
    {
        _api = api;
    }

    public string ApiBaseAddress => _api.BaseAddress?.ToString() ?? "not configured";

    public void OnGet()
    {
    }
}
