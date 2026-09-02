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
/// What the orders RiskGate placed actually got, and what happened to the positions they opened.
///
/// <b>It prices and it does not decide.</b> <see cref="FillModel"/> holds every rule about what a
/// fill costs and is pure; this component walks a session, asks that model, and writes the rows. The
/// split is the one <see cref="RiskLimits"/> and RiskGate already take, and it is what lets the
/// arithmetic be asserted over every price relationship rather than over the ones a fixture holds.
///
/// <b>The one exit it runs is the give-up point, and that is a boundary rather than an omission.</b>
/// The give-up price is a resting instruction the plan carried from 18:30 the evening before, live
/// from the moment the entry fills; it is not a rule anybody evaluates. PositionManager at 4.8 owns
/// the two rule sets, being the long trail on the 9-day average and the short trim at 3R, and those
/// are per-direction rules over daily and hourly series. Until 4.8 lands, <b>a position that never
/// reaches its give-up point is held indefinitely</b>, so a winner occupies a slot the count caps
/// then refuse the next morning's trigger on. That is recorded rather than absorbed, and it is the
/// opposite direction of error from the one 4.6 carried.
///
/// <b>The session is walked one minute at a time, through the same clock the resolver uses.</b> One
/// clock for every name, forward only, enumerable once. Entries are taken before exits inside a
/// minute, so a bar holding both the trigger and the give-up point fills and then stops, which is
/// the pessimistic reading of a bar that carries no order between its own high and low.
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

    /// <summary>No position was carried in and no order was placed, so there was nothing to price.</summary>
    public const string NothingToFill = "no position was open and no order was placed in this session";

    /// <summary>The session holds no stored minute for any name being filled.</summary>
    public const string SessionHeldNoMinutes =
        "the store holds no minute of this session for any name with a position or an order in it";

    /// <summary>Neither spread pass ran, so nothing in this session can be charged a spread.</summary>
    public const string SessionWasNeverSampled =
        "no spread pass was recorded for this session, so no fill in it can be charged the spread it owes";

    /// <summary>The session ran its passes and quoted this name no two-sided book.</summary>
    public const string NoUsableQuote =
        "the session quoted no usable two-sided book for this name, so no spread could be charged";

    /// <summary>The trigger minute the resolver recorded is not among this session's stored bars.</summary>
    public const string TriggerMinuteNotStored =
        "the minute the resolver found the trigger in is not among this session's stored bars";

    /// <summary>The one exit rule this checkpoint runs.</summary>
    public const string GaveUp = "give-up";

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
            $"{Name}: {result.EntriesFilled} entry fill(s), {result.EntriesUnfilled} order(s) not priced, "
            + $"{result.ExitsFilled} exit fill(s)");
        Console.WriteLine(
            $"{Name}: {result.Slipped} charged the captured spread, {result.Gapped} filled at an open "
            + "and charged nothing");
        Console.WriteLine(
            $"{Name}: walked {result.MinutesWalked} minute(s) across {result.NamesWalked} name(s), "
            + $"{result.OpenAtEnd} position(s) open at the end");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Price every fill of <paramref name="sessionDate"/> and carry the book through it.
    ///
    /// Idempotent: a position is keyed on its plan and inserted with do-nothing on conflict, and a
    /// close is applied only to a row this run still reads as open. A rerun over a closed session
    /// writes nothing.
    /// </summary>
    public FillRunResult Fill(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "position", "fill", "fill_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        // Carried in: opened in an earlier session and not closed before this one. A close this run
        // already wrote is visible here, which is what makes a rerun write nothing.
        List<StoredPosition> carried =
            [.. PositionReader.OpenComingInto(connection, sessionDate, sessionDate)
                .Where(p => p.ClosedSession is null)];

        StoredTradeOrder[] placed =
            [.. TradeOrderReader.ForLiveSession(connection, sessionDate, sessionDate)
                .Where(o => string.Equals(o.Status, "placed", StringComparison.Ordinal))];

        tally.OpenAtStart = carried.Count;
        tally.OrdersPlaced = placed.Length;

        if (carried.Count == 0 && placed.Length == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingToFill, observedAt);
        }

        // Fail-closed on a session nobody sampled, and it is a run outcome rather than an exception.
        // A fill charged no slippage on a session nobody measured is the silently wrong result, and
        // a stage that threw would leave the night with no row saying why.
        SessionSampling sampling = SpreadSnapshotReader.SamplingOf(connection, sessionDate, sessionDate);

        string[] names =
            [.. carried.Select(p => p.Ticker).Concat(placed.Select(o => o.Ticker))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Dictionary<string, StoredTradePlan> plans = TradePlanReader
            .ForSetups(connection, [.. carried.Select(p => p.SetupId).Concat(placed.Select(o => o.SetupId))], sessionDate)
            .ToDictionary(p => p.SetupId, StringComparer.Ordinal);

        if (sampling.IsUnsampled)
        {
            // Every order becomes an unfilled row and every carried position stays open. Partial and
            // not failed: the stage did its whole job over a session whose evidence is missing, and
            // a failed run would be indistinguishable from one that could not open the store.
            using SqliteTransaction unsampled = connection.BeginTransaction();

            foreach (StoredTradeOrder order in placed)
            {
                InsertUnfilled(unsampled, plans[order.SetupId], order, SessionWasNeverSampled, observedAt);
                tally.EntriesUnfilled++;
            }

            unsampled.Commit();

            tally.OpenAtEnd = carried.Count;

            return Complete(connection, run, sessionDate, tally, RunOutcome.Partial, SessionWasNeverSampled, observedAt);
        }

        Dictionary<string, QuotedSpread?> quotes = names.ToDictionary(
            name => name,
            name => SpreadCharge.Widest(
                SpreadSnapshotReader.Read(connection, name, sessionDate, sessionDate).Usable
                    .Select(s => new QuotedSpread(s.Pass, s.SpreadBasisPoints!.Value, s.QuoteLagSeconds, s.StraddleSeconds))),
            StringComparer.Ordinal);

        SessionReplayClock clock = SessionReplayClock.ForSession(connection, names, sessionDate, sessionDate);

        List<Holding> live = [.. carried.Select(p => Holding.Carried(p, plans[p.SetupId]))];
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

            // Which names this minute is the session's first for, decided over every name that
            // traded in it rather than over the ones something happens to ask about. A name whose
            // trigger fires at noon has been trading since the open, and asking only at the trigger
            // would call noon its first minute and read an ordinary fill as a gap.
            var firstMinuteOf = new HashSet<string>(StringComparer.Ordinal);

            foreach (string ticker in minute.Bars.Keys)
            {
                if (seen.Add(ticker))
                {
                    firstMinuteOf.Add(ticker);
                }
            }

            // 1. Entries first. A minute holding both a trigger and a give-up point fills and then
            //    stops, which is the pessimistic reading of a bar that carries no order inside it.
            if (byMinute.TryGetValue(minute.OpenedAt, out List<StoredTradeOrder>? triggered))
            {
                foreach (StoredTradeOrder order in triggered)
                {
                    StoredIntradayBar? bar = minute.Of(order.Ticker);

                    if (bar is null)
                    {
                        continue;
                    }

                    StoredTradePlan plan = plans[order.SetupId];
                    Holding? opened = Open(
                        plan, order, bar, firstMinuteOf.Contains(order.Ticker), quotes[order.Ticker],
                        observedAt, writes, tally);
                    filled.Add(order.SetupId);

                    if (opened is not null)
                    {
                        live.Add(opened);
                    }
                }
            }

            // 2. Exits, over everything held, including anything opened a moment ago.
            foreach (Holding holding in live.Where(h => !h.IsClosed).ToArray())
            {
                StoredIntradayBar? bar = minute.Of(holding.Ticker);

                if (bar is null)
                {
                    continue;
                }

                Close(
                    holding, bar, firstMinuteOf.Contains(holding.Ticker), quotes[holding.Ticker],
                    sessionDate, observedAt, writes, tally);
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

            tally.OpenAtEnd = carried.Count;

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
        tally.OpenAtEnd = carried.Count + tally.EntriesFilled - tally.ExitsFilled;

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    /// <summary>
    /// Price an entry, or record why it could not be priced.
    ///
    /// The gap case is decided here rather than in the model, because whether a minute is the
    /// session's first for a name is a fact about the walk and not about a price.
    /// </summary>
    private static Holding? Open(
        StoredTradePlan plan,
        StoredTradeOrder order,
        StoredIntradayBar bar,
        bool firstOfName,
        QuotedSpread? quote,
        DateTimeOffset observedAt,
        List<Action<SqliteTransaction>> writes,
        Tally tally)
    {
        decimal? gapped = firstOfName
            && FillModel.OpenedThrough(plan.Direction, isExit: false, plan.TriggerPrice, bar.Open)
                ? bar.Open
                : null;

        if (gapped is null && quote is null)
        {
            writes.Add(tx => InsertUnfilled(tx, plan, order, NoUsableQuote, observedAt));
            tally.EntriesUnfilled++;
            return null;
        }

        Fill fill = FillModel.Entry(plan.Direction, plan.TriggerPrice, gapped, quote?.BasisPoints ?? 0d);

        string positionId = plan.SetupId;
        string fillId = $"{plan.SetupId}:entry";
        int shares = order.Shares;

        decimal riskIntended = shares * plan.GiveUpDistance;
        decimal riskRealised = shares * Math.Abs(fill.Price - plan.GiveUpPrice);
        decimal value = shares * fill.Price;

        writes.Add(tx =>
        {
            InsertPosition(tx, plan, order, positionId, fillId, bar.OpenedAt, shares, fill.Price,
                value, riskIntended, riskRealised, observedAt);
            InsertFill(tx, plan, positionId, fillId, "entry", bar.SessionDate, bar.OpenedAt,
                plan.TriggerPrice, fill, shares, gapped is null ? quote : quote, observedAt);
        });

        tally.EntriesFilled++;
        tally.Count(fill.Basis);

        return new Holding(
            positionId, plan.SetupId, plan.Ticker, plan.Direction, shares, plan.GiveUpPrice,
            fill.Price, riskRealised, openedThisSession: true);
    }

    /// <summary>Close a holding whose give-up point this minute reached, if it did.</summary>
    private static void Close(
        Holding holding,
        StoredIntradayBar bar,
        bool firstOfName,
        QuotedSpread? quote,
        DateOnly sessionDate,
        DateTimeOffset observedAt,
        List<Action<SqliteTransaction>> writes,
        Tally tally)
    {
        // A gap exit is an overnight jump, so it belongs only to a position that was already held
        // when the session opened. A position entered inside this session cannot have gapped over a
        // price it was not resting behind yet.
        decimal? gapped = firstOfName && !holding.OpenedThisSession
            && FillModel.OpenedThrough(holding.Direction, isExit: true, holding.GiveUpPrice, bar.Open)
                ? bar.Open
                : null;

        if (gapped is null && !TriggerTouch.GaveUp(holding.Direction, holding.GiveUpPrice, bar.High, bar.Low))
        {
            return;
        }

        if (gapped is null && quote is null)
        {
            // Held rather than closed at a price nobody measured. The position stays open and the
            // next session gets another chance to price it, which is the only answer that does not
            // invent a number.
            return;
        }

        Fill fill = FillModel.Exit(holding.Direction, holding.GiveUpPrice, gapped, quote?.BasisPoints ?? 0d);

        decimal perShare = holding.Direction == SetupDirection.Long
            ? fill.Price - holding.EntryPrice
            : holding.EntryPrice - fill.Price;

        decimal pnl = perShare * holding.Shares;
        double realisedR = holding.RiskRealised == 0m ? 0d : (double)(pnl / holding.RiskRealised);

        string fillId = $"{holding.SetupId}:exit";
        holding.IsClosed = true;

        writes.Add(tx =>
        {
            InsertFill(tx, holding, fillId, sessionDate, bar.OpenedAt, holding.GiveUpPrice, fill, quote, observedAt);
            ClosePosition(tx, holding, fillId, sessionDate, bar.OpenedAt, fill.Price, pnl, realisedR, observedAt);
        });

        tally.ExitsFilled++;
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
                position_id, setup_id, order_id, ticker, direction, status, opened_session, opened_at,
                shares, entry_fill_id, entry_price, value_at_entry, fraction_at_entry,
                risk_intended, risk_realised, borrow_rate_assumed, borrow_availability, observed_at)
            VALUES (
                @position_id, @setup_id, @order_id, @ticker, @direction, 'open', @opened_session, @opened_at,
                @shares, @entry_fill_id, @entry_price, @value_at_entry, @fraction_at_entry,
                @risk_intended, @risk_realised, @borrow_rate_assumed, @borrow_availability, @observed_at)
            ON CONFLICT (position_id) DO NOTHING;
            """;

        bool isShort = string.Equals(plan.Direction, SetupDirection.Short, StringComparison.Ordinal);

        command.Parameters.AddWithValue("@position_id", positionId);
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
                position_id, setup_id, order_id, ticker, direction, status, opened_session,
                shares, unfilled_because, borrow_rate_assumed, borrow_availability, observed_at)
            VALUES (
                @position_id, @setup_id, @order_id, @ticker, @direction, 'unfilled', @opened_session,
                0, @unfilled_because, @borrow_rate_assumed, @borrow_availability, @observed_at)
            ON CONFLICT (position_id) DO NOTHING;
            """;

        bool isShort = string.Equals(plan.Direction, SetupDirection.Short, StringComparison.Ordinal);

        command.Parameters.AddWithValue("@position_id", plan.SetupId);
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

    private static void ClosePosition(
        SqliteTransaction transaction,
        Holding holding,
        string fillId,
        DateOnly sessionDate,
        DateTimeOffset closedAt,
        decimal exitPrice,
        decimal pnl,
        double realisedR,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;

        // Guarded on the row still being open, so a rerun of a closed session updates nothing and a
        // second exit for one position is unexpressible rather than merely unwritten.
        command.CommandText = """
            UPDATE position
               SET status = 'closed',
                   closed_session = @closed_session,
                   closed_at = @closed_at,
                   exit_fill_id = @exit_fill_id,
                   exit_price = @exit_price,
                   exit_reason = @exit_reason,
                   realised_pnl = @realised_pnl,
                   realised_r = @realised_r,
                   closed_observed_at = @closed_observed_at
             WHERE position_id = @position_id
               AND status = 'open';
            """;

        command.Parameters.AddWithValue("@closed_session", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@closed_at", StoreText.TimestampToStorageText(closedAt));
        command.Parameters.AddWithValue("@exit_fill_id", fillId);
        command.Parameters.AddWithValue("@exit_price", StoreText.PriceToStorageText(exitPrice));
        command.Parameters.AddWithValue("@exit_reason", GaveUp);
        command.Parameters.AddWithValue("@realised_pnl", StoreText.PriceToStorageText(pnl));
        command.Parameters.AddWithValue("@realised_r", realisedR);
        command.Parameters.AddWithValue("@closed_observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@position_id", holding.PositionId);
        command.ExecuteNonQuery();
    }

    private static void InsertFill(
        SqliteTransaction transaction,
        StoredTradePlan plan,
        string positionId,
        string fillId,
        string leg,
        DateOnly sessionDate,
        DateTimeOffset filledAt,
        decimal restingPrice,
        Fill fill,
        int shares,
        QuotedSpread? quote,
        DateTimeOffset observedAt) =>
        InsertFill(
            transaction, fillId, positionId, plan.SetupId, sessionDate, plan.Ticker, plan.Direction,
            leg, filledAt, restingPrice, fill, shares, quote, observedAt);

    private static void InsertFill(
        SqliteTransaction transaction,
        Holding holding,
        string fillId,
        DateOnly sessionDate,
        DateTimeOffset filledAt,
        decimal restingPrice,
        Fill fill,
        QuotedSpread? quote,
        DateTimeOffset observedAt) =>
        InsertFill(
            transaction, fillId, holding.PositionId, holding.SetupId, sessionDate, holding.Ticker,
            holding.Direction, "exit", filledAt, restingPrice, fill, holding.Shares, quote, observedAt);

    private static void InsertFill(
        SqliteTransaction transaction,
        string fillId,
        string positionId,
        string setupId,
        DateOnly sessionDate,
        string ticker,
        string direction,
        string leg,
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
                fill_id, position_id, setup_id, session_date, ticker, direction, leg, filled_at,
                basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
                quote_lag_seconds, straddle_seconds, observed_at)
            VALUES (
                @fill_id, @position_id, @setup_id, @session_date, @ticker, @direction, @leg, @filled_at,
                @basis, @resting_price, @price, @slippage, @shares, @spread_bps, @spread_pass,
                @quote_lag_seconds, @straddle_seconds, @observed_at)
            ON CONFLICT (fill_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@fill_id", fillId);
        command.Parameters.AddWithValue("@position_id", positionId);
        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@leg", leg);
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
                exits_filled, gapped, slipped, open_at_end, names_walked, minutes_walked,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @open_at_start, @orders_placed, @entries_filled, @entries_unfilled,
                @exits_filled, @gapped, @slipped, @open_at_end, @names_walked, @minutes_walked,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@open_at_start", tally.OpenAtStart);
        command.Parameters.AddWithValue("@orders_placed", tally.OrdersPlaced);
        command.Parameters.AddWithValue("@entries_filled", tally.EntriesFilled);
        command.Parameters.AddWithValue("@entries_unfilled", tally.EntriesUnfilled);
        command.Parameters.AddWithValue("@exits_filled", tally.ExitsFilled);
        command.Parameters.AddWithValue("@gapped", tally.Gapped);
        command.Parameters.AddWithValue("@slipped", tally.Slipped);
        command.Parameters.AddWithValue("@open_at_end", tally.OpenAtEnd);
        command.Parameters.AddWithValue("@names_walked", tally.NamesWalked);
        command.Parameters.AddWithValue("@minutes_walked", tally.MinutesWalked);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// One position as the walk carries it, which is the plan's give-up point and the price the
    /// entry actually got.
    ///
    /// Held rather than re-read because the give-up point is the plan's and never moves, and because
    /// a position opened inside this walk has no store row yet: the writes are deferred to one
    /// transaction so a night is all of a piece.
    /// </summary>
    private sealed class Holding(
        string positionId,
        string setupId,
        string ticker,
        string direction,
        int shares,
        decimal giveUpPrice,
        decimal entryPrice,
        decimal riskRealised,
        bool openedThisSession)
    {
        public string PositionId { get; } = positionId;

        public string SetupId { get; } = setupId;

        public string Ticker { get; } = ticker;

        public string Direction { get; } = direction;

        public int Shares { get; } = shares;

        public decimal GiveUpPrice { get; } = giveUpPrice;

        public decimal EntryPrice { get; } = entryPrice;

        public decimal RiskRealised { get; } = riskRealised;

        /// <summary>Whether the entry happened inside the session being walked, which decides whether a gap exit is available.</summary>
        public bool OpenedThisSession { get; } = openedThisSession;

        public bool IsClosed { get; set; }

        public static Holding Carried(StoredPosition position, StoredTradePlan plan) =>
            new(position.PositionId, position.SetupId, position.Ticker, position.Direction,
                position.Shares, plan.GiveUpPrice, position.EntryPrice!.Value,
                position.RiskRealised!.Value, openedThisSession: false);
    }

    /// <summary>A night's fills counted by what they were and how they were priced.</summary>
    public sealed class Tally
    {
        public int OpenAtStart { get; set; }

        public int OrdersPlaced { get; set; }

        public int EntriesFilled { get; set; }

        public int EntriesUnfilled { get; set; }

        public int ExitsFilled { get; set; }

        public int Gapped { get; private set; }

        public int Slipped { get; private set; }

        public int OpenAtEnd { get; set; }

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

/// <summary>What one run of PaperBroker priced, with the book at both ends of the night.</summary>
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

    public int ExitsFilled => Counts.ExitsFilled;

    public int Gapped => Counts.Gapped;

    public int Slipped => Counts.Slipped;

    public int OpenAtEnd => Counts.OpenAtEnd;

    public int NamesWalked => Counts.NamesWalked;

    public int MinutesWalked => Counts.MinutesWalked;
}
