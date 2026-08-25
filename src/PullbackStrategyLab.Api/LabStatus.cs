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

    private static RunSummaryResponse? LatestRun(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stage, started_at, ended_at, outcome, calls_used
              FROM run_log
             ORDER BY started_at DESC
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
