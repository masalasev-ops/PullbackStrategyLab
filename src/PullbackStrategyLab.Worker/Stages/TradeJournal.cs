using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// What a closed position came to, stated in R after the cost of holding it.
///
/// <b>A trade is not a copy of a position and the borrow line is the difference.</b> A position is
/// what the lab held; a trade is what holding it came to. Everything carried here that also lives on
/// the position is carried because the trade is the row a person reads, and a join to answer "how
/// much did it make" is a join nobody makes. What is new is the borrow charge on the short side and
/// the result after it.
///
/// <b>`result_r` is after borrow and `position.realised_r` is before it, and both names stay.</b>
/// They are equal on every long and differ by the borrow line on every short. One name over two
/// numbers is the fault this corpus keeps finding, so the second one is named differently and the
/// difference is stated where both are declared.
/// see: Long and short are never pooled into one figure
///
/// <b>It runs before PlanAudit and the ordering is a foreign key rather than a note.</b> The audit
/// points at the trade, so the trade has to exist first. That also keeps the audit an observation:
/// nothing it computes can change a result, because the result was written by the time it ran.
/// see: TradeJournal runs first and PlanAudit second, and the audit never changes a result
///
/// <b>A trimmed short's money is both halves and its exit covered what was left.</b>
/// <c>position.shares</c> is the count the entry opened with and stays that; the close covered
/// <c>shares</c> minus <c>trimmed_shares</c>, and <c>realised_pnl</c> is the trim's money plus the
/// close's. Reading <c>shares</c> as what the exit covered would overstate a trimmed short by the
/// trim, which is the obligation 4.8 raised against this checkpoint.
/// </summary>
public sealed class TradeJournal
{
    public const string Name = "trades";

    /// <summary>No position closed in this session, so there was no trade to write.</summary>
    public const string NothingClosed = "no position closed in this session";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public TradeJournal(
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

        DateOnly sessionDate = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        TradeRunResult result = Close(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.ClosedInSession} position(s) closed, "
            + $"{result.Journalled} trade(s) written");
        Console.WriteLine(
            $"{Name}: {result.Longs} long and {result.Shorts} short, {result.ShortsCharged} charged borrow, "
            + $"{result.Trimmed} trimmed before the close");
        Console.WriteLine(
            $"{Name}: {result.ArmedExits} exit(s) filled at an open a rule armed on an earlier session");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Write a trade for every position <paramref name="sessionDate"/> closed.
    ///
    /// Idempotent: a trade is keyed on its position and inserted with do-nothing on conflict, so a
    /// rerun over a journalled session writes nothing.
    /// </summary>
    public TradeRunResult Close(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "trade", "trade_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        StoredPosition[] closed =
            [.. PositionReader.ClosedIn(connection, sessionDate, sessionDate, _options.SessionZone)
                .Where(p => p.ClosedSession is not null)];

        tally.ClosedInSession = closed.Length;

        if (closed.Length == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingClosed, observedAt);
        }

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (StoredPosition position in closed)
        {
            Journal(transaction, connection, position, sessionDate, observedAt, tally, _options.SessionZone);
        }

