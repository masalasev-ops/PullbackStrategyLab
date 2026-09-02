namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// Why a closed loss happened, which is two questions rather than one ordered list.
///
/// <b>A mechanism and an aftermath, and they are answerable at different times.</b> The mechanism
/// names <em>how</em> the loss occurred and is known the moment the trade closes: the exit either
/// filled at an open past the price it named or it crossed the book at that price. The aftermath
/// names <em>what happened next</em> and cannot be known for ten sessions after the trigger. Stating
/// them as one ranked list is what made them look like a conflict, and it would also have made the
/// first answer wait on the second.
/// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
///
/// <b>Both are asked of every loss, which is what makes the decision's own sentence true.</b> A gap
/// loss that later recovers satisfies both without contradiction, and it can only do so if the
/// second question is put to it. Asking the aftermath only of the losses that were not gaps is what
/// the ranked list would have done.
///
/// <b>Pure, on the footing every other rule in this namespace stands on.</b> Nothing here reads a
/// store or a clock, so the taxonomy is assertable over every relationship rather than over the ones
/// a night happened to produce.
/// </summary>
public static class LossCause
{
    /// <summary>
    /// Whether a result is a loss at all, taken after the borrow a short is charged.
    ///
    /// After borrow, because that is what the trade came to. A short whose price move was flat and
    /// whose borrow was not is a small loss and is one this taxonomy should be able to see.
    /// </summary>
    public static bool IsALoss(decimal netPnl) => netPnl < 0m;

    /// <summary>
    /// How the loss occurred, from the basis the exit fill was priced on.
    ///
    /// <b>From the basis and not from the size, and the difference is not academic.</b> The document
    /// said a gap loss is a "loss larger than one unit of risk" from the day the failure table was
    /// written, and that detector fires on every ordinary stop-out: a round trip costs two crossings,
    /// so an ordinary stop loses slightly more than one unit of risk by construction, which 4.7
    /// measured and asserted as an inequality. A taxonomy whose largest bucket is guaranteed to hold
    /// every member of another is one whose shares mean nothing.
    ///
    /// The basis says what actually happened: <see cref="FillModel.Gapped"/> is an exit that filled
    /// at an open already past the price it named, which is the mechanism the bucket is about.
    /// </summary>
    public static string MechanismOf(string exitBasis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exitBasis);

        return exitBasis switch
        {
            FillModel.Gapped => LossMechanism.Gap,
            FillModel.Slipped => LossMechanism.Ordinary,
            _ => throw new ArgumentOutOfRangeException(
                nameof(exitBasis),
                $"'{exitBasis}' is neither '{FillModel.Slipped}' nor '{FillModel.Gapped}'. A basis the store "
                + "does not admit would be classified as whichever arm a default fell through to, and the "
                + "arm it would fall through to is the ordinary one, which is the bucket that hides things."),
        };
    }

    /// <summary>
    /// One unit of risk expressed as a return from the trigger, which is what the aftermath's return
    /// is measured in.
    ///
    /// The boundary is stated in R because R is the unit every other figure in this lab is
    /// denominated in and a percentage is not comparable across names of different volatility. The
    /// return it is compared against is a fraction of the trigger price, taken from the trigger over
    /// the ten sessions after the session it was touched in, so the give-up distance has to be in the
    /// same terms, and the conversion happens here rather than at a call site. <b>Until 4.18 the
    /// return handed in was <c>forward_return.return_signed</c>, a fraction of the setup session's
    /// close over the ten sessions after the setup</b>, so the two sides of the comparison were over
    /// different populations while this comment said they were not.
    /// </summary>
    public static decimal OneRInReturn(decimal giveUpDistance, decimal triggerPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpDistance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(triggerPrice);

        return giveUpDistance / triggerPrice;
    }

    /// <summary>
    /// What happened after the loss, from the direction-signed ten-day return from the trigger.
    ///
    /// At or above one R the move happened anyway and the stop-out was noise, which points at
    /// execution. Below it the setup failed, which points at the filter and is the bucket selection
    /// changes can actually reduce. At or above rather than above, because one R is the point at
    /// which the trade would have paid for the risk it took and a return that reached it exactly did
    /// pay for it.
    /// </summary>
    public static string AftermathOf(decimal signedReturn, decimal oneRInReturn)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oneRInReturn);

        return signedReturn >= oneRInReturn ? LossAftermath.Noise : LossAftermath.FailedSetup;
    }
}

/// <summary>How a loss occurred, which is known the moment the trade closes.</summary>
public static class LossMechanism
{
    /// <summary>The exit filled at an open already past the price it named.</summary>
    public const string Gap = "gap";

    /// <summary>The exit crossed the book at the price it named, which is every other loss.</summary>
    public const string Ordinary = "ordinary";

    /// <summary>The two, named once so nothing compares a literal.</summary>
    public static IReadOnlyList<string> All { get; } = [Gap, Ordinary];
}

/// <summary>
/// What happened after the loss, which is not knowable for ten sessions after the trigger.
///
/// <b>A row awaiting its horizon carries null and not <see cref="Unclassified"/>.</b> The two are
/// different facts: null is a question the lab cannot answer yet, and unclassified is one it could
/// answer and could not place. Collapsing them would make the taxonomy's own coverage unreadable,
/// which is the reason `unclassified` exists as a value at all.
/// </summary>
public static class LossAftermath
{
    /// <summary>The ten-day move happened anyway, so the stop-out was noise. Points at execution.</summary>
    public const string Noise = "noise";

    /// <summary>The follow-up was flat or against the trade, so the setup failed. Points at the filter.</summary>
    public const string FailedSetup = "failed-setup";

    /// <summary>
    /// The horizon closed and the figure is absent, so nothing above fits.
    ///
    /// Written as a value rather than left null, and it is a real category: a taxonomy with no such
    /// bucket makes its own coverage unreadable, because a cause that is always assigned can never
    /// be shown to be missing one. A share that grows here is a finding about the classifier rather
    /// than about the trades.
    /// </summary>
    public const string Unclassified = "unclassified";

    /// <summary>The three, named once.</summary>
    public static IReadOnlyList<string> All { get; } = [Noise, FailedSetup, Unclassified];
}
