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
/// What happened to the positions PaperBroker opened: the two rule sets, and every exit.
///
/// <b>Two rule sets and two code paths, which is the deliverable rather than a preference.</b>
/// <see cref="LongExitRules"/> is a daily-series condition evaluated once at the close and acted on
/// the next morning. <see cref="ShortExitRules"/> is an intraday level plus an hourly-close
/// condition, both acted on inside the session. They are not mirror images and one routine with a
/// sign flag could only be their union, which is a strategy nobody trades and the single easiest
/// way to get a convincing answer to the wrong question.
/// see: Long and short are never pooled into one figure
///
/// <b>Every exit is here, including the give-up point, and that is what moved at 4.8.</b> The rule
/// is that the exit is whichever of the give-up point and the rule set is reached first, and a
/// comparison across rules cannot be made by two components each of which sees one side of it.
/// PaperBroker priced the give-up exit while it was the only one; from 4.8 it prices entries and
/// nothing else, so <c>position</c> has one writer per operation rather than two stages that can
/// both close a row.
/// see: Every exit is PositionManager's and every entry is PaperBroker's
///
/// <b>The trail never takes over from the fixed stop.</b> Both are live from the entry fill to the
/// close and neither replaces the other, so nothing here has a handover threshold; what running both
/// needs instead is a total order over the rules that name one minute, and that is
/// <see cref="ExitReason.First"/>. The reasoning is there rather than here because it is a property
/// of the rules and not of the walk.
/// see: Neither exit rule takes over from the other, and a tie inside one minute resolves as a give-up
///
/// <b>It runs after PaperBroker on the same evening and walks the same session again.</b> A position
/// opened at 09:31 can be stopped out at 09:45, so the manager's subject is every position open at
/// any point in the session rather than only the ones carried in. RiskGate ran before both at 21:10
/// and read the book as it stood coming into the session, so such a position still occupied a slot
/// the 10:00 trigger was refused on. That is not repaired here, because repairing it means merging
/// the gate into the walk and giving orders a second writer; it is counted instead, on the night, as
/// <c>closed_in_their_own_session</c>.
/// see: RiskGate reads the book as it stood coming into the session, and what that costs is counted
///
/// <b>A name the session quoted no usable book for is held rather than closed.</b> The position stays
/// open and the next session gets another chance to price it, which is the only answer that does not
/// invent a number. A gap is exempt, because a gap fill is an open the store holds and charges no
/// spread at all.
/// see: A fill with no usable quote for its name is refused and recorded, never charged nought
/// </summary>
public sealed class PositionManager
{
    public const string Name = "manage";

    /// <summary>No position was open at any point in this session, so there was nothing to manage.</summary>
    public const string NothingToManage = "no position was open at any point in this session";

    /// <summary>The session holds no stored minute for any name with a position in it.</summary>
    public const string SessionHeldNoMinutes =
        "the store holds no minute of this session for any name with a position in it";

    /// <summary>Neither spread pass ran, so no exit in this session can be charged the spread it owes.</summary>
    public const string SessionWasNeverSampled =
        "no spread pass was recorded for this session, so no exit in it can be charged the spread it owes";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public PositionManager(
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

        ManageRunResult result = Manage(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.OpenAtStart} position(s) to manage, "
            + $"{result.LongsManaged} long and {result.ShortsManaged} short");
        Console.WriteLine(
            $"{Name}: {result.ClosedGiveUp} closed on the give-up point, {result.ClosedTrail} on the trail, "
            + $"{result.ClosedReclaim} on an hourly reclaim");
        Console.WriteLine(
            $"{Name}: {result.Trimmed} trimmed at 3R, {result.ExitsArmed} exit(s) armed for the next open, "
            + $"{result.HeldNoQuote} held because the session quoted no book");
        Console.WriteLine(
            $"{Name}: {result.Slipped} charged the captured spread, {result.Gapped} filled at an open "
            + "and charged nothing");
        Console.WriteLine(
            $"{Name}: {result.ClosedInTheirOwnSession} closed in the session they opened in, which the caps "
            + $"could not see; {result.OpenAtEnd} position(s) open at the end");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Run both rule sets over <paramref name="sessionDate"/>.
    ///
    /// Idempotent: every update is guarded on the state it changes, so a close applies only to a row
    /// this run still reads as open and a trim only to one that has not been trimmed. A rerun over a
    /// managed session writes nothing.
    /// </summary>
    public ManageRunResult Manage(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "position", "fill", "manage_run");

