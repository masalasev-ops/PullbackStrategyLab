using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The trade journal as the page renders it: two blocks, never one table with a direction column.
///
/// <b>The two sides are two lists on the wire and two blocks on the screen.</b> The pooling rule is
/// easiest to break on a screen, where one table with a direction column looks tidier and quietly
/// invites a total. The two expectancies sit in the band and the page has no arithmetic that could
/// add them.
/// see: Long and short are never pooled into one figure
///
/// <b>The band carries one figure that is not about a trade at all.</b> How many positions closed in
/// the session they opened in is the size of an approximation the caps make, not a result: RiskGate
/// reads the book as it stood coming into the session, so such a position still occupied a slot the
/// next trigger was refused on. The decision to leave the gate where it is rests on that cost being
/// countable rather than argued, and a figure nobody reads is one nobody reviews the choice against.
/// see: RiskGate reads the book as it stood coming into the session, and what that costs is counted
///
/// <b>Every figure that could be absent says why rather than reading as nought.</b> An expectancy
/// over no trades is not nought, a loss with no aftermath yet is not unclassified, and a long with
/// no borrow cost is not a long that borrowed for free. Each of those is a sentence rather than a
/// blank.
/// </summary>
public sealed record JournalView(
    string AsOf,
    string? Absent,
    string LongExpectancy,
    string ShortExpectancy,
    IReadOnlyList<TradeRow> Long,
    IReadOnlyList<TradeRow> Short,
    int SlotsTheCapsCouldNotSee)
{
    /// <summary>What an expectancy reads as before either side has closed anything.</summary>
    public const string NotYet = "not yet";

    public bool HasTrades => Long.Count > 0 || Short.Count > 0;

    /// <summary>
    /// The one sentence on the page that is about the caps rather than about a trade.
    ///
    /// It is a caption rather than a legend, on the terms every scoreboard panel's condition is: a
    /// legend is read once and a caption is read every time.
    /// </summary>
    public string CapsCaption =>
        $"{SlotsTheCapsCouldNotSee} position(s) closed in the session they opened in, which the caps "
        + "could not see. RiskGate reads the book as it stood coming into the session, so each of "
        + "those occupied a slot a later trigger was refused on. The caps are tighter than the design "
        + "rather than looser, which is the safer of the two directions, and this is the size of it.";

    public static JournalView Empty(string asOf, string why) =>
        new(asOf, why, NotYet, NotYet, [], [], 0);

    /// <summary>An expectancy in R, or the sentence that says nothing has closed on that side.</summary>
    public static string ExpectancyOf(double? mean, int count) =>
        mean is null || count == 0
            ? NotYet
            : $"{mean.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}R over {count}";
}

/// <summary>
/// One closed trade, said in words.
///
/// <b>The plan-against-actual column is the one execution defects surface in</b>, and it is stated in
/// basis points because six cents on a six-dollar stock and six cents on a four-hundred-dollar one
/// are two different execution facts. Where an end gapped it says so instead of a number, because the
/// model charged nothing and the price moved anyway, and a basis-point figure would read as slippage.
/// </summary>
public sealed record TradeRow(
    string TradeId,
    string Ticker,
    string Direction,
    string OpenedSession,
    string ClosedSession,
    string EntryPrice,
    string ExitPrice,
    string ExitReason,
    double ResultR,
    int HeldSessions,
    int Shares,
    int TrimmedShares,
    string? RiskIntended,
    string RiskRealised,
    string? BorrowRateAssumed,
    string? BorrowCost,
    string? BorrowAvailability,
    double? EntryDifferenceBasisPoints,
    double? ExitDifferenceBasisPoints,
    string? EntryBasis,
    string? ExitBasis,
    string? PlannedGiveUp,
    int? PlannedShares,
    int? ExecutedShares,
    string? ReducedBecause,
    string? LossMechanism,
    string? Aftermath,
    string? AftermathBecause)
{
    /// <summary>The result in R, signed so a reader never has to work out which way is good.</summary>
    public string Result => ResultR.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "R";

    /// <summary>Whether the trade lost, which is the only kind a cause is written for.</summary>
    public bool Lost => ResultR < 0d;

    /// <summary>
    /// The plan against the actual, at both ends, in basis points or as the word that replaces one.
    ///
    /// A gap is named rather than numbered. The model charged nothing on it and the price moved
    /// anyway, so a basis-point figure beside a slipped one would be two different quantities in one
    /// column, which is what the fill's basis is carried to prevent.
    /// </summary>
    public string PlanAgainstActual
    {
        get
        {
            if (EntryDifferenceBasisPoints is null || ExitDifferenceBasisPoints is null)
            {
                return "not audited";
            }

            return $"{End(EntryDifferenceBasisPoints.Value, EntryBasis)} in, "
                + $"{End(ExitDifferenceBasisPoints.Value, ExitBasis)} out";
        }
    }

    /// <summary>
    /// What the position risked against what the plan intended it to, side by side.
    ///
    /// The two differ by the entry slippage and by nothing else, and the decision that put them on
    /// the row asked for the gap to be visible rather than assumed away.
    /// </summary>
    public string RiskIntendedAgainstRealised =>
        RiskIntended is null
            ? $"risk realised {RiskRealised}, and the intended risk is not audited"
            : $"risk intended {RiskIntended} beside risk realised {RiskRealised}, so the gap the entry "
              + "slippage opened is visible rather than assumed away";

    /// <summary>
    /// The two unmodelled short assumptions, or the sentence that says a long carries neither.
    ///
    /// Written out rather than left blank, because a blank cell on a long reads as a cost of nought
    /// and the fact is that the question does not arise.
    /// </summary>
    public string BorrowAssumed =>
        BorrowRateAssumed is null
            ? "not a short, so no borrow is assumed"
            : $"{BorrowCost} at {BorrowRateAssumed} a year assumed. {BorrowAvailability}";

    /// <summary>
    /// Why the loss happened, in the two answers it has, or the sentence for each state that is not
    /// an answer.
    ///
    /// A loss waiting on its ten-session horizon reads as waiting rather than as unclassified,
    /// because the two are different facts and only the second is a finding about the taxonomy.
    /// </summary>
    public string Cause
    {
        get
        {
            if (!Lost)
            {
                return string.Empty;
            }

            if (LossMechanism is null)
            {
                return "not classified yet";
            }

            return Aftermath is null
                ? $"{LossMechanism}, awaiting its ten-session horizon"
                : $"{LossMechanism}, {Aftermath}";
        }
    }

    /// <summary>How much of the position a short trim took out before the close, where one did.</summary>
    public string SharesHeld =>
        TrimmedShares == 0
            ? Shares.ToString(CultureInfo.InvariantCulture)
            : $"{Shares} less {TrimmedShares} trimmed";

    private static string End(double basisPoints, string? basis) =>
        string.Equals(basis, "gapped", StringComparison.Ordinal)
            ? "gapped"
            : basisPoints.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "bps";
}
