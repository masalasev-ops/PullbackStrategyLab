using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// What the experiment has done, as the front page states it.
///
/// <b>The two sides are separate properties and there is no total anywhere in this file.</b> A
/// front page is where the pooling rule is easiest to break, because one big number reads better
/// than two smaller ones. Any view that offered a sum would be offering it to a reader who has no
/// way of knowing a short carries a borrow assumption a long does not
/// (see: Long and short are never pooled into one figure).
/// </summary>
public sealed record ExperimentView(
    string AsOf,
    string? Absent,
    int Evenings,
    string FirstEvening,
    string LatestEvening,
    int LatestEveningGeneration,
    ExperimentSideView Long,
    ExperimentSideView Short,
    ExperimentRiskView? Risk)
{
    public static ExperimentView Empty(string asOf, string absent) =>
        new(asOf, absent, 0, string.Empty, string.Empty, 0,
            ExperimentSideView.Empty("long"), ExperimentSideView.Empty("short"), null);

    public bool IsAbsent => Absent is not null;

    /// <summary>
    /// Whether the record has a gap in it, which is the whole reason the count and the two dates
    /// are all stated rather than just the span.
    ///
    /// <b>Weekdays rather than calendar days</b>, because a record running over a weekend is not a
    /// record with a gap in it. It is an approximation and it is named as one: a market holiday
    /// inside the span reads as a gap of one, which overstates by a day rather than inventing a gap
    /// that is not there. The page says how many evenings it holds and between which dates, and a
    /// reader who wants the exact missing nights has the night screen for it.
    /// </summary>
    public bool RecordHasAGap => Evenings > 0 && WeekdaysInSpan > Evenings;

    public int WeekdaysInSpan
    {
        get
        {
            if (!DateOnly.TryParseExact(FirstEvening, "yyyy-MM-dd", out DateOnly first)
                || !DateOnly.TryParseExact(LatestEvening, "yyyy-MM-dd", out DateOnly last))
            {
                return 0;
            }

            int weekdays = 0;

            for (DateOnly day = first; day <= last; day = day.AddDays(1))
            {
                if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    weekdays++;
                }
            }

            return weekdays;
        }
    }
}

/// <summary>One side's record, and the latest evening's funnel on that side.</summary>
public sealed record ExperimentSideView(
    string Direction,
    int PatternsRecorded,
    int PlansWritten,
    int OrdersPlaced,
    int TradesClosed,
    ExperimentFunnelView Funnel)
{
    public static ExperimentSideView Empty(string direction) =>
        new(direction, 0, 0, 0, 0, ExperimentFunnelView.Empty);

    /// <summary>What this side is called on a page read by somebody who is new to it.</summary>
    public string InWords => string.Equals(Direction, "short", StringComparison.Ordinal)
        ? "to sell short"
        : "to buy";
}

/// <summary>The latest evening's three rungs on one side.</summary>
public sealed record ExperimentFunnelView(string Evening, int Examined, int Recorded, int Passed)
{
    public static ExperimentFunnelView Empty { get; } = new(string.Empty, 0, 0, 0);

    /// <summary>
    /// How wide to draw a rung, as a percentage of the widest. Returned as an invariant string
    /// because a page that formats a number in the machine's own culture draws a different bar on
    /// a machine whose decimal separator is a comma.
    /// </summary>
    public string WidthOf(int count) => Examined <= 0
        ? "0%"
        : string.Create(CultureInfo.InvariantCulture, $"{100.0 * count / Examined:0.##}%");
}

/// <summary>What one plan is allowed to lose, as the most recent plan recorded it.</summary>
public sealed record ExperimentRiskView(decimal Equity, decimal Fraction, decimal Budget)
{
    /// <summary>The fraction as a percentage, for a reader who does not read 0.0075 as three quarters of one percent.</summary>
    public string FractionInWords =>
        string.Create(CultureInfo.InvariantCulture, $"{Fraction * 100:0.##}%");
}
