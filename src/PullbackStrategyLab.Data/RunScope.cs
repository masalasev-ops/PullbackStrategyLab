using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// One run of one stage. Holds the call count for the run and the row-count baseline the
/// end entry is measured against. Every statement against run_log itself is issued by
/// <see cref="RunLogger"/>, which is the table's only declared writer.
///
/// A scope that is disposed without being completed writes a failed end entry. A stage
/// that threw is worth more in the record as a failure than as a row that starts and never
/// ends, which reads as a job still running.
/// </summary>
public sealed class RunScope : IDisposable
{
    private readonly RunLogger _runLogger;
    private readonly SqliteConnection _connection;
    private readonly IReadOnlyDictionary<string, int> _baselineRowCounts;
    private readonly int _callsAlreadyUsedToday;
    private bool _completed;

    internal RunScope(
        RunLogger runLogger,
        SqliteConnection connection,
        string runId,
        string stage,
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, int> baselineRowCounts,
        int callsAlreadyUsedToday)
    {
        _runLogger = runLogger;
        _connection = connection;
        _baselineRowCounts = baselineRowCounts;
        _callsAlreadyUsedToday = callsAlreadyUsedToday;
        RunId = runId;
        Stage = stage;
        StartedAt = startedAt;
    }

    public string RunId { get; }

    public string Stage { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Vendor calls this run has spent.</summary>
    public int CallsUsed { get; private set; }

    /// <summary>What is left of the day's ceiling, across every stage that has already run.</summary>
    public int CallsRemaining =>
        Math.Max(0, _runLogger.DailyCallCeiling - _callsAlreadyUsedToday - CallsUsed);

    /// <summary>
    /// Counts one vendor call. Returns false when the day's ceiling is reached, so the
    /// stage stops and completes as partial rather than overrunning. The caller decides
    /// what a partial run means for its own output; nothing here guesses.
    /// </summary>
    public bool TryCountCall()
    {
        if (CallsRemaining == 0)
        {
            return false;
        }

        CallsUsed++;
        return true;
    }

    /// <summary>
    /// Counts one vendor call, throwing at the ceiling. For a caller that has no partial
    /// behaviour to fall back on and would rather stop loudly.
    /// </summary>
    public void CountCall()
    {
        if (!TryCountCall())
        {
            throw new CallCeilingReachedException(_runLogger.DailyCallCeiling, Stage);
        }
    }

    /// <summary>
    /// The end entry. rows_written is recounted from the store here rather than taken from
    /// the stage, which is the whole point of declaring the tables at Begin.
    /// </summary>
    public RunSummary Complete(RunOutcome outcome)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        int rowsWritten = 0;
        foreach ((string table, int baseline) in _baselineRowCounts)
        {
            rowsWritten += Math.Max(0, RunLogger.CountRows(_connection, table) - baseline);
        }

        _runLogger.Complete(_connection, RunId, outcome, rowsWritten, CallsUsed);
        _completed = true;
        return new RunSummary(RunId, Stage, outcome, rowsWritten, CallsUsed);
    }

    public void Dispose()
    {
        if (_completed)
        {
            return;
        }

        Complete(RunOutcome.Failed);
    }
}

public sealed record RunSummary(string RunId, string Stage, RunOutcome Outcome, int RowsWritten, int CallsUsed);

/// <summary>
/// Thrown when a stage asks for a vendor call the day's ceiling cannot cover. The ceiling
/// is a hard rule rather than a configuration detail, so overrunning it is an exception
/// rather than a warning.
/// </summary>
public sealed class CallCeilingReachedException : InvalidOperationException
{
    public CallCeilingReachedException(int dailyCallCeiling, string stage)
        : base($"Stage '{stage}' asked for a vendor call beyond the daily ceiling of {dailyCallCeiling}. " +
               "The stage stops and writes a partial run entry rather than overrunning.")
    {
        DailyCallCeiling = dailyCallCeiling;
        Stage = stage;
    }

    public int DailyCallCeiling { get; }

    public string Stage { get; }
}
