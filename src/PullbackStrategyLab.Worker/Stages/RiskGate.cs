using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The one piece of code that may open a position, and the only writer of orders.
///
/// <b>It applies every limit and it does not size.</b> PlanBuilder sized at 18:30 and the plan's
/// share count is authoritative; this component reduces that count to fit a cap, blocks the order
/// outright, or lets it through unchanged. Nothing here divides a risk budget by a give-up distance.
/// see: RiskGate is the sole writer of orders, for both directions and every version
/// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
///
/// <b>Triggers are taken in the order they happened, which is what the contention rule is.</b> Each
/// placed order changes the book the next one faces, so a mediocre setup triggering at 9:31 consumes
/// capacity a better one at 10:15 cannot use. Rank governs which setups are recorded under the
/// nightly cap and how the screen sorts; it governs no fill.
/// see: Plans are resting orders and fills go in time order when the caps bind
///
/// <b>The book comes in from the positions the lab is holding, and from 4.7 it is read rather than
/// assumed empty.</b> Until PaperBroker existed the only thing this component could count was what
/// it had placed inside the session it was walking, so a position held overnight occupied no slot
/// the next morning and the caps were looser than the design rather than tighter. It now opens on
/// the positions still held coming into the session and adds to that as it goes.
///
/// <b>What remains approximate is intraday and it is the other direction of error.</b> This runs at
/// 21:10 and PaperBroker at 21:15, so nothing here can know that a position opened at 09:31 was
/// stopped out at 09:45: a position placed inside the session occupies its slot for the rest of that
/// session. The caps are therefore tighter than the design within a day and exact across days, where
/// before they were looser on both counts. Two stages cannot be merged to fix it without giving
/// orders a second writer, which costs more than the approximation does.
/// see: RiskGate is the sole writer of orders, for both directions and every version
///
/// <b>Two of the six limits are not applied here and both are named.</b> Risk per trade is what the
/// plan was sized from, so it is asserted rather than enforced: a plan risking more than the budget
/// it names is a defect in the plan. The give-up distance cap is `exit-tight` at detection, so a plan
/// that reached a trigger cleared it hours before, and re-applying it would be a second
/// implementation of a gate that could disagree with the first.
/// </summary>
public sealed class RiskGate
{
    public const string Name = "orders";

    /// <summary>No trigger of this session reached a plan, so there was nothing to gate.</summary>
    public const string NoTriggers = "no plan resting in this session was touched";

