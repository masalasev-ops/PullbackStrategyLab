using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PullbackStrategyLab.Web.Shell;

namespace PullbackStrategyLab.Web.Pages;

/// <summary>
/// The gallery: a night's setups drawn small, paged by keyboard, agreed or disagreed with one at a
/// time.
///
/// <b>This is where the strategy gets transferred into code.</b> The detector applies ten rules a
/// person described; the only way to find out whether it applied them the way that person meant is
/// to look at what it flagged and say so. The agreement column is the record of that, and it is the
/// one column in the store a machine did not produce.
///
/// The thumbnails are laid by the same component the chart page draws one large with. Its own
/// documentation names this checkpoint as the second consumer and says not to write a second
/// implementation: two pictures of the same bars that disagree is exactly the kind of difference
/// nobody would notice on a screen full of small charts.
/// </summary>
public sealed class SetupsModel : ScreenModel
{
    /// <summary>The box a thumbnail is drawn in. Small, because the point is a wall of them.</summary>
    public const int Width = 260;

    public const int Height = 110;

    private readonly LabApiClient _api;

    public SetupsModel(LabApiClient api) : base(api) => _api = api;

    /// <summary>The night being read. Defaults to the last session the store knows about.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AsOf { get; set; }

    /// <summary>Show only the setups this check rejected. The question the gallery is for.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Failed { get; set; }

    public SetupsView Setups { get; private set; } = SetupsView.Empty(string.Empty, "nothing has been read yet");

    /// <summary>Laid once per card, keyed by setup id, so the view does no work per render.</summary>
    public IReadOnlyDictionary<string, CandlestickGeometry> Thumbnails { get; private set; } =
        new Dictionary<string, CandlestickGeometry>(StringComparer.Ordinal);

    /// <summary>What went wrong recording an agreement, shown above the night rather than thrown.</summary>
    public string? Trouble { get; private set; }

    public override async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an agreement and re-reads the night.
    ///
    /// A form post rather than a fetch, so the page works with scripting off: the keyboard navigation
    /// is a convenience and the recording is the function, and the two are separable on purpose.
    /// see: Pages are server-rendered with no build step, and any script is local rather than fetched
    /// </summary>
    public async Task<IActionResult> OnPostAsync(
        string setupId,
        string? agreement,
        string? note,
        CancellationToken cancellationToken)
    {
        await base.OnGetAsync(cancellationToken).ConfigureAwait(false);

        // An empty string from a form field is "clear it", which is a different fact from
        // disagreeing and is the state every setup starts in.
        Trouble = await _api
            .RecordAgreementAsync(
                setupId,
                string.IsNullOrWhiteSpace(agreement) ? null : agreement,
                string.IsNullOrWhiteSpace(note) ? null : note,
                cancellationToken)
            .ConfigureAwait(false);

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        DateOnly? asked = DateOnly.TryParseExact(
            AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : LastSession();

        if (asked is not DateOnly session)
        {
            // No night asked for and none in the band. Saying so is the honest answer: guessing
            // today's date would show an empty page for a day the market may not have traded, which
            // looks exactly like a night the detectors found nothing on.
            Setups = SetupsView.Empty(string.Empty, "the status band names no session yet, so there is no night to show");
            ViewData["Title"] = "Setups";
            return;
        }

        AsOf = session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Setups = await _api.ReadSetupsAsync(session, Failed, cancellationToken).ConfigureAwait(false);
        ViewData["Title"] = $"Setups {AsOf}";

        var thumbnails = new Dictionary<string, CandlestickGeometry>(StringComparer.Ordinal);

        foreach (SetupCardView card in Setups.Long.Concat(Setups.Short))
        {
            thumbnails[card.SetupId] = CandlestickChart.Lay(card.Candles, [], Width, Height);
        }

        Thumbnails = thumbnails;
    }

    /// <summary>
    /// The session the status band says the lab last ran for, or nothing.
    ///
    /// Read from the band rather than from the machine clock, and not only because the clock is
    /// banned outside its abstraction. A person opening the gallery on a Sunday wants Friday's
    /// night, and today's date would give them an empty page for a day the market did not trade,
    /// which is indistinguishable on screen from a night the detectors found nothing on.
    /// </summary>
    private DateOnly? LastSession() =>
        ViewData["Status"] is LabStatusView status
        && !string.IsNullOrWhiteSpace(status.Session)
        && DateOnly.TryParseExact(
            status.Session, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly session)
            ? session
            : null;
}
