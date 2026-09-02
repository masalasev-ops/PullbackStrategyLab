using System.Globalization;
using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// A night's setups as the gallery renders them, and its own shape rather than the Api's type. The
/// two ends agree on a wire format rather than on an assembly.
/// see: The Web project reads through the Api and never opens the store
///
/// <b>Two lists, never one.</b> A single list with a direction column is one careless
/// <c>@foreach</c> away from a pooled figure on a screen, and a short carries a borrow assumption a
/// long does not.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record SetupsView(
    string AsOf,
    string? FailedCheck,
    int Flagged,
    IReadOnlyList<SetupCardView> Long,
    IReadOnlyList<SetupCardView> Short,
    IReadOnlyList<string> CheckNames,
    string? Nothing)
{
    public static SetupsView Empty(string asOf, string why) => new(asOf, null, 0, [], [], [], why);

    /// <summary>How many the filter left, both sides added up. A count of cards, not of figures.</summary>
    public int Shown => Long.Count + Short.Count;

    /// <summary>
    /// Which stages of this night had already ended other than cleanly when its setups were
    /// written, or null on an ordinary night.
    ///
    /// The third clause of the vendor-ceiling rule reaches the screen here. Every setup of a
    /// session carries the same mark, because the question is about the night rather than about
    /// the name, so the page states it once above both sides rather than on forty-four cards.
    ///
    /// Read from the cards rather than counted, so a night in which some rows carry a mark and
    /// others do not would show every distinct value rather than the first. That should not
    /// happen, and a surface that quietly showed one of two answers is how it would stay hidden.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    public string? DegradedBecause
    {
        get
        {
            string[] marks =
            [
                .. Long.Concat(Short)
                    .Select(c => c.DegradedBecause)
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            return marks.Length == 0 ? null : string.Join("; ", marks);
        }
    }

    public bool HasSetups => Shown > 0;
}

/// <summary>
/// One setup as a card in the gallery: the picture, the verdicts, and what a person thought.
///
/// The card carries every check, passed and failed alike. The gallery's use is reading what the
/// detector decided and disagreeing with it, and a card showing only the failures could not be
/// disagreed with on a pass.
/// see: Failed checks are recorded rather than discarded
/// </summary>
public sealed record SetupCardView(
    string SetupId,
    string Ticker,
    string Direction,
    int? Rank,
    bool? CappedOut,
    bool PassedAll,
    decimal? TriggerPrice,
    decimal? StopPrice,
    decimal? StopDistanceRanges,
    string? Agreement,
    string? AgreementNote,
    string? DegradedBecause,
    int? PlannedShares,
    IReadOnlyList<SetupCheckRowView> Checks,
    IReadOnlyList<Candle> Candles)
{
    /// <summary>
    /// A price as the card shows it, and the words "not set" where the detector recorded none.
    ///
    /// A setup whose geometry is degenerate has no trigger and no stop, and until 031 the column
    /// could not say so: the card rendered $0.00, which reads as a price rather than as an
    /// absence, and a give-up of nothing looks like the tightest stop on the page. The corpus
    /// rule is that a claim about what is shown is a claim about the surface, so the absence has
    /// to reach the surface rather than stop at the store.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public string Price(decimal? value) =>
        value is decimal present ? present.ToString("0.00", CultureInfo.InvariantCulture) : NotSet;

    public string Ranges(decimal? value) =>
        value is decimal present ? present.ToString("0.00", CultureInfo.InvariantCulture) : NotSet;

    /// <summary>What a card says where the detector recorded no quantity at all.</summary>
    public const string NotSet = "not set";

    /// <summary>
    /// The share count the plan carries, or the words the gallery uses where no plan was written.
    ///
    /// <b>Not a blank and not a nought.</b> A blank cell reads as a figure the lab computed and got
    /// nothing for, which is exactly the reason the column was left off the watchlist until 4.11, and
    /// a nought reads as a size the lab chose. A candidate the plan stage refused has neither.
    /// </summary>
    public string Shares =>
        PlannedShares is int shares ? shares.ToString("N0", CultureInfo.InvariantCulture) : NotSet;

    /// <summary>The rank, or a word saying there is none, because a blank cell reads as a zero.</summary>
    public string RankLabel => Rank?.ToString(CultureInfo.InvariantCulture) ?? "unranked";

    public int Failed => Checks.Count(c => !c.Passed);

    public string AgreementLabel => Agreement ?? "not looked at";
}

/// <summary>One check's verdict on a card, with the number it turned on.</summary>
public sealed record SetupCheckRowView(
    string Name,
    bool Passed,
    decimal? Value,
    string? Note,
    IReadOnlyList<string> FailedClauses)
{
    /// <summary>
    /// The number the check turned on, or the note where there was none.
    ///
    /// A check with no value did not fail to compute one; it was handed nothing, and the note says
    /// what was absent. Showing a blank would make the two indistinguishable on the screen where a
    /// person is deciding whether they agree with the verdict.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    public string Reading => CheckReading.Of(Name, Value) is CheckReading.Reading reading
        ? reading.Quantity
        : Value is decimal bare
            ? bare.ToString("0.####", CultureInfo.InvariantCulture)
            : Note ?? "no value";

    /// <summary>
    /// The threshold the number was tested against, so the reader can check the verdict rather than
    /// take it. Null where the check compares a word rather than a number.
    /// </summary>
    public string? Against => CheckReading.Of(Name, Value)?.Against;

    /// <summary>
    /// The result's own note, shown whenever there is one.
    ///
    /// <b>It used to be shown only when there was no value, and that dropped the notes that matter
    /// most.</b> `reached-ceiling` records the distance to the nearer average and a note saying it
    /// ran two of its three clauses because the anchored one arrives at 4.4; a calibration row's
    /// `tradable-shortable` records turnover and a note saying the market-cap clause was exempt.
    /// Both have a value, so both notes vanished from the one screen a person reads them on, while
    /// ARCHITECTURE says the setup record states the narrowing outright rather than leaving it to be
    /// inferred from a passing verdict. A caveat that is only in the store is not stated to anybody.
    /// </summary>
    public string? Caveat => Value is null ? null : Note;
}
