using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The pack comparison surface: every evidence-pack version side by side.
///
/// <b>The sixth screen, and the navigation said five while the component catalogue held six.</b>
/// `Navigation` carried a comment reading "matching the screens the architecture describes" and the
/// catalogue had a row this list did not, which 6.0(b) settled in the catalogue's favour: the
/// subject of this page is the set of pack versions, and every other screen is keyed on one night,
/// one setup or one trade, so hanging the comparison off any of them would key a comparison across
/// versions to a single night.
/// see: The pack comparison surface is the sixth screen and the navigation says six
///
/// <b>It reads the session the status band names rather than today's date</b>, on the terms every
/// other screen's read already stands on.
/// </summary>
public sealed class PacksModel : ScreenModel
{
    private readonly LabApiClient _api;

    public PacksModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The date the versions are read as of. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public PacksView Packs { get; private set; } =
        PacksView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(
                session, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly asOf))
        {
            Packs = PacksView.Empty(
                session, "the store records no session yet, so no pack has been cut against one");
            return;
        }

        Packs = await _api.ReadPacksAsync(asOf, cancellationToken).ConfigureAwait(false);
    }
}
