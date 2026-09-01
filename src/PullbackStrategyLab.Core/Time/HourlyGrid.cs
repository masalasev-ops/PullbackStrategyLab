namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// Where the hour boundaries fall inside a regular session, and which of the resulting bars is an
/// hourly bar at all.
///
/// <b>Derived from <see cref="SessionBoundaries"/> and stating neither boundary again.</b> The two
/// times arrived at 4.2 with the minute bars and are facts about the exchange; the grid is
/// arithmetic over them. Restating 09:30 here would put the session's definition in two files, and
/// the one that was not edited would be the one somebody read.
/// see: The hourly grid anchors to the session open, and the closing stub is not an hourly bar
///
/// <b>The session does not divide by an hour and that is the whole subject.</b> It runs
/// <see cref="SessionBoundaries.RegularSessionMinutes"/> minutes, so every grid over it leaves a
/// remainder somewhere, and the only question was where. Anchored to the open, the remainder is the
/// last half hour; anchored to the clock, it would be the first.
/// </summary>
public static class HourlyGrid
{
    private const int MinutesInAnHour = 60;

    /// <summary>How many complete hourly bars a regular session holds.</summary>
    public static int CompleteBars => SessionBoundaries.RegularSessionMinutes / MinutesInAnHour;

    /// <summary>
    /// How many minutes the closing remainder runs, which is nought only if the session ever
    /// divides evenly by an hour.
    /// </summary>
    public static int StubMinutes => SessionBoundaries.RegularSessionMinutes % MinutesInAnHour;

    /// <summary>Whether this session length leaves a stub at all.</summary>
    public static bool HasStub => StubMinutes > 0;

    /// <summary>
    /// The local time each complete hourly bar opens at, in order, first to last.
    ///
    /// The stub's open is not here. It is <see cref="StubOpen"/>, named separately, because a
    /// consumer walking this list must not be able to reach the stub by accident.
    /// </summary>
    public static IReadOnlyList<TimeOnly> Opens =>
    [
        .. Enumerable.Range(0, CompleteBars)
            .Select(i => SessionBoundaries.RegularSessionOpen.AddHours(i)),
    ];

    /// <summary>
    /// The local time the closing stub opens at, or null where the session divides evenly.
    ///
    /// It is exposed so a reader can name the bar being excluded rather than only be told a time
    /// was rejected. A stage that reports "no hourly close at 15:30" is saying something a person
    /// can check; one that reports "not on the grid" is not.
    /// </summary>
    public static TimeOnly? StubOpen =>
        HasStub ? SessionBoundaries.RegularSessionOpen.AddHours(CompleteBars) : null;

    /// <summary>
    /// Whether a bar opening at <paramref name="localOpen"/> is a complete hourly bar, and so
    /// whether its close is a close the short exit rule may read.
    ///
    /// <b>False for the stub, which is the rule rather than an edge case.</b> The exit turns on an
    /// hourly close and a close is only a close of the thing it closes; the last thirty minutes of
    /// the session are not an hour, so a level they end above has not been held for an hour. Nothing
    /// is lost by excluding them, because the session close is already its own signal and this rule
    /// exists to catch the thesis breaking during the day rather than at the bell.
    ///
    /// False, too, for any time not on the grid at all, so a caller handing in a minute rather than
    /// an hour boundary is refused rather than rounded.
    /// </summary>
    public static bool IsHourlyClose(TimeOnly localOpen) => Opens.Contains(localOpen);

    /// <summary>
    /// The same question about an instant, resolved through the session's own zone.
    ///
    /// Both boundaries are resolved through <see cref="SessionBoundaries.At"/> rather than compared
    /// as wall times, for the reason stated there: a session on a day the zone changes offset is
    /// bounded by the instants that session actually had.
    /// </summary>
    public static bool IsHourlyClose(DateTimeOffset barOpen, DateOnly sessionDate, string ianaZoneId) =>
        Opens.Any(open => SessionBoundaries.At(sessionDate, open, ianaZoneId) == barOpen);

    /// <summary>
    /// Which complete hourly bar a minute falls in, as the index of its open, or null where the
    /// minute is outside the regular session or inside the closing stub.
    ///
    /// Null for the stub rather than an index past the last, so a caller that forgets to check gets
    /// nothing rather than a bar that does not exist.
    /// </summary>
    public static int? BarIndexOf(DateTimeOffset instant, DateOnly sessionDate, string ianaZoneId)
    {
        if (!SessionBoundaries.IsRegularSession(instant, sessionDate, ianaZoneId))
        {
            return null;
        }

        DateTimeOffset open = SessionBoundaries.At(sessionDate, SessionBoundaries.RegularSessionOpen, ianaZoneId);
        int index = (int)((instant - open).TotalMinutes / MinutesInAnHour);

        return index < CompleteBars ? index : null;
    }
}
