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
    /// <summary>
    /// Opens a run whose declared tables it only updates, so <c>rows_written</c> is written null
    /// rather than nought.
    ///
    /// The delta cannot see a write that changes a row rather than adding one, so on such a stage a
    /// perfect run and a run that died on the first name both report 0. Null says the measure does
    /// not apply; nought says the stage wrote nothing, and the nightly halt keys on the second. It
    /// is declared here rather than decided at the end, so it is part of what a stage says it writes
    /// rather than something a stage could forget to mention.
    /// see: A run whose writes are updates records no row count rather than a nought
    /// </summary>
    public RunScope BeginUpdatingInPlace(SqliteConnection connection, string stage, params string[] tablesWritten) =>
        Begin(connection, stage, CallCounting.AgainstTheDailyCeiling, RowDelta.DoesNotApply, tablesWritten);

    public RunScope Begin(SqliteConnection connection, string stage, CallCounting counting, params string[] tablesWritten) =>
        Begin(connection, stage, counting, RowDelta.Measured, tablesWritten);

    private RunScope Begin(
        SqliteConnection connection,
        string stage,
        CallCounting counting,
        RowDelta rowDelta,
        params string[] tablesWritten)
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

        int callsAlreadyUsedToday = CallsUsedOn(connection, VendorQuotaDay.Containing(startedAt));
        var baseline = tablesWritten.ToDictionary(t => t, t => CountRows(connection, t), StringComparer.Ordinal);

        return new RunScope(this, connection, runId, stage, startedAt, baseline, callsAlreadyUsedToday, counting, rowDelta);
    }

    /// <summary>The end entry. Called by the scope, never by a stage.</summary>
    internal void Complete(
        SqliteConnection connection,
        string runId,
        RunOutcome outcome,
        int? rowsWritten,
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
        command.Parameters.AddWithValue("@rows_written", (object?)rowsWritten ?? DBNull.Value);
        command.Parameters.AddWithValue("@calls_used", callsUsed);
        command.Parameters.AddWithValue("@skipped", (object?)skipped ?? DBNull.Value);
        command.Parameters.AddWithValue("@run_id", runId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Vendor calls already spent in one quota day, summed across every stage. The ceiling is
    /// a daily total rather than a per-stage allowance, so a stage cannot know its own
    /// budget without reading what the earlier stages spent.
    ///
    /// <b>It takes a <see cref="VendorQuotaDay"/> rather than a date, and that is the 3.12
    /// obligation discharged at 4.3.</b> This read and <see cref="IncompleteStagesOf"/> answer two
    /// different questions about the same column, and until 4.3 both truncated it with
    /// <c>substr(started_at, 1, 10)</c>: the quota day correctly, because the vendor's allowance
    /// resets on a UTC boundary, and the session night incorrectly, because the lab's night crosses
    /// that boundary. 3.12 repaired the second and left one correct use of an expression no guard
    /// could tell from an incorrect one. Now each quantity is a named window and each read is
    /// bounded between its two instants, so the two statements differ in the parameter they take and
    /// not in a comment above them, and the truncation appears nowhere in the shipped source.
    /// </summary>
    public static int CallsUsedOn(SqliteConnection connection, VendorQuotaDay quotaDay)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(calls_used), 0)
              FROM run_log
             WHERE started_at >= @quota_day_start
               AND started_at < @quota_day_end
               AND counts_against_ceiling = 1;
            """;
        command.Parameters.AddWithValue("@quota_day_start", StoreText.TimestampToStorageText(quotaDay.Start));
        command.Parameters.AddWithValue("@quota_day_end", StoreText.TimestampToStorageText(quotaDay.End));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The stages of one session that had already ended other than cleanly, in the order they ran.
    ///
    /// What a detector writes into <c>setup.degraded_because</c>, which is the third clause of the
    /// vendor-ceiling rule: a stage stops rather than overrunning, writes a partial run entry, and
    /// marks the affected setups degraded. The first two held from 1.4 and the third had no column
    /// anywhere until 032.
    ///
    /// Bounded to the session's own day in the session zone, so a night is the night rather than a
    /// UTC date that splits it. The budget read above is bounded on the UTC date instead, and the
    /// two are deliberately different: the ceiling is a fact about the vendor's quota day and this
    /// is a fact about the lab's session.
    ///
    /// Only runs that have ended. A stage still running has not failed, and the detector asking is
    /// itself an unended run, so an unbounded read would have every night report itself degraded.
    /// see: Averages are computed locally, never through the vendor's technical endpoint
    /// </summary>
    /// <summary>
    /// Whether a stage ran to an end on a session's own day, whatever it wrote.
    ///
    /// <b>The reader the two cap readers needed, from 5.8.</b> `SetupCapper` writes its decision
    /// on candidate rows only, and SCHEMA says both columns are null on a setup that failed a gate,
    /// so a night with no candidate leaves no cap decision anywhere; the stages reading the cap
    /// then said the night was never capped, which their own comments reserve for a stage that did
    /// not run. The cap's own run row is the one thing that tells the two nights apart, and it is
    /// read here rather than by each reader, on the terms every statement against this table lives
    /// in this class.
    /// </summary>
    public static bool StageRanOn(SqliteConnection connection, string stage, DateOnly session, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM run_log
             WHERE stage = @stage
               AND ended_at IS NOT NULL
               AND started_at >= @start_of_day
               AND started_at <= @end_of_day;
            """;
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue(
            "@start_of_day", StoreText.TimestampToStorageText(SessionBoundaries.At(session, TimeOnly.MinValue, sessionZone)));
        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(session, sessionZone));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    public static IReadOnlyList<string> IncompleteStagesOf(
        SqliteConnection connection, DateOnly session, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT stage
              FROM run_log
             WHERE ended_at IS NOT NULL
               AND outcome <> 'clean'
               AND started_at >= @start_of_day
               AND started_at <= @end_of_day
             ORDER BY stage;
            """;

        command.Parameters.AddWithValue(
            "@start_of_day", StoreText.TimestampToStorageText(SessionBoundaries.At(session, TimeOnly.MinValue, sessionZone)));
        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(session, sessionZone));

        var stages = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            stages.Add(reader.GetString(0));
        }

        return stages;
    }

    /// <summary>
    /// What a setup row records about the night's inputs, or null on an ordinary night.
    ///
    /// Null rather than an empty string, because "no stage of this session ended other than
    /// cleanly" and "this column was never written" would otherwise be the same value.
    /// </summary>
    public static string? DegradedBecause(
        SqliteConnection connection, DateOnly session, string sessionZone)
    {
        IReadOnlyList<string> stages = IncompleteStagesOf(connection, session, sessionZone);
        return stages.Count == 0 ? null : string.Join(", ", stages);
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