    /// <summary>A plan whose risk at stake is over the budget it was sized from, which is a defect.</summary>
    public const string PlanOverBudget = "a plan risks more than the budget it names";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public RiskGate(
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

        OrderRunResult result = Apply(sessionDate);

        Console.WriteLine($"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.Triggers} trigger(s)");
        Console.WriteLine(
            $"{Name}: {result.Placed} placed, {result.Reduced} of them reduced, {result.Blocked} blocked");
        Console.WriteLine(
            $"{Name}: blocked {result.BlockedOpenPositions} on open positions, "
            + $"{result.BlockedOpenShorts} on open shorts, "
            + $"{result.BlockedBelowOneShare} for falling under one share");
        Console.WriteLine(
            $"{Name}: reduced {result.ReducedPositionSize} by position size, "
            + $"{result.ReducedTotalRisk} by total risk at stake");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Decide every trigger of <paramref name="sessionDate"/> against the caps, in time order.
    ///
    /// Idempotent: the insert takes the store's own key and does nothing on conflict, so a rerun
    /// writes no row. A session that has closed does not change, and nothing in this lab revises an
    /// order.
    /// </summary>
    public OrderRunResult Apply(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "trade_order", "order_run");

        DateTimeOffset observedAt = run.StartedAt;

        // Earliest trigger first, ticker breaking a tie, which is the reader's own order and is the
        // order the caps have to be applied in.
        IReadOnlyList<StoredTriggerResolution> resolutions =
            TriggerResolutionReader.ForLiveSession(connection, sessionDate, sessionDate, _options.SessionZone);

        StoredTriggerResolution[] triggers =
            [.. resolutions.Where(r => string.Equals(r.Outcome, "touched", StringComparison.Ordinal))];

        if (triggers.Length == 0)
        {
            RecordRun(connection, sessionDate, new Tally(), RunOutcome.Clean, NoTriggers, observedAt);
            RunSummary nothing = run.Complete(RunOutcome.Clean);

            return new OrderRunResult(sessionDate, 0, new Tally(), nothing.RowsWritten, RunOutcome.Clean, NoTriggers);
        }

        Dictionary<string, StoredTradePlan> plans = TradePlanReader
            .ForLiveSession(connection, sessionDate, sessionDate, _options.SessionZone)
            .ToDictionary(p => p.SetupId, StringComparer.Ordinal);

        var tally = new Tally();
        OpenBook book = BookComingInto(connection, sessionDate, _options.SessionZone);
        string? stoppedBecause = null;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (StoredTriggerResolution trigger in triggers)
        {
            // A resolution with no plan cannot happen through the store: `trigger_resolution` is
            // keyed on the plan and carries a foreign key to it. Refused rather than skipped, because
            // a trigger silently dropped is a fill this lab would never know it had missed.
            if (!plans.TryGetValue(trigger.SetupId, out StoredTradePlan? plan))
            {
                throw new InvalidOperationException(
                    $"The trigger for {trigger.SetupId} has no plan resting in {sessionDate:yyyy-MM-dd}. A "
                    + "resolution is written against a plan and cannot outlive one, so this is a store whose "
                    + "rows contradict its own key rather than a session with nothing to gate.");
            }

            // Risk per trade is the plan's own budget rather than a cap this component applies, so it
            // is asserted. A plan over its budget is a defect at 18:30 and gating it would be
            // treating a broken plan as an ordinary large one.
            if (plan.RiskAtStake > plan.RiskBudget)
            {
                throw new InvalidOperationException(
                    $"{PlanOverBudget}: {plan.SetupId} risks {plan.RiskAtStake} against a budget of "
                    + $"{plan.RiskBudget}. The size is the plan's and this component does not recompute it, so "
                    + "an order sized from it would carry the defect forward into a position.");
            }

            RiskVerdict verdict = RiskLimits.Apply(
                plan.Direction, plan.Shares, plan.TriggerPrice, plan.GiveUpDistance, book);

            tally.Count(verdict);

            if (verdict.IsPlaced)
            {
                book = book.With(plan.Direction, verdict.RiskAtStake);
            }

            Insert(connection, transaction, plan, trigger, verdict, observedAt);
        }

        transaction.Commit();

        // Clean whatever the caps did. A blocked order is what the caps are for and a night of them
        // is evidence rather than a failure; calling it partial would report almost every busy
        // morning as degraded and make the signal mean nothing.
        RunOutcome outcome = RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        RecordRun(connection, sessionDate, tally, outcome, stoppedBecause, observedAt);

        return new OrderRunResult(
            sessionDate, triggers.Length, tally, summary.RowsWritten, outcome, stoppedBecause);
    }

