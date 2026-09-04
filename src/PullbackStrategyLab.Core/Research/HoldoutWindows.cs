namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The eight holdout windows: which calendar quarters they are, when each becomes available, and
/// the arithmetic that decides both.
///
/// <b>They cannot all exist when the register is created, and that is the whole shape of this
/// type.</b> Evidence accumulates forward only, so a window is a quarter the lab collected in full
/// and the register fills up over two years rather than starting full. Nothing here reads a store:
/// given the first session the lab recorded, it says which quarters are windows and when each one
/// matures, and the registry compares that against what the store holds
/// (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight).
///
/// <b>The first window is the first quarter that begins on or after the first recorded session, and
/// that is a narrower rule than "three months after go-live".</b> A window has to be quarters of
/// forward-collected evidence, so a quarter the lab was running for only part of is not one: the
/// lab's first night was 2026-08-27, which is inside the third quarter of 2026, so that quarter
/// holds sessions nobody recorded and the first window is the fourth. Anything looser would make the
/// first window a mixture of evidence and absence, which is the population defect this corpus keeps
/// finding one level up.
/// </summary>
public static class HoldoutWindows
{
    /// <summary>
    /// How many windows exist in total, ever. Spending is a designed dead end when they run out.
    /// see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight
    /// </summary>
    public const int Capacity = 8;

    /// <summary>One calendar quarter each, which is what makes a window's boundaries not a choice.</summary>
    public const int MonthsPerWindow = 3;

    /// <summary>
    /// The eight windows, oldest first, for a lab whose first recorded session is
    /// <paramref name="firstSession"/>.
    ///
    /// The list is the same list on every call: a window's identity is a fact about the calendar and
    /// about one date, so nothing here can drift with when it is asked.
    /// </summary>
    public static IReadOnlyList<HoldoutWindow> Schedule(DateOnly firstSession)
    {
        DateOnly start = FirstQuarterFullyForwardOf(firstSession);
        var windows = new List<HoldoutWindow>(Capacity);

        for (int ordinal = 1; ordinal <= Capacity; ordinal++)
        {
            DateOnly next = start.AddMonths(MonthsPerWindow);

            windows.Add(new HoldoutWindow(
                Identify(start),
                ordinal,
                start,
                next.AddDays(-1),

                // A window matures the day its quarter completes, which is the first day of the
                // quarter after it. Not the last day of its own: a quarter is not collected in full
                // until its final session has closed and been recorded.
                next));

            start = next;
        }

        return windows;
    }

    /// <summary>
    /// The windows that have matured by <paramref name="asOf"/>, which is what the register should
    /// hold on that date and no more.
    /// </summary>
    public static IReadOnlyList<HoldoutWindow> MaturedBy(DateOnly firstSession, DateOnly asOf) =>
        [.. Schedule(firstSession).Where(w => w.MaturesOn <= asOf)];

    /// <summary>
    /// The first quarter that begins on or after a date, which is the first quarter the lab could
    /// have collected in full.
    ///
    /// A first session landing exactly on a quarter boundary makes that quarter the first window;
    /// anything else pushes to the next. The boundary case is the one worth stating, because it is
    /// the only date on which "the quarter containing it" and "the first quarter after it" are the
    /// same quarter.
    /// </summary>
    public static DateOnly FirstQuarterFullyForwardOf(DateOnly firstSession)
    {
        var containing = new DateOnly(firstSession.Year, (QuarterOf(firstSession) - 1) * MonthsPerWindow + 1, 1);

        return containing == firstSession ? containing : containing.AddMonths(MonthsPerWindow);
    }

    /// <summary>The quarter of the year a date falls in, one to four.</summary>
    public static int QuarterOf(DateOnly date) => ((date.Month - 1) / MonthsPerWindow) + 1;

    /// <summary>A window's name, which is the quarter it is and nothing else.</summary>
    public static string Identify(DateOnly quarterStart) =>
        $"{quarterStart.Year:0000}-Q{QuarterOf(quarterStart)}";
}

/// <summary>
/// One holdout window: a calendar quarter of forward-collected evidence, its place in the eight,
/// and the day it becomes available to spend.
/// </summary>
/// <param name="WindowId">The quarter, as `2026-Q4`.</param>
/// <param name="Ordinal">One to eight, oldest first, which is the order they are spent in.</param>
/// <param name="Start">The first day of the quarter.</param>
/// <param name="End">The last day of the quarter.</param>
/// <param name="MaturesOn">
/// The first day after the quarter, which is the earliest date the window can be spent on. A window
/// is not available on the last day of its own quarter, because that day's session has not closed.
/// </param>
public sealed record HoldoutWindow(
    string WindowId,
    int Ordinal,
    DateOnly Start,
    DateOnly End,
    DateOnly MaturesOn);
