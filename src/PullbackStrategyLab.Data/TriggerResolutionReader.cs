using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What a session did to the plans resting in it, and the run rows the resolver left behind.
///
/// <b>The stamp is bounded because a resolution is an observation about a session.</b> A replay
/// standing at an old date that saw a resolution written after it would answer with a fill the night
/// itself could not have known about. The key is the plan and nothing rewrites a resolution, so the
/// bound will rarely exclude anything today; that is a fact about the writer rather than a property
/// of the read.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class TriggerResolutionReader
{
    private readonly StoreConnectionFactory _connections;

    public TriggerResolutionReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The resolutions of <paramref name="liveSession"/>, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredTriggerResolution> ForLiveSession(DateOnly liveSession, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ForLiveSession(connection, liveSession, asOf);
    }

    /// <summary>
    /// The same read from a connection the caller already holds.
    ///
    /// Ordered by the minute a trigger was touched, earliest first, with the unfired plans after
    /// them. That is the order the contention rule fills in, so the one component that has to see
    /// this ordering reads it rather than sorting a list it was handed.
    /// see: Plans are resting orders and fills go in time order when the caps bind
    /// </summary>
    public static IReadOnlyList<StoredTriggerResolution> ForLiveSession(
        SqliteConnection connection, DateOnly liveSession, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT setup_id, live_session, ticker, direction, outcome,
                   touched_at, minutes_walked, unresolved_because, observed_at
              FROM trigger_resolution
             WHERE live_session = @live_session
               AND observed_at <= @observed_before
             ORDER BY touched_at IS NULL, touched_at, ticker
            """;

        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        var resolutions = new List<StoredTriggerResolution>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            resolutions.Add(new StoredTriggerResolution(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : StoreText.StorageTextToTimestamp(reader.GetString(5)),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                StoreText.StorageTextToTimestamp(reader.GetString(8))));
        }

        return resolutions;
    }

    /// <summary>
    /// The stage's own run rows for one session, most recent first.
    ///
    /// Unbounded, and `trigger_run` is exempted by name for it on the terms `plan_run`, `vwap_run`
    /// and `intraday_fetch` already carry: the row says when the resolver ran and what it walked,
    /// which is operational. Nothing computes a figure about the market from it, and the resolutions
    /// it counts are in `trigger_resolution`, which is stamped and bounded.
    /// </summary>
    public static IReadOnlyList<StoredTriggerRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, setup_as_of, plans, touched, not_touched, unresolvable,
                   names_walked, minutes_walked, outcome, stopped_because, observed_at
              FROM trigger_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredTriggerRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredTriggerRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.IsDBNull(1) ? null : StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                StoreText.StorageTextToTimestamp(reader.GetString(10))));
        }

        return runs;
    }
}

/// <summary>What one session did to one resting plan.</summary>
public sealed record StoredTriggerResolution(
    string SetupId,
    DateOnly LiveSession,
    string Ticker,
    string Direction,
    string Outcome,
    DateTimeOffset? TouchedAt,
    int MinutesWalked,
    string? UnresolvedBecause,
    DateTimeOffset ObservedAt);

/// <summary>One run of the resolver, with what it walked beside what it decided.</summary>
public sealed record StoredTriggerRun(
    DateOnly SessionDate,
    DateOnly? SetupAsOf,
    int Plans,
    int Touched,
    int NotTouched,
    int Unresolvable,
    int NamesWalked,
    int MinutesWalked,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