    /// <summary>
    /// What the lab is already holding when this session opens.
    ///
    /// Read from <c>position</c> rather than accumulated, which is what 4.7 changed. The money at
    /// stake is each position's realised risk, being its share count against the distance from the
    /// price it actually filled at to the give-up point the plan named, because that is what would
    /// be lost rather than what was intended to be.
    /// </summary>
    private static OpenBook BookComingInto(SqliteConnection connection, DateOnly sessionDate, string sessionZone)
    {
        OpenBook book = OpenBook.Empty;

        foreach (StoredPosition position in
                 PositionReader.OpenComingInto(connection, sessionDate, sessionDate, sessionZone))
        {
            book = book.With(position.Direction, position.RiskRealised ?? 0m);
        }

        return book;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredTradePlan plan,
        StoredTriggerResolution trigger,
        RiskVerdict verdict,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and nothing in this lab updates an order. The key is the plan, so a second
        // order for one plan is unexpressible rather than merely unwritten.
        command.CommandText = """
            INSERT INTO trade_order (
                order_id, plan_id, setup_id, variant_id, live_session, ticker, direction, triggered_at, status,
                planned_shares, shares, risk_at_stake, bound_by, blocked_because, observed_at)
            VALUES (
                @order_id, @plan_id, @setup_id, @variant_id, @live_session, @ticker, @direction, @triggered_at, @status,
                @planned_shares, @shares, @risk_at_stake, @bound_by, @blocked_because, @observed_at)
            ON CONFLICT (order_id) DO NOTHING;
            """;

        // The order is the plan's, not the setup's. Two versions triggering one name are two
        // orders in two simulated accounts, and a setup-derived id would collide on the second.
        command.Parameters.AddWithValue("@order_id", plan.PlanId);
        command.Parameters.AddWithValue("@plan_id", plan.PlanId);
        command.Parameters.AddWithValue("@variant_id", plan.VariantId);
        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(plan.LiveSession));
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue(
            "@triggered_at", StoreText.TimestampToStorageText(trigger.TouchedAt!.Value));
        command.Parameters.AddWithValue("@status", verdict.IsPlaced ? "placed" : "blocked");
        command.Parameters.AddWithValue("@planned_shares", plan.Shares);
        command.Parameters.AddWithValue("@shares", verdict.Shares);
        command.Parameters.AddWithValue("@risk_at_stake", StoreText.PriceToStorageText(verdict.RiskAtStake));
        command.Parameters.AddWithValue("@bound_by", (object?)verdict.BoundBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@blocked_because", (object?)verdict.Because ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
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
            INSERT INTO order_run (
                session_date, triggers, placed, reduced, blocked,
                blocked_open_positions, blocked_open_shorts,
                reduced_position_size, reduced_total_risk, blocked_below_one_share,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @triggers, @placed, @reduced, @blocked,
                @blocked_open_positions, @blocked_open_shorts,
                @reduced_position_size, @reduced_total_risk, @blocked_below_one_share,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@triggers", tally.Placed + tally.Blocked);
        command.Parameters.AddWithValue("@placed", tally.Placed);
        command.Parameters.AddWithValue("@reduced", tally.Reduced);
        command.Parameters.AddWithValue("@blocked", tally.Blocked);
        command.Parameters.AddWithValue("@blocked_open_positions", tally.BlockedOpenPositions);
        command.Parameters.AddWithValue("@blocked_open_shorts", tally.BlockedOpenShorts);
        command.Parameters.AddWithValue("@reduced_position_size", tally.ReducedPositionSize);
        command.Parameters.AddWithValue("@reduced_total_risk", tally.ReducedTotalRisk);
        command.Parameters.AddWithValue("@blocked_below_one_share", tally.BlockedBelowOneShare);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A night's decisions counted by cap rather than as two totals.
    ///
    /// A block on a full book and a block on a risk budget with no room are different facts about a
    /// morning, and one `blocked` total reads the same for both.
    /// </summary>
    public sealed class Tally
    {
        public int Placed { get; private set; }

        public int Reduced { get; private set; }

        public int Blocked { get; private set; }

        public int BlockedOpenPositions { get; private set; }

        public int BlockedOpenShorts { get; private set; }

        public int ReducedPositionSize { get; private set; }

        public int ReducedTotalRisk { get; private set; }

        public int BlockedBelowOneShare { get; private set; }

        public void Count(RiskVerdict verdict)
        {
            ArgumentNullException.ThrowIfNull(verdict);

            if (verdict.IsPlaced)
            {
                Placed++;

                if (!verdict.Reduced)
                {
                    return;
                }

                Reduced++;

                if (verdict.BoundBy == RiskLimits.PositionSize)
                {
                    ReducedPositionSize++;
                }
                else
                {
                    ReducedTotalRisk++;
                }

                return;
            }

            Blocked++;

            switch (verdict.BoundBy)
            {
                case RiskLimits.OpenPositions:
                    BlockedOpenPositions++;
                    break;
                case RiskLimits.OpenShorts:
                    BlockedOpenShorts++;
                    break;
                default:
                    // A proportional cap that reduced the order below one share, which is the one
                    // path where reducing ends in a refusal.
                    BlockedBelowOneShare++;
                    break;
            }
        }
    }
}

/// <summary>What one run of the risk gate decided, with its refusals and reductions by cap.</summary>
public sealed record OrderRunResult(
    DateOnly SessionDate,
    int Triggers,
    RiskGate.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int Placed => Counts.Placed;

    public int Reduced => Counts.Reduced;

    public int Blocked => Counts.Blocked;

    public int BlockedOpenPositions => Counts.BlockedOpenPositions;

    public int BlockedOpenShorts => Counts.BlockedOpenShorts;

    public int ReducedPositionSize => Counts.ReducedPositionSize;

    public int ReducedTotalRisk => Counts.ReducedTotalRisk;

    public int BlockedBelowOneShare => Counts.BlockedBelowOneShare;
}
