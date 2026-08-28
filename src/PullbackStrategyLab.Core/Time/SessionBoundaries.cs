using System.Collections.Concurrent;

namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// The one computation that turns a session date and a local time into an instant, and the one place
/// the end of a session is defined.
///
/// <b>It is a pure function, which is why it sits outside <see cref="IClock"/> rather than behind
/// it.</b> Nothing here reads the machine clock; everything is derived from the arguments. That
/// distinction matters because a store reader has to bound a stamp on the end of a session date and
/// has no clock to ask, and the alternative the lab shipped instead was a literal
/// <c>"T23:59:59.999Z"</c> appended to the date in twelve places.
/// <see cref="SystemClock.SessionBoundary"/> delegates here, so there is one implementation of the
/// arithmetic and the clock is still the only thing that knows what time it is.
///
/// <b>What the literal got wrong.</b> Appending <c>T23:59:59.999Z</c> to a session date closes an
/// Eastern trading session at 19:59:59 Eastern in summer and 18:59:59 in winter, so every stage
/// running after the close writes rows its own session cannot see, and the truncation point moves an
/// hour twice a year. Measured on 2026-08-28: the nine scoreboard panels built for the session of
/// 2026-08-27 at 21:50 Eastern were stamped <c>2026-08-28T01:50:03.248Z</c>, and a scoreboard read
/// for 2026-08-27 returned none of them.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public static class SessionBoundaries
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    /// <summary>
    /// The zone the lab's trading sessions are defined in.
    ///
    /// One constant rather than a literal per site, and <see cref="object"/>-level configuration
    /// defaults to it rather than restating it, so the bound and the clock cannot be given different
    /// zones. A test asserts that every <c>appsettings.json</c> in the repository sets
    /// <c>SessionZone</c> to this value, which is what stops configuration diverging from the store
    /// readers that cannot read configuration.
    /// </summary>
    public const string UsEquities = "America/New_York";

    /// <summary>
    /// The last local instant of a session date, to the millisecond the store records.
    ///
    /// One millisecond before local midnight rather than local midnight itself, so the bound is
    /// inclusive and a row stamped at the very end of a session is inside it.
    /// </summary>
    public static readonly TimeOnly LastInstantOfDay = new(23, 59, 59, 999);

    /// <summary>
    /// The instant at which <paramref name="localTime"/> occurs on <paramref name="sessionDate"/> in
    /// <paramref name="ianaZoneId"/>.
    ///
    /// A local wall time can be invalid, in the hour spring-forward skips, or ambiguous, in the hour
    /// autumn repeats. Both are resolved explicitly here, because the alternative is a framework
    /// default that differs from the one a reader assumed.
    /// </summary>
    public static DateTimeOffset At(DateOnly sessionDate, TimeOnly localTime, string ianaZoneId)
    {
        TimeZoneInfo zone = Zone(ianaZoneId);
        DateTime wall = sessionDate.ToDateTime(localTime, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(wall))
        {
            // The named local time does not exist. Take the first instant that does.
            TimeSpan gap = zone.GetAdjustmentRules()
                .Where(r => r.DateStart <= wall && wall <= r.DateEnd)
                .Select(r => r.DaylightDelta)
                .DefaultIfEmpty(TimeSpan.FromHours(1))
                .First();
            wall = wall.Add(gap);
        }

        TimeSpan offset = zone.IsAmbiguousTime(wall)
            // The named local time happens twice. Take the first, which is the one still on the
            // pre-transition offset, so a session boundary does not silently move an hour on one
            // night of the year.
            ? zone.GetAmbiguousTimeOffsets(wall).Max()
            : zone.GetUtcOffset(wall);

        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    /// <summary>
    /// The last instant of <paramref name="sessionDate"/> in <paramref name="ianaZoneId"/>, which is
    /// what every point-in-time bound in the lab compares an observation stamp against.
    ///
    /// A row observed at or before this instant is something the session could have known. The
    /// answer moves with the clock change by design: it is 03:59:59.999Z the next morning through
    /// daylight time and 04:59:59.999Z through standard time, because both are local midnight less a
    /// millisecond, and a fixed UTC offset is exactly what produced a session that changed length
    /// twice a year.
    /// </summary>
    public static DateTimeOffset EndOfSession(DateOnly sessionDate, string ianaZoneId) =>
        At(sessionDate, LastInstantOfDay, ianaZoneId);

    internal static TimeZoneInfo Zone(string ianaZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaZoneId);
        return Zones.GetOrAdd(ianaZoneId, static id =>
        {
            // A Windows identifier resolves on Windows and throws on macOS, so it is
            // rejected here rather than discovered by a runner.
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out _))
            {
                throw new ArgumentException(
                    $"'{id}' is a Windows timezone identifier. Session boundaries resolve through IANA identifiers only, " +
                    "because a Windows identifier resolves on one of the two development machines and throws on the other.",
                    nameof(ianaZoneId));
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException e)
            {
                throw new TimeZoneNotFoundException(
                    $"IANA timezone '{id}' was not found. If this machine has InvariantGlobalization enabled, IANA lookup " +
                    "fails silently for every identifier; Directory.Build.props sets it to false for exactly this reason.",
                    e);
            }
        });
    }
}
