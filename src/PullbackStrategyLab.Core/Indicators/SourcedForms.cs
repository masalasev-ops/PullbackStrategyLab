namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The daily-bar quantities the trader's own clause forms compare, as arithmetic, from 7.4.
///
/// <b>Each one is a quantity a sourced form in SOURCES.md reads and the library did not carry.</b>
/// His ladder is the lab's ladder, and he also reads the 150 EMA and the slope of the averages; his
/// long scan looks back thirty days; his base lasts eight weeks and never loses the weekly 21 EMA;
/// his reclaim is a level undercut and taken back within the session; his chase filter measures from
/// the session's own low; his ceiling on a stop is half the daily range capped at 5%; and his squeeze
/// is read on the weekly averages. Each is frozen nightly as a candidate signal, so a rule written
/// over it can be replayed across every night from 7.4 before generation 1 registers.
/// see: Generation 1's baseline is written clause by clause from SOURCES.md, and a stated qualifier makes a gate recorded rather than screening
///
/// <b>Arithmetic only, in Core</b>, because the vectorizer freezes these and a replay reads them, and a
/// second implementation of one would eventually disagree with the first by an amount nobody could
/// see. Every input is on the adjusted basis and every result is a fraction or a flag. The lookbacks
/// that are not his, the five-session slope and the forty-session base, are stated as the author's
/// where the formula is declared.
/// </summary>
public static class SourcedForms
{
    /// <summary>The fourth average he names beside the lab's three.</summary>
    public const int LongestAveragePeriod = 150;

    /// <summary>Sessions of history the longest average and the weekly averages are read over.</summary>
    public const int ExtendedHistorySessions = 300;

    /// <summary>Sessions between the two readings a slope is taken over. The author's, a trading week.</summary>
    public const int SlopeLagSessions = 5;

    /// <summary>Calendar days his leader scan looks back over.</summary>
    public const int LeaderScanDays = 30;

    /// <summary>Sessions the base is measured over: eight weeks, the shortest base his sources describe.</summary>
    public const int BaseSessions = 40;

    /// <summary>The absolute cap on a stop, whatever the range, in his own words.</summary>
    public const decimal StopCapFraction = 0.05m;

    /// <summary>The two weekly averages the squeeze and the base are read on.</summary>
    public const int WeeklyShortPeriod = 9;

    public const int WeeklyMediumPeriod = 21;

    /// <summary>(close − the 150-session exponential average) / that average, or null short of 150 closes.</summary>
    public static decimal? DistanceFromLongestAverage(IReadOnlyList<decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(closes);

        if (closes.Count < LongestAveragePeriod)
        {
            return null;
        }

        decimal average = Averages.Exponential(closes, LongestAveragePeriod);
        return average == 0m ? null : (closes[^1] - average) / average;
    }

    /// <summary>
    /// How far an average moved over the last <see cref="SlopeLagSessions"/> sessions, as a fraction of
    /// where it started, each reading taken the way the engine computes the average: over the
    /// <paramref name="window"/> sessions ending at that session. Null short of that history.
    /// </summary>
    public static decimal? Slope(IReadOnlyList<decimal> closes, int period, int window)
    {
        ArgumentNullException.ThrowIfNull(closes);

        if (closes.Count < window + SlopeLagSessions)
        {
            return null;
        }

        IReadOnlyList<decimal?> series = Averages.ExponentialSeries(closes, period, window);

        return series[^1] is decimal now && series[^(SlopeLagSessions + 1)] is decimal then && then != 0m
            ? (now - then) / then
            : null;
    }

    /// <summary>
    /// The move over the last <see cref="LeaderScanDays"/> calendar days: the latest close over the
    /// close of the last session on or before thirty days earlier, less one. Null where the history
    /// does not reach that far back.
    /// </summary>
    public static decimal? ReturnOverLeaderWindow(IReadOnlyList<DateOnly> dates, IReadOnlyList<decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(closes);

        if (dates.Count == 0 || dates.Count != closes.Count)
        {
            return null;
        }

        DateOnly from = dates[^1].AddDays(-LeaderScanDays);
        int start = -1;

        for (int i = dates.Count - 1; i >= 0; i--)
        {
            if (dates[i] <= from)
            {
                start = i;
                break;
            }
        }

        return start < 0 || closes[start] == 0m ? null : (closes[^1] / closes[start]) - 1m;
    }

