using PullbackStrategyLab.Core.Trading;

namespace PullbackStrategyLab.Core.Measurement;

/// <summary>
/// Which one-minute windows the calibration backfill buys, and which rows they leave short, from 7.7.
///
/// <b>The scope is derived from what 7.8 reads rather than chosen.</b> 7.8's levels are the hourly 9
/// and 21 averages built from minutes, so a row needs minutes for its entry session and for enough
/// sessions before it for the hourly 21 to have converged, and the window cannot be the entry session
/// alone. One request buys at most the vendor's 120 days, so each flagged calibration row is served by
/// the 120-day window ending at its entry session, one request per name per window, and a window is
/// not bought twice for rows it already covers.
/// see: A one-time backfill is outside the nightly ceiling, whether it buys daily history or minutes
///
/// <b>Deduplicated from the latest row backwards.</b> For each name the latest entry session not yet
/// covered ends a window, every row of that name whose entry falls inside it is served by it, and the
/// next window ends at the latest entry left over. That buys the fewest windows the rule allows, and it
/// leaves one kind of row short: one whose entry lands near a window's start, with fewer warm-up
/// sessions inside the window than the hourly 21 needs. **Those rows are reported with their shortfall
/// rather than left silently short**, and buying a second window for them was not the rule written.
///
/// Pure, so the plan is proved over authored cases and the stage that spends the calls only carries it
/// out.
/// </summary>
public static class MinuteBackfillPlan
{
    /// <summary>The widest span one intraday request may cover, in calendar days, as the vendor documents it.</summary>
    public const int WindowDays = 120;

    /// <summary>
    /// The longest hourly average 7.8 reads, whose warm-up is what a window has to hold before an entry.
    /// </summary>
    public const int LongestHourlyPeriod = EntryRule.LongPeriod;

    /// <summary>
    /// How many hourly bars a warm-up needs: three times the period, the lab's own convergence rule for
    /// an exponential average, which is why the daily warm-up is 150 sessions for the 50-day average.
    /// </summary>
    public const int WarmupHourlyBars = EntryRule.WarmupHourlyBars;

    /// <summary>
    /// Complete hourly bars in a regular session on the grid anchored to the open. The closing half hour
    /// is not an hourly bar under the standing grid decision, so it is not counted towards a warm-up;
    /// if 7.8 lets it contribute a value, a warm-up counted without it is longer rather than shorter.
    /// see: The hourly grid anchors to the session open, and the closing stub is not an hourly bar
    /// </summary>
    public const int HourlyBarsPerSession = 6;

    /// <summary>Sessions a row needs inside its window before its entry session: sixty-three bars at six a session.</summary>
    public const int WarmupSessions = (WarmupHourlyBars + HourlyBarsPerSession - 1) / HourlyBarsPerSession;

    /// <summary>One flagged calibration row, with the session its plan would have been live in.</summary>
    public sealed record Row(string SetupId, string Ticker, DateOnly EntrySession);

    /// <summary>One request: a name, the first and last calendar day it covers, and the rows it serves.</summary>
    public sealed record Window(string Ticker, DateOnly From, DateOnly To, IReadOnlyList<Row> Rows);

    /// <summary>
    /// A row left short of warm-up, with the unbroken run of bought sessions it has before its entry.
    /// </summary>
    public sealed record Shortfall(Row Row, DateOnly WindowFrom, int WarmupSessionsHeld)
    {
        public int Missing => WarmupSessions - WarmupSessionsHeld;
    }

    public sealed record Plan(IReadOnlyList<Window> Windows, IReadOnlyList<Shortfall> Short);

    /// <summary>The first calendar day of the window ending at <paramref name="entrySession"/>.</summary>
    public static DateOnly WindowStart(DateOnly entrySession) => entrySession.AddDays(-(WindowDays - 1));

    /// <summary>
    /// The plan over <paramref name="rows"/>, with warm-up counted in <paramref name="sessions"/>, the
    /// sessions the store knows traded. The lab authors no calendar, so a session is a day the store
    /// holds a daily bar for, on the terms the intraday fetch's own anchor window counts them.
    /// </summary>
    public static Plan Of(IReadOnlyList<Row> rows, IReadOnlyList<DateOnly> sessions)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(sessions);

        DateOnly[] traded = [.. sessions.Distinct().Order()];
        var windows = new List<Window>();
        var shortRows = new List<Shortfall>();

        foreach (IGrouping<string, Row> name in rows
                     .GroupBy(r => r.Ticker, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Latest first, and ties broken on the identifier so the plan is the same plan every run.
            Row[] remaining = [.. name.OrderByDescending(r => r.EntrySession).ThenBy(r => r.SetupId, StringComparer.Ordinal)];
            var named = new List<Window>();
            int at = 0;

            while (at < remaining.Length)
            {
                DateOnly to = remaining[at].EntrySession;
                DateOnly from = WindowStart(to);
                var served = new List<Row>();

                while (at < remaining.Length && remaining[at].EntrySession >= from)
                {
                    served.Add(remaining[at]);
                    at++;
                }

                named.Add(new Window(name.Key, from, to, served));
            }

            // A row's warm-up is the run of traded sessions immediately before its entry that some
            // window of the same name bought, because an average needs an unbroken series: sessions an
            // earlier window happens to reach count, and a gap between two windows ends the run.
            foreach (Window window in named)
            {
                foreach (Row row in window.Rows)
                {
                    int found = Array.BinarySearch(traded, row.EntrySession);
                    int i = (found >= 0 ? found : ~found) - 1;
                    int held = 0;

                    while (i >= 0 && held < WarmupSessions && named.Any(w => traded[i] >= w.From && traded[i] <= w.To))
                    {
                        held++;
                        i--;
                    }

                    if (held < WarmupSessions)
                    {
                        shortRows.Add(new Shortfall(row, window.From, held));
                    }
                }
            }

            // Oldest first within a name, which is the order a person reads a history in.
            windows.AddRange(named.OrderBy(w => w.To));
        }

        return new Plan(windows, [.. shortRows.OrderBy(s => s.Row.Ticker, StringComparer.Ordinal).ThenBy(s => s.Row.EntrySession)]);
    }
}
