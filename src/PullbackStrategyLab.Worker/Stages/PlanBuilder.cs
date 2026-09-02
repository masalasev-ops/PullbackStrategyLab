using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// One committed instruction per capped candidate: enter here, give up here, this many shares.
///
/// <b>Declared in SCHEMA since phase 4 was planned and built by no checkpoint until 4.16.</b> The
/// catalogue slots it at 18:30, the runbook reserves the slot, and the phase built PlanAudit without
/// it, so as written it built an auditor of a thing it never built.
/// see: The plan is written before the session and is immutable after publication
///
/// <b>This stage sizes, and the size it writes is authoritative.</b> RiskGate at 4.6 may reduce a
/// size or block the order and never recomputes one. Three places in the corpus answered this
/// differently: the vocabulary calls a plan a committed instruction naming this many shares, the
/// catalogue gives sizing to the component that runs on trigger in the following session, and 4.1's
/// watchlist renders no share count because it was waiting for that component. The plan is locked
/// before the open and the watchlist publishes it at 18:40, so a size has to exist by then; and
/// recomputing at trigger would leave `plan_audit` comparing two of this lab's own numbers rather
/// than an intention against an outcome.
/// see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
///
/// <b>The population is the rows the cap kept, and 2.11's row can be read as saying otherwise.</b>
/// That row settled for this phase that "the plan is written against flagged setups rather than
/// passing ones, which is what 4.1 renders in any case". Read as a statement about this stage it
/// would mean a committed instruction for every flagged name, including ones a gate refused. Read as
/// a statement about what the phase is designed around, which the clause after it is, it means the
/// surfaces and the records cover the flagged population rather than assuming the passing one is
/// non-empty. The second reading is taken, for three reasons: 4.16's own row says one plan per
/// capped candidate, the catalogue says per version per candidate, and a plan is an instruction to
/// trade, so writing one for a setup the lab has already declined would put RiskGate in the position
/// of blocking every order the lab ever placed. The first reading is not obviously wrong and the
/// ambiguity is recorded rather than resolved silently.
///
/// <b>The consequence is that this stage plans nothing on almost every night, and that is the
/// finding rather than a fault.</b> The funnel passes a median of nought candidates a night on both
/// sides. `capped_out` is written only by SetupCapper and only over rows that passed every gating
/// check, so on a night with no candidate there is nothing here to plan, and the run row says which
/// of the three shapes of nothing it was.
///
/// <b>The order prices are the final pullback session's regular-hours extremes with the give-up
/// point 0.1 ADR beyond, and until 4.18 they were the screening geometry.</b> This stage copied
/// `setup.trigger_price` and `setup.stop_price` into the plan from 4.16, which is the low of the
/// whole dip and the reading the order-price decision names as the one to refuse, and its entry
/// did not say so. The 4.13 sign-off found it by reading the stage against the decision. The
/// derivation is <see cref="OrderPrices"/>, read from the session's daily bar rather than its
/// minutes, because the vendor's daily bar carries the regular-hours extremes and the minutes are
/// not in the store at 18:30; the reasoning and the measurement are on that type.
/// see: The order prices are derived from the final pullback session's minutes, not from the screening geometry
///
/// <b>A setup with no trade geometry gets no plan.</b> Not a plan sized on nought: a give-up
/// distance of nought divides into the risk budget as many times as you like, and the share count
/// that comes back is a number with nothing behind it. Two shapes reach this stage and both are
/// refused, counted apart because only one of them is the defect the 3.15 obligation named. An
/// absent price is the shape migration 031 made expressible. An equal pair is the shape that
/// survived it, where the thrust has not pulled back yet so the entry level and the give-up point
/// are the same price and two of the four columns still state a number.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
///
/// <b>`live_session` is the next weekday and the limitation is stated rather than hidden.</b> A plan
/// written on the evening of N is live in the next session, and on that evening nothing in this lab
/// knows whether the next weekday is a trading day: the store holds bars for sessions that have
/// happened and no holiday calendar exists anywhere in the corpus. Inventing one here would be
/// authoring a market calendar rather than recording one. So a plan written before a holiday carries
/// that holiday as its live session and resolves against nothing, which is a plan that does not fire
/// rather than a plan that fires on the wrong day. It is carried as an obligation due at 4.5, which
/// is the first component that reads this column.
/// </summary>
public sealed class PlanBuilder
{
    public const string Name = "plans";

    /// <summary>Nothing was flagged, so there was never a candidate list to cap or to plan from.</summary>
    public const string NothingFlagged = "no setup was flagged for this session";

    /// <summary>The night was never capped, so there is no candidate list to plan from.</summary>
    public const string NeverCapped =
        "no setup of this session carries a cap decision, so the night was never capped";

