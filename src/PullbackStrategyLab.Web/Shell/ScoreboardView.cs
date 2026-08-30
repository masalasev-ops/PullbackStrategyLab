using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The scoreboard as the page renders it: three bands, two sides never added together.
///
/// <b>Every panel carries the condition under which it reads badly</b>, because a scoreboard that
/// can only show good news is decoration. Those conditions are written here rather than in the
/// template, so a reader of the code can see what each figure is supposed to warn about.
/// </summary>
public sealed record ScoreboardView(
    string AsOf,
    string? Absent,
    IReadOnlyList<PanelView> Health,
    IReadOnlyList<PanelView> Long,
    IReadOnlyList<PanelView> Short)
{
    public bool HasPanels => Health.Count > 0 || Long.Count > 0 || Short.Count > 0;

    public static ScoreboardView Empty(string asOf, string why) => new(asOf, why, [], [], []);
}

/// <summary>
/// One panel, said in words.
///
/// <b>The count is not optional and there is no branch that omits it.</b> A number without one is
/// not shown at all, because the failure this whole system exists to avoid is reading a pattern in
/// forty observations.
/// </summary>
public sealed record PanelView(
    string Name,
    string? Direction,
    string Figure,
    string? Low,
    string? High,
    int Rows,
    int? Effective,
    string Population,
    int? Minimum,
    string? WithheldBecause,
    int? Sessions = null,
    int? MinimumSessions = null)
{
    /// <summary>What the panel is, in words, rather than the identifier the store keys it on.</summary>
    public string Title => Name switch
    {
        "band0.nightsRecorded" => "Nights recorded",
        "band0.degradedRuns" => "Degraded runs",
        "band0.setupsOnFile" => "Setups on file",
        "band0.correctedRows" => "Corrected rows",
        "band0.worstLatenessMinutes" => "Worst lateness, minutes",
        "band1.vsLoose" => "Against loose controls",
        "band1.vsTight" => "Against tight controls",
        "band2.ceilingGap" => "Ceiling gap",
        _ when Name.StartsWith("band2.decile", StringComparison.Ordinal) =>
            $"Decile {Name["band2.decile".Length..]}",
        _ => Name,
    };

    /// <summary>
    /// The condition under which this panel reads badly, which is shown beside it.
    ///
    /// Written per panel rather than as a legend, because a legend is read once and a caption is
    /// read every time.
    /// </summary>
    public string? ReadsBadlyWhen => Name switch
    {
        "band0.degradedRuns" =>
            "Reads red above 5% of the record, because excluded nights are not missing at random",
        "band0.correctedRows" =>
            "A correction is a repair the night could not make. Rising means an input stage is failing rather than that the record is improving",
        "band0.worstLatenessMinutes" =>
            "Measured from the session's own end of day. Approaching the lateness bound means a repair is close to being refused outright",
        "band1.vsLoose" =>
            "Measures the whole funnel, thrust scan included. Expected to be the larger of the two",
        "band1.vsTight" =>
            "The honest comparison, and the one that can embarrass the project. Reads green only when the lower bound clears zero",
        "band2.ceilingGap" =>
            "Near zero means the stop is the binding constraint and no selection change can help. Wide means selection has room",
        _ when Name.StartsWith("band2.decile", StringComparison.Ordinal) =>
            "A flat curve across the deciles means the rank is decorative and the cap is truncating at random",
        _ => null,
    };

    /// <summary>Whether the panel is withheld for want of a sample, which is a state rather than a value.</summary>
    public bool Withheld => string.Equals(Figure, "withheld", StringComparison.Ordinal);

    /// <summary>The interval, or null where the panel carries none.</summary>
    public string? Interval =>
        Low is null || High is null ? null : $"[{Low}, {High}]";

    /// <summary>
    /// Whether the lower bound clears zero, which is what band 1 reads green on.
    ///
    /// Null where there is no interval, and null is not false: "no interval yet" and "the interval
    /// does not clear zero" are different sentences and only one of them is a finding.
    /// </summary>
    /// <remarks>
    /// TryParse rather than Parse, because Low is a TEXT column value carried through the read
    /// surface unchanged and this runs during template render, after the response has begun. A
    /// value this view did not write would have taken the page down mid-render instead of
    /// degrading to the null path the property already has.
    /// </remarks>
    public bool? ClearsZero =>
        Low is not null && decimal.TryParse(Low, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal low)
            ? low > 0m
            : null;

    /// <summary>
    /// Which rows the figure was computed over, shown on every panel without exception.
    ///
    /// <b>Two panels on this page use different populations.</b> Band 1 is over every flagged setup;
    /// band 2's decile curve is over the capped candidates, because a decile needs a rank and only a
    /// candidate carries one. At the calibrated thresholds those differ by three orders of magnitude,
    /// so a reader comparing the two without knowing which rows each used is comparing numbers whose
    /// samples have nothing to do with each other.
    ///
    /// Shown rather than left to a legend, on the same grounds the count is: a legend is read once
    /// and a caption is read every time.
    /// see: The subject is the flagged setup population, not the trade log
    /// </summary>
    public string Over => $"over {Population}";

    /// <summary>
    /// The count, said so the three numbers cannot be confused.
    ///
    /// The effective count is shown beside the row count wherever it exists, because they are
    /// different quantities: ten-day labels overlap, so the information in a thousand rows is worth
    /// fewer than a thousand observations. Where a minimum exists it is shown beside both, because
    /// the panel is what a checkpoint fires on and a target nobody can see is a date in disguise.
    ///
    /// <b>All three from the first night, not once there is enough to say something.</b> The figure
    /// is withheld until an interval means anything; the counts are not, because a number climbing
    /// from nothing tells a reader how far off the answer is and whether the overlap is costing
    /// forty percent or eighty-five. A calendar could say neither.
    /// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
    /// </summary>
    public string Count
    {
        get
        {
            string rows = Rows.ToString("N0", CultureInfo.InvariantCulture);

            if (Effective is not int effective)
            {
                return $"n {rows}";
            }

            string counted =
                $"n {rows} rows, {effective.ToString("N0", CultureInfo.InvariantCulture)} effective";

            if (Minimum is int minimum)
            {
                counted += $" of {minimum.ToString("N0", CultureInfo.InvariantCulture)} needed";
            }

            // The session count, beside the other two rather than instead of them. It is the second
            // half of the trigger and it was computed and dropped from the day the interval was
            // written: `PairedInterval.Estimate` carried it, the builder read five of six fields,
            // the store had no column, and this line rendered two numbers. The only place it
            // appeared was inside the withheld sentence, which is null the moment an interval
            // exists, so the count vanished exactly when it started to decide how much the interval
            // was worth.
            return Sessions is int sessions
                ? $"{counted}, over {sessions.ToString("N0", CultureInfo.InvariantCulture)} session(s)"
                    + (MinimumSessions is int floor
                        ? $" of {floor.ToString("N0", CultureInfo.InvariantCulture)} needed"
                        : string.Empty)
                : counted;
        }
    }

    /// <summary>
    /// Whether this panel has reached the trigger, which is <b>both</b> conditions and not either
    /// of them.
    ///
    /// <b>It read one of the two until this was repaired, and it said so in the words of the
    /// whole.</b> 3.6 fires on at least twenty sessions <b>and</b> at least 262 effective
    /// observations, BUILD_PLAN says both are needed because they are settled by different things,
    /// and this property compared the effective count alone and then rendered "the minimum sample
    /// is reached". A fortnight of very wide nights reaches the minimum sample before it reaches
    /// twenty sessions, so the page could have announced the trigger on a panel whose interval the
    /// bootstrap had refused to produce at all.
    ///
    /// Null where the panel carries neither minimum, and null is not false: "this panel answers no
    /// question a checkpoint waits on" and "it waits and has not arrived" are different sentences.
    /// see: The minimum sample is 262 effective observations, ratified at two points and 90% power
    /// </summary>
    /// <remarks>
    /// <b>A panel missing one of the two counts is never "reached", and that is deliberate rather
    /// than incidental.</b> Every row written before migration 034 carries a minimum and no session
    /// count, and falling back to whichever half is present would reproduce the exact defect this
    /// property was repaired for: a legacy row above 262 observations would announce the trigger on
    /// evidence alone. Every such row in the live store reads nought effective today, so the case is
    /// hypothetical, and "hypothetical" is how each of the shapes in CLAUDE.md's list started. The
    /// panel still reads "not yet an answer" rather than falling silent, because that is true of it.
    /// </remarks>
    public bool? Reached => Minimum is null && MinimumSessions is null
        ? null
        : ReachedObservations == true && ReachedSessions == true;

    /// <summary>Whether the evidence condition is met, on its own.</summary>
    public bool? ReachedObservations => Minimum is int minimum && Effective is int effective
        ? effective >= minimum
        : null;

    /// <summary>Whether the session condition is met, on its own.</summary>
    public bool? ReachedSessions => MinimumSessions is int floor && Sessions is int sessions
        ? sessions >= floor
        : null;

    /// <summary>
    /// Which half of the trigger is still short, in words, on the panel.
    ///
    /// <b>Naming the half is the point of having two counts rather than one.</b> "Below the minimum
    /// sample" sends a reader to wait for evidence, and if what is actually short is sessions then
    /// no amount of evidence closes it: a night of eighty pairs moves the effective count and moves
    /// the session count by one whatever it carries. A reader told only that the trigger has not
    /// fired cannot tell which of the two they are waiting on, which is the same defect the
    /// withheld sentence was repaired for one branch up.
    /// </summary>
    public string? ShortOf
    {
        get
        {
            if (Reached is not bool reached || reached)
            {
                return null;
            }

            List<string> shortfalls = [];

            if (ReachedSessions == false && Sessions is int sessions && MinimumSessions is int floor)
            {
                shortfalls.Add(
                    $"{(floor - sessions).ToString("N0", CultureInfo.InvariantCulture)} more session(s)");
            }

            if (ReachedObservations == false && Effective is int effective && Minimum is int minimum)
            {
                shortfalls.Add(
                    $"{(minimum - effective).ToString("N0", CultureInfo.InvariantCulture)} more effective observation(s)");
            }

            // A panel whose counts are both met and which still does not read as reached is one
            // recorded before the session count existed. Saying so is better than saying nothing:
            // the alternative is a panel that is not an answer and gives no reason, which is the
            // fault the withheld sentence was repaired for one branch up.
            if (shortfalls.Count == 0 && ReachedSessions is null)
            {
                return "a session count, which this panel was recorded before the store kept";
            }

            return shortfalls.Count == 0 ? null : string.Join(" and ", shortfalls);
        }
    }
}
