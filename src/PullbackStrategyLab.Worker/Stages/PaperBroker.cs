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
/// What the orders RiskGate placed actually got.
///
/// <b>It prices and it does not decide.</b> <see cref="FillModel"/> holds every rule about what a
/// fill costs and is pure; this component walks a session, asks that model, and writes the rows. The
/// split is the one <see cref="RiskLimits"/> and RiskGate already take, and it is what lets the
/// arithmetic be asserted over every price relationship rather than over the ones a fixture holds.
///
/// <b>Every entry is this stage's and every exit is PositionManager's, from 4.8.</b> Until then the
/// give-up point was closed here, because it was the only way a position could end and it is a
/// resting instruction rather than a rule. From 4.8 a position can end three ways and the rule is
/// that the exit is whichever is reached first, which is a comparison across rules; a comparison
/// cannot be made by two components each of which sees one side of it. So the whole of it moved,
/// rather than the two new rules joining the old one here, and the boundary is now the one sentence
/// above rather than a list of which exits live where.
/// see: Every exit is PositionManager's and every entry is PaperBroker's
///
/// <b>The session is walked one minute at a time, through the same clock the resolver uses.</b> One
/// clock for every name, forward only, enumerable once.
///
/// <b>A name the session quoted no usable book for is not filled.</b> Charging nought is a free
/// entry that clears every threshold written as a maximum, and charging a figure taken from other
/// names would be a spread nobody measured wearing the authority of one that was. The order becomes
/// an unfilled position row with the reason, so the refusal is countable.
/// see: A fill with no usable quote for its name is refused and recorded, never charged nought
/// see: A fill is charged the widest usable quote of its session, not the nearest one
/// </summary>
public sealed class PaperBroker
{
    public const string Name = "fills";

    /// <summary>No order was placed, so there was nothing to price.</summary>
    public const string NothingToFill = "no order was placed in this session";

    /// <summary>The session holds no stored minute for any name being filled.</summary>
    public const string SessionHeldNoMinutes =
        "the store holds no minute of this session for any name with an order in it";

    /// <summary>Neither spread pass ran, so nothing in this session can be charged a spread.</summary>
    public const string SessionWasNeverSampled =
        "no spread pass was recorded for this session, so no fill in it can be charged the spread it owes";

    /// <summary>The session ran its passes and quoted this name no two-sided book.</summary>
    public const string NoUsableQuote =
        "the session quoted no usable two-sided book for this name, so no spread could be charged";

