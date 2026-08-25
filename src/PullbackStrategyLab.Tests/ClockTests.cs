using PullbackStrategyLab.Core.Time;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The clock, proven on whichever platform this suite is running on. The CI matrix runs both,
/// which is what makes these assertions worth anything: Windows and macOS name timezones
/// differently from each other and from Linux, and the two development machines agree with
/// each other and are capable of being wrong in the same way at the same time.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public sealed class ClockTests
{
    /// <summary>
    /// Etc/UTC rather than UTC. "UTC" is also a Windows identifier, so it is rejected by the
    /// same guard that rejects every other Windows name, which is the behaviour the last test
    /// in this class pins.
    /// </summary>
    private const string UtcZone = "Etc/UTC";

    private const string SessionZone = "America/New_York";

    [Fact]
    public void The_session_zone_resolves_and_sits_behind_utc_by_at_most_a_day()
    {
        IClock clock = new SystemClock();
        DateTimeOffset now = clock.UtcNow;

        // Both bounds read from the clock. Reading one from the clock and the other from
        // DateTime would prove the two agree rather than that the clock resolves the zone,
        // and it is the resolution that fails when IANA lookup is unavailable.
        DateTimeOffset utc = clock.ToZone(now, UtcZone);
        DateTimeOffset session = clock.ToZone(now, SessionZone);

        TimeSpan behind = utc.DateTime - session.DateTime;

        Assert.True(behind > TimeSpan.Zero,
            $"{SessionZone} read as {session:o} against UTC {utc:o}, which is not behind UTC at all. "
            + "On a machine with invariant globalization enabled every zone resolves to UTC and this is what that looks like.");

        Assert.True(behind <= TimeSpan.FromDays(1),
            $"{SessionZone} read as {behind} behind UTC, which is more than a day and so is not a timezone offset.");
    }

    [Theory]
    // Eastern Daylight Time, UTC-4. The regular session opens at 09:30 local.
    [InlineData("2026-06-15", "09:30", "2026-06-15T13:30:00Z")]
    // Eastern Standard Time, UTC-5. The same local time, five hours later in UTC.
    [InlineData("2026-01-15", "09:30", "2026-01-15T14:30:00Z")]
    // The close, on both sides of the transition.
    [InlineData("2026-06-15", "16:00", "2026-06-15T20:00:00Z")]
    [InlineData("2026-01-15", "16:00", "2026-01-15T21:00:00Z")]
    public void A_session_boundary_resolves_to_the_instant_the_local_time_names(string date, string local, string expected)
    {
        IClock clock = new SystemClock();

        DateTimeOffset boundary = clock.SessionBoundary(DateOnly.Parse(date), TimeOnly.Parse(local), SessionZone);

        Assert.Equal(DateTimeOffset.Parse(expected).ToUniversalTime(), boundary);
    }

    [Fact]
    public void A_local_time_the_zone_skips_resolves_forward_rather_than_throwing()
    {
        IClock clock = new SystemClock();

        // 02:30 on the second Sunday in March does not exist in America/New_York: the clock
        // goes straight from 02:00 to 03:00. The first instant that does exist is 03:30 local,
        // which is 07:30 UTC on the new offset.
        DateTimeOffset boundary = clock.SessionBoundary(new DateOnly(2026, 3, 8), new TimeOnly(2, 30), SessionZone);

        Assert.Equal(DateTimeOffset.Parse("2026-03-08T07:30:00Z").ToUniversalTime(), boundary);
    }

    [Fact]
    public void A_local_time_the_zone_repeats_resolves_to_the_first_of_the_two()
    {
        IClock clock = new SystemClock();

        // 01:30 on the first Sunday in November happens twice. The first is still on daylight
        // time at UTC-4, so it is 05:30 UTC. Taking the first is what keeps a session boundary
        // from moving an hour on one night of the year.
        DateTimeOffset boundary = clock.SessionBoundary(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), SessionZone);

        Assert.Equal(DateTimeOffset.Parse("2026-11-01T05:30:00Z").ToUniversalTime(), boundary);
    }

    [Fact]
    public void The_session_date_is_the_calendar_date_in_the_session_zone_not_in_utc()
    {
        IClock clock = new SystemClock();

        // 02:00 UTC on the 15th is 22:00 on the 14th in New York. A run that resolved this in
        // UTC would file an evening's work under the following day.
        DateTimeOffset instant = DateTimeOffset.Parse("2026-06-15T02:00:00Z");

        Assert.Equal(new DateOnly(2026, 6, 14), clock.SessionDate(instant, SessionZone));
        Assert.Equal(new DateOnly(2026, 6, 15), clock.SessionDate(instant, UtcZone));
    }

    [Fact]
    public void A_session_boundary_and_the_session_date_agree_with_each_other()
    {
        IClock clock = new SystemClock();
        var date = new DateOnly(2026, 8, 25);

        DateTimeOffset open = clock.SessionBoundary(date, new TimeOnly(9, 30), SessionZone);

        Assert.Equal(date, clock.SessionDate(open, SessionZone));
    }

    [Theory]
    [InlineData("Eastern Standard Time")]
    [InlineData("Pacific Standard Time")]
    [InlineData("GMT Standard Time")]
    // "UTC" is a Windows identifier as well as an everyday word, so it is rejected too and
    // Etc/UTC is what the code uses. Silently accepting it would be the one exception that
    // makes the rule unenforceable.
    [InlineData("UTC")]
    public void A_windows_timezone_identifier_is_rejected_rather_than_translated(string windowsIdentifier)
    {
        IClock clock = new SystemClock();

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => clock.NowIn(windowsIdentifier));

        Assert.Contains("IANA", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_zone_fails_loudly_and_names_the_setting_that_causes_it()
    {
        IClock clock = new SystemClock();

        TimeZoneNotFoundException failure = Assert.Throws<TimeZoneNotFoundException>(
            () => clock.NowIn("Nowhere/Nothing"));

        Assert.Contains("InvariantGlobalization", failure.Message, StringComparison.Ordinal);
    }
}
