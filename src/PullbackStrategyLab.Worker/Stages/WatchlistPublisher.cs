using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Publishes the night's watchlist, and writes nothing.
///
/// <b>It owns no table, and that is a decision rather than an omission.</b> It was the only phase-4
/// component with no store anywhere in SCHEMA, and the two available answers were a `watchlist`
/// table freezing what was shown, or no table and a page that projects the setups. The second holds
/// because the first would be a second copy of the night: `setup` already carries rank and the cap
/// flag, every read of it is bounded on when its rows were observed, and a replay of an evening
/// therefore returns the list that evening showed, corrections and all. A stored copy could disagree
/// with the rows it was copied from, and the disagreement would be undetectable, because nothing
/// downstream reads both.
///
/// It is the same shape SetupJournal already sets: a component whose work is to establish something
/// about rows another component wrote, where writing would make it the second writer of the thing it
/// is about.
///
/// <b>So what it does is check and report.</b> The page renders whenever somebody opens it; this
/// stage runs at 18:40 and says what would be on it, which is the only moment anybody would notice
/// that the cap ran and published nothing, or that the evening produced a list nobody could read.
/// A slot that printed nothing would leave that discoverable only by opening a browser.
/// </summary>
public sealed class WatchlistPublisher
{
    public const string Name = "publish-watchlist";

    /// <summary>Recorded where the night has no capped rows at all, which is not the same as a night with none published.</summary>
    public const string NeverCapped = "no setup of this session carries a cap decision, so the night was never capped";

    /// <summary>The cap ran and kept nobody, on the terms PlanBuilder states it; a night the page can describe.</summary>
    public const string CapKeptNobody =
        "the cap ran for this session and kept nobody: no setup passed every gating check, so the page shows a night with no candidate";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public WatchlistPublisher(
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

    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        WatchlistPublication published = Publish(asOf);

        Console.WriteLine(
            $"{Name}: session {published.AsOf:yyyy-MM-dd}, {published.Long} long and {published.Short} short published "
            + $"of {published.Flagged} flagged");
        Console.WriteLine(
            published.NothingBecause is null
                ? $"{Name}: {published.Outcome.ToStorageText()}, no rows written, the page projects the setups"
                : $"{Name}: {published.Outcome.ToStorageText()}, nothing published because {published.NothingBecause}");

        return Task.FromResult(published.Outcome == RunOutcome.Failed ? 1 : 0);
    }

    /// <summary>
    /// What the watchlist would show for one session.
    ///
    /// <b>The run entry is opened declaring no table</b>, which is what makes "writes nothing" a
    /// property of the record rather than of this comment: the scope measures rows written from the
    /// store, so a row appearing here would be counted and reported by the run rather than by
    /// anything remembering to look.
    /// </summary>
    public WatchlistPublication Publish(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name);

        int flagged = Count(connection, asOf, capped: null);
        int longs = Count(connection, asOf, capped: true, SetupDirection.Long);
        int shorts = Count(connection, asOf, capped: true, SetupDirection.Short);

        // A night nobody capped and a night the cap published none are different facts. The first
        // means a stage did not run and the page shows a session it cannot describe; the second is
        // an ordinary outcome of the gates. Only the first is worth waking anybody for.
        // A night the cap ran and kept nobody is a third fact, from 5.8, and until then it read as
        // the first: the cap writes its decision on candidate rows only, so a night with no
        // candidate carries no decision and this said the night was never capped. The cap's own run
        // row is what tells the two apart.
        string? nothing = flagged == 0
            ? "no setup was flagged for this session"
            : longs + shorts == 0
                ? Count(connection, asOf, capped: false) == 0
                    ? RunLogger.StageRanOn(connection, SetupCapper.Name, asOf, _options.SessionZone) ? CapKeptNobody : NeverCapped
                    : "every flagged setup was capped out"
                : null;

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new WatchlistPublication(
            asOf, flagged, longs, shorts, summary.RowsWritten, RunOutcome.Clean, nothing);
    }

    private static int Count(SqliteConnection connection, DateOnly asOf, bool? capped, string? direction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM setup WHERE as_of = @as_of"
            + (capped is null ? string.Empty : capped.Value ? " AND capped_out = 0" : " AND capped_out = 1")
            + (direction is null ? string.Empty : " AND direction = @direction");
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        if (direction is not null)
        {
            command.Parameters.AddWithValue("@direction", direction);
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>What the watchlist holds for one session, as the stage reports it.</summary>
public sealed record WatchlistPublication(
    DateOnly AsOf,
    int Flagged,
    int Long,
    int Short,
    int RowsWritten,
    RunOutcome Outcome,
    string? NothingBecause);
