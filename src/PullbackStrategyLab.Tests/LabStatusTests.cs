using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the status band reads, which had no test of any kind until 3.11.
///
/// Every figure on the band comes from here and the band is on every page, so this is the read a
/// person sees most often and the one nothing was asserting. The property that matters is the run
/// summary: a night is about eighteen stages, the band has room for one, and which one it picks is
/// the difference between a screen that reports a degraded night and a screen that hides it.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class LabStatusTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 23, 30, 0, TimeSpan.Zero));

    public LabStatusTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private StatusResponse Read() => LabStatus.Read(_connections, _clock, dailyCallCeiling: 5000);

    /// <summary>
    /// A night in which one stage stopped short and every stage after it finished cleanly.
    ///
    /// The shape the hard rule describes: the ingestor reaches the call ceiling in the evening,
    /// stops rather than overrunning and writes a partial entry, and the stages that do not spend
    /// calls run afterwards and complete.
    /// </summary>
    private void SeedANightThatStoppedShort()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
            VALUES
                ('r1', 'daily-bars', '2026-08-27T20:10:00.000Z', '2026-08-27T20:12:00.000Z', 'partial', 4900, 12, 1),
                ('r2', 'indicators', '2026-08-27T21:00:00.000Z', '2026-08-27T21:04:00.000Z', 'clean',    0, 900, 1),
                ('r3', 'vectorize',  '2026-08-27T22:40:00.000Z', '2026-08-27T22:41:00.000Z', 'clean',    0,  99, 1);
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void The_band_reports_the_night_that_stopped_short_rather_than_the_stage_that_ran_last()
    {
        SeedANightThatStoppedShort();

        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        // Before 3.11 this was "vectorize" and "clean", because the read ordered by started_at and
        // took one row. The partial entry the ceiling rule requires was written, was correct, and
        // never reached the screen it exists to appear on.
        Assert.Equal("daily-bars", run.Stage);
        Assert.Equal("partial", run.Outcome);
    }

    [Fact]
    public void A_night_in_which_every_stage_completed_reports_clean()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
                VALUES
                    ('r1', 'daily-bars', '2026-08-27T20:10:00.000Z', '2026-08-27T20:12:00.000Z', 'clean', 100, 2000, 1),
                    ('r2', 'vectorize',  '2026-08-27T22:40:00.000Z', '2026-08-27T22:41:00.000Z', 'clean',   0,   99, 1);
                """;
            command.ExecuteNonQuery();
        }

        // The other direction, so "always reports the worst thing it can find" is not what passes
        // the test above. A clean night has to read as clean or the band says nothing at all.
        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("clean", run.Outcome);
        Assert.Equal("vectorize", run.Stage);
    }

    [Fact]
    public void A_failure_outranks_a_partial_within_the_same_night()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
                VALUES
                    ('r1', 'daily-bars', '2026-08-27T20:10:00.000Z', '2026-08-27T20:12:00.000Z', 'partial', 4900, 12, 1),
                    ('r2', 'sectors',    '2026-08-27T20:30:00.000Z', '2026-08-27T20:31:00.000Z', 'failed',     8,  0, 1);
                """;
            command.ExecuteNonQuery();
        }

        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("sectors", run.Stage);
        Assert.Equal("failed", run.Outcome);
    }

    [Fact]
    public void An_earlier_night_does_not_answer_for_the_most_recent_one()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
                VALUES
                    ('r0', 'daily-bars', '2026-08-26T20:10:00.000Z', '2026-08-26T20:12:00.000Z', 'failed', 0,   0, 1),
                    ('r1', 'daily-bars', '2026-08-27T20:10:00.000Z', '2026-08-27T20:12:00.000Z', 'clean', 100, 20, 1);
                """;
            command.ExecuteNonQuery();
        }

        // The worst outcome is taken within the most recent night, not across the whole log. A read
        // that scanned every night would report a failure from a week ago for ever.
        RunSummaryResponse run = Assert.IsType<RunSummaryResponse>(Read().LastRun);

        Assert.Equal("clean", run.Outcome);
        Assert.StartsWith("2026-08-27", run.StartedAt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_with_no_runs_reports_no_run_rather_than_throwing()
    {
        Assert.Null(Read().LastRun);
    }
}
