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
/// The plan held against what happened, on every trade.
///
/// <b>Three pairs, because the corpus named three different things and each of them is real.</b>
/// SCHEMA said planned stop beside executed stop, the mockup's column shows an entry difference in
/// basis points, and the catalogue said "planned against executed" and named no field at all. None
/// of the three was wrong and none was the whole, so the row carries all three and says which
/// question each answers.
///
/// <list type="number">
/// <item><b>Execution, at both ends.</b> The price an instruction named against the price it got, in
/// money and in basis points, with the fill's basis beside it. This is the pair the journal page's
/// plan-against-actual column reads and the one an execution defect surfaces in.</item>
/// <item><b>The plan's stop against where the trade ended.</b> Equal to the exit pair on a give-up
/// exit and a different quantity on every other one: a trail exit ends nowhere near the give-up
/// point by design, so reading the two as one would report every winner as a huge execution
/// failure.</item>
/// <item><b>The gate.</b> The size the plan carried against the size that was placed, the cap that
/// bound if one did, and the risk each implies. This is the pair
/// <see cref="RiskGate"/> exists to make readable: it may reduce a size and may not recompute one,
/// so what is compared here is an intention against an outcome rather than two runs of one
/// formula.</item>
/// </list>
/// see: The audit holds three pairs and they answer three different questions
///
/// <b>Every difference is derived from the two prices and never copied from the fill's own
/// charge.</b> An audit reading <c>fill.slippage</c> would be comparing the model's number against
/// itself. The two legitimately differ on a gap, where the model charges nothing and the price moved
/// anyway, so the basis is on the row and a gap is never read as slippage.
///
/// <b>It runs after TradeJournal and writes nothing but its own rows.</b> The audit points at a
/// trade, so the trade has to exist; and because the result was written before this ran, nothing
/// here can change one. A component that could both produce a result and adjust it would be
/// auditing itself.
/// see: TradeJournal runs first and PlanAudit second, and the audit never changes a result
/// </summary>
public sealed class PlanAudit
{
    public const string Name = "audit";

    /// <summary>No trade was closed in this session, so there was nothing to audit.</summary>
    public const string NothingToAudit = "no trade was closed in this session";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public PlanAudit(
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

        AuditRunResult result = Audit(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.TradesRead} trade(s) read, "
            + $"{result.Audited} audited");
        Console.WriteLine(
            $"{Name}: {result.Longs} long and {result.Shorts} short, {result.ReducedByACap} sized down by a cap, "
            + $"{result.GappedAtAnEnd} gapped at one end or the other");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Audit every trade <paramref name="sessionDate"/> closed.
    ///
    /// Idempotent: an audit is keyed on its trade and inserted with do-nothing on conflict.
    /// </summary>
    public AuditRunResult Audit(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "plan_audit", "audit_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        StoredTrade[] trades = [.. TradeReader.ClosedIn(connection, sessionDate, sessionDate, _options.SessionZone)];
        tally.TradesRead = trades.Length;

        if (trades.Length == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingToAudit, observedAt);
        }

        string[] setupIds = [.. trades.Select(t => t.SetupId)];

        Dictionary<string, StoredTradePlan> plans = TradePlanReader
            .ForSetups(connection, setupIds, sessionDate, _options.SessionZone)
            .ToDictionary(p => p.SetupId, StringComparer.Ordinal);

        Dictionary<string, StoredTradeOrder> orders = TradeOrderReader
            .ForSetups(connection, setupIds, sessionDate, _options.SessionZone)
            .ToDictionary(o => o.SetupId, StringComparer.Ordinal);

        ILookup<string, StoredFill> fills = PositionReader
            .FillsFor(connection, [.. trades.Select(t => t.PositionId)], sessionDate, _options.SessionZone)
            .ToLookup(f => f.PositionId, StringComparer.Ordinal);

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (StoredTrade trade in trades)
        {
            StoredFill? entry = fills[trade.PositionId]
                .FirstOrDefault(f => string.Equals(f.Leg, "entry", StringComparison.Ordinal));
            StoredFill? exit = fills[trade.PositionId]
                .FirstOrDefault(f => string.Equals(f.Leg, "exit", StringComparison.Ordinal));

            // A trade with no fill on one end cannot be audited and is not invented. It cannot
            // happen through this store as it stands, because a position closes by writing an exit
            // fill; it is refused rather than filled with noughts so the count says so if it ever
            // does (see: A gate handed an absent or degenerate quantity fails rather than passing).
            if (entry is null || exit is null
                || !plans.TryGetValue(trade.SetupId, out StoredTradePlan? plan)
                || !orders.TryGetValue(trade.SetupId, out StoredTradeOrder? order))
            {
                continue;
            }

            Write(transaction, trade, plan, order, entry, exit, observedAt, tally);
        }

