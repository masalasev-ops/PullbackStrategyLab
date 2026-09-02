using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The trade journal: every closed trade, split long and short, with the plan held against it and
/// the cause of every loss.
///
/// <b>Two blocks and never one table with a direction column.</b> The pooling rule is easiest to
/// break on a screen, where one table looks tidier and quietly invites a total. The two expectancies
/// sit in the band and nothing on the page adds them.
/// see: Long and short are never pooled into one figure
///
/// <b>It absorbs the layout `SCREENS.html` carries, which is why the mockup can be deleted at
/// 4.12.</b> The band with a closed count and an expectancy a side, two panels, and a row per trade
/// carrying its dates, its two prices, its result in R, how long it was held, its cause and the plan
/// against the actual. What the mockup drew as a borrow column is here as a sentence per short,
/// because the assumption it carries is not a number.
///
/// <b>It reads through the Api like every other page and adds one endpoint.</b> `/journal/{asOf}`
/// has no existing answer to filter: the scoreboard returns panels and the setups endpoint returns a
/// night's candidates, and neither is a list of closed trades.
/// see: The Web project reads through the Api and never opens the store
///
/// <b>The chart per trade is a link rather than an embed.</b> `/chart/{ticker}` already renders one
/// name's session with the levels drawn on it, and a page embedding sixty of them would fetch sixty
/// series to render a table nobody reads that way. The link carries the session the trade closed in,
/// so the chart opens on the day the exit happened.
/// </summary>
public sealed class JournalModel : ScreenModel
{
    private readonly LabApiClient _api;

    public JournalModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The date being read. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public JournalView Journal { get; private set; } =
        JournalView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(session, "yyyy-MM-dd", out DateOnly asOf))
        {
            Journal = JournalView.Empty(session, "the store records no session yet");
            return;
        }

        Journal = await _api.ReadJournalAsync(asOf, cancellationToken).ConfigureAwait(false);
    }
}
