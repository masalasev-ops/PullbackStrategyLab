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
    public static StatusResponse Read(
        StoreConnectionFactory connections, IClock clock, int dailyCallCeiling, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        if (!connections.StoreExists)
        {
            return StatusResponse.NoStore(dailyCallCeiling);
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        // The call budget counts against the vendor's quota day, which is what the ceiling is
        // enforced on, so the band shows the same window the stages are counting. It is named as a
        // quota day rather than as a UTC date because the run beside it is bounded on a session, and
        // the two windows do not have the same edges.
        int callsUsed = RunLogger.CallsUsedOn(connection, VendorQuotaDay.Containing(clock.UtcNow));

        return new StatusResponse(
            "ready",
            MigrationRunner.ReadUserVersion(connection),
            MigrationRunner.LatestVersion,
            LatestSession(connection),
            LatestRun(connection, clock, sessionZone),
            CountRows(connection, "universe_member", "removed_on IS NULL"),
            CountRows(connection, "daily_bar", null),
            callsUsed,
            dailyCallCeiling,
            MarketMood: MoodOfLatestSession(connection),
            PositionsOpen: null,
            ShortPositionsOpen: null,
            RiskAtStake: null);
    }

    /// <summary>
    /// The market mood for the session the store is current to, or null where that night was never
    /// labelled.
    ///
    /// <b>It has a source as of 4.1 and had one since 2.5.</b> The band rendered "not until 2.5" for
    /// every night RegimeLabeler had already labelled, which is a deferral outliving its own due
    /// point on a surface: the same fault the report's out-of-scope rule exists to stop, arriving
    /// where no report looks. A field waiting on a landed checkpoint is worse than one waiting on an
    /// unlanded one, because the checkpoint that would have filled it is not coming back.
    ///
    /// Null still means the night was not labelled, which is a real state and different again from
    /// "this is not built yet". The band shows the first as a dash with a reason and would show the
    /// second with a checkpoint, and it can no longer show the second for this field at all.
    /// </summary>
    private static string? MoodOfLatestSession(SqliteConnection connection)
    {
        string? session = LatestSession(connection);

        return session is null
            ? null
            : RegimeReader.Read(connection, StoreText.StorageTextToDate(session))?.Label;
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
    ///
    /// <b>And the night is bounded in the session zone, not on the UTC date.</b> That grouping was
    /// <c>substr(started_at, 1, 10)</c>, which is the stored UTC day, and the lab's night crosses it:
    /// the schedule runs 17:15 to 22:00 Eastern, so the slots land between 21:15Z and 02:00Z the
    /// following morning. On 2026-08-28 detect-long, vectorize, controls and cap all failed at
    /// 22:20Z to 22:28Z and forward-returns and scoreboard ran clean at 01:30Z and 01:50Z the next
    /// day, so the newest UTC date held those two rows alone and the band read "scoreboard clean"
    /// over a night that produced no setups at all. The ordering was right and the population was a
    /// different night. <see cref="RunLogger.IncompleteStagesOf"/> bounds the same table correctly
    /// and says why in the same words; this read is the one that did not use it.
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    private static RunSummaryResponse? LatestRun(
        SqliteConnection connection, IClock clock, string sessionZone)
    {
        DateOnly? session = LatestSessionInTheLog(connection, clock, sessionZone);
        if (session is null)
        {
            return null;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stage, started_at, ended_at, outcome, calls_used
              FROM run_log
             WHERE started_at >= @start_of_session
               AND started_at <= @end_of_session
             ORDER BY CASE outcome
                          WHEN 'failed'  THEN 0
                          WHEN 'partial' THEN 1
                          ELSE 2
                      END,
                      started_at DESC
             LIMIT 1;
            """;

        command.Parameters.AddWithValue(
            "@start_of_session",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(session.Value, TimeOnly.MinValue, sessionZone)));
        command.Parameters.AddWithValue(
            "@end_of_session", StoreText.EndOfSession(session.Value, sessionZone));

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

    /// <summary>
    /// The session the newest run in the log belongs to, resolved through the clock rather than by
    /// truncating the stored instant.
    ///
    /// Truncating is what the grouping above used to do, and a stored instant of
    /// <c>2026-08-29T01:50Z</c> truncates to the 29th while belonging to the session of the 28th.
    /// </summary>
    private static DateOnly? LatestSessionInTheLog(
        SqliteConnection connection, IClock clock, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(started_at) FROM run_log;";

        return command.ExecuteScalar() is string newest
            ? clock.SessionDate(StoreText.StorageTextToTimestamp(newest), sessionZone)
            : null;
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
    int SchemaVersionExpected,
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
        new("no-store", 0, MigrationRunner.LatestVersion, null, null, 0, 0, 0, dailyCallCeiling,
            null, null, null, null);
}

public sealed record RunSummaryResponse(string Stage, string StartedAt, string? EndedAt, string Outcome, int CallsUsed);
