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
    decimal TriggerPrice,
    decimal StopPrice,
    decimal StopDistanceRanges,
    string? Agreement,
    string? AgreementNote,
    IReadOnlyList<SetupCheckRowView> Checks,
    IReadOnlyList<Candle> Candles)
{
    public string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public string Ranges(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The rank, or a word saying there is none, because a blank cell reads as a zero.</summary>
    public string RankLabel => Rank?.ToString(CultureInfo.InvariantCulture) ?? "unranked";

    public int Failed => Checks.Count(c => !c.Passed);

    public string AgreementLabel => Agreement ?? "not looked at";
}

/// <summary>One check's verdict on a card, with the number it turned on.</summary>
public sealed record SetupCheckRowView(string Name, bool Passed, decimal? Value, string? Note)
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
