using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Reads each recent session's schedule against <c>run_log</c> and that night's log, and writes a
/// row for every slot that did not run, saying which of four reasons it was.
///
/// <b>The fact it records is one no stage can record about itself.</b> The tree guard in
/// <c>tools/nightly.ps1</c> exits before the worker starts, so a refused slot never reaches the one
/// declared writer of <c>run_log</c>, and invoking a verb from a tree the guard has just called
/// untrusted to record that it is untrusted would be circular, and would itself refuse where that
/// tree's migrations were ahead. So the guard writes a line to the night's log, and this reads it the
/// next night from a tree the guard passed, and writes the row through <see cref="RunLogger"/>.
///
/// <b>It runs as the second verb of the <c>index</c> slot, and the reason is the holiday trap.</b> A
/// night whose ingest never ran and a session the market never held look the same in the daily bars,
/// because <c>daily-bars</c> asks the vendor's bulk endpoint for its own date and nothing else: a
/// session whose night was lost is never ingested by the night after it. <c>index-bars</c> refetches
/// each tracker's whole history every night, so once it has run the store holds every session the
/// market held up to today, whatever happened on the nights between. A session with no index bar,
/// once a later one is stored, is one the market did not hold. Before a later one is stored nothing
/// can tell, and the slot is left unrecorded rather than guessed at.
///
/// <b>What it will not write.</b> A slot with no run, no line in the log, and a session the index
/// history cannot yet place is counted as missing and gets no row, because the only thing known about
/// it is that nothing is known. The morning report already says it never ran.
/// see: A slot that did not run is recorded the next night from its log, and a holiday is read from the index history
/// </summary>
public sealed partial class NightReconciler
{
    public const string Name = "reconcile-night";

    /// <summary>
    /// How far back an ordinary night looks. A week rather than one night, so a night on which this
    /// stage did not run is caught up by the next one that does, and each fact is still written once
    /// because the store refuses a second row for the same slot and session.
    /// </summary>
    public const int LookbackDays = 7;

    /// <summary>How many index bars back from today the market test reads, which covers the lookback with room.</summary>
    private const int IndexSessionsRead = 30;

