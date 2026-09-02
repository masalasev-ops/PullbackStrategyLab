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
/// <b>The share count arrives at 4.11 and it is the plan's rather than the gate's.</b> The column had
/// no source at 4.1, when sizing was RiskGate's and RiskGate did not exist, and it was left off rather
/// than rendered empty because a blank column reads as a figure the lab computed and got nothing for.
/// That is no longer where a size comes from: PlanBuilder writes one at 18:30 and this page publishes
/// at 18:40, so the column has a source ten minutes before the page runs, and a page that went on
/// omitting it would understate what the lab committed to.
/// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
///
/// <b>What it is not is the size that will be placed.</b> RiskGate may reduce it at the trigger or
/// block the order outright, hours after anybody reads this screen, so the column is the intention
/// and the executed figure is the trade journal's. The two are compared on `plan_audit` and never
/// here.
/// </summary>
public sealed record WatchlistRowView(
    string SetupId,
    string Ticker,
    int? Rank,
    bool PassedAll,
    decimal? TriggerPrice,
    decimal? StopPrice,
    decimal? StopDistanceRanges,
    int? PlannedShares,
    decimal? PlannedTrigger,
    decimal? PlannedGiveUp,
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
            card.PlannedShares,
            card.PlannedTrigger,
            card.PlannedGiveUp,
            [.. card.Checks.Where(c => !c.Passed).Select(WatchlistFailureView.Of)]);
    }

    /// <summary>
    /// The trigger this screen publishes: the plan's where a plan was written, the detector's where
    /// none was.
    ///
    /// <b>The two are different numbers from 4.18 and the page shows the one the lab will act on.</b>
    /// The detector's pair is the screening geometry and feeds two gates; the plan's is the final
    /// pullback session's extremes with the give-up point 0.1 ADR beyond, which is what an order is
    /// placed at. A greyed row and a capped-out row have no plan and show what the detector computed,
    /// which is all there is to show for a row the lab declined.
    /// </summary>
    public decimal? PublishedTrigger => PlannedTrigger ?? TriggerPrice;

    /// <summary>The give-up point this screen publishes, on the same terms as the trigger.</summary>
    public decimal? PublishedGiveUp => PlannedGiveUp ?? StopPrice;

    /// <summary>A price, or the words the gallery uses where the detector recorded none.</summary>
    public string Price(decimal? value) =>
        value is decimal present ? present.ToString("0.00", CultureInfo.InvariantCulture) : SetupCardView.NotSet;

    /// <summary>
    /// The share count the plan carries, or the words the page uses where no plan was written.
    ///
    /// A greyed row and a capped-out row both reach this screen and neither is planned, so the
    /// absent form is the ordinary case rather than the exception.
    /// </summary>
    public string Shares =>
        PlannedShares is int shares ? shares.ToString("N0", CultureInfo.InvariantCulture) : SetupCardView.NotSet;

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
