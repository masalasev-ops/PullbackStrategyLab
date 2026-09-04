using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Tracks the eight holdout windows and refuses to re-spend one.
///
/// <b>The register is created empty and fills up over two years.</b> A window is a calendar quarter
/// of forward-collected evidence, so it exists the day its quarter completes and not before. At this
/// checkpoint the lab has been running since 2026-08-27 and no quarter has completed, so the
/// register holds nothing and every property here is verified over an authored population
/// (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight).
///
/// <b>A spent window is never re-spent, and the store is what enforces it.</b> The spend is a row
/// whose primary key is the window, so a second spend of the same window is refused by SQLite before
/// any code here sees it. That is deliberate and it is the reason the spend is not a nullable column
/// on the window row: a rule held in an `UPDATE` statement's `WHERE` clause is a rule the next
/// statement can be written without.
///
/// <b>A register holding nothing has three causes and they are different facts.</b> No session has
/// been recorded, so no quarter has begun. Sessions have been recorded and no quarter has completed,
/// which is the ordinary state for the first months. Or every matured window has been spent, which
/// is the designed dead end. A run that reported one empty count could not tell them apart, and for
/// the first three months of this lab's life the first two will read identically, so the reason is
/// stored rather than inferred.
///
/// <b>And a fourth state is a defect rather than a cause</b>: quarters have matured and the register
/// does not hold them. The registry computes what should exist from the store's own earliest session
/// and compares, so a run that never recorded a window is partial and says which are missing rather
/// than reporting an empty register as correct.
/// </summary>
public sealed class HoldoutRegistry
{
    public const string Name = "holdout";

    // The four reasons a register holds nothing moved to `HoldoutRegister` in the Data assembly at
    // 5.5, with the comparison that produces them. The research ledger reads the same register and
    // the read surface may not reference this assembly, so leaving them here would have meant either
    // a second copy of four sentences or a page that could not say why the register was empty.

    /// <summary>What a spend is refused with where the register holds no window to spend.</summary>
    public const string NothingToSpend =
        "the register holds no window available to spend";

    /// <summary>What a spend is refused with where the window named is already spent.</summary>
    public const string AlreadySpent =
        "that window has already been spent, and a spent window is never reused for any purpose";

    /// <summary>What a spend is refused with where the window named is not in the register.</summary>
    public const string NoSuchWindow =
        "the register holds no window of that name as of this date";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public HoldoutRegistry(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    /// <summary><c>holdout [as-of]</c>, which records every window that has matured and reports the register.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        HoldoutRegisterState state = Mature(asOf);

        Console.WriteLine(
            $"{Name}: as of {state.AsOf:yyyy-MM-dd}, first session "
            + $"{(state.FirstSession is DateOnly first ? first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "none")}");
        Console.WriteLine(
            $"{Name}: {state.Matured} of {HoldoutWindows.Capacity} window(s) matured, {state.Recorded} recorded, "
            + $"{state.Written} written this run, {state.Spent} spent, {state.Available} available");

        if (state.EmptyBecause is string why)
        {
            Console.WriteLine($"{Name}: no window is available to spend, and {why}");
        }

        if (state.Missing.Count > 0)
        {
            Console.Error.WriteLine(
                $"{Name}: {state.Missing.Count} matured window(s) the register does not hold: "
                + string.Join(", ", state.Missing));
        }

        Console.WriteLine($"{Name}: {state.Outcome.ToStorageText()}");

        return state.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// What the register holds as of a date, and what the calendar says it should hold, without
    /// writing anything.
    ///
    /// <b>This is the read that tells an empty register from a defective one</b>, and it is separate
    /// from the run below because the run cures the defect in the act of looking for it: a stage that
    /// records the matured windows and then reports can never report one missing. What can is a read
    /// standing outside it, which is what the research ledger makes and what an operator makes on a
    /// morning the job did not fire.
    /// </summary>
    public HoldoutRegisterState Read(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Describe(connection, asOf, written: 0);
    }

    /// <summary>
    /// Records every window that has matured and is not yet in the register, and reports what the
    /// register then holds.
    ///
    /// <b>Insert only, and it is idempotent by the store's own key.</b> A window is a fact about the
    /// calendar, so a second run of the same evening finds every matured window already recorded and
    /// writes none.
    /// </summary>
    public HoldoutRegisterState Mature(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "holdout_window", "holdout_run");

        DateTimeOffset observedAt = _clock.UtcNow;
        string zone = _options.SessionZone;

        DateOnly? firstSession = HoldoutWindowReader.FirstSession(connection, asOf);

        IReadOnlyList<HoldoutWindow> matured = firstSession is DateOnly first
            ? HoldoutWindows.MaturedBy(first, asOf)
            : [];

        var held = new HashSet<string>(
            HoldoutWindowReader.Read(connection, asOf, zone).Select(w => w.Window.WindowId),
            StringComparer.Ordinal);

        int written = 0;

        foreach (HoldoutWindow window in matured.Where(w => !held.Contains(w.WindowId)))
        {
            Insert(connection, window, observedAt);
            written++;
        }

        HoldoutRegisterState state = Describe(connection, asOf, written);

        WriteRun(connection, state, observedAt);
        run.Complete(state.Outcome);

        return state;
    }

