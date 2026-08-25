namespace PullbackStrategyLab.Core.Time;

/// <summary>
/// The only source of the current instant, and the only place a session boundary is
/// resolved. Direct <c>DateTime.Now</c>, <c>DateTime.UtcNow</c> and
/// <c>DateTimeOffset.UtcNow</c> are banned outside the implementation and are grepped
/// for by tools/ci.*.
///
/// Zones are named by IANA identifier only. Windows identifiers are rejected rather
/// than translated, because a Windows identifier that happens to resolve on one machine
/// throws on the other and the failure surfaces as a session boundary in the wrong place.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public interface IClock
{
    /// <summary>The current instant, in UTC. Everything is stored in this form.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The current instant expressed in <paramref name="ianaZoneId"/>.</summary>
    DateTimeOffset NowIn(string ianaZoneId);

    /// <summary>The same instant expressed in <paramref name="ianaZoneId"/>.</summary>
    DateTimeOffset ToZone(DateTimeOffset instant, string ianaZoneId);

    /// <summary>
    /// The instant at which <paramref name="localTime"/> occurs on <paramref name="sessionDate"/>
    /// in <paramref name="ianaZoneId"/>. This is the only way a session boundary is computed.
    /// </summary>
    DateTimeOffset SessionBoundary(DateOnly sessionDate, TimeOnly localTime, string ianaZoneId);

    /// <summary>The calendar date in <paramref name="ianaZoneId"/> at the given instant.</summary>
    DateOnly SessionDate(DateTimeOffset instant, string ianaZoneId);
}
