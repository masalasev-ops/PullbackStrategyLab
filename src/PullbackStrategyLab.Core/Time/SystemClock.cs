using System.Collections.Concurrent;

namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// The one implementation permitted to read the machine clock. Everything else asks
/// <see cref="IClock"/>, and tools/ci.* asserts that by grepping the source.
/// </summary>
public sealed class SystemClock : IClock
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset NowIn(string ianaZoneId) => ToZone(UtcNow, ianaZoneId);

    public DateTimeOffset ToZone(DateTimeOffset instant, string ianaZoneId) =>
        TimeZoneInfo.ConvertTime(instant, Zone(ianaZoneId));

    public DateOnly SessionDate(DateTimeOffset instant, string ianaZoneId) =>
        DateOnly.FromDateTime(ToZone(instant, ianaZoneId).DateTime);

    public DateTimeOffset SessionBoundary(DateOnly sessionDate, TimeOnly localTime, string ianaZoneId)
    {
        TimeZoneInfo zone = Zone(ianaZoneId);
        DateTime wall = sessionDate.ToDateTime(localTime, DateTimeKind.Unspecified);

        // A local wall time can be invalid, in the hour that spring-forward skips, or
        // ambiguous, in the hour autumn repeats. Resolving both explicitly is the point
        // of routing every boundary through here: the alternative is a framework default
        // that differs from the one a reader assumed.
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
            // The named local time happens twice. Take the first, which is the one still
            // on the pre-transition offset, so a session boundary does not silently move
            // an hour on one night of the year.
            ? zone.GetAmbiguousTimeOffsets(wall).Max()
            : zone.GetUtcOffset(wall);

        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    private static TimeZoneInfo Zone(string ianaZoneId)
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
