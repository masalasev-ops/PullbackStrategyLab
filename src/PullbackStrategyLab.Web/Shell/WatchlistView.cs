using System.Globalization;
using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The night's published candidates as the watchlist renders them.
///
/// <b>Two lists, never one</b>, on the same terms the gallery's view states: a single list with a
/// direction column is one careless loop away from a pooled figure on a screen, and a short carries
/// a borrow assumption a long does not.
/// see: Long and short are never pooled into one figure
///
/// <b>Derived from the gallery's own answer rather than fetched separately.</b> The capped set is
/// the night filtered on the flag the capper writes, so this view is a projection and not a second
/// read: two endpoints answering about one night is two definitions of what the night was.
/// </summary>
public sealed record WatchlistView(
    string AsOf,
    IReadOnlyList<WatchlistRowView> Long,
    IReadOnlyList<WatchlistRowView> Short,
    string? DegradedBecause,
    string? Nothing)
{
    public static WatchlistView Empty(string asOf, string why) => new(asOf, [], [], null, why);

    /// <summary>
    /// The capped rows of a night, ranked, from the gallery's answer.
    ///
    /// A row the cap cut is dropped rather than greyed: it was never a candidate, and a screen that
    /// showed it among the published ones would be showing the shared candidate list rather than the
    /// watchlist. A row that failed a gate is kept and greyed, because that is a candidate the lab
    /// looked at and rejected, and the reason is the point of the column.
    /// see: Failed checks are recorded rather than discarded
    /// </summary>
    public static WatchlistView Of(SetupsView night)
    {
        ArgumentNullException.ThrowIfNull(night);

        IReadOnlyList<WatchlistRowView> Published(IReadOnlyList<SetupCardView> side) =>
        [
            .. side
                .Where(c => c.CappedOut == false)
                .OrderBy(c => c.Rank ?? int.MaxValue)
                .ThenBy(c => c.Ticker, StringComparer.Ordinal)
                .Select(WatchlistRowView.Of),
        ];

        var view = new WatchlistView(
            night.AsOf, Published(night.Long), Published(night.Short), night.DegradedBecause, null);

        return view.Published == 0
            ? view with { Nothing = "the cap published nothing for this session" }
            : view;
    }

    public int Published => Long.Count + Short.Count;

    /// <summary>
    /// Names published on one side while a position is open on the other.
    ///
    /// <b>Empty until 4.7, and the banner says which rather than saying nothing.</b> The conflict is
    /// a name flagged one way against a position held the other, so it needs the position table,
    /// which does not exist. A banner that never fired would look identical to a banner that had
    /// nothing to fire about, which is the conflation the whole status band is careful about.
    /// </summary>
    public const string ConflictArrivesAt = "4.7";
}

/// <summary>
/// One published candidate: what would be entered, where it would give up, and how far that is.
///
/// <b>No share count.</b> The mockup draws one and RiskGate sizes at 4.6, so the column has no
/// source at 4.1. It is absent rather than rendered empty, because a blank column reads as a figure
/// the lab computed and got nothing for.
/// </summary>
public sealed record WatchlistRowView(
    string SetupId,
    string Ticker,
    int? Rank,
    bool PassedAll,
    decimal? TriggerPrice,
    decimal? StopPrice,
    decimal? StopDistanceRanges,
    IReadOnlyList<WatchlistFailureView> Failures)
{
    public static WatchlistRowView Of(SetupCardView card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return new WatchlistRowView(
            card.SetupId,
            card.Ticker,
            card.Rank,
            card.PassedAll,
            card.TriggerPrice,
            card.StopPrice,
            card.StopDistanceRanges,
            [.. card.Checks.Where(c => !c.Passed).Select(WatchlistFailureView.Of)]);
    }

    /// <summary>A price, or the words the gallery uses where the detector recorded none.</summary>
    public string Price(decimal? value) =>
        value is decimal present ? present.ToString("0.00", CultureInfo.InvariantCulture) : SetupCardView.NotSet;

    public string RankLabel => Rank?.ToString(CultureInfo.InvariantCulture) ?? "unranked";

    /// <summary>Greyed, which is what the corpus says a failed row looks like on this screen.</summary>
    public bool Greyed => !PassedAll;
}

/// <summary>
/// One failing check on a published row, and which of its clauses fell over.
///
/// <b>The clause is the 2.9 obligation reaching the screen.</b> A row greyed for
/// `tradable-shortable` told a reader nothing about whether it was turnover, price, capitalisation
/// or listing age, and this is the screen the corpus named as the place that question gets asked.
/// A gate with one clause names itself and says nothing further, so the extra words appear only
/// where they carry something.
/// </summary>
public sealed record WatchlistFailureView(string Check, IReadOnlyList<string> Clauses)
{
    public static WatchlistFailureView Of(SetupCheckRowView check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new WatchlistFailureView(check.Name, check.FailedClauses);
    }

    /// <summary>The check, and its failing clauses where it has more than one to fail.</summary>
    public string Label => Clauses.Count == 0
        ? Check
        : $"{Check} ({string.Join(", ", Clauses)})";
}
