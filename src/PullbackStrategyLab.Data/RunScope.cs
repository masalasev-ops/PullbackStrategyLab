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
public sealed class RunScope : ICallBudget, IDisposable
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
        int callsAlreadyUsedToday,
        CallCounting counting,
        RowDelta rowDelta)
    {
        Counting = counting;
        RowDelta = rowDelta;
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

    /// <summary>Whether this run's calls count against the day's ceiling.</summary>
    public CallCounting Counting { get; }

    /// <summary>
    /// Whether a row-count delta measures anything about this stage.
    ///
    /// Declared at <see cref="RunLogger.BeginUpdatingInPlace"/> rather than worked out here, because
    /// a stage that only updates its declared tables reports 0 on a perfect run and 0 on a run that
    /// died on the first name, and a figure that cannot distinguish those is not a measurement.
    /// see: A run whose writes are updates records no row count rather than a nought
    /// </summary>
    public RowDelta RowDelta { get; }

    /// <summary>
    /// Names this run walked past after a failure it survived, or null where it walked no list.
    ///
    /// Reported by the stage rather than measured, which is the opposite of how rows_written works
    /// and is deliberate: a skip is a decision the stage made and nothing in the store records it.
    /// The reason rows_written is measured is that a stage counting its own output reports what it
    /// believes it wrote, and there is no equivalent belief to guard against here.
    ///
    /// It exists because rows_written distinguishes nothing on an update-only stage. `sectors`
    /// issues UPDATE and never INSERT, so the delta is 0 whether it resolved every name or died on
    /// the first, and on 2026-08-27 it recorded 149 calls against 0 rows, which is what a clean run
    /// would also have recorded.
    /// </summary>
    public int? Skipped { get; private set; }

    /// <summary>Records that the run passed over one name, with the count kept for the end entry.</summary>
    public void CountSkipped() => Skipped = (Skipped ?? 0) + 1;

    /// <summary>
    /// What is left of the day's ceiling, across every stage that has already run, or no limit
    /// at all for a run outside it. A one-time operation is not the nightly job and is not
    /// charged against the guard the nightly job needs.
    /// </summary>
    public int CallsRemaining =>
        Counting == CallCounting.OutsideTheDailyCeiling
            ? int.MaxValue
            : Math.Max(0, _runLogger.DailyCallCeiling - _callsAlreadyUsedToday - CallsUsed);

    /// <summary>
    /// Counts one vendor call. Returns false when the day's ceiling is reached, so the
    /// stage stops and completes as partial rather than overrunning. The caller decides
    /// what a partial run means for its own output; nothing here guesses.
    /// </summary>
    public bool TryCountCall() => TryCountCalls(1);

    /// <summary>
    /// Counts a request costing more than one. A whole-market bulk request is priced far above
    /// a single-ticker one, and a budget that counted requests rather than their cost would
    /// report a fifth of what the day actually spent.
    /// </summary>
    public bool TryCountCalls(int cost)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost);

        if (CallsRemaining < cost)
        {
            return false;
        }

        CallsUsed += cost;
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

        int? rowsWritten = null;

        if (RowDelta == RowDelta.Measured)
        {
            int delta = 0;

            foreach ((string table, int baseline) in _baselineRowCounts)
            {
                delta += Math.Max(0, RunLogger.CountRows(_connection, table) - baseline);
            }

            rowsWritten = delta;
        }

        _runLogger.Complete(_connection, RunId, outcome, rowsWritten, CallsUsed, Skipped);
        _completed = true;

        // The summary keeps a number rather than a null, and says separately whether it measures
        // anything. A stage's own result record is read by its console line and by the phase replay,
        // and neither has a question the null answers; the column the nightly halt keys on does.
        return new RunSummary(RunId, Stage, outcome, rowsWritten ?? 0, CallsUsed, Skipped, RowDelta);
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

/// <summary>
/// What one run did. <see cref="RowDelta"/> says whether <see cref="RowsWritten"/> measures
/// anything: it is nought and meaningless on a stage whose declared tables it only updates, which is
/// the state <c>run_log.rows_written</c> records as null rather than as a figure.
/// </summary>
public sealed record RunSummary(
    string RunId,
    string Stage,
    RunOutcome Outcome,
    int RowsWritten,
    int CallsUsed,
    int? Skipped = null,
    RowDelta RowDelta = RowDelta.Measured);

/// <summary>Whether a row-count delta measures what a stage wrote.</summary>
public enum RowDelta
{
    /// <summary>The stage inserts, so the delta is a count of the rows it added.</summary>
    Measured,

    /// <summary>The stage only updates its declared tables, so the delta is 0 whatever it did.</summary>
    DoesNotApply,
}

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