    /// <summary>
    /// The span of the last <see cref="BaseSessions"/> sessions, highest high to lowest low, in daily
    /// ranges of the latest close. Null short of that history or with no range.
    /// </summary>
    public static decimal? BaseSpanInRanges(
        IReadOnlyList<decimal> highs, IReadOnlyList<decimal> lows, decimal close, decimal averageDailyRange)
    {
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);

        decimal dailyRange = averageDailyRange * close;

        if (highs.Count < BaseSessions || lows.Count < BaseSessions || dailyRange <= 0m)
        {
            return null;
        }

        decimal high = highs.Skip(highs.Count - BaseSessions).Max();
        decimal low = lows.Skip(lows.Count - BaseSessions).Min();

        return (high - low) / dailyRange;
    }

    /// <summary>
    /// Whether the session undercut the 9-day average and closed back through it: below it at the low
    /// and above it at the close on a long, and the mirror on a short, the mirror being the author's.
    /// </summary>
    public static bool UndercutAndReclaimed(decimal high, decimal low, decimal close, decimal average, bool isLong) =>
        isLong
            ? low < average && close > average
            : high > average && close < average;

    /// <summary>
    /// How far the close sits from the session's own extreme, as a fraction of that extreme: from the
    /// low on a long, which is his chase filter, and from the high on a short, its mirror.
    /// </summary>
    public static decimal? FromTheSessionExtreme(decimal high, decimal low, decimal close, bool isLong)
    {
        decimal extreme = isLong ? low : high;
        return extreme == 0m ? null : isLong ? (close - low) / low : (high - close) / high;
    }

    /// <summary>The widest stop he takes on a name: half its daily range, capped at 5%.</summary>
    public static decimal EntryCeiling(decimal averageDailyRange) =>
        Math.Min(averageDailyRange / 2m, StopCapFraction);

    /// <summary>
    /// The closes a weekly chart draws: the last close of each calendar week, Monday to Sunday, the
    /// week in progress closing at the latest session. Oldest first.
    /// </summary>
    public static IReadOnlyList<decimal> WeeklyCloses(IReadOnlyList<DateOnly> dates, IReadOnlyList<decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(dates);
        ArgumentNullException.ThrowIfNull(closes);

        var weekly = new List<decimal>();
        int? week = null;

        for (int i = 0; i < dates.Count; i++)
        {
            int monday = dates[i].DayNumber - (((int)dates[i].DayOfWeek + 6) % 7);

            if (week == monday)
            {
                weekly[^1] = closes[i];
            }
            else
            {
                weekly.Add(closes[i]);
                week = monday;
            }
        }

        return weekly;
    }

    /// <summary>(weekly 9-week average − weekly 21-week average) / the 21-week, or null short of 21 weeks.</summary>
    public static decimal? WeeklyAverageGap(IReadOnlyList<decimal> weeklyCloses)
    {
        ArgumentNullException.ThrowIfNull(weeklyCloses);

        if (weeklyCloses.Count < WeeklyMediumPeriod)
        {
            return null;
        }

        decimal shorter = Averages.Exponential(weeklyCloses, WeeklyShortPeriod);
        decimal medium = Averages.Exponential(weeklyCloses, WeeklyMediumPeriod);
        return medium == 0m ? null : (shorter - medium) / medium;
    }

    /// <summary>(the week's close − the 21-week average) / the 21-week, or null short of 21 weeks.</summary>
    public static decimal? DistanceFromWeeklyMedium(IReadOnlyList<decimal> weeklyCloses)
    {
        ArgumentNullException.ThrowIfNull(weeklyCloses);

        if (weeklyCloses.Count < WeeklyMediumPeriod)
        {
            return null;
        }

        decimal medium = Averages.Exponential(weeklyCloses, WeeklyMediumPeriod);
        return medium == 0m ? null : (weeklyCloses[^1] - medium) / medium;
    }
}