    /// <summary>
    /// The register against the calendar, which is one implementation in the Data assembly rather
    /// than a method here.
    ///
    /// It was private to this stage at 5.4 and moved at 5.5, when the research ledger needed the
    /// same answer and could not reach it: the read surface has no reference to the Worker and
    /// <c>api-isolation</c> asserts that against the compiled dependency file. The alternative was a
    /// page that reported the register from the last run row, and nothing schedules this stage, so
    /// that page would have said the register was empty for a reason about the scheduler.
    /// </summary>
    private HoldoutRegisterState Describe(SqliteConnection connection, DateOnly asOf, int written) =>
        HoldoutRegister.Describe(connection, asOf, _options.SessionZone, written);

    /// <summary>
    /// Spends the oldest available window on one decision, or refuses and says why.
    ///
    /// <b>Oldest first, which is the ordering the authored parameter states.</b> Choosing which
    /// window to spend would let a decision pick the quarter that suits it, and the whole point of a
    /// finite budget is that it cannot be shopped.
    /// </summary>
    public HoldoutSpendResult SpendOldest(string spentOn, string outcome, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spentOn);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        using SqliteConnection connection = _connections.OpenWrite();

        StoredHoldoutWindow? oldest = HoldoutWindowReader
            .Read(connection, asOf, _options.SessionZone)
            .FirstOrDefault(w => w.IsAvailable);

        return oldest is null
            ? new HoldoutSpendResult(null, false, NothingToSpend)
            : Spend(connection, oldest.Window.WindowId, spentOn, outcome, asOf);
    }

    /// <summary>
    /// Spends one named window, or refuses and says why.
    ///
    /// <b>The refusal a re-spend gets is the store's, not this method's.</b> The check below reads
    /// the register first so a caller gets a sentence rather than a constraint violation, and the
    /// insert is still what enforces the rule: strip the check and a second spend is refused
    /// anyway, which is what <c>HoldoutRegistryTests</c> proves by writing straight to the store.
    /// </summary>
    public HoldoutSpendResult Spend(string windowId, string spentOn, string outcome, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);

        using SqliteConnection connection = _connections.OpenWrite();
        return Spend(connection, windowId, spentOn, outcome, asOf);
    }

    private HoldoutSpendResult Spend(
        SqliteConnection connection, string windowId, string spentOn, string outcome, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spentOn);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        StoredHoldoutWindow? window = HoldoutWindowReader
            .Read(connection, asOf, _options.SessionZone)
            .FirstOrDefault(w => string.Equals(w.Window.WindowId, windowId, StringComparison.Ordinal));

        if (window is null)
        {
            return new HoldoutSpendResult(null, false, NoSuchWindow);
        }

        if (!window.IsAvailable)
        {
            return new HoldoutSpendResult(windowId, false, AlreadySpent);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO holdout_spend (window_id, spent_on, outcome, spent_at)
            VALUES (@window_id, @spent_on, @outcome, @spent_at)
            """;

        command.Parameters.AddWithValue("@window_id", windowId);
        command.Parameters.AddWithValue("@spent_on", spentOn);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@spent_at", StoreText.TimestampToStorageText(_clock.UtcNow));
        command.ExecuteNonQuery();

        return new HoldoutSpendResult(windowId, true, null);
    }

    private static void Insert(SqliteConnection connection, HoldoutWindow window, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO holdout_window (window_id, ordinal, quarter_start, quarter_end, matures_on, recorded_at)
            VALUES (@window_id, @ordinal, @quarter_start, @quarter_end, @matures_on, @recorded_at)
            """;

        command.Parameters.AddWithValue("@window_id", window.WindowId);
        command.Parameters.AddWithValue("@ordinal", window.Ordinal);
        command.Parameters.AddWithValue("@quarter_start", StoreText.DateToStorageText(window.Start));
        command.Parameters.AddWithValue("@quarter_end", StoreText.DateToStorageText(window.End));
        command.Parameters.AddWithValue("@matures_on", StoreText.DateToStorageText(window.MaturesOn));
        command.Parameters.AddWithValue("@recorded_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void WriteRun(
        SqliteConnection connection, HoldoutRegisterState state, DateTimeOffset observedAt)
    {
        string? stoppedBecause = state.Missing.Count == 0
            ? null
            : $"{state.Missing.Count} matured window(s) are not in the register: "
              + string.Join(", ", state.Missing);

        using SqliteCommand command = connection.CreateCommand();

        // On conflict do nothing, on `loss_run`'s precedent: a rerun under a fixed clock lands on
        // the same instant and the first row is the one that happened.
        command.CommandText = """
            INSERT INTO holdout_run (observed_at, as_of, first_session, matured, recorded, written,
                                     spent, available, outcome, empty_because, stopped_because)
            VALUES (@observed_at, @as_of, @first_session, @matured, @recorded, @written,
                    @spent, @available, @outcome, @empty_because, @stopped_because)
            ON CONFLICT (observed_at) DO NOTHING
            """;

        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(state.AsOf));
        command.Parameters.AddWithValue(
            "@first_session",
            state.FirstSession is DateOnly first ? StoreText.DateToStorageText(first) : (object)DBNull.Value);
        command.Parameters.AddWithValue("@matured", state.Matured);
        command.Parameters.AddWithValue("@recorded", state.Recorded);
        command.Parameters.AddWithValue("@written", state.Written);
        command.Parameters.AddWithValue("@spent", state.Spent);
        command.Parameters.AddWithValue("@available", state.Available);
        command.Parameters.AddWithValue("@outcome", state.Outcome.ToStorageText());
        command.Parameters.AddWithValue("@empty_because", (object?)state.EmptyBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}

/// <summary>What a spend did, or why it was refused.</summary>
public sealed record HoldoutSpendResult(string? WindowId, bool Spent, string? RefusedBecause);
