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
/// Why each closed loss happened. One demoralising number becomes four actionable ones.
///
/// <b>Two passes, because the two answers arrive at different times.</b> The mechanism names how the
/// loss occurred and is known the moment the trade closes; the aftermath names what happened next and
/// cannot be known for ten sessions after the trigger. So the first pass classifies the losses this
/// session closed, and the second walks every row still waiting on a horizon, whatever session it
/// closed in. Holding the first answer back until the second existed would be discarding an answer
/// the lab already has.
/// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
///
/// <b>Both questions are asked of every loss.</b> A gap loss that later recovers satisfies both
/// without contradiction, and it can only do so if the second is put to it. Asking the aftermath only
/// of the losses that were not gaps is what a single ranked list would have done, and the ranked list
/// is what the decision refuses.
///
/// <b>Awaiting a horizon is not being unclassified.</b> Null is a question the lab cannot answer yet;
/// <c>unclassified</c> is one it could answer and could not place. The second is a real category and
/// a share that grows in it is a finding about this component rather than about the trades, which is
/// only readable because the first is not folded into it.
///
/// <b>The mechanism is read from the exit fill's basis, and the document said something else.</b>
/// ARCHITECTURE's failure table has said since it was written that a gap loss is a "loss larger than
/// one unit of risk". That detector fires on every ordinary stop-out, because a round trip costs two
/// crossings and an ordinary stop therefore loses slightly more than one unit of risk by
/// construction. The document is corrected at 4.10 rather than the code being written to it.
/// </summary>
public sealed class LossClassifier
{
    public const string Name = "losses";

    /// <summary>Nothing closed at a loss and nothing was waiting on a horizon.</summary>
    public const string NothingToClassify =
        "no loss closed in this session and no earlier one is waiting on a horizon";

    /// <summary>The horizon the aftermath question is answered over, in sessions from the setup.</summary>
    public const int HorizonDays = 10;

    /// <summary>What is written where the horizon closed and the forward return is absent.</summary>
    public const string HorizonClosedWithNoFigure =
        "the ten-session horizon closed and no forward return was filled for this setup, so nothing "
        + "in the taxonomy fits and the row says so rather than being placed in the nearest bucket";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public LossClassifier(
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

        LossRunResult result = Classify(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.LossesClosed} loss(es) closed, "
            + $"{result.MechanismsWritten} mechanism(s) written");
        Console.WriteLine(
            $"{Name}: {result.Gap} gap, {result.Ordinary} ordinary, over {result.Longs} long and "
            + $"{result.Shorts} short");
        Console.WriteLine(
            $"{Name}: {result.AftermathsWritten} aftermath(s) written, {result.Noise} noise, "
            + $"{result.FailedSetup} failed setup, {result.Unclassified} unclassified");
        Console.WriteLine(
            $"{Name}: {result.AwaitingAftermath} row(s) still waiting on a horizon, which is not the same "
            + "as unclassified");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Classify what <paramref name="sessionDate"/> closed at a loss, and place any earlier loss
    /// whose horizon has since closed.
    ///
    /// Idempotent: a mechanism is keyed on its trade and inserted with do-nothing on conflict, and an
    /// aftermath is applied only to a row that still has none.
    /// </summary>
    public LossRunResult Classify(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "loss_class", "loss_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        StoredTrade[] losses =
            [.. TradeReader.ClosedIn(connection, sessionDate, sessionDate)
                .Where(t => LossCause.IsALoss(t.NetPnl))];

        tally.LossesClosed = losses.Length;

        int waitingAtTheStart = LossClassReader.All(connection, sessionDate).Count(l => l.AwaitsItsHorizon);

        if (losses.Length == 0 && waitingAtTheStart == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingToClassify, observedAt);
        }