    /// <summary>The cap ran and kept nobody, which is an ordinary outcome of the gates.</summary>
    public const string AllCappedOut = "every flagged setup was capped out";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public PlanBuilder(
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

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        PlanRunResult result = Build(asOf);

        Console.WriteLine(
            $"{Name}: evening of {result.AsOf:yyyy-MM-dd}, plans live in {result.LiveSession:yyyy-MM-dd}");
        Console.WriteLine(
            $"{Name}: {result.Candidates} capped candidate(s), {result.Planned} planned, "
            + $"{result.Refused} refused");
        Console.WriteLine(
            $"{Name}: refused {result.RefusedAbsentGeometry} for absent geometry, "
            + $"{result.RefusedEqualPrices} for an equal trigger and give-up point, "
            + $"{result.RefusedBelowOneShare} because the risk budget buys under one share");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Write one plan per capped candidate of the evening of <paramref name="asOf"/>.
    ///
    /// Idempotent: the insert takes the store's own key and does nothing on conflict, so a rerun of
    /// the same evening writes no row. The key is the setup, which is what makes a second plan for
    /// one candidate unexpressible rather than merely unwritten.
    /// </summary>
    public PlanRunResult Build(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "trade_plan", "plan_run");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly liveSession = NextWeekday(asOf);

        // Three ways a night produces no plan and only one of them is worth waking anybody for, which
        // is the same ladder WatchlistPublisher reads over the same population. Nothing flagged is a
        // pipeline that did not run; nothing carrying a cap decision is a cap that did not run; a cap
        // that ran and kept nobody is an ordinary outcome of the gates and is most nights.
        IReadOnlyList<StoredSetup> flagged = SetupReader.Read(connection, asOf);

        // <b>The population is exactly the rows the cap kept, and this stage does not re-derive it.</b>
        // `capped_out` is written only by SetupCapper and only over rows that passed every gating
        // check, so a plan is written for a row another component decided was tradeable. Reading
        // `passed_all` here as well would be a second implementation of the gate list, and the two
        // could disagree with nothing reading both.
        IReadOnlyList<StoredSetup> capped = [.. flagged.Where(s => s.CappedOut == false)];

        string? stoppedBecause = flagged.Count == 0
            ? NothingFlagged
            : capped.Count == 0
                ? flagged.Any(s => s.CappedOut == true) ? AllCappedOut : NeverCapped
                : null;

        int planned = 0;
        int absentGeometry = 0;
        int equalPrices = 0;
        int belowOneShare = 0;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (StoredSetup setup in capped)
        {
            // The setup's own pair is read for one thing only: whether there is a pullback to plan
            // against. Both prices absent is a detector that could not compute a geometry; both
            // present and equal is a thrust that has not pulled back. Neither gets a plan, and the
            // two are counted apart because only the second is the one the 3.15 obligation named.
            // The pair is not the order prices, which is what this stage got wrong until 4.18.
            if (PositionSizing.GiveUpDistanceOf(setup.TriggerPrice, setup.StopPrice) is null)
            {
                if (setup.TriggerPrice is null || setup.StopPrice is null)
                {
                    absentGeometry++;
                }
                else
                {
                    equalPrices++;
                }

                continue;
            }

            // The order prices, from the final pullback session's regular-hours extremes and the
            // name's average daily range, both read from the store as they stood on this evening.
            // A candidate whose bar or range the store does not hold is refused as an absent
            // geometry rather than planned on a stand-in, which cannot happen to a row the detector
            // flagged from those same figures and is counted where it would show if it did.
            OrderPrices.Pair? prices = PricesFor(connection, setup, asOf);

            if (prices is null)
            {
                absentGeometry++;
                continue;
            }

            int shares = PositionSizing.SharesFor(prices.Distance);

            if (shares < 1)
            {
                belowOneShare++;
                continue;
            }

            Insert(connection, transaction, setup, liveSession, prices, shares, observedAt);
            planned++;
        }

        transaction.Commit();

        // Clean whatever the refusals did. A capped candidate with no trade geometry is an ordinary
        // state of this store rather than a stage that failed: nothing was asked of the vendor and
        // nothing threw, and the counts say exactly what was refused and for which of three reasons.
        // A run that called this partial would report almost every night as partial, which is a
        // signal that means nothing.
        RunOutcome outcome = RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        RecordRun(
            connection, asOf, liveSession, capped.Count, planned,
            absentGeometry, equalPrices, belowOneShare, outcome, stoppedBecause, observedAt);

        return new PlanRunResult(
            asOf, liveSession, capped.Count, planned,
            absentGeometry, equalPrices, belowOneShare,
            summary.RowsWritten, outcome, stoppedBecause);
    }

