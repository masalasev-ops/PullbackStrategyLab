using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// A clock a test can move. Zone resolution is delegated to the real implementation rather
/// than reimplemented, so a test that depends on a session boundary still exercises the code
/// that resolves one.
/// </summary>
public sealed class FixedClock : IClock
{
    private readonly IClock _zones = new SystemClock();

    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public DateTimeOffset NowIn(string ianaZoneId) => _zones.ToZone(UtcNow, ianaZoneId);

    public DateTimeOffset ToZone(DateTimeOffset instant, string ianaZoneId) => _zones.ToZone(instant, ianaZoneId);

    public DateOnly SessionDate(DateTimeOffset instant, string ianaZoneId) => _zones.SessionDate(instant, ianaZoneId);

    public DateTimeOffset SessionBoundary(DateOnly sessionDate, TimeOnly localTime, string ianaZoneId) =>
        _zones.SessionBoundary(sessionDate, localTime, ianaZoneId);
}
