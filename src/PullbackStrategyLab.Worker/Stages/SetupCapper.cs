using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Sixty setups a night, forty long and twenty short, unused slots released.
///
/// The number is set by what tomorrow costs rather than by anything about the strategy: every kept
/// setup is five minute-bar calls in the morning.
///
/// <b>It caps the shared candidate list, before any version selects.</b> There are no versions yet,
/// which is exactly why the property is worth asserting now: once several versions pick from the
/// same night, a cap applied per version would leave their disagreements unscoreable, and by then
/// the record it destroyed cannot be reconstructed. `setup` carries no version column, so a
/// per-version cap is not expressible without a schema change, and a test says so.
/// see: The nightly cap is 60, split forty long and twenty short, unused slots released
/// see: Versions select from one shared nightly candidate list rather than each re-scanning
///
/// <b>Truncated rows keep their rank.</b> A night that recorded only what it kept could never answer
/// whether the cap was binding, and how far past sixty a night went is the thing that decides whether
/// sixty is the right number.
///
/// Updates `rank` and `capped_out` and nothing else, which is what SCHEMA declares. The arithmetic is
/// in <see cref="NightlyCap"/> so the release rule can be swept over every arrangement of the two
/// counts rather than over the ones a fixture happened to produce.
/// </summary>
public sealed class SetupCapper
{
    public const string Name = "cap";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SetupCapper(
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

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        CapResult result = Cap(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Setups} setup(s), {result.Candidates} passing every gating check");
        Console.WriteLine($"{Name}: long {result.LongCandidates} candidate(s), {result.LongKept} kept; short {result.ShortCandidates} candidate(s), {result.ShortKept} kept");
        Console.WriteLine($"{Name}: {result.CappedOut} truncated by the cap");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One night's cap, over the setups the detectors recorded.</summary>
    public CapResult Cap(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, SetupReader.SetupTable);

        // Read by date and by nothing else. The candidate list is the night's, shared, and a read
        // that could take a version is the first half of a cap that could be applied per version.
        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, asOf);

        // A candidate is a setup that cleared every gating check. A recorded setup that failed one
        // is evidence and is not competing for a slot, so it keeps a null rank rather than a rank
        // among names it was never ranked against.
        NightlyCap.Candidate[] candidates =
        [
            // The give-up distance is nullable from 031 and cannot be null here, because a setup
            // that passed every check passed `exit-tight`, which fails outright on an absent stop
            // distance. The pattern makes that a filter rather than an assumption: a candidate that
            // somehow arrived without one is dropped from the ranking rather than ranked at nought,
            // which is the position a cap ordered on give-up would put it in.
            // see: A gate handed an absent or degenerate quantity fails rather than passing
            .. setups
                .Where(s => s.PassedAll)
                .Where(s => s.StopDistanceRanges is not null)
                .Select(s => new NightlyCap.Candidate(
                    s.SetupId, s.Ticker, s.Direction, s.StopDistanceRanges!.Value)),
        ];

        IReadOnlyList<NightlyCap.Placement> placements = NightlyCap.Apply(candidates);

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (NightlyCap.Placement placement in placements)
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE setup SET rank = @rank, capped_out = @capped_out
                     WHERE setup_id = @setup_id
                    """;
                command.Parameters.AddWithValue("@rank", placement.Rank);
                command.Parameters.AddWithValue("@capped_out", placement.CappedOut ? 1 : 0);
                command.Parameters.AddWithValue("@setup_id", placement.SetupId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        int longCandidates = candidates.Count(c => c.Direction == LongSetupDetector.Direction);
        int shortCandidates = candidates.Length - longCandidates;
        (int keptLong, int keptShort) = NightlyCap.Take(longCandidates, shortCandidates);

        return new CapResult(
            asOf,
            setups.Count,
            candidates.Length,
            longCandidates,
            shortCandidates,
            keptLong,
            keptShort,
            placements.Count(p => p.CappedOut),
            summary.RowsWritten,
            RunOutcome.Clean);
    }
}

/// <summary>
/// What one night's cap did, with the pre-cap counts beside the kept ones.
///
/// The pre-cap counts are the point. "Forty long kept" says nothing about whether the cap bound;
/// "ninety candidates, forty kept" says it bound hard, and that is what decides whether sixty is the
/// right number.
/// </summary>
public sealed record CapResult(
    DateOnly AsOf,
    int Setups,
    int Candidates,
    int LongCandidates,
    int ShortCandidates,
    int LongKept,
    int ShortKept,
    int CappedOut,
    int RowsWritten,
    RunOutcome Outcome);
