using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The positions the lab holds, the fills that opened and closed them, and PaperBroker's own run
/// rows.
///
/// <b>Three stamps and all three are bounded, because this is the one updated table in the phase.</b>
/// A position is inserted when it is filled, updated when a short is trimmed, and updated again
/// when it closes, so a single stamp would answer a replay standing between any two of those with
/// the state the row ended in. Every read here bounds <c>observed_at</c> for whether the row exists
/// at all, <c>trim_observed_at</c> for whether it had been trimmed yet and <c>closed_observed_at</c>
/// for whether it had closed yet, so a position closed after the as-of reads as open, which is what
/// it was. <c>trail_armed_session</c> needs no stamp because it is a session rather than a fact.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>An unfilled position is read on the same footing as a filled one.</b> A placed order the
/// session quoted no usable book for is a row rather than an absence, on the terms a blocked order
/// already sits on: a morning on which two orders could not be priced is evidence about the capture
/// and reads as a quiet morning unless the refusals are stored.
/// see: A fill with no usable quote for its name is refused and recorded, never charged nought
/// </summary>
public sealed class PositionReader
{
    private const string Columns = """
        position_id, setup_id, order_id, ticker, direction, status, opened_session, opened_at,
        shares, entry_fill_id, entry_price, value_at_entry, fraction_at_entry, risk_intended,
        risk_realised, unfilled_because, borrow_rate_assumed, borrow_availability, closed_session,
        closed_at, exit_fill_id, exit_price, exit_reason, realised_pnl, realised_r, observed_at,
        closed_observed_at, trim_fill_id, trimmed_at, trimmed_shares, trim_price, trim_realised_pnl,
        trim_observed_at, exit_armed_session, exit_armed_reason
        """;

    private readonly StoreConnectionFactory _connections;

    public PositionReader(StoreConnectionFactory connections) => _connections = connections;

    /// <summary>The positions opened in <paramref name="openedSession"/>, as at <paramref name="asOf"/>.</summary>
    public IReadOnlyList<StoredPosition> ForOpenedSession(DateOnly openedSession, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return ForOpenedSession(connection, openedSession, asOf);
    }

    /// <summary>The same read from a connection the caller already holds, ticker ordering the rows.</summary>
    public static IReadOnlyList<StoredPosition> ForOpenedSession(
        SqliteConnection connection, DateOnly openedSession, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM position
             WHERE opened_session = @opened_session
               AND observed_at <= @observed_before
             ORDER BY opened_at, ticker
            """;

        command.Parameters.AddWithValue("@opened_session", StoreText.DateToStorageText(openedSession));
        Bound(command, asOf);

        return Read(command, asOf);
    }

    /// <summary>
    /// The positions still open coming into <paramref name="session"/>, as at
    /// <paramref name="asOf"/>.
    ///
    /// <b>Bounded on the session that opened them rather than on a stamp, and that is deliberate.</b>
    /// RiskGate runs at 21:10 and PaperBroker at 21:15, so the positions of the session being gated
    /// do not exist yet when the gate reads this; a stamp bound would give the right answer only
    /// while the two stages ran in that order on that evening, and a rerun would quietly change it.
    /// Asking for the positions opened in an earlier session is the same answer and does not depend
    /// on when anything ran.
    /// </summary>
    public static IReadOnlyList<StoredPosition> OpenComingInto(
        SqliteConnection connection, DateOnly session, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM position
             WHERE opened_session < @session
               AND status <> 'unfilled'
               AND observed_at <= @observed_before
               AND (closed_observed_at IS NULL OR closed_observed_at > @observed_before
                    OR closed_session >= @session)
             ORDER BY opened_at, ticker
            """;

        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        Bound(command, asOf);

        return Read(command, asOf);
    }