        transaction.Commit();

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    private static void Journal(
        SqliteTransaction transaction,
        SqliteConnection connection,
        StoredPosition position,
        DateOnly sessionDate,
        DateTimeOffset observedAt,
        Tally tally, string sessionZone)
    {
        bool isShort = string.Equals(position.Direction, SetupDirection.Short, StringComparison.Ordinal);

        // Calendar days for the borrow, because borrow accrues overnight rather than per session,
        // and stored sessions for the holding period a person reads. The two differ over a weekend
        // and the row carries both rather than leaving a reader to guess which one it is looking at.
        int calendarDays = position.ClosedSession!.Value.DayNumber - position.OpenedSession.DayNumber;
        // At least one, because a position that opened and closed traded on at least one session.
        // A store that holds no daily bar for the name at all would otherwise report nought held,
        // which reads as a trade that never existed rather than as a series the fetch has not
        // reached, and the two are different findings.
        int heldSessions = Math.Max(1, DailyBarReader.SessionsBetween(
            connection, position.Ticker, position.OpenedSession, position.ClosedSession.Value, sessionDate, sessionZone));

        decimal grossPnl = position.RealisedPnl!.Value;
        decimal? borrow = isShort
            ? BorrowCost.Charged(position.ValueAtEntry!.Value, position.BorrowRateAssumed!.Value, calendarDays)
            : null;

        decimal netPnl = grossPnl - (borrow ?? 0m);
        double resultR = position.RiskRealised!.Value == 0m
            ? 0d
            : (double)(netPnl / position.RiskRealised.Value);

        // How long an armed exit waited for an open to fill at. The rule fills at the next open the
        // store has minutes for, so a session it was blind on postpones the fill rather than
        // reconsidering it, and this is the size of that on each trade.
        int? waited = position.ExitArmedSession is DateOnly armed
            ? Math.Max(0, DailyBarReader.SessionsBetween(
                connection, position.Ticker, armed, position.ClosedSession.Value, sessionDate, sessionZone) - 1)
            : null;

        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trade (
                trade_id, position_id, setup_id, ticker, direction, opened_session, closed_session,
                held_calendar_days, held_sessions, entry_price, exit_price, exit_reason, shares,
                trimmed_shares, value_at_entry, risk_realised, gross_pnl, borrow_rate_assumed,
                borrow_cost, net_pnl, result_r, exit_armed_session, armed_sessions_waited, observed_at)
            VALUES (
                @trade_id, @position_id, @setup_id, @ticker, @direction, @opened_session, @closed_session,
                @held_calendar_days, @held_sessions, @entry_price, @exit_price, @exit_reason, @shares,
                @trimmed_shares, @value_at_entry, @risk_realised, @gross_pnl, @borrow_rate_assumed,
                @borrow_cost, @net_pnl, @result_r, @exit_armed_session, @armed_sessions_waited, @observed_at)
            ON CONFLICT (trade_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@trade_id", position.PositionId);
        command.Parameters.AddWithValue("@position_id", position.PositionId);
        command.Parameters.AddWithValue("@setup_id", position.SetupId);
        command.Parameters.AddWithValue("@ticker", position.Ticker);
        command.Parameters.AddWithValue("@direction", position.Direction);
        command.Parameters.AddWithValue("@opened_session", StoreText.DateToStorageText(position.OpenedSession));
        command.Parameters.AddWithValue("@closed_session", StoreText.DateToStorageText(position.ClosedSession.Value));
        command.Parameters.AddWithValue("@held_calendar_days", calendarDays);
        command.Parameters.AddWithValue("@held_sessions", heldSessions);
        command.Parameters.AddWithValue("@entry_price", StoreText.PriceToStorageText(position.EntryPrice!.Value));
        command.Parameters.AddWithValue("@exit_price", StoreText.PriceToStorageText(position.ExitPrice!.Value));
        command.Parameters.AddWithValue("@exit_reason", position.ExitReason!);
        command.Parameters.AddWithValue("@shares", position.Shares);
        command.Parameters.AddWithValue("@trimmed_shares", position.TrimmedShares ?? 0);
        command.Parameters.AddWithValue("@value_at_entry", StoreText.PriceToStorageText(position.ValueAtEntry!.Value));
        command.Parameters.AddWithValue("@risk_realised", StoreText.PriceToStorageText(position.RiskRealised.Value));
        command.Parameters.AddWithValue("@gross_pnl", StoreText.PriceToStorageText(grossPnl));
        command.Parameters.AddWithValue(
            "@borrow_rate_assumed",
            isShort ? StoreText.PriceToStorageText(position.BorrowRateAssumed!.Value) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@borrow_cost", borrow is null ? DBNull.Value : StoreText.PriceToStorageText(borrow.Value));
        command.Parameters.AddWithValue("@net_pnl", StoreText.PriceToStorageText(netPnl));
        command.Parameters.AddWithValue("@result_r", resultR);
        command.Parameters.AddWithValue(
            "@exit_armed_session",
            position.ExitArmedSession is null
                ? DBNull.Value
                : StoreText.DateToStorageText(position.ExitArmedSession.Value));
        command.Parameters.AddWithValue("@armed_sessions_waited", (object?)waited ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        if (command.ExecuteNonQuery() == 0)
        {
            return;
        }

        tally.Journalled++;
        tally.Count(position.Direction);

        // Charged, not merely eligible. A short closed in the session it opened in was never held
        // overnight and pays nothing, so counting every short here would report a cost that was not
        // charged rather than a cost too small to see.
        if (borrow > 0m)
        {
            tally.ShortsCharged++;
        }

        if ((position.TrimmedShares ?? 0) > 0)
        {
            tally.Trimmed++;
        }

        if (waited is not null)
        {
            tally.ArmedExits++;
        }
    }

    private static TradeRunResult Complete(
        SqliteConnection connection,
        RunScope run,
        DateOnly sessionDate,
        Tally tally,
        RunOutcome outcome,
        string? because,
        DateTimeOffset observedAt)
    {
        RecordRun(connection, sessionDate, tally, outcome, because, observedAt);
        RunSummary summary = run.Complete(outcome);

        return new TradeRunResult(sessionDate, tally, summary.RowsWritten, outcome, because);
    }

    private static void RecordRun(
        SqliteConnection connection,
        DateOnly sessionDate,
        Tally tally,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trade_run (
                session_date, closed_in_session, journalled, longs, shorts, shorts_charged,
                trimmed, armed_exits, outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @closed_in_session, @journalled, @longs, @shorts, @shorts_charged,
                @trimmed, @armed_exits, @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@closed_in_session", tally.ClosedInSession);
        command.Parameters.AddWithValue("@journalled", tally.Journalled);
        command.Parameters.AddWithValue("@longs", tally.Longs);
        command.Parameters.AddWithValue("@shorts", tally.Shorts);
        command.Parameters.AddWithValue("@shorts_charged", tally.ShortsCharged);
        command.Parameters.AddWithValue("@trimmed", tally.Trimmed);
        command.Parameters.AddWithValue("@armed_exits", tally.ArmedExits);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>A night's trades counted by side and by what the row had to say about them.</summary>
    public sealed class Tally
    {
        public int ClosedInSession { get; set; }

        public int Journalled { get; set; }

        public int Longs { get; private set; }

        public int Shorts { get; private set; }

        public int ShortsCharged { get; set; }

        public int Trimmed { get; set; }

        public int ArmedExits { get; set; }

        /// <summary>
        /// One trade, counted on its own side.
        ///
        /// Refused rather than defaulted, so a direction the store somehow admitted is a loud
        /// failure rather than a long, which is the reading every other switch in this lab refuses.
        /// </summary>
        public void Count(string direction)
        {
            switch (direction)
            {
                case SetupDirection.Long:
                    Longs++;
                    return;
                case SetupDirection.Short:
                    Shorts++;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(direction),
                        $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}', so the "
                        + "night's row has no column for it and the two sides would stop adding to the total.");
            }
        }
    }
}

/// <summary>What one run of TradeJournal wrote.</summary>
public sealed record TradeRunResult(
    DateOnly SessionDate,
    TradeJournal.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int ClosedInSession => Counts.ClosedInSession;

    public int Journalled => Counts.Journalled;

    public int Longs => Counts.Longs;

    public int Shorts => Counts.Shorts;

    public int ShortsCharged => Counts.ShortsCharged;

    public int Trimmed => Counts.Trimmed;

    public int ArmedExits => Counts.ArmedExits;
}