    /// <summary>
    /// The next weekday after a session, which is this lab's whole knowledge of what trades next.
    ///
    /// Weekends are a property of the calendar and holidays are a property of an exchange, and only
    /// the first is derivable from a date. See the class comment for why the second is not invented
    /// here and where it is carried.
    /// </summary>
    public static DateOnly NextWeekday(DateOnly session)
    {
        DateOnly next = session.AddDays(1);

        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    /// <summary>
    /// The trigger and the give-up point for one capped candidate, or null where the store holds
    /// no bar or no range for its final pullback session.
    ///
    /// The final pullback session is the evening the setup was flagged on, which is the session
    /// whose extremes the decision names and whose daily bar carries them. The range is the same
    /// figure the detector measured the screening distances in, being the average daily range as a
    /// fraction of price put back into price through that session's close, so the offset is in the
    /// unit the row was flagged in.
    /// see: The order prices are derived from the final pullback session's minutes, not from the screening geometry
    /// </summary>
    private static OrderPrices.Pair? PricesFor(SqliteConnection connection, StoredSetup setup, DateOnly asOf)
    {
        StoredDailyBar? session = DailyBarReader.Latest(
            connection,
            setup.Ticker,
            asOf,
            StoreText.StorageTextToTimestamp(StoreText.EndOfSession(asOf, SessionBoundaries.UsEquities)));
        StoredIndicators? figures = IndicatorDailyReader.Read(connection, setup.Ticker, asOf, asOf);

        if (session is null || figures is null || figures.AverageDailyRange <= 0m || session.Close <= 0m)
        {
            return null;
        }

        return OrderPrices.For(setup.Direction, session.High, session.Low, figures.AverageDailyRange * session.Close);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredSetup setup,
        DateOnly liveSession,
        OrderPrices.Pair prices,
        int shares,
        DateTimeOffset observedAt)
    {
        decimal distance = prices.Distance;

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and nothing in this lab updates a plan. The conflict clause is what makes a
        // rerun write nothing; the key is what makes a second plan for one candidate unexpressible.
        // see: The plan is written before the session and is immutable after publication
        command.CommandText = """
            INSERT INTO trade_plan (
                setup_id, as_of, live_session, ticker, direction,
                trigger_price, give_up_price, give_up_distance, shares,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @setup_id, @as_of, @live_session, @ticker, @direction,
                @trigger_price, @give_up_price, @give_up_distance, @shares,
                @equity, @risk_fraction, @risk_budget, @risk_at_stake, @observed_at)
            ON CONFLICT (setup_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@setup_id", setup.SetupId);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(setup.AsOf));
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue("@ticker", setup.Ticker);
        command.Parameters.AddWithValue("@direction", setup.Direction);
        command.Parameters.AddWithValue("@trigger_price", StoreText.PriceToStorageText(prices.Trigger));
        command.Parameters.AddWithValue("@give_up_price", StoreText.PriceToStorageText(prices.GiveUp));
        command.Parameters.AddWithValue("@give_up_distance", StoreText.PriceToStorageText(distance));
        command.Parameters.AddWithValue("@shares", shares);
        command.Parameters.AddWithValue("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity));
        command.Parameters.AddWithValue("@risk_fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade));
        command.Parameters.AddWithValue("@risk_budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget));
        command.Parameters.AddWithValue(
            "@risk_at_stake", StoreText.PriceToStorageText(PositionSizing.RiskAtStake(shares, distance)));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RecordRun(
        SqliteConnection connection,
        DateOnly asOf,
        DateOnly liveSession,
        int candidates,
        int planned,
        int absentGeometry,
        int equalPrices,
        int belowOneShare,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plan_run (
                session_date, live_session, candidates, planned,
                refused_absent_geometry, refused_equal_prices, refused_below_one_share,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @live_session, @candidates, @planned,
                @refused_absent_geometry, @refused_equal_prices, @refused_below_one_share,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue("@candidates", candidates);
        command.Parameters.AddWithValue("@planned", planned);
        command.Parameters.AddWithValue("@refused_absent_geometry", absentGeometry);
        command.Parameters.AddWithValue("@refused_equal_prices", equalPrices);
        command.Parameters.AddWithValue("@refused_below_one_share", belowOneShare);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

/// <summary>What one run of the plan stage did, with its refusals broken out by reason.</summary>
public sealed record PlanRunResult(
    DateOnly AsOf,
    DateOnly LiveSession,
    int Candidates,
    int Planned,
    int RefusedAbsentGeometry,
    int RefusedEqualPrices,
    int RefusedBelowOneShare,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    /// <summary>Every candidate that got no plan, which is the three reasons added up.</summary>
    public int Refused => RefusedAbsentGeometry + RefusedEqualPrices + RefusedBelowOneShare;
}
