using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the status band across the top of every screen reads.
///
/// It reports what the store holds and says nothing about what it does not. The band the
/// architecture describes also carries market mood, open positions and total risk at stake, and
/// none of those exist before phases 2 and 4, so each comes back null and the band renders a
/// dash with the checkpoint that fills it. A zero would read as "no positions open" rather than
/// as "positions are not a thing yet", and those are different statements.
/// </summary>
public static class LabStatus
{
    public static StatusResponse Read(StoreConnectionFactory connections, IClock clock, int dailyCallCeiling)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);

        if (!connections.StoreExists)
        {
            return StatusResponse.NoStore(dailyCallCeiling);
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        // The call budget counts against the UTC date, which is what the ceiling is enforced on,
        // so the band shows the same day the stages are counting.
        int callsUsed = RunLogger.CallsUsedOn(connection, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));

        return new StatusResponse(
            "ready",
            MigrationRunner.ReadUserVersion(connection),
            LatestSession(connection),
            LatestRun(connection),
            CountRows(connection, "universe_member", "removed_on IS NULL"),
            CountRows(connection, "daily_bar", null),
            callsUsed,
            dailyCallCeiling,
            MarketMood: null,
            PositionsOpen: null,
            ShortPositionsOpen: null,
            RiskAtStake: null);
    }

    /// <summary>
    /// The last session the lab took a universe snapshot for. That row is written every night
    /// without exception, including on a degraded run, which makes it the one column that says
    /// which session the store is current to.
    /// </summary>
    private static string? LatestSession(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(as_of) FROM universe_snapshot;";
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The worst outcome of the most recent night's runs, rather than the last row written.
    ///
    /// <b>A night is about eighteen stages and this read returned one of them.</b> Ordering by
    /// started_at and taking one row means the band shows whichever stage happened to finish last,
    /// so a DailyBarIngestor that stopped on the call ceiling at 20:10 and wrote outcome 'partial'
    /// was replaced on the screen by a SignalVectorizer that finished clean at 22:40, and the band
    /// read "vectorize clean" for the whole of the next day.
    ///
    /// The hard rule says a stage stops rather than overrunning and writes a partial run entry. The
    /// entry was written, was correct, and was invisible, which is the failure shape where the
    /// instrument is right and the surface discards the answer.
    ///
    /// So the read takes the night rather than the row: the most recent session in the log, and
    /// within it the worst outcome any stage reached, failed before partial before clean. The
    /// stage named is the one that reached it, so the band names the stage that went wrong rather
    /// than the stage that went last.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    private static RunSummaryResponse? LatestRun(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stage, started_at, ended_at, outcome, calls_used
              FROM run_log
             WHERE substr(started_at, 1, 10) = (SELECT substr(MAX(started_at), 1, 10) FROM run_log)
             ORDER BY CASE outcome
                          WHEN 'failed'  THEN 0
                          WHEN 'partial' THEN 1
                          ELSE 2
                      END,
                      started_at DESC
             LIMIT 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RunSummaryResponse(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            // A run with no outcome did not finish. Reported as it stands rather than as clean,
            // because a stage that was killed part way is exactly what the band is for.
            reader.IsDBNull(3) ? "unfinished" : reader.GetString(3),
            reader.GetInt32(4));
    }

    private static long CountRows(SqliteConnection connection, string table, string? where)
    {
        using SqliteCommand command = connection.CreateCommand();

        // The table name is a literal from the two call sites above and never reaches here from
        // a request. Stated because a SQL string built by concatenation deserves the sentence.
        command.CommandText = string.Create(
            CultureInfo.InvariantCulture,
            $"SELECT COUNT(*) FROM {table}{(where is null ? string.Empty : " WHERE " + where)};");

        return (long)(command.ExecuteScalar() ?? 0L);
    }
}

/// <summary>
/// The band's contents. Every figure the lab does not yet produce is null rather than zero, and
/// the page renders a dash for it.
/// </summary>
public sealed record StatusResponse(
    string Store,
    int SchemaVersion,
    string? Session,
    RunSummaryResponse? LastRun,
    long UniverseMembers,
    long BarsStored,
    int CallsUsed,
    int DailyCallCeiling,
    string? MarketMood,
    int? PositionsOpen,
    int? ShortPositionsOpen,
    decimal? RiskAtStake)
{
    public static StatusResponse NoStore(int dailyCallCeiling) =>
        new("no-store", 0, null, null, 0, 0, 0, dailyCallCeiling, null, null, null, null);
}

public sealed record RunSummaryResponse(string Stage, string StartedAt, string? EndedAt, string Outcome, int CallsUsed);
