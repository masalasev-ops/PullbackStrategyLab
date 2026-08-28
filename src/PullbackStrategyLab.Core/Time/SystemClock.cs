namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// The one implementation permitted to read the machine clock. Everything else asks
/// <see cref="IClock"/>, and tools/ci.* asserts that by grepping the source.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset NowIn(string ianaZoneId) => ToZone(UtcNow, ianaZoneId);

    public DateTimeOffset ToZone(DateTimeOffset instant, string ianaZoneId) =>
        TimeZoneInfo.ConvertTime(instant, SessionBoundaries.Zone(ianaZoneId));

    public DateOnly SessionDate(DateTimeOffset instant, string ianaZoneId) =>
        DateOnly.FromDateTime(ToZone(instant, ianaZoneId).DateTime);

    public DateTimeOffset SessionBoundary(DateOnly sessionDate, TimeOnly localTime, string ianaZoneId) =>
        // Delegated rather than implemented here. The arithmetic is a pure function of its
        // arguments and a store reader needs it without holding a clock, so it lives in
        // SessionBoundaries and this interface method is the way everything holding a clock asks
        // for it. Two implementations of a session boundary is the thing that must not exist.
        SessionBoundaries.At(sessionDate, localTime, ianaZoneId);
}
