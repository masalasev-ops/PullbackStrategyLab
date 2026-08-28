using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The run log, and the two properties SCHEMA states about it: rows written is measured from
/// the store rather than reported by the stage, and the daily call ceiling is counted as the
/// job goes rather than checked afterwards.
/// </summary>
public sealed class RunLoggerTests : IDisposable
{
    private static readonly DateTimeOffset Evening =
        new(2026, 8, 25, 22, 5, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly FixedClock _clock = new(Evening);
    private readonly StoreConnectionFactory _connections;

    public RunLoggerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private RunLogger Logger(int dailyCallCeiling = 5000) =>
        new(_clock, Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            DailyCallCeiling = dailyCallCeiling,
        }));

    [Fact]
    public void Rows_written_is_measured_from_the_store_rather_than_reported_by_the_stage()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        Execute(connection, "CREATE TABLE measured_rows (id INTEGER PRIMARY KEY);");

        RunSummary summary;
        using (RunScope scope = Logger().Begin(connection, "measurement", "measured_rows"))
        {
            Execute(connection, "INSERT INTO measured_rows (id) VALUES (1), (2), (3);");
            summary = scope.Complete(RunOutcome.Clean);
        }

        // The scope was never told how many rows the stage wrote. It counted them, which is the
        // whole reason the stage declares its tables at the start.
        Assert.Equal(3, summary.RowsWritten);
        Assert.Equal(3, ReadInt(connection, "SELECT rows_written FROM run_log WHERE run_id = @id;", summary.RunId));
        Assert.Equal("clean", ReadText(connection, "SELECT outcome FROM run_log WHERE run_id = @id;", summary.RunId));
    }

    [Fact]
    public void A_scope_disposed_without_completing_writes_a_failed_end_entry()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        string runId;
        using (RunScope scope = Logger().Begin(connection, "abandoned"))
        {
            runId = scope.RunId;

            // No Complete. A stage that threw is worth more in the record as a failure than as a
            // row that starts and never ends, which reads as a job still running.
        }

        Assert.Equal("failed", ReadText(connection, "SELECT outcome FROM run_log WHERE run_id = @id;", runId));
        Assert.NotNull(ReadText(connection, "SELECT ended_at FROM run_log WHERE run_id = @id;", runId));
    }

    [Fact]
    public void The_daily_ceiling_is_counted_across_stages_rather_than_per_stage()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        RunLogger logger = Logger(dailyCallCeiling: 10);

        using (RunScope first = logger.Begin(connection, "symbol-list"))
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.True(first.TryCountCall());
            }

            Assert.Equal(6, first.CallsRemaining);
            first.Complete(RunOutcome.Clean);
        }

        using RunScope second = logger.Begin(connection, "bulk-bars");

        // The ceiling is a daily total across every stage, so the second stage starts with what
        // the first left rather than with a fresh allowance.
        Assert.Equal(6, second.CallsRemaining);
        second.Complete(RunOutcome.Clean);
    }

    [Fact]
    public void A_stage_stops_at_the_ceiling_and_completes_partial_rather_than_overrunning()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        RunLogger logger = Logger(dailyCallCeiling: 3);

        using RunScope scope = logger.Begin(connection, "bulk-bars");

        int made = 0;
        while (scope.TryCountCall())
        {
            made++;
        }

        Assert.Equal(3, made);
        Assert.Equal(0, scope.CallsRemaining);
        Assert.Throws<CallCeilingReachedException>(scope.CountCall);

        RunSummary summary = scope.Complete(RunOutcome.Partial);
        Assert.Equal(3, summary.CallsUsed);
        Assert.Equal("partial", ReadText(connection, "SELECT outcome FROM run_log WHERE run_id = @id;", summary.RunId));
    }

    [Fact]
    public void Calls_are_counted_against_the_utc_date_the_run_started_on()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        RunLogger logger = Logger(dailyCallCeiling: 10);

        using (RunScope today = logger.Begin(connection, "bulk-bars"))
        {
            today.CountCall();
            today.CountCall();
            today.Complete(RunOutcome.Clean);
        }

        Assert.Equal(2, RunLogger.CallsUsedOn(connection, DateOnly.FromDateTime(Evening.UtcDateTime)));

        _clock.Advance(TimeSpan.FromDays(1));
        using RunScope tomorrow = logger.Begin(connection, "bulk-bars");

        // A new day, a fresh allowance. The vendor's quota resets and the store's does with it.
        Assert.Equal(10, tomorrow.CallsRemaining);
        tomorrow.Complete(RunOutcome.Clean);
    }

    [Fact]
    public void The_store_refuses_an_outcome_that_is_not_one_of_the_three()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Execute(connection,
            "INSERT INTO run_log (run_id, stage, started_at) VALUES ('x', 'stage', '2026-08-25T22:05:00.000Z');");

        SqliteException failure = Assert.Throws<SqliteException>(() =>
            Execute(connection, "UPDATE run_log SET outcome = 'mostly-fine' WHERE run_id = 'x';"));

        Assert.Contains("CHECK", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int ReadInt(SqliteConnection connection, string sql, string runId) =>
        Convert.ToInt32(Scalar(connection, sql, runId), CultureInfo.InvariantCulture);

    private static string? ReadText(SqliteConnection connection, string sql, string runId) =>
        Scalar(connection, sql, runId)?.ToString();

    private static object? Scalar(SqliteConnection connection, string sql, string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", runId);
        object? value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void A_night_with_a_stage_that_stopped_short_names_it_and_an_ordinary_night_names_nothing()
    {
        var session = new DateOnly(2026, 8, 27);
        const string Zone = "America/New_York";

        using SqliteConnection connection = _connections.OpenWrite();

        // Two stages of the session's own evening, one of which stopped short of the ceiling.
        // 22:10Z is 18:10 Eastern, which is inside the session's own day in its own zone and is
        // the previous day in UTC, so a read bounded on the UTC date would miss both.
        Insert(connection, "daily-bars", "2026-08-27T22:10:00.000Z", "2026-08-27T22:12:00.000Z", "partial");
        Insert(connection, "indicators", "2026-08-27T23:00:00.000Z", "2026-08-27T23:04:00.000Z", "clean");

        Assert.Equal("daily-bars", RunLogger.DegradedBecause(connection, session, Zone));

        // The other direction: a night in which nothing stopped short is null rather than empty,
        // because "no stage ended other than cleanly" and "this was never written" would otherwise
        // be the same value on the row.
        Assert.Null(RunLogger.DegradedBecause(connection, new DateOnly(2026, 8, 26), Zone));
    }

    [Fact]
    public void A_stage_still_running_has_not_failed()
    {
        var session = new DateOnly(2026, 8, 27);
        const string Zone = "America/New_York";

        using SqliteConnection connection = _connections.OpenWrite();

        // An unended run. The detector asking this question is itself one, so a read that counted
        // unended runs would have every night report itself degraded.
        Insert(connection, "sectors", "2026-08-27T22:10:00.000Z", null, "partial");

        Assert.Null(RunLogger.DegradedBecause(connection, session, Zone));
    }

    private static void Insert(
        SqliteConnection connection, string stage, string startedAt, string? endedAt, string outcome)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, calls_used, rows_written, counts_against_ceiling)
            VALUES (@id, @stage, @started_at, @ended_at, @outcome, 0, 0, 1);
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("n"));
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue("@started_at", startedAt);
        command.Parameters.AddWithValue("@ended_at", (object?)endedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.ExecuteNonQuery();
    }

}
