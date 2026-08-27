using System.Globalization;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The three bands phase 3 fills. Band 3 arrives at 6.8 with the research loop.
///
/// It reads the session the status band names rather than today's date, so a scoreboard opened on a
/// Sunday shows the last session the lab actually recorded instead of an empty page. An empty page
/// would read as "the lab has measured nothing" rather than "no panels were built today", and those
/// are different sentences.
/// </summary>
public sealed class ScoreboardModel : ScreenModel
{
    private readonly LabApiClient _api;

    public ScoreboardModel(LabApiClient api) : base(api) => _api = api;

    public ScoreboardView Scoreboard { get; private set; } =
        ScoreboardView.Empty(string.Empty, "nothing has been read yet");

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        if (LastSession() is not DateOnly session)
        {
            Scoreboard = ScoreboardView.Empty(
                string.Empty, "the status band names no session yet, so there is nothing to score");
            return;
        }

        Scoreboard = await _api.ReadScoreboardAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private DateOnly? LastSession() =>
        ViewData["Status"] is LabStatusView status
        && !string.IsNullOrWhiteSpace(status.Session)
        && DateOnly.TryParseExact(
            status.Session, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly session)
            ? session
            : null;
}
