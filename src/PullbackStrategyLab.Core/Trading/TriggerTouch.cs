using PullbackStrategyLab.Core.Detection;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// Whether a minute bar traded through a resting order's price.
///
/// <b>Touched, not closed through.</b> For a long, a minute whose high reaches the trigger. For a
/// short, a minute whose low reaches it. No margin either way, so a bar that reaches the price
/// exactly has reached it.
/// see: The trigger is touched, not closed through
///
/// <b>It is here rather than inside the resolver because it is not a detail of the resolver.</b> The
/// contention rule fills the earliest trigger and blocks the later ones, so this predicate decides
/// the order of a session across names as well as whether any one name fired. The three readings the
/// phrase carried, touched, closed through, and traded at or beyond on the close, order the same
/// session differently, and a component that could be re-implemented with a different reading is one
/// that could reorder a day's fills without any figure moving.
/// see: Plans are resting orders and fills go in time order when the caps bind
///
/// <b>Pure, on the footing <see cref="PositionSizing"/> already sets.</b> Nothing here reads a store
/// or a clock, so the rule is assertable over every price relationship rather than over the ones a
/// fixture happened to hold, and the resolver's own tests are about walking a session rather than
/// about which side of a comparison an inequality falls.
/// </summary>
public static class TriggerTouch
{
    /// <summary>
    /// Whether a bar spanning <paramref name="high"/> to <paramref name="low"/> reached
    /// <paramref name="triggerPrice"/> for a plan in <paramref name="direction"/>.
    ///
    /// The direction is compared against the two constants the store constrains it to rather than
    /// defaulted, so an unknown direction throws instead of being read as one of the two. A silent
    /// default here would resolve every short plan on the long side's comparison, which is a fill at
    /// a price the plan never named and nothing downstream could see.
    /// </summary>
    public static bool Reached(string direction, decimal triggerPrice, decimal high, decimal low)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        if (high < low)
        {
            throw new ArgumentOutOfRangeException(
                nameof(high),
                $"A minute bar cannot have a high of {high} below its low of {low}. The store holds "
                + "vendor figures verbatim, so a bar this shape is a fault in what was stored rather "
                + "than a bar that did not trade, and reading it as either touch or no touch would "
                + "answer a question about a price that never existed.");
        }

        return direction switch
        {
            SetupDirection.Long => high >= triggerPrice,
            SetupDirection.Short => low <= triggerPrice,
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}'. The two "
                + "sides compare opposite ends of a bar, so there is no reading of an unknown direction "
                + "that is safer than refusing it."),
        };
    }
}
