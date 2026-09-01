using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The morning screen: last night's capped candidates, long and short in divided panels, ranked.
///
/// <b>It reads the capped set rather than every flagged name.</b> `SetupCapper` truncates the night
/// to sixty by rank and this is the published list, so a name the cap cut is evidence and was never
/// a candidate. The gallery is where every flagged row is read; this page is where the ones the lab
/// would act on are.
///
/// <b>Plans do not exist yet and the page does not pretend they do.</b> `trade_plan` arrives at 4.16
/// and share counts arrive with PlanBuilder at 4.16, so each row shows the trigger, the give-up price
/// and the distance the detector computed, and no share count at all. A column drawn with a number
/// nothing produced is the one thing that cannot be told from a working screen later.
///
/// <b>It reads through the Api like every other page</b>, and it adds no endpoint: `/setups/{asOf}`
/// already returns rank and the cap flag, so the watchlist is that answer filtered and ordered.
/// A second endpoint returning the same rows in a different shape is two definitions of the night.
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public sealed class WatchlistModel : ScreenModel
{
    private readonly LabApiClient _api;

    public WatchlistModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The night being read. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public WatchlistView Watchlist { get; private set; } =
        WatchlistView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(session, "yyyy-MM-dd", out DateOnly asOf))
        {
            Watchlist = WatchlistView.Empty(session, "the store records no session yet");
            return;
        }

        SetupsView night = await _api
            .ReadSetupsAsync(asOf, failedCheck: null, cancellationToken)
            .ConfigureAwait(false);

        Watchlist = WatchlistView.Of(night);
    }
}
