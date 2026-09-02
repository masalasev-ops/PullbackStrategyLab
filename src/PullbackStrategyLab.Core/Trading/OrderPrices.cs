using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// The two prices a plan commits to, derived from the final pullback session's regular-hours
/// extremes and not from the screening geometry.
///
/// <b>`PullbackGeometry.Of` computes an entry level and a give-up point from the whole dip, and those
/// are screening quantities.</b> They feed `trigger-near` and `exit-tight` and nothing that places an
/// order; the give-up point in particular is the low of the whole dip, which the corpus rejects as an
/// order reference under "Why the exit-tight check is the interesting one". PlanBuilder copied that
/// pair into the plan from 4.16 until 4.18, which is the reading the decision names as the one to
/// refuse, and the 4.13 sign-off found it by reading the stage against the decision
/// (see: The order prices are derived from the final pullback session's minutes, not from the
/// screening geometry).
///
/// <b>The source is the daily bar, and the decision's text said minute bars.</b> Both name the same
/// two numbers: the vendor's daily bar carries the regular-hours extremes, established on 2026-09-02
/// by one <c>eod/AAPL.US</c> call for 2026-08-25 read against the captured minutes of that session,
/// 313.59 and 308.21 both ways where the extended-hours low was 290.46. The minute form could not
/// hold at 18:30, because the minutes a session's evening fetch buys are the previous evening's
/// names', and the daily bar is in the store by then. The floor is therefore the final session's
/// low and the ceiling its high, as the decision states them, read from where they are.
///
/// <b>The trigger is the same session's extreme on the entry side.</b> A long enters through the
/// final pullback session's high and a short through its low, which is the reading of "the order
/// prices come from the final pullback session" taken at 4.18 and stated as one: the decision
/// names the floor, the ceiling and the offset and does not name the trigger separately.
///
/// <b>The offset is a fraction of the average daily range, in price.</b> Scale-free across names and
/// the same both ways, so neither side carries an offset the other does not. The 0.1 is arbitrary
/// within a defensible range and is recorded as such in the decision.
/// </summary>
public static class OrderPrices
{
    /// <summary>How far beyond the session's extreme the give-up point sits, in average daily ranges.</summary>
    public const decimal GiveUpOffsetInRanges = 0.1m;

    /// <summary>
    /// The trigger and the give-up point for one direction, from the final pullback session's high
    /// and low and the name's average daily range in price.
    ///
    /// Refuses a range that is not positive and a session whose low is above its high, rather than
    /// returning a pair sized on a nought or the wrong way round: a give-up point on top of the
    /// trigger divides into the risk budget as many times as anyone likes
    /// (see: A gate handed an absent or degenerate quantity fails rather than passing).
    /// </summary>
    public static Pair For(string direction, decimal sessionHigh, decimal sessionLow, decimal averageDailyRange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageDailyRange);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionLow);

        if (sessionHigh < sessionLow)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionHigh),
                $"A session high of {sessionHigh} below its low of {sessionLow} is not a session, and a pair "
                + "derived from it would put the give-up point on the wrong side of the trigger.");
        }

        decimal offset = averageDailyRange * GiveUpOffsetInRanges;

        return direction switch
        {
            SetupDirection.Long => new Pair(sessionHigh, sessionLow - offset),
            SetupDirection.Short => new Pair(sessionLow, sessionHigh + offset),
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                $"'{direction}' is neither long nor short. The two sides put the give-up point on opposite "
                + "sides of the trigger, so a default would price every plan of the unknown side backwards."),
        };
    }

    /// <summary>The trigger and the give-up point, both raw prices, with the distance between them in money.</summary>
    public sealed record Pair(decimal Trigger, decimal GiveUp)
    {
        public decimal Distance => Math.Abs(Trigger - GiveUp);
    }
}