        transaction.Commit();

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    private static void Write(
        SqliteTransaction transaction,
        StoredTrade trade,
        StoredTradePlan plan,
        StoredTradeOrder order,
        StoredFill entry,
        StoredFill exit,
        DateTimeOffset observedAt,
        Tally tally)
    {
        decimal entryDifference = PlanDifference.PerShare(
            trade.Direction, isExit: false, entry.RestingPrice, entry.Price);
        decimal exitDifference = PlanDifference.PerShare(
            trade.Direction, isExit: true, exit.RestingPrice, exit.Price);

        // The second question, and it is not the first one restated. On a give-up exit these two are
        // the same number; on a trail exit the plan's stop is nowhere near where the trade ended,
        // and reading the two as one would report every winner as a huge execution failure.
        decimal giveUpDifference = PlanDifference.PerShare(
            trade.Direction, isExit: true, plan.GiveUpPrice, exit.Price);

        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plan_audit (
                trade_id, setup_id, ticker, direction,
                planned_trigger, executed_entry, entry_difference, entry_difference_bps, entry_basis,
                exit_resting_price, executed_exit, exit_difference, exit_difference_bps, exit_basis,
                exit_reason, planned_give_up, give_up_difference, give_up_difference_bps,
                planned_shares, executed_shares, shares_difference, reduced_because,
                risk_intended, risk_realised, risk_difference, observed_at)
            VALUES (
                @trade_id, @setup_id, @ticker, @direction,
                @planned_trigger, @executed_entry, @entry_difference, @entry_difference_bps, @entry_basis,
                @exit_resting_price, @executed_exit, @exit_difference, @exit_difference_bps, @exit_basis,
                @exit_reason, @planned_give_up, @give_up_difference, @give_up_difference_bps,
                @planned_shares, @executed_shares, @shares_difference, @reduced_because,
                @risk_intended, @risk_realised, @risk_difference, @observed_at)
            ON CONFLICT (trade_id) DO NOTHING;
            """;

        decimal riskIntended = order.Shares * plan.GiveUpDistance;
        decimal riskRealised = trade.RiskRealised;

        command.Parameters.AddWithValue("@trade_id", trade.TradeId);
        command.Parameters.AddWithValue("@setup_id", trade.SetupId);
        command.Parameters.AddWithValue("@ticker", trade.Ticker);
        command.Parameters.AddWithValue("@direction", trade.Direction);
        command.Parameters.AddWithValue("@planned_trigger", StoreText.PriceToStorageText(entry.RestingPrice));
        command.Parameters.AddWithValue("@executed_entry", StoreText.PriceToStorageText(entry.Price));
        command.Parameters.AddWithValue("@entry_difference", StoreText.PriceToStorageText(entryDifference));
        command.Parameters.AddWithValue(
            "@entry_difference_bps", PlanDifference.BasisPoints(entryDifference, entry.RestingPrice));
        command.Parameters.AddWithValue("@entry_basis", entry.Basis);
        command.Parameters.AddWithValue("@exit_resting_price", StoreText.PriceToStorageText(exit.RestingPrice));
        command.Parameters.AddWithValue("@executed_exit", StoreText.PriceToStorageText(exit.Price));
        command.Parameters.AddWithValue("@exit_difference", StoreText.PriceToStorageText(exitDifference));
        command.Parameters.AddWithValue(
            "@exit_difference_bps", PlanDifference.BasisPoints(exitDifference, exit.RestingPrice));
        command.Parameters.AddWithValue("@exit_basis", exit.Basis);
        command.Parameters.AddWithValue("@exit_reason", trade.ExitReason);
        command.Parameters.AddWithValue("@planned_give_up", StoreText.PriceToStorageText(plan.GiveUpPrice));
        command.Parameters.AddWithValue("@give_up_difference", StoreText.PriceToStorageText(giveUpDifference));
        command.Parameters.AddWithValue(
            "@give_up_difference_bps", PlanDifference.BasisPoints(giveUpDifference, plan.GiveUpPrice));
        command.Parameters.AddWithValue("@planned_shares", plan.Shares);
        command.Parameters.AddWithValue("@executed_shares", order.Shares);
        command.Parameters.AddWithValue("@shares_difference", plan.Shares - order.Shares);
        command.Parameters.AddWithValue("@reduced_because", (object?)order.BoundBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@risk_intended", StoreText.PriceToStorageText(riskIntended));
        command.Parameters.AddWithValue("@risk_realised", StoreText.PriceToStorageText(riskRealised));
        command.Parameters.AddWithValue(
            "@risk_difference", StoreText.PriceToStorageText(riskRealised - riskIntended));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        if (command.ExecuteNonQuery() == 0)
        {
            return;
        }

        tally.Audited++;
        tally.Count(trade.Direction);

        if (order.BoundBy is not null)
        {
            tally.ReducedByACap++;
        }

        if (string.Equals(entry.Basis, FillModel.Gapped, StringComparison.Ordinal)
            || string.Equals(exit.Basis, FillModel.Gapped, StringComparison.Ordinal))
        {
            tally.GappedAtAnEnd++;
        }
    }

    private static AuditRunResult Complete(
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

        return new AuditRunResult(sessionDate, tally, summary.RowsWritten, outcome, because);
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
            INSERT INTO audit_run (
                session_date, trades_read, audited, longs, shorts, reduced_by_a_cap,
                gapped_at_an_end, outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @trades_read, @audited, @longs, @shorts, @reduced_by_a_cap,
                @gapped_at_an_end, @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@trades_read", tally.TradesRead);
        command.Parameters.AddWithValue("@audited", tally.Audited);
        command.Parameters.AddWithValue("@longs", tally.Longs);
        command.Parameters.AddWithValue("@shorts", tally.Shorts);
        command.Parameters.AddWithValue("@reduced_by_a_cap", tally.ReducedByACap);
        command.Parameters.AddWithValue("@gapped_at_an_end", tally.GappedAtAnEnd);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>A night's audits, counted by side and by the two things worth reading off a total.</summary>
    public sealed class Tally
    {
        public int TradesRead { get; set; }

        public int Audited { get; set; }

        public int Longs { get; private set; }

        public int Shorts { get; private set; }

        public int ReducedByACap { get; set; }

        public int GappedAtAnEnd { get; set; }

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

/// <summary>What one run of PlanAudit wrote.</summary>
public sealed record AuditRunResult(
    DateOnly SessionDate,
    PlanAudit.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int TradesRead => Counts.TradesRead;

    public int Audited => Counts.Audited;

    public int Longs => Counts.Longs;

    public int Shorts => Counts.Shorts;

    public int ReducedByACap => Counts.ReducedByACap;

    public int GappedAtAnEnd => Counts.GappedAtAnEnd;
}
