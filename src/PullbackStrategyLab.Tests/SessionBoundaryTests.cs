using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The point-in-time bound closes an Eastern session at the end of its own Eastern day.
///
/// <b>The defect this replaces, measured rather than reasoned about.</b> Every bound in the lab was
/// built by appending <c>T23:59:59.999Z</c> to the session date, which closes the session at
/// 19:59:59 Eastern through daylight time and 18:59:59 through standard time. Every stage after the
/// close therefore wrote rows its own session could not read, and the truncation moved an hour twice
/// a year. On 2026-08-28 the nine scoreboard panels built for the session of 2026-08-27 at 21:50
/// Eastern carried <c>2026-08-28T01:50:03.248Z</c>, and a scoreboard read for 2026-08-27 returned
/// none of them: 0 panels under the old expression and 9 under this one.
///
/// <b>What it does not close.</b> A stamp still records when the lab asked, not which session the
/// answer belongs to, and most stamped tables have no session-date column to compare against. That
/// is a separate defect with a separate cost and it is carried as its own obligation. Nothing here
/// should be read as having closed it.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class SessionBoundaryTests
{
    /// <summary>What the twelve sites used to append, kept so the assertions can be against it.</summary>
    private static string OldExpression(DateOnly date) =>
        StoreText.DateToStorageText(date) + "T23:59:59.999Z";

    /// <summary>
    /// A row stamped at 22:00 Eastern is inside its own session, in January and in July.
    ///
    /// Both months, because a bound with a fixed UTC offset can be made to pass on one of them by
    /// choosing the offset, and the whole point is that the session does not change length with the
    /// clock change. Each case also asserts the old expression excluded it, so this test fails if
    /// the expression is put back.
    /// </summary>
    [Theory]
    [InlineData("2026-01-14", "2026-01-15T03:00:00.000Z")]  // 22:00 EST, standard time
    [InlineData("2026-07-14", "2026-07-15T02:00:00.000Z")]  // 22:00 EDT, daylight time
    public void A_row_stamped_at_ten_at_night_eastern_is_inside_its_own_session(string sessionDate, string stampedAt)
    {
        DateOnly session = DateOnly.Parse(sessionDate);

        string bound = StoreText.EndOfSession(session, SessionBoundaries.UsEquities);

        Assert.True(
            string.CompareOrdinal(stampedAt, bound) <= 0,
            $"a row stamped {stampedAt} is 22:00 Eastern on {sessionDate} and must be inside that session, "
            + $"but the bound is {bound}.");

        // And the expression this replaced excluded it, in both months. Without this half the test
        // would pass against the defect it was written for.
        Assert.True(
            string.CompareOrdinal(stampedAt, OldExpression(session)) > 0,
            $"the old expression {OldExpression(session)} already admitted {stampedAt}, so this test would "
            + "not have failed against it and proves nothing.");
    }

    /// <summary>
    /// The session ends at local midnight less a millisecond, whatever the offset, so the window
    /// after the 18:12 slot is the same length in January and in July.
    ///
    /// This is the seasonal cliff stated as a number. Under the old expression the window was 1h48m
    /// in daylight time and 0h48m in standard time; under this one it is the same both times.
    /// </summary>
    [Fact]
    public void The_window_after_the_last_slot_does_not_move_with_the_clock_change()
    {
        TimeSpan january = Window(new DateOnly(2026, 1, 14));
        TimeSpan july = Window(new DateOnly(2026, 7, 14));

        Assert.Equal(july, january);

        // Stated rather than left to the reader: 18:12 to local midnight is five hours forty-eight.
        Assert.Equal(TimeSpan.FromMinutes((5 * 60) + 48), TimeSpan.FromMinutes(Math.Round(july.TotalMinutes)));
    }

    /// <summary>
    /// Every appsettings file sets the session zone to the one constant the bound uses.
    ///
    /// <b>This is what keeps the bound from being a second source of truth.</b> A store reader is a
    /// static helper with no configuration to read, so it names
    /// <see cref="SessionBoundaries.UsEquities"/> directly while a stage passes
    /// <c>_options.SessionZone</c>. Those are the same string today and nothing but this assertion
    /// would notice if configuration moved. Threading the configured zone into the store readers is
    /// the real fix and it is carried as an obligation; until then this is the guard, and it is
    /// stated as a guard rather than implied to be a design.
    /// </summary>
    [Fact]
    public void Every_configured_session_zone_is_the_zone_the_bound_uses()
    {
        string[] settings =
        [
            .. RepositoryLayout.TrackedTextFiles
                .Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.Ordinal))
                .Where(f => Path.GetExtension(f) == ".json"),
        ];

        // Stated in advance, so a sweep that matched nothing fails rather than passing silently.
        Assert.True(settings.Length >= 3,
            $"expected at least three appsettings files and found {settings.Length}. A sweep that matches "
            + "nothing is self-validating.");

        var wrong = new List<string>();

        foreach (string file in settings)
        {
            string text = RepositoryLayout.Read(file);

            if (!text.Contains("\"SessionZone\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (!text.Contains($"\"SessionZone\": \"{SessionBoundaries.UsEquities}\"", StringComparison.Ordinal))
            {
                wrong.Add(RepositoryLayout.Relative(file));
            }
        }

        Assert.True(wrong.Count == 0,
            $"{wrong.Count} settings file(s) set a session zone other than {SessionBoundaries.UsEquities}, which the "
            + $"store readers name directly because they have no configuration to read:\n  {string.Join("\n  ", wrong)}");
    }

    /// <summary>How long after the 18:12 slot a session still has, in the configured zone.</summary>
    private static TimeSpan Window(DateOnly session) =>
        SessionBoundaries.EndOfSession(session, SessionBoundaries.UsEquities)
        - SessionBoundaries.At(session, new TimeOnly(18, 12), SessionBoundaries.UsEquities);
}
