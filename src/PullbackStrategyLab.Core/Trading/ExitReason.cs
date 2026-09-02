namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// Why a position ended, and which of two rules that fired in one minute ended it.
///
/// <b>The trail never takes over from the fixed stop, and that is the rule 4.8 owed.</b> Both are
/// live from the entry fill to the close and the exit is whichever is reached first, so there is no
/// handover and no threshold at which one replaces the other. A handover rule would need a moment to
/// happen at, and the only moments available are authored ones: a number of R, a number of sessions,
/// a distance. Every one of those is a parameter nobody derived, and the corpus already carries
/// three arbitrary-within-a-range values it would rather not add a fourth to. Running both to the
/// end needs no parameter at all (see: The long trail is evaluated on the daily close and fills at
/// the next open).
///
/// <b>What running both needs instead is a total order, because a minute bar carries no order inside
/// it.</b> Two rules can name the same minute and the bar cannot say which price traded first, which
/// is the same ambiguity <see cref="FillModel.GiveUpComesFirst"/> was written for one level down.
/// So the order is stated here once rather than being an artefact of the sequence a walk happens to
/// evaluate its rules in, and <see cref="First"/> is the only thing that applies it.
///
/// <b>Two ranks and not four.</b> An exit at a minute's open happens before an exit inside that
/// minute, which is a fact about the bar rather than a choice. Within the open, giving up comes
/// first, on the same pessimism the model takes everywhere else and for one further reason: a gap
/// through the stop names <em>how</em> the loss occurred, and LossClassifier at 4.10 keys on that.
/// Recording such a minute as a trail exit would hide a gap loss inside a rule exit, where nothing
/// downstream could tell the two apart (see: A stop-out is noise when the ten-day return reached one
/// R, and cause of loss is two questions rather than one ordered list).
///
/// <b>The two rule-set exits never contest each other</b>, because one is the long side's and one is
/// the short side's and no position has both. That is asserted rather than assumed, so a later
/// session adding a third rule finds a rank missing rather than a silent tie.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class ExitReason
{
    /// <summary>The plan's give-up point, which is a resting instruction rather than a rule.</summary>
    public const string GaveUp = "give-up";

    /// <summary>The long trail: a daily close below the 9-day average, filling at the next open.</summary>
    public const string Trail = "trail";

    /// <summary>The short exit: an hourly bar closing back above the 50-day average.</summary>
    public const string Reclaim = "hourly-reclaim";

    /// <summary>
    /// The short trim at 3R. Present for completeness and never an exit reason on a position row: a
    /// trim reduces a position and does not end one, so it carries its own fill leg and leaves the
    /// row open (see: The short trim is 15% of the planned position, once, at 3R).
    /// </summary>
    public const string Trim = "trim";

    /// <summary>The three reasons that can close a position, in the order they resolve a tie.</summary>
    public static IReadOnlyList<string> ThatCloseAPosition { get; } = [GaveUp, Trail, Reclaim];

    /// <summary>
    /// Which of two reasons resolves first when both name the same minute. Lower wins.
    ///
    /// Refused rather than defaulted for anything else, on the grounds every direction comparison in
    /// this corpus is refused: a rank nobody wrote would sort to one end and the exit would silently
    /// become whichever reason the enumeration happened to reach first.
    /// </summary>
    public static int Rank(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return reason switch
        {
            GaveUp => 0,
            Trail => 1,
            Reclaim => 1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                $"'{reason}' is not one of the {ThatCloseAPosition.Count} reasons that close a position. "
                + $"'{Trim}' reduces a position rather than ending one and has no rank here, and a reason "
                + "with no rank would sort to one end of a tie and decide an exit by accident."),
        };
    }

    /// <summary>
    /// The exit that happened, out of every rule that named this minute, or null where none did.
    ///
    /// <see cref="ExitCandidate.AtTheOpen"/> outranks the reason, because an exit at the open of a
    /// bar happened before one reached inside it whatever rule sent either.
    /// </summary>
    public static ExitCandidate? First(IEnumerable<ExitCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .OrderBy(c => c.AtTheOpen ? 0 : 1)
            .ThenBy(c => Rank(c.Reason))
            .FirstOrDefault();
    }
}

/// <summary>
/// One rule saying this minute ended the position, with the price it names and whether it happened
/// at the minute's open.
///
/// <see cref="RestingPrice"/> is the price the rule named and not the price the fill got:
/// <see cref="FillModel"/> is what turns one into the other, and keeping them apart is what lets the
/// order be decided without pricing every candidate first.
/// </summary>
public sealed record ExitCandidate(string Reason, decimal RestingPrice, bool AtTheOpen);
