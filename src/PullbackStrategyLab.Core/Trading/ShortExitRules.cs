namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The short side's rule set: trim 15% of the planned position once at 3R, then close on an hourly
/// bar that closes back above the 50-day average.
///
/// <b>Not the long side's rule with a sign flipped, and the shape is why.</b> This side has two
/// rules where the long side has one, one of them reduces a position rather than ending it, and both
/// are evaluated inside the session rather than on its close. The done condition asks for separate
/// code paths because a single routine could only be their union, and the union is a strategy
/// nobody trades (see: Long and short are never pooled into one figure).
///
/// <b>Both numbers are recorded as arbitrary within a defensible range.</b> The 15 is inherited from
/// the strategy's own "about 15%" and the 3 is the level it names; nothing derives either. They are
/// constants here rather than configuration so a later session reads a choice with a citation rather
/// than a knob (see: The short trim is 15% of the planned position, once, at 3R).
///
/// <b>The trim into support is not here and its absence is a decision rather than an oversight.</b>
/// Support is defined nowhere in this corpus, so a level written now would be authored rather than
/// recovered, and phase 5 is where a rule variant carrying its own stated level is tested against
/// evidence (see: Trimming into support is dropped from the baseline rather than defined here).
/// </summary>
public static class ShortExitRules
{
    /// <summary>The fraction of the planned share count the trim takes, once.</summary>
    public const decimal TrimFraction = 0.15m;

    /// <summary>How many R of open profit fires the trim.</summary>
    public const decimal TrimAt = 3m;

    /// <summary>
    /// The price at which a short is <see cref="TrimAt"/> R in profit, measured from the price the
    /// entry actually got.
    ///
    /// <b>From the realised risk, not the planned one.</b> R is taken over the distance from the
    /// fill to the give-up point, because that is the money the position can lose; the plan's
    /// intended distance is a figure the slippage moved. Sizing the trim level off the intended
    /// distance would put the trim at a different multiple of the risk actually taken, which is the
    /// same two-numbers-one-name fault <c>risk_intended</c> beside <c>risk_realised</c> exists to
    /// make visible.
    /// </summary>
    public static decimal TrimLevel(decimal entryPrice, decimal giveUpPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(giveUpPrice);

        if (giveUpPrice <= entryPrice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(giveUpPrice),
                $"A short's give-up point of {giveUpPrice} is not above its entry of {entryPrice}. The "
                + "risk per share is the distance between them and a figure at or below zero would put "
                + "the trim level at or above the entry, where the position is not in profit at all.");
        }

        return entryPrice - (TrimAt * (giveUpPrice - entryPrice));
    }

    /// <summary>
    /// How many shares the trim takes, given what the plan was sized at and what is actually held.
    ///
    /// <b>A fraction of the planned count and not of what remains.</b> A fraction of the remainder is
    /// a decaying ladder that never fully exits and makes R accounting depend on how many times the
    /// rule has already fired; a fraction of the original is a fixed share count computable at plan
    /// time, which keeps it immutable with the rest of the plan.
    ///
    /// <b>Floored, and capped at what is held.</b> Floored because a share count is whole and the
    /// rounding has to go somewhere; capped because RiskGate may have reduced the order below the
    /// plan's size, and a trim larger than the position would close more than was ever opened.
    /// Both are guards on the arithmetic rather than rules, so neither is a parameter.
    /// </summary>
    public static int TrimShares(int plannedShares, int heldShares)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plannedShares);
        ArgumentOutOfRangeException.ThrowIfNegative(heldShares);

        int wanted = (int)Math.Floor(plannedShares * TrimFraction);

        return Math.Min(wanted, heldShares);
    }

    /// <summary>
    /// Whether an hourly bar closing at <paramref name="adjustedHourlyClose"/> has reclaimed the
    /// 50-day average, which ends the short.
    ///
    /// <b>The close is put on the adjusted basis and the average is not moved.</b> <c>ema_50</c> is
    /// computed on adjusted close and <c>intraday_bar</c> holds what the vendor printed, so the two
    /// live on different bases and a comparison between them is wrong by every split since. The
    /// caller converts, because the factor is a fact about a stored daily bar and this stays pure.
    ///
    /// <b>Above, not at.</b> A bar closing exactly on the average has not closed back above it, on
    /// the same reading <see cref="LongExitRules.TrailArmedBy"/> takes from the other side.
    /// </summary>
    public static bool Reclaimed(decimal adjustedHourlyClose, decimal fiftyDayAverage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustedHourlyClose);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fiftyDayAverage);

        return adjustedHourlyClose > fiftyDayAverage;
    }

    /// <summary>
    /// What multiplies a printed price to put it on the adjusted basis the averages are computed on.
    ///
    /// One session's own <c>adj_close / close</c>, taken from the daily bar the average was last
    /// computed against. It is exactly right while no action falls between that session and the
    /// minute being converted, and the store raises a rebuild demand on every action it observes, so
    /// the window in which it is wrong is the window in which the averages are stale anyway.
    /// </summary>
    public static decimal AdjustmentFactor(decimal close, decimal adjustedClose)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(close);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adjustedClose);

        return adjustedClose / close;
    }
}
