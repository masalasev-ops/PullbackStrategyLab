using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web.Shell;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the status band reads, which had no test of any kind until 3.11.
///
/// Every figure on the band comes from here and the band is on every page, so this is the read a
/// person sees most often and the one nothing was asserting. The property that matters is the run
/// summary: a night is about eighteen stages, the band has room for one, and which one it picks is
/// the difference between a screen that reports a degraded night and a screen that hides it.
///
/// <b>Every instant below is on the schedule RUNBOOK installs</b>, which is Eastern and which
/// crosses UTC midnight. The rows this file seeded at 3.11 were the RUNBOOK's Eastern clock times
/// written with a Z on the end, so all of them fell inside one UTC day and the population could not
/// tell a night bounded in the session zone from a night bounded on the UTC date. The read was
/// bounded on the UTC date, the tests passed, and the band reported the night of 2026-08-28 as
/// "scoreboard clean" over four failed stages.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class LabStatusTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 29, 3, 30, 0, TimeSpan.Zero));

    public LabStatusTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private StatusResponse Read() => LabStatus.Read(
        _connections, _clock, dailyCallCeiling: 5000, SessionBoundaries.UsEquities);

    private void Seed(string rows)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
            VALUES {rows};
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The night of 2026-08-28 as the store actually recorded it, in UTC, on the installed schedule.
    ///
    /// The evening slots run 17:15 to 18:28 Eastern and land on the 28th in UTC; forward and
    /// scoreboard run 21:30 and 21:50 Eastern and land on the 29th. So the night has stages on two
    /// UTC dates, and the four that failed are all on the earlier one.
    /// </summary>
    private void SeedTheNightThatCrossedUtcMidnight() => Seed("""
                ('r1', 'daily-bars',      '2026-08-28T21:30:03.059Z', '2026-08-28T21:30:18.561Z', 'clean',  100, 2005, 1),
                ('r2', 'indicators',      '2026-08-28T22:00:03.910Z', '2026-08-28T22:00:13.633Z', 'clean',    0, 1989, 1),
                ('r3', 'detect-long',     '2026-08-28T22:20:03.501Z', '2026-08-28T22:20:04.187Z', 'failed',   0,    0, 1),
                ('r4', 'vectorize',       '2026-08-28T22:25:03.510Z', '2026-08-28T22:25:03.786Z', 'failed',   0,    0, 1),
                ('r5', 'forward-returns', '2026-08-29T01:30:03.051Z', '2026-08-29T01:30:03.165Z', 'clean',    0,  483, 1),
                ('r6', 'scoreboard',      '2026-08-29T01:50:02.702Z', '2026-08-29T01:50:02.777Z', 'clean',    0,   11, 1)
        """);

    [Fact]
    public void A_night_is_bounded_in_the_session_zone_rather_than_on_the_utc_date()
    {
        SeedTheNightThatCrossedUtcMidnight();

        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        // The store this reproduces is the live one on the morning of 2026-08-29. Grouped on the UTC
        // date the newest day held forward-returns and scoreboard alone, both clean, and the band
        // read "scoreboard clean" while detect-long, vectorize, controls and cap had all failed and
        // the night had produced no setups at all. The ordering was right; the population was a
        // different night.
        Assert.Equal("failed", run.Outcome);

        // vectorize rather than detect-long, because within one outcome the read takes the latest
        // stage that reached it and vectorize failed five minutes after the detector did. Which of
        // the two an operator wants named is a separate question from the one this test is about,
        // and it is carried as an obligation rather than settled here: on this night detect-long is
        // the cause and vectorize is a consequence of it.
        Assert.Equal("vectorize", run.Stage);
    }

    [Fact]
    public void The_stages_after_utc_midnight_do_not_become_a_night_of_their_own()
    {
        SeedTheNightThatCrossedUtcMidnight();

        // The same property read the other way. Under the UTC grouping the two stages that ran after
        // midnight were the whole of the newest group, so this asserts they are not: the summary the
        // band shows starts on the 28th, which is the evening the night began on.
        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.StartsWith("2026-08-28T", run.StartedAt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_band_reports_the_night_that_stopped_short_rather_than_the_stage_that_ran_last()
    {
        // The shape the hard rule describes: the ingestor reaches the call ceiling in the evening,
        // stops rather than overrunning and writes a partial entry, and the stages that do not spend
        // calls run afterwards and complete. The last of them is on the next UTC day.
        Seed("""
                ('r1', 'daily-bars', '2026-08-28T21:30:00.000Z', '2026-08-28T21:32:00.000Z', 'partial', 4900, 12, 1),
                ('r2', 'indicators', '2026-08-28T22:00:00.000Z', '2026-08-28T22:04:00.000Z', 'clean',      0, 900, 1),
                ('r3', 'scoreboard', '2026-08-29T01:50:00.000Z', '2026-08-29T01:51:00.000Z', 'clean',      0,  11, 1)
        """);

        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        // Before 3.11 this was "scoreboard" and "clean", because the read ordered by started_at and
        // took one row. The partial entry the ceiling rule requires was written, was correct, and
        // never reached the screen it exists to appear on.
        Assert.Equal("daily-bars", run.Stage);
        Assert.Equal("partial", run.Outcome);
    }

    [Fact]
    public void A_night_in_which_every_stage_completed_reports_clean()
    {
        Seed("""
                ('r1', 'daily-bars', '2026-08-28T21:30:00.000Z', '2026-08-28T21:32:00.000Z', 'clean', 100, 2000, 1),
                ('r2', 'scoreboard', '2026-08-29T01:50:00.000Z', '2026-08-29T01:51:00.000Z', 'clean',   0,   11, 1)
        """);

        // The other direction, so "always reports the worst thing it can find" is not what passes
        // the tests above. A clean night has to read as clean or the band says nothing at all.
        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("clean", run.Outcome);
        Assert.Equal("scoreboard", run.Stage);
    }

    [Fact]
    public void A_failure_outranks_a_partial_within_the_same_night()
    {
        Seed("""
                ('r1', 'daily-bars', '2026-08-28T21:30:00.000Z', '2026-08-28T21:32:00.000Z', 'partial', 4900, 12, 1),
                ('r2', 'sectors',    '2026-08-28T22:12:00.000Z', '2026-08-28T22:12:30.000Z', 'failed',     8,  0, 1)
        """);

        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("sectors", run.Stage);
        Assert.Equal("failed", run.Outcome);
    }

    [Fact]
    public void An_earlier_night_does_not_answer_for_the_most_recent_one()
    {
        // The previous night's tail lands on the same UTC date as this night's head, which is the
        // second thing the UTC grouping got wrong: 2026-08-28T01:50Z belongs to the session of the
        // 27th and 2026-08-28T21:30Z to the session of the 28th, and grouping on the date put a
        // failure from the earlier night into the later one.
        Seed("""
                ('r0', 'daily-bars', '2026-08-27T21:30:00.000Z', '2026-08-27T21:32:00.000Z', 'failed',  0,  0, 1),
                ('r1', 'scoreboard', '2026-08-28T01:50:00.000Z', '2026-08-28T01:51:00.000Z', 'clean',   0, 11, 1),
                ('r2', 'daily-bars', '2026-08-28T21:30:00.000Z', '2026-08-28T21:32:00.000Z', 'clean', 100, 20, 1)
        """);

        // The worst outcome is taken within the most recent night, not across the whole log. A read
        // that scanned every night would report a failure from a week ago for ever.
        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("clean", run.Outcome);
        Assert.Equal("daily-bars", run.Stage);
        Assert.StartsWith("2026-08-28T21:30", run.StartedAt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_with_no_runs_reports_no_run_rather_than_throwing()
    {
        Assert.Null(Read().LastRun);
    }

    [Fact]
    public void The_band_reports_the_version_the_build_needs_beside_the_one_the_store_is_at()
    {
        StatusResponse status = Read();

        // A store this test has just migrated is at the version the build carries, so the two agree
        // and the band reads "schema N of N". What makes the pair worth carrying is the case where
        // they do not: the number was already on the band with nothing beside it, and on 2026-08-28
        // it read 30 all night against a build that needed 32.
        Assert.Equal(MigrationRunner.LatestVersion, status.SchemaVersionExpected);
        Assert.Equal(status.SchemaVersionExpected, status.SchemaVersion);
    }

    /// <summary>
    /// The band when the read surface did not answer, which used to state positively that the store
    /// was fine.
    ///
    /// `Down` built the view with both version fields at nought, and the mismatch flag was
    /// `SchemaVersion != SchemaVersionExpected`, so nought against nought said no mismatch. The
    /// class of fault that stops the read surface answering is exactly the class the line exists to
    /// report, so the fallback asserted the negative of the thing it could not see. It was latent
    /// only because the status read happened to select no column a recent migration had added; 4.2
    /// adds the first migration since the phase 3 sign-off, which is why the row fell due here.
    /// </summary>
    [Fact]
    public void An_unreachable_band_says_the_versions_are_unknown_rather_than_that_they_agree()
    {
        LabStatusView down = LabStatusView.Down("the read surface is not answering");

        Assert.False(down.VersionsKnown);
        Assert.False(down.SchemaMismatch);
        Assert.False(down.StoreAhead);
        Assert.Equal(LabStatusView.UnknownVersion, down.SchemaVersion);
        Assert.Equal(LabStatusView.UnknownVersion, down.SchemaVersionExpected);
    }

    /// <summary>
    /// A store ahead of its build is not a store behind it, and the two need different acts.
    ///
    /// `Program.WhyTheStoreCannotBeRead` has always distinguished them: a store behind the build is
    /// repaired by `tools/migrate`, and a store ahead of it means an older binary is reading columns
    /// whose meaning has moved, so the checkout is what has to change. The band collapsed both into
    /// one label named for one of them, which is a correct answer discarded by the surface that
    /// carries it.
    /// </summary>
    [Theory]
    [InlineData(30, 32, false)]
    [InlineData(37, 36, true)]
    public void The_band_says_which_way_a_schema_mismatch_runs(int store, int build, bool ahead)
    {
        LabStatusView view = Band(store, build);

        Assert.True(view.SchemaMismatch);
        Assert.Equal(ahead, view.StoreAhead);
    }

    [Fact]
    public void Matching_versions_are_not_a_mismatch_in_either_direction()
    {
        LabStatusView view = Band(37, 37);

        Assert.False(view.SchemaMismatch);
        Assert.False(view.StoreAhead);
        Assert.Contains("schema 37 of 37", view.StoreText, StringComparison.Ordinal);
    }

    /// <summary>A band over a reachable store at the two versions given, and nothing else stated.</summary>
    private static LabStatusView Band(int store, int build) =>
        new(true, null, "ready", store, build, null, null, null, 0, 0, 0, 5000, null, null, null, null);
}
