using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Sole writer of <c>run_log</c>, both operations. Stages do not write this table; they
/// open a scope here. Declaring every stage as a writer would put the run accounting in a
/// dozen places and writer-ownership could never pass.
///
/// Every statement against run_log lives in this class for the same reason: the check
/// attributes a write to the type that issues it, so a helper elsewhere issuing one would
/// be a second declared writer of the same table.
/// </summary>
public sealed class RunLogger
{
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public RunLogger(IClock clock, IOptions<PullbackStrategyLabOptions> options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public IClock Clock => _clock;

    public int DailyCallCeiling => _options.DailyCallCeiling;

    /// <summary>
    /// Opens a run. <paramref name="tablesWritten"/> is what the stage declares it writes,
    /// and it is only used to count: the scope reads those tables' row counts now and again
    /// at the end, so rows_written is measured from the store rather than reported by the
    /// stage. A stage counting its own output reports what it believes it wrote, and the
    /// nightly halt keys on this number.
    /// </summary>
    public RunScope Begin(SqliteConnection connection, string stage, params string[] tablesWritten) =>
        Begin(connection, stage, CallCounting.AgainstTheDailyCeiling, tablesWritten);

    /// <summary>
    /// Opens a run that says whether its vendor calls count against the day's ceiling.
    ///
    /// The ceiling guards the nightly job. A one-time operation is not the nightly job, and
    /// charging the two against each other is what made the history backfill look like a
    /// two-day procedure: it was never too large for the vendor, only for a budget that had
    /// already spent itself on the evening's work. The calls are recorded either way, because
    /// what a run cost is worth knowing about every run.
    /// </summary>
    public RunScope Begin(SqliteConnection connection, string stage, CallCounting counting, params string[] tablesWritten)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(tablesWritten);

        foreach (string table in tablesWritten)
        {
            SqliteIdentifier.Validate(table);
        }

        DateTimeOffset startedAt = _clock.UtcNow;
        string runId = Guid.NewGuid().ToString("n");

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO run_log
                    (run_id, stage, started_at, ended_at, outcome, rows_written, calls_used, counts_against_ceiling)
                VALUES (@run_id, @stage, @started_at, NULL, NULL, NULL, 0, @counts);
                """;
            command.Parameters.AddWithValue("@run_id", runId);
            command.Parameters.AddWithValue("@stage", stage);
            command.Parameters.AddWithValue("@started_at", StoreText.TimestampToStorageText(startedAt));
            command.Parameters.AddWithValue("@counts", counting == CallCounting.AgainstTheDailyCeiling ? 1 : 0);
            command.ExecuteNonQuery();
        }

        int callsAlreadyUsedToday = CallsUsedOn(connection, DateOnly.FromDateTime(startedAt.UtcDateTime));
        var baseline = tablesWritten.ToDictionary(t => t, t => CountRows(connection, t), StringComparer.Ordinal);

        return new RunScope(this, connection, runId, stage, startedAt, baseline, callsAlreadyUsedToday, counting);
    }

    /// <summary>The end entry. Called by the scope, never by a stage.</summary>
    internal void Complete(
        SqliteConnection connection,
        string runId,
        RunOutcome outcome,
        int rowsWritten,
        int callsUsed,
        int? skipped = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE run_log
               SET ended_at = @ended_at,
                   outcome = @outcome,
                   rows_written = @rows_written,
                   calls_used = @calls_used,
                   skipped = @skipped
             WHERE run_id = @run_id;
            """;
        command.Parameters.AddWithValue("@ended_at", StoreText.TimestampToStorageText(_clock.UtcNow));
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@rows_written", rowsWritten);
        command.Parameters.AddWithValue("@calls_used", callsUsed);
        command.Parameters.AddWithValue("@skipped", (object?)skipped ?? DBNull.Value);
        command.Parameters.AddWithValue("@run_id", runId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Vendor calls already spent on a UTC date, summed across every stage. The ceiling is
    /// a daily total rather than a per-stage allowance, so a stage cannot know its own
    /// budget without reading what the earlier stages spent.
    ///
    /// The budget day is the UTC date because time is UTC in storage and the vendor's own
    /// quota resets on a fixed daily boundary. Whether that boundary is exactly UTC
    /// midnight is confirmed against the vendor at 1.3, when the first real call is made.
    /// </summary>
    public static int CallsUsedOn(SqliteConnection connection, DateOnly utcDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(calls_used), 0)
              FROM run_log
             WHERE substr(started_at, 1, 10) = @utc_date
               AND counts_against_ceiling = 1;
            """;
        command.Parameters.AddWithValue("@utc_date", StoreText.DateToStorageText(utcDate));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static int CountRows(SqliteConnection connection, string table)
    {
        SqliteIdentifier.Validate(table);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Whether a run's vendor calls count against the day's ceiling. A one-time operation does not,
/// and says so in the run log rather than being recognised by its stage name.
/// </summary>
public enum CallCounting
{
    /// <summary>The nightly default. Every stage in the evening's sequence.</summary>
    AgainstTheDailyCeiling,

    /// <summary>A one-time operation. Its calls are recorded and the nightly total does not see them.</summary>
    OutsideTheDailyCeiling,
}

public enum RunOutcome
{
    /// <summary>Everything the stage set out to do, done.</summary>
    Clean,

    /// <summary>Stopped short rather than overrunning the ceiling. The affected setups are degraded.</summary>
    Partial,

    /// <summary>Threw, or was abandoned. Written rather than left with no end entry at all.</summary>
    Failed,
}

public static class RunOutcomeText
{
    public static string ToStorageText(this RunOutcome outcome) => outcome switch
    {
        RunOutcome.Clean => "clean",
        RunOutcome.Partial => "partial",
        RunOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}
