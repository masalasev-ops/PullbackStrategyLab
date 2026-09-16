using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// What the lab is watching, in plain words, and what it would risk on each.
///
/// <b>It reads the evening rather than naming a date the market will open on.</b> The lab has no
/// holiday calendar and inventing one would be authoring a market calendar rather than recording
/// one, so this page says which evening chose the list and that the list is live on the next
/// session, which is true whatever the market does next.
///
/// <b>It shows a check the rule recorded and did not require, where one failed.</b> That is the
/// only place a reader can see that a name passed on a technicality: generation 1 recorded the
/// daily range and never required it, so on 2026-09-15 it chose ten names moving less than the
/// figure the source trades by. A page that showed only "passed" would have shown all fourteen as
/// equally chosen (see: Failed checks are recorded, not discarded).
/// see: A surface a person opens is a screen, and the navigation names the way in apart from the research screens
/// </summary>
public sealed class TodayModel : ScreenModel
{
    private readonly LabApiClient _api;

    public TodayModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The evening being read. Defaults to the last one the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    public SetupsView Chosen { get; private set; } =
        SetupsView.Empty(string.Empty, "nothing has been read yet");

    public ExperimentView Experiment { get; private set; } =
        ExperimentView.Empty(string.Empty, "nothing has been read yet");

    /// <summary>How many names the evening recorded in all, which is what the chosen few came from.</summary>
    public int Recorded => Experiment.Long.Funnel.Recorded + Experiment.Short.Funnel.Recorded;

    /// <summary>
    /// The checks this evening's rule recorded without requiring, which is what makes a failed
    /// clause on a chosen name possible rather than a contradiction.
    /// </summary>
    public IReadOnlySet<string> RecordedNotRequired =>
        SetupChecks.RecordedNotRequiredFor(Experiment.LatestEveningGeneration);

    /// <summary>Every check a chosen name failed that its own rule recorded and did not require.</summary>
    public IReadOnlyList<SetupCheckRowView> PassedOnATechnicality(SetupCardView card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return [.. card.Checks.Where(c => !c.Passed && RecordedNotRequired.Contains(c.Name))];
    }

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        var status = ViewData["Status"] as LabStatusView;
        string session = string.IsNullOrWhiteSpace(AsOf) ? status?.Session ?? string.Empty : AsOf;

        if (!DateOnly.TryParseExact(session, "yyyy-MM-dd", out DateOnly asOf))
        {
            Chosen = SetupsView.Empty(session, "the store records no session yet");
            return;
        }

        Experiment = await _api.ReadExperimentAsync(asOf, cancellationToken).ConfigureAwait(false);

        Chosen = await _api
            .ReadSetupsAsync(asOf, failedCheck: null, outcome: SetupOutcomes.PassedEverything, cancellationToken)
            .ConfigureAwait(false);
    }
}
