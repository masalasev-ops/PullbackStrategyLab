using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Why each closed loss happened, and the classifier's own run rows.
///
/// <b>Two stamps and both bounded, because this table is updated once.</b> A row is inserted with a
/// mechanism when the trade closes and updated with an aftermath when the horizon does, so a single
/// stamp would answer a replay standing between the two with the state the row ended in. Every read
/// bounds <c>observed_at</c> for whether the row exists at all and <c>aftermath_observed_at</c> for
/// whether the second answer had arrived, and a row whose aftermath the as-of could not have seen
/// reads as awaiting one, which is what it was.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Awaiting an aftermath and being unclassified are different states and the read keeps them
/// apart.</b> Null is a question the lab cannot answer yet; <c>unclassified</c> is one it could
/// answer and could not place. A read that projected the second back to the first, or the first
/// forward to the second, would make the taxonomy's own coverage unreadable.
/// </summary>
public sealed class LossClassReader
{
    private const string Columns = """
        trade_id, setup_id, ticker, direction, closed_session, net_pnl, result_r, mechanism,
        exit_basis, aftermath, forward_return_signed, one_r_in_return, aftermath_because,
        observed_at, aftermath_observed_at, exit_return_signed
        """;

    private readonly StoreConnectionFactory _connections;

    public LossClassReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The losses closed in <paramref name="session"/>, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredLossClass> ClosedIn(DateOnly session, DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ClosedIn(connection, session, asOf, sessionZone);
    }

    /// <summary>The same read from a connection the caller already holds.</summary>
    public static IReadOnlyList<StoredLossClass> ClosedIn(
        SqliteConnection connection, DateOnly session, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM loss_class
             WHERE closed_session = @session
               AND observed_at <= @observed_before
             ORDER BY direction, ticker
            """;

        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        Bound(command, asOf, sessionZone);

        return Read(command, asOf, sessionZone);
    }

    /// <summary>
    /// Every classified loss the lab holds, as at <paramref name="asOf"/>, most recent first.
    ///
    /// The journal page's read at 4.11 and the classifier's own second pass, which asks which rows
    /// are still waiting on a horizon. Unbounded by session on purpose: a row inserted weeks ago is
    /// exactly the one whose aftermath is now knowable.
    /// </summary>
    public static IReadOnlyList<StoredLossClass> All(SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM loss_class
             WHERE observed_at <= @observed_before
             ORDER BY closed_session DESC, direction, ticker
            """;

        Bound(command, asOf, sessionZone);

        return Read(command, asOf, sessionZone);
    }

    /// <summary>
    /// The classifier's own run rows for one session, most recent first.
    ///
    /// Unbounded, and <c>loss_run</c> is exempted by name on the terms every other run row in this
    /// phase carries: it says when the stage ran and what each pass wrote, which is operational. The
    /// classifications it counts are in <c>loss_class</c>, which is stamped twice and bounded on
    /// both.
    /// </summary>
    public static IReadOnlyList<StoredLossRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, losses_closed, mechanisms_written, gap, ordinary, longs, shorts,
                   awaiting_aftermath, aftermaths_written, noise, failed_setup, unclassified,
                   outcome, stopped_because, observed_at
              FROM loss_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredLossRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredLossRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                StoreText.StorageTextToTimestamp(reader.GetString(14))));
        }

        return runs;
    }

    private static void Bound(SqliteCommand command, DateOnly asOf, string sessionZone) =>
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, sessionZone));

    /// <summary>
    /// Materialise the rows, projecting an aftermath the as-of could not have seen back to absent.
    ///
    /// Done here rather than in SQL and in one place, on exactly the terms
    /// <see cref="PositionReader"/> projects a close: the row existed and carried a mechanism, which
    /// is the fact a reader of that date needs, so it cannot simply be filtered out.
    /// </summary>
    private static IReadOnlyList<StoredLossClass> Read(SqliteCommand command, DateOnly asOf, string sessionZone)
    {
        DateTimeOffset bound = StoreText.StorageTextToTimestamp(
            StoreText.EndOfSession(asOf, sessionZone));

        var rows = new List<StoredLossClass>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateTimeOffset? aftermathAt = reader.IsDBNull(14)
                ? null
                : StoreText.StorageTextToTimestamp(reader.GetString(14));

            bool aftermathIsVisible = aftermathAt is not null && aftermathAt <= bound;

            rows.Add(new StoredLossClass(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                StoreText.StorageTextToDate(reader.GetString(4)),
                StoreText.StorageTextToPrice(reader.GetString(5)),
                reader.GetDouble(6),
                reader.GetString(7),
                reader.GetString(8),
                aftermathIsVisible && !reader.IsDBNull(9) ? reader.GetString(9) : null,
                aftermathIsVisible && !reader.IsDBNull(10) ? StoreText.StorageTextToPrice(reader.GetString(10)) : null,
                aftermathIsVisible && !reader.IsDBNull(11) ? StoreText.StorageTextToPrice(reader.GetString(11)) : null,
                aftermathIsVisible && !reader.IsDBNull(12) ? reader.GetString(12) : null,
                StoreText.StorageTextToTimestamp(reader.GetString(13)),
                aftermathIsVisible ? aftermathAt : null,
                // The second figure arrives with the aftermath and is hidden on the same stamp, so
                // a replay standing between the close and the horizon sees neither half of the pair.
                aftermathIsVisible && !reader.IsDBNull(15) ? StoreText.StorageTextToPrice(reader.GetString(15)) : null));
        }

        return rows;
    }
}

/// <summary>
/// One classified loss, with the aftermath hidden where the as-of predates it.
///
/// <see cref="Aftermath"/> null means the horizon has not closed for this row as far as the as-of
/// could know. It is not the same as <c>unclassified</c>, which is the horizon having closed and the
/// figure being absent.
///
/// <b>Two aftermath figures, named apart.</b> <see cref="ForwardReturnSigned"/> is what the day
/// offered, from the trigger to the close of the tenth session after the trigger's;
/// <see cref="ExitReturnSigned"/> is what the trade earned, from the trigger to the exit fill. The
/// gap between them is what the trail rule is judged on, and neither replaces the other.
/// see: The aftermath is measured from the exit as well as from the close, as two figures and never one
/// </summary>
public sealed record StoredLossClass(
    string TradeId,
    string SetupId,
    string Ticker,
    string Direction,
    DateOnly ClosedSession,
    decimal NetPnl,
    double ResultR,
    string Mechanism,
    string ExitBasis,
    string? Aftermath,
    decimal? ForwardReturnSigned,
    decimal? OneRInReturn,
    string? AftermathBecause,
    DateTimeOffset ObservedAt,
    DateTimeOffset? AftermathObservedAt,
    decimal? ExitReturnSigned)
{
    /// <summary>Whether this row is still waiting on a horizon, which is not the same as unplaceable.</summary>
    public bool AwaitsItsHorizon => Aftermath is null;
}

/// <summary>One run of LossClassifier, with its two passes counted apart.</summary>
public sealed record StoredLossRun(
    DateOnly SessionDate,
    int LossesClosed,
    int MechanismsWritten,
    int Gap,
    int Ordinary,
    int Longs,
    int Shorts,
    int AwaitingAftermath,
    int AftermathsWritten,
    int Noise,
    int FailedSetup,
    int Unclassified,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