        // 1. The mechanism, for what closed tonight. Known from the exit fill and nothing else.
        //
        //    Committed before the second pass reads, and that ordering is the point rather than an
        //    accident of transactions: a loss whose horizon has already closed is one both answers
        //    are available for tonight, and a second pass reading the book as it stood before the
        //    first would leave it waiting a night for a figure the lab already had.
        if (losses.Length > 0)
        {
            ILookup<string, StoredFill> fills = PositionReader
                .FillsFor(connection, [.. losses.Select(t => t.PositionId)], sessionDate)
                .ToLookup(f => f.PositionId, StringComparer.Ordinal);

            using SqliteTransaction mechanisms = connection.BeginTransaction();

            foreach (StoredTrade loss in losses)
            {
                StoredFill? exit = fills[loss.PositionId]
                    .FirstOrDefault(f => string.Equals(f.Leg, "exit", StringComparison.Ordinal));

                // A closed trade with no exit fill cannot happen through this store, because a
                // position closes by writing one. Refused rather than classified from the size of
                // the loss, which is the detector this component exists to have replaced.
                if (exit is null)
                {
                    continue;
                }

                InsertMechanism(mechanisms, loss, exit, observedAt, tally);
            }

            mechanisms.Commit();
        }

        // 2. The aftermath, for every row still waiting, whatever session it closed in, including
        //    the ones the pass above just wrote. A row inserted weeks ago is exactly the one whose
        //    horizon has now closed, and a row inserted a moment ago may be one too.
        StoredLossClass[] waiting =
            [.. LossClassReader.All(connection, sessionDate).Where(l => l.AwaitsItsHorizon)];

        using SqliteTransaction aftermaths = connection.BeginTransaction();

        foreach (StoredLossClass row in waiting)
        {
            Place(aftermaths, connection, row, sessionDate, observedAt, tally);
        }

        aftermaths.Commit();

        // Counted after both passes and read from the store rather than derived, because a row this
        // run inserted and did not place is one still waiting and a row it placed is not.
        tally.AwaitingAftermath = LossClassReader.All(connection, sessionDate).Count(l => l.AwaitsItsHorizon);

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    private static void InsertMechanism(
        SqliteTransaction transaction,
        StoredTrade loss,
        StoredFill exit,
        DateTimeOffset observedAt,
        Tally tally)
    {
        string mechanism = LossCause.MechanismOf(exit.Basis);

        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO loss_class (
                trade_id, setup_id, ticker, direction, closed_session, net_pnl, result_r,
                mechanism, exit_basis, observed_at)
            VALUES (
                @trade_id, @setup_id, @ticker, @direction, @closed_session, @net_pnl, @result_r,
                @mechanism, @exit_basis, @observed_at)
            ON CONFLICT (trade_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@trade_id", loss.TradeId);
        command.Parameters.AddWithValue("@setup_id", loss.SetupId);
        command.Parameters.AddWithValue("@ticker", loss.Ticker);
        command.Parameters.AddWithValue("@direction", loss.Direction);
        command.Parameters.AddWithValue("@closed_session", StoreText.DateToStorageText(loss.ClosedSession));
        command.Parameters.AddWithValue("@net_pnl", StoreText.PriceToStorageText(loss.NetPnl));
        command.Parameters.AddWithValue("@result_r", loss.ResultR);
        command.Parameters.AddWithValue("@mechanism", mechanism);
        command.Parameters.AddWithValue("@exit_basis", exit.Basis);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        if (command.ExecuteNonQuery() == 0)
        {
            return;
        }

        tally.CountMechanism(mechanism);
        tally.CountSide(loss.Direction);
    }

    /// <summary>
    /// Answer the aftermath for one row, or leave it waiting.
    ///
    /// Three outcomes and they are three different states. A forward return the lab could have known
    /// by this session places the row. No return and a horizon that has closed makes it
    /// <c>unclassified</c>, which is a real category rather than a silent skip. No return and a
    /// horizon still open leaves it null, which is the state the count reports separately.
    /// </summary>
    private static void Place(
        SqliteTransaction transaction,
        SqliteConnection connection,
        StoredLossClass row,
        DateOnly sessionDate,
        DateTimeOffset observedAt,
        Tally tally)
    {
        (DateOnly setupDate, decimal triggerPrice, decimal giveUpDistance)? plan =
            PlanBehind(connection, row.SetupId, sessionDate);

        if (plan is null)
        {
            return;
        }

        decimal? forward = ForwardReturnOf(connection, row.SetupId, sessionDate);

        if (forward is decimal signed)
        {
            decimal oneR = LossCause.OneRInReturn(plan.Value.giveUpDistance, plan.Value.triggerPrice);
            string aftermath = LossCause.AftermathOf(signed, oneR);

            Apply(transaction, row.TradeId, aftermath, signed, oneR,
                $"the direction-signed {HorizonDays}-session return from the trigger was {signed} against "
                + $"one unit of risk of {oneR}",
                observedAt, tally);

            return;
        }

        // The horizon is closed when the store holds more than ten sessions for the name after the
        // setup's own. Counted from the bars rather than from an authored calendar, which is the
        // ruling 4.5 took (see: A session is a date the store holds minutes for, and no calendar is
        // authored here). The setup's own session is in the count, so eleven is ten having passed.
        int sessions = DailyBarReader.SessionsBetween(
            connection, row.Ticker, plan.Value.setupDate, sessionDate, sessionDate);

        if (sessions > HorizonDays)
        {
            Apply(transaction, row.TradeId, LossAftermath.Unclassified, null, null,
                HorizonClosedWithNoFigure, observedAt, tally);
        }
    }

    private static void Apply(
        SqliteTransaction transaction,
        string tradeId,
        string aftermath,
        decimal? signed,
        decimal? oneR,
        string because,
        DateTimeOffset observedAt,
        Tally tally)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;

        // Guarded on the row still having none, so a rerun applies nothing and a second answer for
        // one loss is unexpressible rather than merely unwritten.
        command.CommandText = """
            UPDATE loss_class
               SET aftermath = @aftermath,
                   forward_return_signed = @forward_return_signed,
                   one_r_in_return = @one_r_in_return,
                   aftermath_because = @aftermath_because,
                   aftermath_observed_at = @aftermath_observed_at
             WHERE trade_id = @trade_id
               AND aftermath IS NULL;
            """;

        command.Parameters.AddWithValue("@aftermath", aftermath);
        command.Parameters.AddWithValue(
            "@forward_return_signed",
            signed is null ? DBNull.Value : StoreText.PriceToStorageText(signed.Value));
        command.Parameters.AddWithValue(
            "@one_r_in_return", oneR is null ? DBNull.Value : StoreText.PriceToStorageText(oneR.Value));
        command.Parameters.AddWithValue("@aftermath_because", because);
        command.Parameters.AddWithValue(
            "@aftermath_observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@trade_id", tradeId);

        if (command.ExecuteNonQuery() == 0)
        {
            return;
        }

        tally.CountAftermath(aftermath);
    }