    /// <summary>
    /// The positions that were open at some point during <paramref name="session"/>, which is what
    /// PositionManager manages.
    ///
    /// <b>A third question, and it is neither of the two above.</b> <see cref="OpenComingInto"/> is
    /// the caps' read and deliberately excludes the session's own entries, because RiskGate decides
    /// before they exist. <see cref="OpenAt"/> is the status band's and asks what is held now. This
    /// one is the manager's: it runs after PaperBroker on the same evening, so a position opened at
    /// 09:31 of this session is one it has to walk, and a position closed in an earlier session is
    /// one it must not.
    ///
    /// A rerun reads nothing to do, because a close this evening already wrote is visible at this
    /// as-of and the caller drops any row that comes back closed.
    /// </summary>
    public static IReadOnlyList<StoredPosition> OpenDuring(
        SqliteConnection connection, DateOnly session, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM position
             WHERE opened_session <= @session
               AND status <> 'unfilled'
               AND observed_at <= @observed_before
               AND (closed_observed_at IS NULL OR closed_observed_at > @observed_before
                    OR closed_session >= @session)
             ORDER BY opened_at, ticker
            """;

        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        Bound(command, asOf);

        return Read(command, asOf);
    }

    /// <summary>
    /// The positions the lab is holding as at <paramref name="asOf"/>, whatever session opened them.
    ///
    /// <b>The status band's read, and it is a different question from the caps'.</b>
    /// <see cref="OpenComingInto"/> asks what was open at the start of a named session, which is
    /// what RiskGate needs and which does not depend on when a stage ran. This asks what is open
    /// now, as far as the lab could know by the as-of, so a close observed after it reads as still
    /// held.
    /// </summary>
    public static IReadOnlyList<StoredPosition> OpenAt(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
              FROM position
             WHERE status <> 'unfilled'
               AND observed_at <= @observed_before
               AND (closed_observed_at IS NULL OR closed_observed_at > @observed_before)
             ORDER BY opened_at, ticker
            """;

        Bound(command, asOf);

        return Read(command, asOf);
    }

    /// <summary>Every fill of one session, in the order they happened.</summary>
    public static IReadOnlyList<StoredFill> FillsOf(
        SqliteConnection connection, DateOnly sessionDate, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT fill_id, position_id, setup_id, session_date, ticker, direction, leg, filled_at,
                   basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
                   quote_lag_seconds, straddle_seconds, observed_at
              FROM fill
             WHERE session_date = @session_date
               AND observed_at <= @observed_before
             ORDER BY filled_at, ticker, leg
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        Bound(command, asOf);

        var fills = new List<StoredFill>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            fills.Add(new StoredFill(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToDate(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                StoreText.StorageTextToTimestamp(reader.GetString(7)),
                reader.GetString(8),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                StoreText.StorageTextToPrice(reader.GetString(10)),
                StoreText.StorageTextToPrice(reader.GetString(11)),
                reader.GetInt32(12),
                reader.IsDBNull(13) ? null : reader.GetDouble(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                StoreText.StorageTextToTimestamp(reader.GetString(17))));
        }

        return fills;
    }

    /// <summary>
    /// The stage's own run rows for one session, most recent first.
    ///
    /// Unbounded, and <c>fill_run</c> is exempted by name on the terms <c>order_run</c>,
    /// <c>trigger_run</c>, <c>plan_run</c>, <c>vwap_run</c> and <c>intraday_fetch</c> already carry:
    /// it says when PaperBroker ran and what it could not price, which is operational. The positions
    /// it counts are in <c>position</c>, which is stamped twice and bounded on both.
    /// </summary>
    public static IReadOnlyList<StoredFillRun> RunsFor(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, open_at_start, orders_placed, entries_filled, entries_unfilled,
                   gapped, slipped, names_walked, minutes_walked,
                   outcome, stopped_because, observed_at
              FROM fill_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredFillRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredFillRun(
                StoreText.StorageTextToDate(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                StoreText.StorageTextToTimestamp(reader.GetString(11))));
        }

        return runs;
    }