        DateTimeOffset observedAt = run.StartedAt;
        var tally = new Tally();

        StoredPosition[] open =
            [.. PositionReader.OpenDuring(connection, sessionDate, sessionDate)
                .Where(p => string.Equals(p.Status, PositionStatus.Open, StringComparison.Ordinal))];

        tally.OpenAtStart = open.Length;
        tally.LongsManaged = open.Count(p => string.Equals(p.Direction, SetupDirection.Long, StringComparison.Ordinal));
        tally.ShortsManaged = open.Length - tally.LongsManaged;

        if (open.Length == 0)
        {
            return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, NothingToManage, observedAt);
        }

        Dictionary<string, StoredTradePlan> plans = TradePlanReader
            .ForSetups(connection, [.. open.Select(p => p.SetupId)], sessionDate)
            .ToDictionary(p => p.SetupId, StringComparer.Ordinal);

        string[] names =
            [.. open.Select(p => p.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        SessionSampling sampling = SpreadSnapshotReader.SamplingOf(connection, sessionDate, sessionDate);

        if (sampling.IsUnsampled)
        {
            // Nothing closes and nothing is armed. Partial rather than failed, on the terms
            // PaperBroker reports one: the stage did its whole job over a session whose evidence is
            // missing, and an exit charged no slippage on a session nobody measured is the silently
            // wrong result this outcome exists to keep out of the record.
            tally.OpenAtEnd = open.Length;

            return Complete(connection, run, sessionDate, tally, RunOutcome.Partial, SessionWasNeverSampled, observedAt);
        }

        Dictionary<string, QuotedSpread?> quotes = names.ToDictionary(
            name => name,
            name => SpreadCharge.Widest(
                SpreadSnapshotReader.Read(connection, name, sessionDate, sessionDate).Usable
                    .Select(s => new QuotedSpread(s.Pass, s.SpreadBasisPoints!.Value, s.QuoteLagSeconds, s.StraddleSeconds))),
            StringComparer.Ordinal);

        // The 50-day average as it stood before this session, and the factor that puts a printed
        // price on the basis it is computed on. Read once a name rather than once a minute, and
        // strictly before this session on both halves, because an hourly bar at 11:30 cannot be
        // measured against an average computed from the 16:00 close.
        Dictionary<string, Reclaim?> reclaimAgainst = names.ToDictionary(
            name => name,
            name => ReclaimLevelOf(connection, name, sessionDate),
            StringComparer.Ordinal);

        SessionReplayClock clock = SessionReplayClock.ForSession(connection, names, sessionDate, sessionDate);

        List<Holding> live = [.. open.Select(p => Holding.From(p, plans[p.SetupId]))];
        var writes = new List<Action<SqliteTransaction>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hourOf = new Dictionary<string, int?>(StringComparer.Ordinal);
        var lastBarOfHour = new Dictionary<string, StoredIntradayBar>(StringComparer.Ordinal);
        int minutesWalked = 0;

        foreach (ReplayMinute minute in clock.Walk())
        {
            minutesWalked++;

            // Both of these are facts about the minute rather than about any one position, so they
            // are decided once over every name that traded in it. Two positions can share a ticker,
            // and asking each of them separately would give the second one a different answer to
            // the same question.
            var firstMinuteOf = new HashSet<string>(StringComparer.Ordinal);
            var reclaimedNow = new HashSet<string>(StringComparer.Ordinal);

            foreach ((string ticker, StoredIntradayBar traded) in minute.Bars)
            {
                if (seen.Add(ticker))
                {
                    firstMinuteOf.Add(ticker);
                }

                // The hourly grid, closed out one bar behind: an hourly close is only known once a
                // minute of the next hour has printed, and the fill is at that minute's open, which
                // is the next price after the close and the same mechanic the trail takes from the
                // daily series.
                if (ClosedTheHourAboveTheAverage(
                        ticker, traded, sessionDate, hourOf, lastBarOfHour, reclaimAgainst[ticker]))
                {
                    reclaimedNow.Add(ticker);
                }
            }

            foreach (Holding holding in live.Where(h => !h.IsClosed).ToArray())
            {
                StoredIntradayBar? maybe = minute.Of(holding.Ticker);

                if (maybe is not StoredIntradayBar bar)
                {
                    continue;
                }

                if (!holding.IsLong && holding.ArmedReason is null && reclaimedNow.Contains(holding.Ticker))
                {
                    holding.ArmFor(ExitReason.Reclaim);
                }

                QuotedSpread? quote = quotes[holding.Ticker];
                ExitCandidate? exit = ExitReason.First(
                    CandidatesAt(holding, bar, firstMinuteOf.Contains(holding.Ticker)));

                if (exit is not null)
                {
                    Close(holding, exit, bar, quote, sessionDate, observedAt, writes, tally);
                }
                else if (holding.TrimIsAvailable && TriggerTouch.Reached(
                             SetupDirection.Short, holding.TrimLevel!.Value, bar.High, bar.Low))
                {
                    Trim(holding, bar, quote, observedAt, writes, tally);
                }
            }
        }

        if (minutesWalked == 0)
        {
            // A session with positions in it and no stored minute is partial, on the terms the
            // resolver and PaperBroker both report one. Nothing is armed either: the arming reads a
            // daily close, and a night the store is blind on is one whose figures nothing should
            // act on.
            tally.OpenAtEnd = open.Length;

            return Complete(connection, run, sessionDate, tally, RunOutcome.Partial, SessionHeldNoMinutes, observedAt);
        }

        // Arm the trail on the session's own close, for every long still open. This is the last
        // thing the stage does, because a long closed inside the session has no next open to be
        // exited at and arming it would leave an instruction against a closed row.
        foreach (Holding holding in live.Where(h => !h.IsClosed && h.IsLong && h.ArmedReason is null))
        {
            if (!ArmTheTrail(connection, holding, sessionDate))
            {
                continue;
            }

            Holding armed = holding;
            writes.Add(tx => ArmExit(tx, armed.PositionId, sessionDate, ExitReason.Trail));
            tally.ExitsArmed++;
        }

        // An arm the walk raised and could not fill, because the store held no later minute of the
        // session. It fills at the next session's open, which is the same answer the trail takes.
        foreach (Holding holding in live.Where(h => !h.IsClosed && h.PendingReclaim))
        {
            Holding armed = holding;
            writes.Add(tx => ArmExit(tx, armed.PositionId, sessionDate, ExitReason.Reclaim));
            tally.ExitsArmed++;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (Action<SqliteTransaction> write in writes)
        {
            write(transaction);
        }

        transaction.Commit();

        tally.NamesWalked = seen.Count;
        tally.MinutesWalked = minutesWalked;
        tally.OpenAtEnd = live.Count(h => !h.IsClosed);

        return Complete(connection, run, sessionDate, tally, RunOutcome.Clean, null, observedAt);
    }

    /// <summary>
    /// Every rule saying this minute ended the position.
    ///
    /// The give-up point appears twice on purpose: once as a gap, where the bar opened past it, and
    /// once as a touch inside the bar. They are the same reason at different instants and
    /// <see cref="ExitReason.First"/> is what puts the earlier one first, rather than an ordering
    /// implied by which branch this method happens to take.
    /// </summary>
    private static IEnumerable<ExitCandidate> CandidatesAt(
        Holding holding, StoredIntradayBar bar, bool firstOfSession)
    {
        // The give-up price and not the open, on both of these. A candidate names the price its rule
        // named; what the fill actually got is the model's answer, and a candidate carrying the open
        // would have handed the model an exit price it was also being told the session opened
        // through, which is not a gap at all.
        if (FillModel.OpenedThrough(holding.Direction, isExit: true, holding.GiveUpPrice, bar.Open))
        {
            yield return new ExitCandidate(ExitReason.GaveUp, holding.GiveUpPrice, AtTheOpen: true);
        }

        // An exit armed in an earlier session fires at this name's first minute of this one; an exit
        // armed inside this session fires at the minute the walk armed it for, which is this one.
        if (holding.ArmedReason is string reason
            && (!holding.ArmedInAnEarlierSession || firstOfSession))
        {
            yield return new ExitCandidate(reason, bar.Open, AtTheOpen: true);
        }

        if (TriggerTouch.GaveUp(holding.Direction, holding.GiveUpPrice, bar.High, bar.Low))
        {
            yield return new ExitCandidate(ExitReason.GaveUp, holding.GiveUpPrice, AtTheOpen: false);
        }
    }

    /// <summary>
    /// Whether the hourly bar that ended before <paramref name="bar"/> closed back above the 50-day
    /// average, which arms the short exit for this minute's open.
    ///
    /// The stub is not an hourly bar, so its close never reaches this: <see cref="HourlyGrid"/>
    /// returns null for it, and null is not a completed hour to compare against
    /// (see: The hourly grid anchors to the session open, and the closing stub is not an hourly bar).
    /// </summary>
    private static bool ClosedTheHourAboveTheAverage(
        string ticker,
        StoredIntradayBar bar,
        DateOnly sessionDate,
        Dictionary<string, int?> hourOf,
        Dictionary<string, StoredIntradayBar> lastBarOfHour,
        Reclaim? against)
    {
        int? hour = HourlyGrid.BarIndexOf(bar.OpenedAt, sessionDate, SessionBoundaries.UsEquities);

        bool reclaimed = hourOf.TryGetValue(ticker, out int? previous)
            && previous is not null
            && previous != hour
            && lastBarOfHour.TryGetValue(ticker, out StoredIntradayBar? closed)
            && against is not null
            && ShortExitRules.Reclaimed(closed.Close * against.Factor, against.FiftyDayAverage);

        hourOf[ticker] = hour;
        lastBarOfHour[ticker] = bar;

        return reclaimed;
    }

    /// <summary>
    /// The 50-day average this session's hourly closes are measured against, with the factor that
    /// puts a printed price on the basis it is computed on, or null where the store holds neither.
    ///
    /// Null rather than a stand-in. An average approximated from what is to hand is a number that
    /// looks like the real thing inside the rule deciding whether a short is over, which is the
    /// refusal <c>reached-ceiling</c> already carries one level up.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    private static Reclaim? ReclaimLevelOf(SqliteConnection connection, string ticker, DateOnly sessionDate)
    {
        StoredIndicators? indicators =
            IndicatorDailyReader.LatestBefore(connection, ticker, sessionDate, sessionDate);

        if (indicators is null)
        {
            return null;
        }

        StoredDailyBar? bar = DailyBarReader.Latest(
            connection,
            ticker,
            indicators.AsOf,
            StoreText.StorageTextToTimestamp(StoreText.EndOfSession(sessionDate, SessionBoundaries.UsEquities)));

        return bar is null
            ? null
            : new Reclaim(
                indicators.EmaLong,
                ShortExitRules.AdjustmentFactor(bar.Close, bar.AdjustedClose));
    }

    /// <summary>Whether this session's close arms the long trail, read on the adjusted basis at both ends.</summary>
    private static bool ArmTheTrail(SqliteConnection connection, Holding holding, DateOnly sessionDate)
    {
        StoredIndicators? indicators =
            IndicatorDailyReader.Read(connection, holding.Ticker, sessionDate, sessionDate);

        StoredDailyBar? bar = DailyBarReader.Latest(
            connection,
            holding.Ticker,
            sessionDate,
            StoreText.StorageTextToTimestamp(StoreText.EndOfSession(sessionDate, SessionBoundaries.UsEquities)));

        return indicators is not null
            && bar is not null
            && LongExitRules.TrailArmedBy(bar.AdjustedClose, indicators.EmaShort);
    }

    /// <summary>Close a holding at the price the winning rule named.</summary>
    private static void Close(
        Holding holding,
        ExitCandidate exit,
        StoredIntradayBar bar,
        QuotedSpread? quote,
        DateOnly sessionDate,
        DateTimeOffset observedAt,
        List<Action<SqliteTransaction>> writes,
        Tally tally)
    {
        bool gapped = string.Equals(exit.Reason, ExitReason.GaveUp, StringComparison.Ordinal) && exit.AtTheOpen;

        if (!gapped && quote is null)
        {
            // Held rather than closed at a price nobody measured. The position stays open and the
            // next session gets another chance to price it, which is the only answer that does not
            // invent a number. Counted once per position and not once per minute: a name with no
            // quote has none for the whole session, so a per-minute count would report the length of
            // the hold rather than the number of them.
            holding.CountHeldForNoQuote(tally);
            return;
        }

        Fill fill = FillModel.Exit(
            holding.Direction,
            exit.RestingPrice,
            gapped ? bar.Open : null,
            quote?.BasisPoints ?? 0d);

        decimal perShare = holding.IsLong
            ? fill.Price - holding.EntryPrice
            : holding.EntryPrice - fill.Price;

        int shares = holding.SharesRemaining;
        decimal pnl = (perShare * shares) + holding.TrimRealisedPnl;
        double realisedR = holding.RiskRealised == 0m ? 0d : (double)(pnl / holding.RiskRealised);

        string fillId = $"{holding.SetupId}:exit";
        holding.IsClosed = true;

        writes.Add(tx =>
        {
            InsertFill(tx, holding, fillId, "exit", sessionDate, bar.OpenedAt, exit.RestingPrice, fill, shares, quote, observedAt);
            ClosePosition(tx, holding, fillId, sessionDate, bar.OpenedAt, fill.Price, exit.Reason, pnl, realisedR, observedAt);
        });

        tally.Count(exit.Reason);
        tally.CountBasis(fill.Basis);

        if (holding.OpenedSession == sessionDate)
        {
            tally.ClosedInTheirOwnSession++;
        }
    }

    /// <summary>
    /// Take the short trim, which reduces the position and leaves it open.
    ///
    /// <b>Filled at the trim level and charged the whole spread, never at a better open.</b> A bar
    /// that opens past the trim level has opened in the position's favour, and taking that open
    /// would price a fill better than a resting instruction could have got. The gap rule is for an
    /// open that is past a price the wrong way, and this is the other way.
    /// </summary>
    private static void Trim(
        Holding holding,
        StoredIntradayBar bar,
        QuotedSpread? quote,
        DateTimeOffset observedAt,
        List<Action<SqliteTransaction>> writes,
        Tally tally)
    {
        if (quote is null)
        {
            holding.CountHeldForNoQuote(tally);
            return;
        }

        int shares = ShortExitRules.TrimShares(holding.PlannedShares, holding.SharesRemaining);

        if (shares == 0)
        {
            return;
        }

        Fill fill = FillModel.Exit(
            SetupDirection.Short, holding.TrimLevel!.Value, openedThrough: null, quote.BasisPoints);

        decimal pnl = (holding.EntryPrice - fill.Price) * shares;
        string fillId = $"{holding.SetupId}:trim";

        holding.RecordTrim(shares, pnl);

        writes.Add(tx =>
        {
            InsertFill(tx, holding, fillId, "trim", bar.SessionDate, bar.OpenedAt, holding.TrimLevel!.Value, fill, shares, quote, observedAt);
            TrimPosition(tx, holding.PositionId, fillId, bar.OpenedAt, shares, fill.Price, pnl, observedAt);
        });

        tally.Trimmed++;
        tally.CountBasis(fill.Basis);
    }

    private static void ClosePosition(
        SqliteTransaction transaction,
        Holding holding,
        string fillId,
        DateOnly sessionDate,
        DateTimeOffset closedAt,
        decimal exitPrice,
        string reason,
        decimal pnl,
        double realisedR,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;

        // Guarded on the row still being open, so a rerun of a managed session updates nothing and a
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
        command.Parameters.AddWithValue("@exit_reason", reason);
        command.Parameters.AddWithValue("@realised_pnl", StoreText.PriceToStorageText(pnl));
        command.Parameters.AddWithValue("@realised_r", realisedR);
        command.Parameters.AddWithValue("@closed_observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@position_id", holding.PositionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Record the trim, guarded so a rerun cannot trim a second time.</summary>
    private static void TrimPosition(
        SqliteTransaction transaction,
        string positionId,
        string fillId,
        DateTimeOffset trimmedAt,
        int shares,
        decimal price,
        decimal pnl,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE position
               SET trim_fill_id = @trim_fill_id,
                   trimmed_at = @trimmed_at,
                   trimmed_shares = @trimmed_shares,
                   trim_price = @trim_price,
                   trim_realised_pnl = @trim_realised_pnl,
                   trim_observed_at = @trim_observed_at
             WHERE position_id = @position_id
               AND status = 'open'
               AND trim_fill_id IS NULL;
            """;

        command.Parameters.AddWithValue("@trim_fill_id", fillId);
        command.Parameters.AddWithValue("@trimmed_at", StoreText.TimestampToStorageText(trimmedAt));
        command.Parameters.AddWithValue("@trimmed_shares", shares);
        command.Parameters.AddWithValue("@trim_price", StoreText.PriceToStorageText(price));
        command.Parameters.AddWithValue("@trim_realised_pnl", StoreText.PriceToStorageText(pnl));
        command.Parameters.AddWithValue("@trim_observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@position_id", positionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Record an exit decided in this session and filled at the open of the next.</summary>
    private static void ArmExit(
        SqliteTransaction transaction, string positionId, DateOnly session, string reason)
    {
        using SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE position
               SET exit_armed_session = @exit_armed_session,
                   exit_armed_reason = @exit_armed_reason
             WHERE position_id = @position_id
               AND status = 'open'
               AND exit_armed_session IS NULL;
            """;

        command.Parameters.AddWithValue("@exit_armed_session", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@exit_armed_reason", reason);
        command.Parameters.AddWithValue("@position_id", positionId);
        command.ExecuteNonQuery();
    }

    private static void InsertFill(
        SqliteTransaction transaction,
        Holding holding,
        string fillId,
        string leg,
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
        command.Parameters.AddWithValue("@position_id", holding.PositionId);
        command.Parameters.AddWithValue("@setup_id", holding.SetupId);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@ticker", holding.Ticker);
        command.Parameters.AddWithValue("@direction", holding.Direction);
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

    private static ManageRunResult Complete(
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

        return new ManageRunResult(sessionDate, tally, summary.RowsWritten, outcome, because);
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
            INSERT INTO manage_run (
                session_date, open_at_start, longs_managed, shorts_managed, closed_give_up,
                closed_trail, closed_reclaim, trimmed, exits_armed, gapped, slipped, held_no_quote,
                closed_in_their_own_session, open_at_end, names_walked, minutes_walked,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @open_at_start, @longs_managed, @shorts_managed, @closed_give_up,
                @closed_trail, @closed_reclaim, @trimmed, @exits_armed, @gapped, @slipped, @held_no_quote,
                @closed_in_their_own_session, @open_at_end, @names_walked, @minutes_walked,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@open_at_start", tally.OpenAtStart);
        command.Parameters.AddWithValue("@longs_managed", tally.LongsManaged);
        command.Parameters.AddWithValue("@shorts_managed", tally.ShortsManaged);
        command.Parameters.AddWithValue("@closed_give_up", tally.ClosedGiveUp);
        command.Parameters.AddWithValue("@closed_trail", tally.ClosedTrail);
        command.Parameters.AddWithValue("@closed_reclaim", tally.ClosedReclaim);
        command.Parameters.AddWithValue("@trimmed", tally.Trimmed);
        command.Parameters.AddWithValue("@exits_armed", tally.ExitsArmed);
        command.Parameters.AddWithValue("@gapped", tally.Gapped);
        command.Parameters.AddWithValue("@slipped", tally.Slipped);
        command.Parameters.AddWithValue("@held_no_quote", tally.HeldNoQuote);
        command.Parameters.AddWithValue("@closed_in_their_own_session", tally.ClosedInTheirOwnSession);
        command.Parameters.AddWithValue("@open_at_end", tally.OpenAtEnd);
        command.Parameters.AddWithValue("@names_walked", tally.NamesWalked);
        command.Parameters.AddWithValue("@minutes_walked", tally.MinutesWalked);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>The 50-day average a short is measured against, and what puts a printed price on its basis.</summary>
    private sealed record Reclaim(decimal FiftyDayAverage, decimal Factor);

    /// <summary>
    /// One position as the walk carries it: the plan's give-up point, the trim level derived from
    /// the price the entry actually got, and whatever the walk has already done to it.
    ///
    /// Held rather than re-read, because the writes are deferred to one transaction so a night is
    /// all of a piece and a trim taken at 10:00 has to be visible to a close at 14:00.
    /// </summary>
    private sealed class Holding
    {
        private bool _heldForNoQuote;

        private Holding(
            string positionId,
            string setupId,
            string ticker,
            string direction,
            DateOnly openedSession,
            int shares,
            int plannedShares,
            decimal giveUpPrice,
            decimal entryPrice,
            decimal riskRealised,
            decimal? trimLevel,
            int trimmedShares,
            decimal trimRealisedPnl,
            string? armedReason)
        {
            PositionId = positionId;
            SetupId = setupId;
            Ticker = ticker;
            Direction = direction;
            OpenedSession = openedSession;
            Shares = shares;
            PlannedShares = plannedShares;
            GiveUpPrice = giveUpPrice;
            EntryPrice = entryPrice;
            RiskRealised = riskRealised;
            TrimLevel = trimLevel;
            TrimmedShares = trimmedShares;
            TrimRealisedPnl = trimRealisedPnl;
            ArmedReason = armedReason;
            ArmedInAnEarlierSession = armedReason is not null;
        }

        public string PositionId { get; }

        public string SetupId { get; }

        public string Ticker { get; }

        public string Direction { get; }

        public DateOnly OpenedSession { get; }

        public int Shares { get; }

        public int PlannedShares { get; }

        public decimal GiveUpPrice { get; }

        public decimal EntryPrice { get; }

        public decimal RiskRealised { get; }

        /// <summary>The 3R level, on the short side only. Null on a long, which has no trim rule.</summary>
        public decimal? TrimLevel { get; }

        public int TrimmedShares { get; private set; }

        public decimal TrimRealisedPnl { get; private set; }

        /// <summary>Which rule has an exit armed against this position, or null where none has.</summary>
        public string? ArmedReason { get; private set; }

        /// <summary>Whether the arming came in from a previous session, which decides the minute it fires in.</summary>
        public bool ArmedInAnEarlierSession { get; }

        public bool IsClosed { get; set; }

        public bool IsLong => string.Equals(Direction, SetupDirection.Long, StringComparison.Ordinal);

        public int SharesRemaining => Shares - TrimmedShares;

        /// <summary>Whether the trim rule can still fire: a short, with a level, not yet trimmed.</summary>
        public bool TrimIsAvailable => TrimLevel is not null && TrimmedShares == 0;

        /// <summary>An arming this walk raised that no later minute of this session could fill.</summary>
        public bool PendingReclaim => !ArmedInAnEarlierSession && ArmedReason is not null;

        public void ArmFor(string reason) => ArmedReason = reason;

        /// <summary>Count this position among the night's holds, once however many minutes it lasts.</summary>
        public void CountHeldForNoQuote(Tally tally)
        {
            ArgumentNullException.ThrowIfNull(tally);

            if (_heldForNoQuote)
            {
                return;
            }

            _heldForNoQuote = true;
            tally.HeldNoQuote++;
        }

        public void RecordTrim(int shares, decimal pnl)
        {
            TrimmedShares = shares;
            TrimRealisedPnl = pnl;
        }

        public static Holding From(StoredPosition position, StoredTradePlan plan)
        {
            bool isShort = string.Equals(position.Direction, SetupDirection.Short, StringComparison.Ordinal);
            decimal entryPrice = position.EntryPrice!.Value;

            return new Holding(
                position.PositionId,
                position.SetupId,
                position.Ticker,
                position.Direction,
                position.OpenedSession,
                position.Shares,
                plan.Shares,
                plan.GiveUpPrice,
                entryPrice,
                position.RiskRealised!.Value,
                isShort && plan.GiveUpPrice > entryPrice
                    ? ShortExitRules.TrimLevel(entryPrice, plan.GiveUpPrice)
                    : null,
                position.TrimmedShares ?? 0,
                position.TrimRealisedPnl ?? 0m,
                position.ExitArmedReason);
        }
    }

    /// <summary>A night's exits counted by the rule that produced them and by how they were priced.</summary>
    public sealed class Tally
    {
        public int OpenAtStart { get; set; }

        public int LongsManaged { get; set; }

        public int ShortsManaged { get; set; }

        public int ClosedGiveUp { get; private set; }

        public int ClosedTrail { get; private set; }

        public int ClosedReclaim { get; private set; }

        public int Trimmed { get; set; }

        public int ExitsArmed { get; set; }

        public int Gapped { get; private set; }

        public int Slipped { get; private set; }

        public int HeldNoQuote { get; set; }

        public int ClosedInTheirOwnSession { get; set; }

        public int OpenAtEnd { get; set; }

        public int NamesWalked { get; set; }

        public int MinutesWalked { get; set; }

        /// <summary>
        /// One close, counted under the rule that produced it.
        ///
        /// Refused rather than defaulted for an unknown reason, so a fourth rule added later fails
        /// here instead of being absorbed into whichever counter the switch fell through to.
        /// </summary>
        public void Count(string reason)
        {
            switch (reason)
            {
                case ExitReason.GaveUp:
                    ClosedGiveUp++;
                    return;
                case ExitReason.Trail:
                    ClosedTrail++;
                    return;
                case ExitReason.Reclaim:
                    ClosedReclaim++;
                    return;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(reason),
                        $"'{reason}' is not one of the {ExitReason.ThatCloseAPosition.Count} reasons that "
                        + "close a position, so the night's row has no column for it and a close counted "
                        + "nowhere would leave the totals disagreeing with the rows.");
            }
        }

        public void CountBasis(string basis)
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

/// <summary>What one run of PositionManager did, with the book at the end of the night.</summary>
public sealed record ManageRunResult(
    DateOnly SessionDate,
    PositionManager.Tally Counts,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    public int OpenAtStart => Counts.OpenAtStart;

    public int LongsManaged => Counts.LongsManaged;

    public int ShortsManaged => Counts.ShortsManaged;

    public int ClosedGiveUp => Counts.ClosedGiveUp;

    public int ClosedTrail => Counts.ClosedTrail;

    public int ClosedReclaim => Counts.ClosedReclaim;

    public int Trimmed => Counts.Trimmed;

    public int ExitsArmed => Counts.ExitsArmed;

    public int Gapped => Counts.Gapped;

    public int Slipped => Counts.Slipped;

    public int HeldNoQuote => Counts.HeldNoQuote;

    public int ClosedInTheirOwnSession => Counts.ClosedInTheirOwnSession;

    public int OpenAtEnd => Counts.OpenAtEnd;

    public int NamesWalked => Counts.NamesWalked;

    public int MinutesWalked => Counts.MinutesWalked;
}