    private readonly StoreConnectionFactory _connections;
    private readonly PullbackStrategyLabPaths _paths;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public NightReconciler(
        StoreConnectionFactory connections,
        PullbackStrategyLabPaths paths,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _runLogger = runLogger ?? throw new ArgumentNullException(nameof(runLogger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly today = _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        IReadOnlyList<DateOnly> sessions = args.Length > 0
            ? [DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)]
            : Lookback(today);

        ReconcileResult result = Reconcile(sessions, ReadLog);

        foreach (NightReconciliation night in result.Nights)
        {
            Console.WriteLine(
                $"{Name}: {night.Session:yyyy-MM-dd}, {night.Due} slot(s) fired by the schedule, {night.Ran} ran, "
                + $"{night.AlreadyRecorded} already recorded, {night.Written} written"
                + (night.WrittenBecause.Count == 0
                    ? string.Empty
                    : " (" + string.Join(", ", night.WrittenBecause.Select(w => $"{w.Value} {w.Key}")) + ")")
                + $", {night.Missing} with no reason anything can establish, {night.Unobservable} the store cannot see"
                + $"; the market {night.Market.Reads()}");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>The calendar days before today an ordinary night reconciles, oldest first.</summary>
    public static IReadOnlyList<DateOnly> Lookback(DateOnly today) =>
        [.. Enumerable.Range(1, LookbackDays).Select(back => today.AddDays(-back)).OrderBy(d => d)];

    /// <summary>
    /// The reconciliation itself, over the sessions given and the log each one's night left.
    ///
    /// The log is handed in rather than read here, so a fixture can feed the guard's own line to it
    /// without a file, and the stage's <see cref="Run"/> is the only place the night's log is opened.
    /// </summary>
    public ReconcileResult Reconcile(IReadOnlyList<DateOnly> sessions, Func<DateOnly, IReadOnlyList<string>> logFor)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logFor);

        DateOnly today = _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "run_log");

        var nights = new List<NightReconciliation>();

        foreach (DateOnly session in sessions.Distinct().Order())
        {
            // A night that has not finished has nothing to reconcile yet, and this stage runs at
            // 17:50 on a night whose later slots are still ahead of it.
            if (session >= today)
            {
                throw new ArgumentException(
                    $"{session:yyyy-MM-dd} is not before today, {today:yyyy-MM-dd}, so its night has not finished "
                    + "and a slot still ahead of it would be recorded as not having run.",
                    nameof(sessions));
            }

            nights.Add(ReconcileOne(connection, session, NightLog.Parse(logFor(session), session)));
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new ReconcileResult(nights, summary.RowsWritten, RunOutcome.Clean);
    }

    private NightReconciliation ReconcileOne(SqliteConnection connection, DateOnly session, NightLog log)
    {
        IReadOnlyList<NightSlot> fired = NightlySchedule.FiresOn(session.DayOfWeek);

        HashSet<string> ran =
        [
            .. RunLogger.StagesOn(connection, session, _options.SessionZone).Select(r => r.Stage),
        ];

        IReadOnlyDictionary<string, string> recorded = RunLogger.DidNotRunOn(connection, session);

        // Only a weekday is a session the market could have held. The weekly slots fire on a
        // Saturday for the lab's own research and have nothing to do with whether anybody traded.
        MarketDay market = session.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            ? MarketDay.NotASession
            : Market(connection, session);

        int ranCount = 0;
        int alreadyRecorded = 0;
        int missing = 0;
        int unobservable = 0;
        var written = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (NightSlot slot in fired)
        {
            if (slot.Stages.Any(ran.Contains))
            {
                ranCount++;
                continue;
            }

            if (recorded.ContainsKey(slot.Slot))
            {
                alreadyRecorded++;
                continue;
            }

            string? because = log.Reasons.GetValueOrDefault(slot.Slot);

            if (because is null && log.Started.Contains(slot.Slot))
            {
                // It fired and nothing it runs reached the store. On the one slot that never writes
                // a run entry that is what a clean run looks like too, so this says nothing about it.
                if (slot.LeavesNoRunEntry is not null)
                {
                    unobservable++;
                    continue;
                }

                because = DidNotRunBecause.Fault;
            }

            if (because is null && market == MarketDay.NotHeld)
            {
                because = DidNotRunBecause.MarketClosed;
            }

            if (because is null)
            {
                missing++;
                continue;
            }

            if (_runLogger.RecordDidNotRun(connection, Name, slot.Slot, session, because))
            {
                written[because] = written.GetValueOrDefault(because) + 1;
            }
            else
            {
                alreadyRecorded++;
            }
        }

        return new NightReconciliation(
            session, fired.Count, ranCount, alreadyRecorded, written.Values.Sum(), missing, unobservable,
            written, market);
    }

    /// <summary>
    /// Whether the market held a session on a weekday, read from the index history.
    ///
    /// Every tracker has to agree, so one symbol the vendor stopped answering for cannot declare a
    /// holiday on its own. A session is not held only where every tracker holds a bar dated after it
    /// and none holds one dated on it; where no tracker holds a later bar the ingest has not reached
    /// past it yet and nothing can be said.
    /// </summary>
    private MarketDay Market(SqliteConnection connection, DateOnly session)
    {
        DateOnly today = _clock.SessionDate(_clock.UtcNow, _options.SessionZone);
        bool everyTrackerHasALaterBar = _options.IndexSymbols.Count > 0;

        foreach (string symbol in _options.IndexSymbols)
        {
            IReadOnlyList<StoredDailyBar> bars = IndexBarReader.Read(
                connection, symbol, today, IndexSessionsRead, _clock.UtcNow, _options.SessionZone);

            if (bars.Any(b => b.BarDate == session))
            {
                return MarketDay.Held;
            }

            everyTrackerHasALaterBar &= bars.Any(b => b.BarDate > session);
        }

        return everyTrackerHasALaterBar ? MarketDay.NotHeld : MarketDay.NotYetKnown;
    }

    private IReadOnlyList<string> ReadLog(DateOnly session)
    {
        string file = _paths.NightLogFile(session);
        return File.Exists(file) ? File.ReadAllLines(file) : [];
    }

    /// <summary>
    /// What one night's log says about its slots: which ones fired, and which ones the script itself
    /// said did not run and why.
    /// </summary>
    public sealed partial record NightLog(IReadOnlyDictionary<string, string> Reasons, IReadOnlySet<string> Started)
    {
        /// <summary>
        /// The structured line <c>tools/nightly.ps1</c> writes for a slot it will not run. Read against
        /// the script's own format string by a test, so the two cannot drift into two spellings.
        /// </summary>
        [GeneratedRegex(
            @"\bdid-not-run slot=(?<slot>[a-z-]+) session=(?<session>\d{4}-\d{2}-\d{2}) because=(?<because>[a-z-]+)",
            RegexOptions.CultureInvariant)]
        private static partial Regex DidNotRunLine();

        /// <summary>The line every slot writes first, before any guard runs.</summary>
        [GeneratedRegex(@"\bslot (?<slot>[a-z-]+) starting,", RegexOptions.CultureInvariant)]
        private static partial Regex StartingLine();

        /// <summary>
        /// The guard's own refusal, as it has read since 4.2. Read as well as the structured line
        /// because every night before 7.1 carries only this one, and the nights it matters most for
        /// are those: the operator reconciles them by naming the date.
        /// </summary>
        [GeneratedRegex(@"^\S+ \S+\s+refusing:", RegexOptions.CultureInvariant)]
        private static partial Regex RefusingLine();

        public static NightLog Parse(IEnumerable<string> lines, DateOnly session)
        {
            ArgumentNullException.ThrowIfNull(lines);

            string sessionText = session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            var started = new HashSet<string>(StringComparer.Ordinal);

            // The slots that have fired and have no reason yet, most recent last. A refusal in the
            // guard's older wording names no slot, and it belongs to the slot that started most
            // recently before it without one: slots that share a minute each write their own start
            // and their own refusal, so the pairing can move a refusal between two slots that were
            // both refused and never onto one that was not.
            var open = new List<string>();

            foreach (string line in lines)
            {
                Match structured = DidNotRunLine().Match(line);
                if (structured.Success)
                {
                    if (string.Equals(structured.Groups["session"].Value, sessionText, StringComparison.Ordinal))
                    {
                        string slot = structured.Groups["slot"].Value;
                        reasons[slot] = structured.Groups["because"].Value;
                        open.Remove(slot);
                    }

                    continue;
                }

                Match starting = StartingLine().Match(line);
                if (starting.Success)
                {
                    string slot = starting.Groups["slot"].Value;
                    started.Add(slot);
                    open.Remove(slot);
                    open.Add(slot);
                    continue;
                }

                if (RefusingLine().IsMatch(line) && open.Count > 0)
                {
                    string slot = open[^1];
                    reasons[slot] = DidNotRunBecause.RefusedByTheTreeGuard;
                    open.RemoveAt(open.Count - 1);
                }
            }

            return new NightLog(reasons, started);
        }
    }
}

/// <summary>What the index history says about one day.</summary>
public enum MarketDay
{
    /// <summary>A tracker holds a bar dated on it.</summary>
    Held,

    /// <summary>Every tracker holds a later bar and none holds one dated on it.</summary>
    NotHeld,

    /// <summary>No tracker holds a bar after it yet, so a lost night and a holiday cannot be told apart.</summary>
    NotYetKnown,

    /// <summary>A weekend day, which is nobody's session.</summary>
    NotASession,
}

public static class MarketDayText
{
    public static string Reads(this MarketDay day) => day switch
    {
        MarketDay.Held => "held the session",
        MarketDay.NotHeld => "held no session, by the index history",
        MarketDay.NotYetKnown => "cannot be placed yet, because no index bar after it is stored",
        MarketDay.NotASession => "does not open on a weekend",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null),
    };
}

/// <summary>What one session's reconciliation found and wrote.</summary>
public sealed record NightReconciliation(
    DateOnly Session,
    int Due,
    int Ran,
    int AlreadyRecorded,
    int Written,
    int Missing,
    int Unobservable,
    IReadOnlyDictionary<string, int> WrittenBecause,
    MarketDay Market);

/// <summary>What one run of the reconciliation did, session by session.</summary>
public sealed record ReconcileResult(
    IReadOnlyList<NightReconciliation> Nights,
    int RowsWritten,
    RunOutcome Outcome);