    /// <summary>
    /// The plan the loss came from, for the two figures the boundary is read in.
    ///
    /// Bounded on the plan's own stamp, on the terms every read of it is: the plan is immutable and
    /// keyed on the setup, so the bound will rarely exclude anything, and a read that trusted that
    /// would stop being point-in-time the day a backfill existed.
    /// </summary>
    private static (DateOnly, decimal, decimal)? PlanBehind(
        SqliteConnection connection, string setupId, DateOnly asOf)
    {
        IReadOnlyList<StoredTradePlan> plans = TradePlanReader.ForSetups(connection, [setupId], asOf);

        return plans.Count == 0
            ? null
            : (plans[0].AsOf, plans[0].TriggerPrice, plans[0].GiveUpDistance);
    }

    /// <summary>
    /// The direction-signed ten-session return from the trigger, as far as this session could know
    /// it, or null where none was filled.
    ///
    /// Bounded on <c>filled_at</c>, which is when the lab could first have known the figure. A
    /// classification standing at an old session that saw a return filled after it would place a loss
    /// the night could not have placed.
    /// </summary>
    private static decimal? ForwardReturnOf(SqliteConnection connection, string setupId, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT return_signed
              FROM forward_return
             WHERE subject_id = @subject_id
               AND subject_kind = 'setup'
               AND horizon_days = @horizon
               AND filled_at <= @filled_before
             ORDER BY filled_at DESC
             LIMIT 1;
            """;

        command.Parameters.AddWithValue("@subject_id", setupId);
        command.Parameters.AddWithValue("@horizon", HorizonDays);
        command.Parameters.AddWithValue(
            "@filled_before", StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities));

        object? value = command.ExecuteScalar();

        return value is string text ? StoreText.StorageTextToPrice(text) : null;
    }

    private static LossRunResult Complete(
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

        return new LossRunResult(sessionDate, tally, summary.RowsWritten, outcome, because);
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
            INSERT INTO loss_run (
                session_date, losses_closed, mechanisms_written, gap, ordinary, longs, shorts,
                awaiting_aftermath, aftermaths_written, noise, failed_setup, unclassified,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @losses_closed, @mechanisms_written, @gap, @ordinary, @longs, @shorts,
                @awaiting_aftermath, @aftermaths_written, @noise, @failed_setup, @unclassified,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@losses_closed", tally.LossesClosed);
        command.Parameters.AddWithValue("@mechanisms_written", tally.MechanismsWritten);
        command.Parameters.AddWithValue("@gap", tally.Gap);
        command.Parameters.AddWithValue("@ordinary", tally.Ordinary);
        command.Parameters.AddWithValue("@longs", tally.Longs);
        command.Parameters.AddWithValue("@shorts", tally.Shorts);
        command.Parameters.AddWithValue("@awaiting_aftermath", tally.AwaitingAftermath);
        command.Parameters.AddWithValue("@aftermaths_written", tally.AftermathsWritten);
        command.Parameters.AddWithValue("@noise", tally.Noise);
        command.Parameters.AddWithValue("@failed_setup", tally.FailedSetup);
        command.Parameters.AddWithValue("@unclassified", tally.Unclassified);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>A night's classifications, with the two passes counted apart because they are two questions.</summary>
    public sealed class Tally
    {
        public int LossesClosed { get; set; }

        public int MechanismsWritten { get; private set; }

        public int Gap { get; private set; }

        public int Ordinary { get; private set; }

        public int Longs { get; private set; }

        public int Shorts { get; private set; }

        public int AwaitingAftermath { get; set; }

        public int AftermathsWritten { get; private set; }

        public int Noise { get; private set; }

        public int FailedSetup { get; private set; }

        public int Unclassified { get; private set; }

        /// <summary>
        /// One mechanism, counted under its own value.
        ///
        /// Refused rather than defaulted, so a value the taxonomy gains later fails here instead of
        /// being absorbed into the ordinary bucket, which is the one that hides things.
        /// </summary>
        public void CountMechanism(string mechanism)
        {
            MechanismsWritten++;

            switch (mechanism)
            {
                case LossMechanism.Gap:
                    Gap++;
                    return;
                case LossMechanism.Ordinary:
                    Ordinary++;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mechanism),
                        $"'{mechanism}' is not one of the {LossMechanism.All.Count} mechanisms, so the night's "
                        + "row has no column for it and the two would stop adding to the total.");
            }
        }

        /// <summary>One aftermath, counted under its own value, with the same refusal.</summary>
        public void CountAftermath(string aftermath)
        {
            AftermathsWritten++;

            switch (aftermath)
            {
                case LossAftermath.Noise:
                    Noise++;
                    return;
                case LossAftermath.FailedSetup:
                    FailedSetup++;
                    return;
                case LossAftermath.Unclassified:
                    Unclassified++;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(aftermath),
                        $"'{aftermath}' is not one of the {LossAftermath.All.Count} aftermaths, so the night's "
                        + "row has no column for it and the three would stop adding to the total.");
            }
        }

        public void CountSide(string direction)
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
                        + "two sides would stop adding to the total (see: Long and short are never pooled into "
                        + "one figure).");
            }
        }
    }
}

/// <summary>What one run of LossClassifier wrote, with each pass counted apart.</summary>
public sealed record LossRunResult(
    DateOnly SessionDate,
    LossClassifier.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int LossesClosed => Counts.LossesClosed;

    public int MechanismsWritten => Counts.MechanismsWritten;

    public int Gap => Counts.Gap;

    public int Ordinary => Counts.Ordinary;

    public int Longs => Counts.Longs;

    public int Shorts => Counts.Shorts;

    public int AwaitingAftermath => Counts.AwaitingAftermath;

    public int AftermathsWritten => Counts.AftermathsWritten;

    public int Noise => Counts.Noise;

    public int FailedSetup => Counts.FailedSetup;

    public int Unclassified => Counts.Unclassified;
}
