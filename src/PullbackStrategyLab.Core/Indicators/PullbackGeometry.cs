namespace PullbackStrategyLab.Core.Indicators;

/// <summary>
/// The shape of a pullback: where the thrust peaked, how far the drift has given back, and where
/// the trade would be entered and abandoned.
///
/// In Core because two components need the same numbers and only one of them writes them down. The
/// detector uses them to decide checks; SignalVectorizer freezes them as evidence. A second
/// implementation would eventually disagree, and a disagreement here is invisible: every quantity is
/// a plausible small number whichever way it was computed.
///
/// <b>The mirror is a parameter, not a second class.</b> Long and short are the same geometry read
/// in opposite directions, and writing them twice is how the two drift apart. What is genuinely not
/// a sign flip lives in the detectors, which is where the corpus says the three differences are.
///
/// Everything is on the adjusted basis except the trigger and the stop, which are raw prices because
/// they are what trades tomorrow. Mixing them produces a plan that says buy at 37.67 when the real
/// price is 150.68, and it is silent because both numbers look reasonable.
/// </summary>
public static class PullbackGeometry
{
    /// <summary>
    /// The geometry of one setup, or null where the window cannot support one.
    ///
    /// <paramref name="thrustIndex"/> is the position of the session the mover scan flagged, and
    /// <paramref name="thrustSpanSessions"/> is how many sessions of move that scan flags, which
    /// <see cref="ScanSpans.SessionsFor"/> answers. The thrust runs over the last
    /// <paramref name="thrustSpanSessions"/> sessions ending at the flag; the extreme is the
    /// furthest the move reached from the start of that span to the as-of date, and the pullback is
    /// everything after it. A thrust whose extreme is the last bar has no pullback yet, which is a
    /// real state and is returned as zero bars rather than as nothing.
    ///
    /// <b>The span is a parameter because the six scans do not flag the same kind of move.</b> Until
    /// 3.0(c) this took every thrust as one session. That is right for the four day scans and wrong
    /// for `leader` and `laggard`, which rank on a twenty-session change: the retrace's denominator
    /// became one session of a twenty-session run, and the extreme was found at the flag whenever
    /// the real high sat before it. Both errors push the same way, and each one on its own produces
    /// a number that reads as an ordinary shallow pullback.
    /// </summary>
    public static Pullback? Of(IReadOnlyList<Bar> bars, int thrustIndex, int thrustSpanSessions, bool isLong)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentOutOfRangeException.ThrowIfLessThan(thrustSpanSessions, 1);

        if (bars.Count == 0 || thrustIndex < 0 || thrustIndex >= bars.Count)
        {
            return null;
        }

        int thrustStart = ThrustStart(thrustIndex, thrustSpanSessions);

        // The origin is the close before the move, which is what the move is measured from. With
        // the move starting on the first bar of the window there is nothing before it, and its own
        // open is the nearest honest stand-in: the close sits inside the move being measured, so
        // using it would report a shorter thrust than happened.
        decimal origin = thrustStart > 0 ? bars[thrustStart - 1].Close : bars[thrustStart].Open;

        int extremeIndex = thrustStart;
        for (int i = thrustStart; i < bars.Count; i++)
        {
            bool further = isLong
                ? bars[i].High > bars[extremeIndex].High
                : bars[i].Low < bars[extremeIndex].Low;

            if (further)
            {
                extremeIndex = i;
            }
        }

        decimal extreme = isLong ? bars[extremeIndex].High : bars[extremeIndex].Low;
        int pullbackBars = bars.Count - 1 - extremeIndex;

        // The furthest the drift has gone back the other way, measured from the bar after the
        // extreme. With no bars after it there is no pullback and the extreme is its own answer.
        decimal pullbackExtreme = extreme;
        decimal trigger = isLong ? bars[extremeIndex].RawHigh : bars[extremeIndex].RawLow;
        decimal stop = trigger;

        for (int i = extremeIndex + 1; i < bars.Count; i++)
        {
            if (isLong)
            {
                pullbackExtreme = Math.Min(pullbackExtreme, bars[i].Low);
                stop = i == extremeIndex + 1 ? bars[i].RawLow : Math.Min(stop, bars[i].RawLow);
                trigger = i == extremeIndex + 1 ? bars[i].RawHigh : Math.Max(trigger, bars[i].RawHigh);
            }
            else
            {
                pullbackExtreme = Math.Max(pullbackExtreme, bars[i].High);
                stop = i == extremeIndex + 1 ? bars[i].RawHigh : Math.Max(stop, bars[i].RawHigh);
                trigger = i == extremeIndex + 1 ? bars[i].RawLow : Math.Min(trigger, bars[i].RawLow);
            }
        }

        // The fraction of the thrust given back, signed so both directions read the same way: zero
        // is no give-back and one is the whole move. A thrust of no size at all cannot be retraced
        // by a fraction of itself, so the depth is undefined rather than infinite.
        decimal move = isLong ? extreme - origin : origin - extreme;
        decimal? retrace = move == 0m
            ? null
            : (isLong ? extreme - pullbackExtreme : pullbackExtreme - extreme) / move;

