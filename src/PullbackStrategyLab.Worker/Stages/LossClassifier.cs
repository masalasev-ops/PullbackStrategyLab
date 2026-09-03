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

    /// <summary>The horizon the aftermath question is answered over, in sessions after the trigger's.</summary>
    public const int HorizonDays = 10;

    /// <summary>What is written where the horizon closed and the store holds no close to read it from.</summary>
    public const string HorizonClosedWithNoFigure =
        "the ten-session horizon closed and the store holds no close for the trigger session or the "
        + "tenth session after it, so nothing in the taxonomy fits and the row says so rather than "
        + "being placed in the nearest bucket";

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
        StoredTradePlan? plan = PlanBehind(connection, row.SetupId, sessionDate);

        if (plan is null)
        {
            return;
        }

        // The horizon is closed when the store holds more than ten sessions for the name from the
        // session the trigger was touched in. Counted from the bars rather than from an authored
        // calendar, which is the ruling 4.5 took (see: A session is a date the store holds minutes
        // for, and no calendar is authored here). The trigger's own session is in the count, so
        // eleven is ten having passed.
        int sessions = DailyBarReader.SessionsBetween(
            connection, row.Ticker, plan.LiveSession, sessionDate, sessionDate);

        if (sessions <= HorizonDays)
        {
            return;
        }

        AftermathFigures figures = FiguresFromTheTrigger(connection, row, plan, sessions, sessionDate);

        if (figures.Offered is decimal signed)
        {
            decimal oneR = LossCause.OneRInReturn(plan.GiveUpDistance, plan.TriggerPrice);
            string aftermath = LossCause.AftermathOf(signed, oneR);

            // Both figures in one sentence, named apart, because the gap between them is the
            // thing a person reads it for.
            string because =
                $"the direction-signed {HorizonDays}-session return from the trigger price of "
                + $"{plan.TriggerPrice}, over the ten sessions after {plan.LiveSession:yyyy-MM-dd}, was "
                + $"{signed} against one unit of risk of {oneR}"
                + (figures.Earned is decimal earned
                    ? $"; the trade itself earned {earned} from the same trigger to its exit at "
                      + $"{figures.ExitPrice} in {figures.ClosedSession:yyyy-MM-dd}"
                    : $"; {ExitFigureNotReadable}");

            Apply(transaction, row.TradeId, aftermath, signed, oneR, figures.Earned, because, observedAt, tally);

            return;
        }

        Apply(transaction, row.TradeId, LossAftermath.Unclassified, null, null, null,
            HorizonClosedWithNoFigure, observedAt, tally);
    }

    /// <summary>What is written beside the first figure where the second could not be put on the same basis.</summary>
    public const string ExitFigureNotReadable =
        "what the trade earned is not stated, because the store holds no bar for the session it "
        + "closed in and the exit could not be put on the adjusted basis the first figure is on";

    /// <summary>
    /// The two aftermath figures over one row, both from the trigger and both on the adjusted basis.
    ///
    /// <b>What the day offered</b> is the direction-signed return from the trigger price to the
    /// close of the tenth session after the one the trigger was touched in, or null where the store
    /// does not hold that bar. <b>What the trade earned</b> is the same return taken to the exit
    /// fill instead, or null where the store holds no bar for the session the trade closed in. The
    /// two differ only in where they end, and the gap between them is what the trail rule is
    /// judged on: with one figure a trail that captured a move and a trail that gave one back are
    /// the same number.
    /// see: The aftermath is measured from the exit as well as from the close, as two figures and never one
    ///
    /// <b>From the trigger, over the sessions after the trigger, which is the population the
    /// decision names and the one the code did not measure until 4.18.</b> Until then this read
    /// <c>forward_return.return_signed</c>, which <c>ForwardOutcome.Of</c> measures from the setup
    /// session's close over the ten sessions after the setup, and compared it against one R over the
    /// trigger price. A long's trigger sits above the setup close by construction, so that return
    /// exceeded the return from the trigger by the whole gap, and a loss that never reached one R
    /// from the trigger was placed as noise. Every number was right and the sentence beside it named
    /// a different population, which is the fifth failure shape with the code as the subject.
    /// see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
    ///
    /// The trigger and the exit are raw prices and each is put on the adjusted basis through its
    /// own session's bar, on the terms the short reclaim puts a printed hourly close against the
    /// average, so a split inside the window does not read as a move on either figure.
    /// </summary>
    private static AftermathFigures FiguresFromTheTrigger(
        SqliteConnection connection,
        StoredLossClass row,
        StoredTradePlan plan,
        int sessionsFromTheTrigger,
        DateOnly asOf)
    {
        // The newest `sessionsFromTheTrigger` bars at or before the as-of are exactly the trigger
        // session and everything after it, because that count was taken between the two dates.
        IReadOnlyList<StoredDailyBar> bars = DailyBarReader.Read(connection, row.Ticker, asOf, sessionsFromTheTrigger);

        StoredDailyBar? triggerSession = bars.FirstOrDefault(b => b.BarDate == plan.LiveSession);
        StoredDailyBar[] after = [.. bars.Where(b => b.BarDate > plan.LiveSession)];

        if (triggerSession is null || after.Length < HorizonDays || triggerSession.Close == 0m)
        {
            return AftermathFigures.None;
        }

        decimal factor = ShortExitRules.AdjustmentFactor(triggerSession.Close, triggerSession.AdjustedClose);
        decimal from = plan.TriggerPrice * factor;

        if (from == 0m)
        {
            return AftermathFigures.None;
        }

        decimal offered = LossCause.SignedReturn(from, after[HorizonDays - 1].AdjustedClose, plan.Direction);

        // The trade this row explains, read on the same bound as everything else here. It closed in
        // `closed_session`, which is at or before the as-of, so its bar is in the set already read.
        StoredTrade? trade = TradeReader.ClosedIn(connection, row.ClosedSession, asOf)
            .FirstOrDefault(t => string.Equals(t.TradeId, row.TradeId, StringComparison.Ordinal));
        StoredDailyBar? closedSession = bars.FirstOrDefault(b => b.BarDate == row.ClosedSession);

        if (trade is null || closedSession is null || closedSession.Close == 0m)
        {
            return new AftermathFigures(offered, null, null, row.ClosedSession);
        }

        decimal exitFactor = ShortExitRules.AdjustmentFactor(closedSession.Close, closedSession.AdjustedClose);
        decimal earned = LossCause.SignedReturn(from, trade.ExitPrice * exitFactor, plan.Direction);

        return new AftermathFigures(offered, earned, trade.ExitPrice, row.ClosedSession);
    }

    /// <summary>
    /// The pair, with the exit the second was taken to so the sentence can name it.
    /// </summary>
    private sealed record AftermathFigures(decimal? Offered, decimal? Earned, decimal? ExitPrice, DateOnly ClosedSession)
    {
        public static AftermathFigures None { get; } = new(null, null, null, default);
    }

    private static void Apply(
        SqliteTransaction transaction,
        string tradeId,
        string aftermath,
        decimal? signed,
        decimal? oneR,
        decimal? earned,
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
                   exit_return_signed = @exit_return_signed,
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
        command.Parameters.AddWithValue(
            "@exit_return_signed", earned is null ? DBNull.Value : StoreText.PriceToStorageText(earned.Value));
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
    private static StoredTradePlan? PlanBehind(SqliteConnection connection, string setupId, DateOnly asOf)
    {
        IReadOnlyList<StoredTradePlan> plans = TradePlanReader.ForSetups(connection, [setupId], asOf);

        return plans.Count == 0 ? null : plans[0];
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