    /// <summary>The trigger minute the resolver recorded is not among this session's stored bars.</summary>
    public const string TriggerMinuteNotStored =
        "the minute the resolver found the trigger in is not among this session's stored bars";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public PaperBroker(
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

        FillRunResult result = Fill(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.OpenAtStart} position(s) carried in, "
            + $"{result.OrdersPlaced} order(s) placed");
        Console.WriteLine(
            $"{Name}: {result.EntriesFilled} entry fill(s), {result.EntriesUnfilled} order(s) not priced");
        Console.WriteLine(
            $"{Name}: {result.Slipped} charged the captured spread, {result.Gapped} filled at an open "
            + "and charged nothing");
        Console.WriteLine(
            $"{Name}: walked {result.MinutesWalked} minute(s) across {result.NamesWalked} name(s)");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Price every entry of <paramref name="sessionDate"/>.
    ///
    /// Idempotent: a position is keyed on its plan and inserted with do-nothing on conflict, so a
    /// rerun over a session already priced writes nothing.
    /// </summary>
    public FillRunResult Fill(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "position", "fill", "fill_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        // Past local midnight of the session's own day the order read answers with nothing, so a
        // run would record "no order was placed in this session" over a read it could not make. It
        // refuses, recorded rather than thrown, on the terms the two stages above it do.
        if (TradeChainWindow.Closed(observedAt, sessionDate, _options.SessionZone) is string closed)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Failed, closed, observedAt);
        }

        // The book coming into the session, reported and not walked. It is what the caps saw at
        // 21:10 and it belongs on the night's row; nothing here can change it, because a position
        // opened before this session ends by a rule this stage does not run.
        tally.OpenAtStart = PositionReader.OpenComingInto(connection, sessionDate, sessionDate, _options.SessionZone)
            .Count(p => p.ClosedSession is null);

        StoredTradeOrder[] placed =
            [.. TradeOrderReader.ForLiveSession(connection, sessionDate, sessionDate, _options.SessionZone)
                .Where(o => string.Equals(o.Status, "placed", StringComparison.Ordinal))];

        tally.OrdersPlaced = placed.Length;

        if (placed.Length == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingToFill, observedAt);
        }

        // Fail-closed on a session nobody sampled, and it is a run outcome rather than an exception.
        // A fill charged no slippage on a session nobody measured is the silently wrong result, and
        // a stage that threw would leave the night with no row saying why.
        SessionSampling sampling = SpreadSnapshotReader.SamplingOf(connection, sessionDate, sessionDate, _options.SessionZone);

        string[] names =
            [.. placed.Select(o => o.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Dictionary<string, StoredTradePlan> plans = TradePlanReader
            .ForSetups(connection, [.. placed.Select(o => o.SetupId)], sessionDate, _options.SessionZone)
            .ToDictionary(p => p.SetupId, StringComparer.Ordinal);

        if (sampling.IsUnsampled)
        {
            // Every order becomes an unfilled row. Partial and not failed: the stage did its whole
            // job over a session whose evidence is missing, and a failed run would be
            // indistinguishable from one that could not open the store.
            using SqliteTransaction unsampled = connection.BeginTransaction();

            foreach (StoredTradeOrder order in placed)
            {
                InsertUnfilled(unsampled, plans[order.SetupId], order, SessionWasNeverSampled, observedAt);
                tally.EntriesUnfilled++;
            }

            unsampled.Commit();

            return Complete(connection, run, sessionDate, tally, RunOutcome.Partial, SessionWasNeverSampled, observedAt);
        }

        Dictionary<string, QuotedSpread?> quotes = names.ToDictionary(
            name => name,
            name => SpreadCharge.Widest(
                SpreadSnapshotReader.Read(connection, name, sessionDate, sessionDate, _options.SessionZone).Usable
                    .Select(s => new QuotedSpread(s.Pass, s.SpreadBasisPoints!.Value, s.QuoteLagSeconds, s.StraddleSeconds))),
            StringComparer.Ordinal);

        SessionReplayClock clock = SessionReplayClock.ForSession(connection, names, sessionDate, sessionDate, _options.SessionZone);

        Dictionary<DateTimeOffset, List<StoredTradeOrder>> byMinute = placed
            .GroupBy(o => o.TriggeredAt)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.Ticker, StringComparer.Ordinal).ToList());

        var filled = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var writes = new List<Action<SqliteTransaction>>();
        int minutesWalked = 0;

        foreach (ReplayMinute minute in clock.Walk())
        {
            minutesWalked++;

            foreach (string ticker in minute.Bars.Keys)
            {
                seen.Add(ticker);
            }

            if (!byMinute.TryGetValue(minute.OpenedAt, out List<StoredTradeOrder>? triggered))
            {
                continue;
            }

            foreach (StoredTradeOrder order in triggered)
            {
                StoredIntradayBar? bar = minute.Of(order.Ticker);

                if (bar is null)
                {
                    continue;
                }

                Open(plans[order.SetupId], order, bar, quotes[order.Ticker], observedAt, writes, tally);
                filled.Add(order.SetupId);
            }
        }

        // An order whose trigger minute is not in the stored bars. It cannot happen through the
        // store as it stands, since the resolver found that minute in the same table; it can happen
        // after a vendor correction removed one. Recorded rather than dropped: a placed order with
        // no row at all is a fill nobody would know was missing.
        foreach (StoredTradeOrder order in placed.Where(o => !filled.Contains(o.SetupId)))
        {
            StoredTradeOrder captured = order;
            writes.Add(tx => InsertUnfilled(
                tx, plans[captured.SetupId], captured, TriggerMinuteNotStored, observedAt));
            tally.EntriesUnfilled++;
        }

        if (minutesWalked == 0)
        {
            // A session with something to fill and no stored minute is partial, on exactly the terms
            // the resolver reports one: a blind night reported as a night on which nothing filled is
            // the shape that cost this lab an evening of evidence.
            using SqliteTransaction blind = connection.BeginTransaction();

            foreach (Action<SqliteTransaction> write in writes)
            {
                write(blind);
            }

            blind.Commit();

            return Complete(connection, run, sessionDate, tally, RunOutcome.Partial, SessionHeldNoMinutes, observedAt);
        }

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (Action<SqliteTransaction> write in writes)
        {
            write(transaction);
        }

        transaction.Commit();

        // `names_walked` is the names the session actually had minutes for rather than the names
        // asked about, so a night the fetch missed half of is legible from the row.
        tally.NamesWalked = seen.Count;
        tally.MinutesWalked = minutesWalked;

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    /// <summary>
    /// Price an entry, or record why it could not be priced.
    ///
    /// <b>A minute that opens through the trigger fills at that open, whatever time of day it is.</b>
    /// Until 4.8 the gap rule ran only on the session's first regular minute, which is the overnight
    /// case the decision was written about, and an intraday minute opening past the trigger filled at
    /// the trigger: a price that did not trade in that minute at all, and one that flatters every
    /// time. The rule now reads the bar rather than the clock
    /// (see: A minute that opens through a resting price fills at that open, whatever time of day it
    /// is).
    /// </summary>
    private static void Open(
        StoredTradePlan plan,
        StoredTradeOrder order,
        StoredIntradayBar bar,
        QuotedSpread? quote,
        DateTimeOffset observedAt,
        List<Action<SqliteTransaction>> writes,
        Tally tally)
    {
        decimal? gapped = FillModel.OpenedThrough(plan.Direction, isExit: false, plan.TriggerPrice, bar.Open)
            ? bar.Open
            : null;

        if (gapped is null && quote is null)
        {
            writes.Add(tx => InsertUnfilled(tx, plan, order, NoUsableQuote, observedAt));
            tally.EntriesUnfilled++;
            return;
        }

        Fill fill = FillModel.Entry(plan.Direction, plan.TriggerPrice, gapped, quote?.BasisPoints ?? 0d);

        // The position is the plan's, not the setup's: two versions holding one name are two
        // positions in two simulated accounts, and a setup-derived id would collide on the second.
        string positionId = plan.PlanId;
        string fillId = $"{plan.PlanId}:entry";
        int shares = order.Shares;

        decimal riskIntended = shares * plan.GiveUpDistance;
        decimal riskRealised = shares * Math.Abs(fill.Price - plan.GiveUpPrice);
        decimal value = shares * fill.Price;

        writes.Add(tx =>
        {
            InsertPosition(tx, plan, order, positionId, fillId, bar.OpenedAt, shares, fill.Price,
                value, riskIntended, riskRealised, observedAt);
            InsertFill(tx, plan, positionId, fillId, bar.SessionDate, bar.OpenedAt,
                plan.TriggerPrice, fill, shares, quote, observedAt);
        });

        tally.EntriesFilled++;
        tally.Count(fill.Basis);
    }

    private static void InsertPosition(
        SqliteTransaction transaction,
        StoredTradePlan plan,
        StoredTradeOrder order,
        string positionId,
        string fillId,
        DateTimeOffset openedAt,
        int shares,
        decimal entryPrice,
        decimal value,
        decimal riskIntended,
        decimal riskRealised,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO position (
                position_id, plan_id, setup_id, variant_id, order_id, ticker, direction, status, opened_session, opened_at,
                shares, entry_fill_id, entry_price, value_at_entry, fraction_at_entry,
                risk_intended, risk_realised, borrow_rate_assumed, borrow_availability, observed_at)
            VALUES (
                @position_id, @plan_id, @setup_id, @variant_id, @order_id, @ticker, @direction, 'open', @opened_session, @opened_at,
                @shares, @entry_fill_id, @entry_price, @value_at_entry, @fraction_at_entry,
                @risk_intended, @risk_realised, @borrow_rate_assumed, @borrow_availability, @observed_at)
            ON CONFLICT (position_id) DO NOTHING;
            """;

        bool isShort = string.Equals(plan.Direction, SetupDirection.Short, StringComparison.Ordinal);

        command.Parameters.AddWithValue("@position_id", positionId);
        command.Parameters.AddWithValue("@plan_id", plan.PlanId);
        command.Parameters.AddWithValue("@variant_id", plan.VariantId);
        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@order_id", order.OrderId);
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue("@opened_session", StoreText.DateToStorageText(plan.LiveSession));
        command.Parameters.AddWithValue("@opened_at", StoreText.TimestampToStorageText(openedAt));
        command.Parameters.AddWithValue("@shares", shares);
        command.Parameters.AddWithValue("@entry_fill_id", fillId);
        command.Parameters.AddWithValue("@entry_price", StoreText.PriceToStorageText(entryPrice));
        command.Parameters.AddWithValue("@value_at_entry", StoreText.PriceToStorageText(value));
        command.Parameters.AddWithValue("@fraction_at_entry", (double)(value / PositionSizing.NotionalEquity));
        command.Parameters.AddWithValue("@risk_intended", StoreText.PriceToStorageText(riskIntended));
        command.Parameters.AddWithValue("@risk_realised", StoreText.PriceToStorageText(riskRealised));
        command.Parameters.AddWithValue(
            "@borrow_rate_assumed",
            isShort ? StoreText.PriceToStorageText(BorrowAssumption.AnnualisedRate) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@borrow_availability",
            isShort ? BorrowAssumption.AvailabilityIsNotModelled : (object)DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void InsertUnfilled(
        SqliteTransaction transaction,
        StoredTradePlan plan,
        StoredTradeOrder order,
        string because,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO position (
                position_id, plan_id, setup_id, variant_id, order_id, ticker, direction, status, opened_session,
                shares, unfilled_because, borrow_rate_assumed, borrow_availability, observed_at)
            VALUES (
                @position_id, @plan_id, @setup_id, @variant_id, @order_id, @ticker, @direction, 'unfilled', @opened_session,
                0, @unfilled_because, @borrow_rate_assumed, @borrow_availability, @observed_at)
            ON CONFLICT (position_id) DO NOTHING;
            """;

        bool isShort = string.Equals(plan.Direction, SetupDirection.Short, StringComparison.Ordinal);

        command.Parameters.AddWithValue("@position_id", plan.PlanId);
        command.Parameters.AddWithValue("@plan_id", plan.PlanId);
        command.Parameters.AddWithValue("@variant_id", plan.VariantId);
        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@order_id", order.OrderId);
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue("@opened_session", StoreText.DateToStorageText(plan.LiveSession));
        command.Parameters.AddWithValue("@unfilled_because", because);
        command.Parameters.AddWithValue(
            "@borrow_rate_assumed",
            isShort ? StoreText.PriceToStorageText(BorrowAssumption.AnnualisedRate) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@borrow_availability",
            isShort ? BorrowAssumption.AvailabilityIsNotModelled : (object)DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void InsertFill(
        SqliteTransaction transaction,
        StoredTradePlan plan,
        string positionId,
        string fillId,
        DateOnly sessionDate,
        DateTimeOffset filledAt,
        decimal restingPrice,
        Fill fill,
        int shares,
        QuotedSpread? quote,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fill (
                fill_id, position_id, plan_id, setup_id, variant_id, session_date, ticker, direction, leg, filled_at,
                basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
                quote_lag_seconds, straddle_seconds, observed_at)
            VALUES (
                @fill_id, @position_id, @plan_id, @setup_id, @variant_id, @session_date, @ticker, @direction, 'entry', @filled_at,
                @basis, @resting_price, @price, @slippage, @shares, @spread_bps, @spread_pass,
                @quote_lag_seconds, @straddle_seconds, @observed_at)
            ON CONFLICT (fill_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@fill_id", fillId);
        command.Parameters.AddWithValue("@position_id", positionId);
        command.Parameters.AddWithValue("@plan_id", plan.PlanId);
        command.Parameters.AddWithValue("@variant_id", plan.VariantId);
        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));
        command.Parameters.AddWithValue("@basis", fill.Basis);
        command.Parameters.AddWithValue("@resting_price", StoreText.PriceToStorageText(restingPrice));
        command.Parameters.AddWithValue("@price", StoreText.PriceToStorageText(fill.Price));
        command.Parameters.AddWithValue("@slippage", StoreText.PriceToStorageText(fill.Slippage));
        command.Parameters.AddWithValue("@shares", shares);
        command.Parameters.AddWithValue("@spread_bps", quote is null ? DBNull.Value : quote.BasisPoints);
        command.Parameters.AddWithValue("@spread_pass", quote is null ? DBNull.Value : quote.Pass);
        command.Parameters.AddWithValue(
            "@quote_lag_seconds", (object?)quote?.QuoteLagSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@straddle_seconds", (object?)quote?.StraddleSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static FillRunResult Complete(
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

        return new FillRunResult(sessionDate, tally, summary.RowsWritten, outcome, because);
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
            INSERT INTO fill_run (
                session_date, open_at_start, orders_placed, entries_filled, entries_unfilled,
                gapped, slipped, names_walked, minutes_walked,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @open_at_start, @orders_placed, @entries_filled, @entries_unfilled,
                @gapped, @slipped, @names_walked, @minutes_walked,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@open_at_start", tally.OpenAtStart);
        command.Parameters.AddWithValue("@orders_placed", tally.OrdersPlaced);
        command.Parameters.AddWithValue("@entries_filled", tally.EntriesFilled);
        command.Parameters.AddWithValue("@entries_unfilled", tally.EntriesUnfilled);
        command.Parameters.AddWithValue("@gapped", tally.Gapped);
        command.Parameters.AddWithValue("@slipped", tally.Slipped);
        command.Parameters.AddWithValue("@names_walked", tally.NamesWalked);
        command.Parameters.AddWithValue("@minutes_walked", tally.MinutesWalked);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>A night's entries counted by what they were and how they were priced.</summary>
    public sealed class Tally
    {
        public int OpenAtStart { get; set; }

        public int OrdersPlaced { get; set; }

        public int EntriesFilled { get; set; }

        public int EntriesUnfilled { get; set; }

        public int Gapped { get; private set; }

        public int Slipped { get; private set; }

        public int NamesWalked { get; set; }

        public int MinutesWalked { get; set; }

        public void Count(string basis)
        {
            if (string.Equals(basis, FillModel.Gapped, StringComparison.Ordinal))
            {
                Gapped++;
                return;
            }

            Slipped++;
        }
    }
}

/// <summary>What one run of PaperBroker priced, with the book it was handed.</summary>
public sealed record FillRunResult(
    DateOnly SessionDate,
    PaperBroker.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int OpenAtStart => Counts.OpenAtStart;

    public int OrdersPlaced => Counts.OrdersPlaced;

    public int EntriesFilled => Counts.EntriesFilled;

    public int EntriesUnfilled => Counts.EntriesUnfilled;

    public int Gapped => Counts.Gapped;

    public int Slipped => Counts.Slipped;

    public int NamesWalked => Counts.NamesWalked;

    public int MinutesWalked => Counts.MinutesWalked;
}