    /// <summary>
    /// PositionManager's own run rows for one session, most recent first.
    ///
    /// Unbounded, and <c>manage_run</c> is exempted by name on the terms <c>fill_run</c> already
    /// carries: it says when the manager ran and what each rule closed, which is operational. The
    /// positions it counts are in <c>position</c>, which is stamped three times and bounded on all
    /// three.
    /// </summary>
    public static IReadOnlyList<StoredManageRun> ManageRunsFor(
        SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_date, open_at_start, longs_managed, shorts_managed, closed_give_up,
                   closed_trail, closed_reclaim, trimmed, exits_armed, gapped, slipped,
                   held_no_quote, closed_in_their_own_session, open_at_end, names_walked,
                   minutes_walked, outcome, stopped_because, observed_at
              FROM manage_run
             WHERE session_date = @session_date
             ORDER BY observed_at DESC
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));

        var runs = new List<StoredManageRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new StoredManageRun(
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
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                StoreText.StorageTextToTimestamp(reader.GetString(18))));
        }

        return runs;
    }

    private static void Bound(SqliteCommand command, DateOnly asOf) =>
        command.Parameters.AddWithValue(
            "@observed_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

    /// <summary>
    /// Materialise the rows, projecting a close the as-of could not have seen back to open.
    ///
    /// <b>The projection is the second half of the bound and it is done here rather than in SQL.</b>
    /// A row whose close was observed after the as-of has to read as open, and it cannot simply be
    /// filtered out: the position existed and was held, which is the fact the caps count. So the
    /// exit columns are dropped and the status is put back, in one place, so no query has to
    /// remember to do it.
    /// </summary>
    private static IReadOnlyList<StoredPosition> Read(SqliteCommand command, DateOnly asOf)
    {
        DateTimeOffset bound = StoreText.StorageTextToTimestamp(
            StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        var positions = new List<StoredPosition>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateTimeOffset? closedObservedAt = reader.IsDBNull(26)
                ? null
                : StoreText.StorageTextToTimestamp(reader.GetString(26));

            bool closeIsVisible = closedObservedAt is not null && closedObservedAt <= bound;

            DateTimeOffset? trimObservedAt = reader.IsDBNull(32)
                ? null
                : StoreText.StorageTextToTimestamp(reader.GetString(32));

            bool trimIsVisible = trimObservedAt is not null && trimObservedAt <= bound;

            // An arming needs no stamp of its own, because the column is the session that armed
            // the exit rather than the fact that something did. A session later than the as-of is
            // a reading the lab had not made yet and reads as unarmed, which is what it was, and
            // the reason goes with it.
            DateOnly? armedSession = reader.IsDBNull(33)
                ? null
                : StoreText.StorageTextToDate(reader.GetString(33));

            bool armIsVisible = armedSession is not null && armedSession <= asOf;

            // Only a close is projected away. An unfilled row has no close and is not an open
            // position, so rewriting every status the as-of cannot see the close of would turn the
            // one refusal this stage records into a position the lab is holding.
            string stored = reader.GetString(5);
            string status = string.Equals(stored, PositionStatus.Closed, StringComparison.Ordinal)
                && !closeIsVisible
                    ? PositionStatus.Open
                    : stored;

            positions.Add(new StoredPosition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                status,
                StoreText.StorageTextToDate(reader.GetString(6)),
                reader.IsDBNull(7) ? null : StoreText.StorageTextToTimestamp(reader.GetString(7)),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : StoreText.StorageTextToPrice(reader.GetString(10)),
                reader.IsDBNull(11) ? null : StoreText.StorageTextToPrice(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetDouble(12),
                reader.IsDBNull(13) ? null : StoreText.StorageTextToPrice(reader.GetString(13)),
                reader.IsDBNull(14) ? null : StoreText.StorageTextToPrice(reader.GetString(14)),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : StoreText.StorageTextToPrice(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                closeIsVisible && !reader.IsDBNull(18) ? StoreText.StorageTextToDate(reader.GetString(18)) : null,
                closeIsVisible && !reader.IsDBNull(19) ? StoreText.StorageTextToTimestamp(reader.GetString(19)) : null,
                closeIsVisible && !reader.IsDBNull(20) ? reader.GetString(20) : null,
                closeIsVisible && !reader.IsDBNull(21) ? StoreText.StorageTextToPrice(reader.GetString(21)) : null,
                closeIsVisible && !reader.IsDBNull(22) ? reader.GetString(22) : null,
                closeIsVisible && !reader.IsDBNull(23) ? StoreText.StorageTextToPrice(reader.GetString(23)) : null,
                closeIsVisible && !reader.IsDBNull(24) ? reader.GetDouble(24) : null,
                StoreText.StorageTextToTimestamp(reader.GetString(25)),
                closeIsVisible ? closedObservedAt : null,
                trimIsVisible ? reader.GetString(27) : null,
                trimIsVisible ? StoreText.StorageTextToTimestamp(reader.GetString(28)) : null,
                trimIsVisible ? reader.GetInt32(29) : null,
                trimIsVisible ? StoreText.StorageTextToPrice(reader.GetString(30)) : null,
                trimIsVisible ? StoreText.StorageTextToPrice(reader.GetString(31)) : null,
                trimIsVisible ? trimObservedAt : null,
                armIsVisible ? armedSession : null,
                armIsVisible && !reader.IsDBNull(34) ? reader.GetString(34) : null));
        }

        return positions;
    }
}

/// <summary>The three states a position row can be in, named once so nothing compares a literal.</summary>
public static class PositionStatus
{
    /// <summary>A placed order the session quoted no usable book for, so no fill was priced.</summary>
    public const string Unfilled = "unfilled";

    /// <summary>Filled and held.</summary>
    public const string Open = "open";

    /// <summary>Filled and exited, with the fill that closed it.</summary>
    public const string Closed = "closed";
}

/// <summary>One position as the store holds it, with the close hidden where the as-of predates it.</summary>
public sealed record StoredPosition(
    string PositionId,
    string SetupId,
    string OrderId,
    string Ticker,
    string Direction,
    string Status,
    DateOnly OpenedSession,
    DateTimeOffset? OpenedAt,
    int Shares,
    string? EntryFillId,
    decimal? EntryPrice,
    decimal? ValueAtEntry,
    double? FractionAtEntry,
    decimal? RiskIntended,
    decimal? RiskRealised,
    string? UnfilledBecause,
    decimal? BorrowRateAssumed,
    string? BorrowAvailability,
    DateOnly? ClosedSession,
    DateTimeOffset? ClosedAt,
    string? ExitFillId,
    decimal? ExitPrice,
    string? ExitReason,
    decimal? RealisedPnl,
    double? RealisedR,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ClosedObservedAt,
    string? TrimFillId,
    DateTimeOffset? TrimmedAt,
    int? TrimmedShares,
    decimal? TrimPrice,
    decimal? TrimRealisedPnl,
    DateTimeOffset? TrimObservedAt,
    DateOnly? ExitArmedSession,
    string? ExitArmedReason)
{
    /// <summary>What is still held, which is what an exit closes and what a further trim could not.</summary>
    public int SharesRemaining => Shares - (TrimmedShares ?? 0);
}

/// <summary>One end of one trade as the store holds it.</summary>
public sealed record StoredFill(
    string FillId,
    string PositionId,
    string SetupId,
    DateOnly SessionDate,
    string Ticker,
    string Direction,
    string Leg,
    DateTimeOffset FilledAt,
    string Basis,
    decimal RestingPrice,
    decimal Price,
    decimal Slippage,
    int Shares,
    double? SpreadBasisPoints,
    string? SpreadPass,
    int? QuoteLagSeconds,
    int? StraddleSeconds,
    DateTimeOffset ObservedAt);

/// <summary>
/// One run of PaperBroker, which from 4.8 prices entries and nothing else.
///
/// <c>exits_filled</c> and <c>open_at_end</c> were dropped by migration 045 rather than kept
/// reading nought: exits moved to PositionManager, and a stage's record that can only report zero
/// is one a later session reads as broken. The night's book at its end is
/// <see cref="StoredManageRun"/>'s.
/// </summary>
public sealed record StoredFillRun(
    DateOnly SessionDate,
    int OpenAtStart,
    int OrdersPlaced,
    int EntriesFilled,
    int EntriesUnfilled,
    int Gapped,
    int Slipped,
    int NamesWalked,
    int MinutesWalked,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);

/// <summary>One run of PositionManager, with every exit counted by the rule that produced it.</summary>
public sealed record StoredManageRun(
    DateOnly SessionDate,
    int OpenAtStart,
    int LongsManaged,
    int ShortsManaged,
    int ClosedGiveUp,
    int ClosedTrail,
    int ClosedReclaim,
    int Trimmed,
    int ExitsArmed,
    int Gapped,
    int Slipped,
    int HeldNoQuote,
    int ClosedInTheirOwnSession,
    int OpenAtEnd,
    int NamesWalked,
    int MinutesWalked,
    string Outcome,
    string? StoppedBecause,
    DateTimeOffset ObservedAt);
