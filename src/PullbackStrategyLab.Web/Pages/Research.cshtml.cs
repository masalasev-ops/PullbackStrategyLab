using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The research ledger: the register of rule versions, what each pre-registered, what has
/// accumulated against it, and the holdout budget.
///
/// <b>Designed from `ARCHITECTURE.html` rather than from a drawing, and the drawing is gone on
/// purpose.</b> `SCREENS.html` was retired at 4.12 once four of the five screens it drew existed,
/// because a mockup and a built page are two answers to one question and the day they disagreed
/// nothing would say which was the specification. This is the fifth, so its layout comes from the
/// two sections that describe what it is for rather than from a picture: Two experiment families
/// says a version is pre-registered with a target and a minimum sample and closes as refuted in
/// public, and Replay and holdout windows says the budget is quarters, capped at eight, spent once.
/// see: The corpus is eight documents and a ninth requires retiring one
///
/// <b>It reads the session the status band names rather than today's date</b>, on the terms the
/// scoreboard's read already stands on: a ledger opened on a Sunday should show Friday's register
/// rather than an empty page, and an empty page reads as a lab that has registered nothing.
/// </summary>
public sealed class ResearchModel : ScreenModel
{
    private readonly LabApiClient _api;

    public ResearchModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The date the register is read as of. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public ResearchView Research { get; private set; } =
        ResearchView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(
                session, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly asOf))
        {
            Research = ResearchView.Empty(
                session, "the store records no session yet, so the holdout schedule has no start date");
            return;
        }

        Research = await _api.ReadResearchAsync(asOf, cancellationToken).ConfigureAwait(false);
    }
}