        return new Pullback(thrustIndex, extremeIndex, origin, extreme, pullbackExtreme, pullbackBars, retrace, trigger, stop);
    }

    /// <summary>
    /// Where the flagged move began. A one-session scan starts where it is flagged; a
    /// twenty-session scan started nineteen sessions earlier. Clamped at the window's own start
    /// rather than returning null, because a window that holds only part of the move still holds a
    /// real shape, and refusing it would drop exactly the names whose run began before the history
    /// the detector reads.
    ///
    /// Extracted at 4.4 so <see cref="SwingIndexOf"/> reads the same start rather than a second copy
    /// of the clamp. Two copies of this line would put a swing outside the move it belongs to on
    /// exactly the names whose window is short, which is the population nobody looks at.
    /// </summary>
    private static int ThrustStart(int thrustIndex, int thrustSpanSessions) =>
        Math.Max(0, thrustIndex - thrustSpanSessions + 1);

    /// <summary>
    /// Where the swing the move ran from sits: the high the thrust fell from on the short side, the
    /// low it rose from on the long one.
    ///
    /// <b>This is the anchor, stated as an index into the same bars the rest of the geometry
    /// reads.</b> The short check list asks whether the bounce reached "the declining average price
    /// anchored to the last swing high", and until 4.4 the anchor was a phrase: nothing in the
    /// corpus said which bar it was, so no two sessions computing it would have had to agree.
    /// see: The anchored average price is anchored at the swing the thrust ran from
    ///
    /// <b>Searched from the start of the thrust to its extreme, inclusive of both.</b> The move
    /// runs from the swing to the extreme by construction, so the swing cannot be after the extreme,
    /// and a search over the whole window would find the bounce's own high on a name that has
    /// already recovered past where it started. Ties take the earliest bar, because the anchor is
    /// where the move began and a later equal high is a retest of it.
    ///
    /// The mirror is a parameter here as everywhere else in this class, so the long side's swing low
    /// is the same arithmetic read the other way rather than a second method.
    /// </summary>
    public static int? SwingIndexOf(
        IReadOnlyList<Bar> bars, Pullback pullback, int thrustSpanSessions, bool isLong)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(pullback);
        ArgumentOutOfRangeException.ThrowIfLessThan(thrustSpanSessions, 1);

        int start = ThrustStart(pullback.ThrustIndex, thrustSpanSessions);

        if (bars.Count == 0 || start < 0 || pullback.ExtremeIndex >= bars.Count || start > pullback.ExtremeIndex)
        {
            return null;
        }

        int swing = start;
        for (int i = start; i <= pullback.ExtremeIndex; i++)
        {
            bool further = isLong
                ? bars[i].Low < bars[swing].Low
                : bars[i].High > bars[swing].High;

            if (further)
            {
                swing = i;
            }
        }

        return swing;
    }

    /// <summary>
    /// How many sessions of the pullback closed the wrong side of its floor, each against the
    /// average as at that session.
    ///
    /// The floor is the 21-day average on the long side and the 50-day on the short side, which is
    /// the one place the two check lists are not mirrors: `held-floor` reads the medium average and
    /// `no-reclaim` reads the long one. Passed in rather than chosen here, so the asymmetry sits in
    /// the detectors where the corpus states it.
    ///
    /// <b>The floor was one number until 3.11 and it is a series.</b> ARCHITECTURE says "No daily
    /// close below the 21-day average during the dip". The dip is a span, the average is a series,
    /// and the chart draws it as one, so the document and the screen already agreed with each other
    /// and the code was the odd one out: it compared every bar of the dip against the average as at
    /// the setup date. On a rising average that is stricter than what the chart shows, because the
    /// as-of value is the highest the line reached, and a bar that closed above its own session's
    /// average is counted as beyond the floor while the chart shows it above the line. On a falling
    /// average it is looser in the same way, which is the direction that admits a setup rather than
    /// dropping one.
    ///
    /// A session with no average yet, which is a bar inside the warm-up, is not counted either way.
    /// It is not evidence that the close held and not evidence that it did not, and counting it as
    /// a breach would fail a setup for the age of its history rather than for its shape.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    public static int ClosesBeyondFloor(
        IReadOnlyList<Bar> bars, Pullback pullback, IReadOnlyList<decimal?> floor, bool isLong)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(pullback);
        ArgumentNullException.ThrowIfNull(floor);

        int beyond = 0;
        for (int i = pullback.ExtremeIndex + 1; i < bars.Count; i++)
        {
            if (i >= floor.Count || floor[i] is not decimal atThatSession)
            {
                continue;
            }

            if (isLong ? bars[i].Close < atThatSession : bars[i].Close > atThatSession)
            {
                beyond++;
            }
        }

        return beyond;
    }

    /// <summary>
    /// One session as the geometry reads it: adjusted for shape, raw for the two prices that trade.
    ///
    /// Both bases on one record rather than two lists, because the whole class of error here is
    /// reading one where the other was meant, and a caller holding two parallel lists has to keep
    /// them aligned itself.
    /// </summary>
    public sealed record Bar(decimal Open, decimal High, decimal Low, decimal Close, decimal RawHigh, decimal RawLow);

    /// <summary>The measured shape. Prices on the adjusted basis except the trigger and the stop.</summary>
    public sealed record Pullback(
        int ThrustIndex,
        int ExtremeIndex,
        decimal ThrustOrigin,
        decimal ThrustExtreme,
        decimal PullbackExtreme,
        int PullbackBars,
        decimal? RetraceDepth,
        decimal Trigger,
        decimal Stop);
}
