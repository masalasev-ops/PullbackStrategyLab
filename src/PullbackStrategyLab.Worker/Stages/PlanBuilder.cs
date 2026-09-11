using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// One committed instruction per capped candidate and live version: the entry rule, and the widest stop
/// the entry may take.
///
/// <b>Declared in SCHEMA since phase 4 was planned and built by no checkpoint until 4.16.</b> The
/// catalogue slots it at 18:30, the runbook reserves the slot, and the phase built PlanAudit without
/// it, so as written it built an auditor of a thing it never built.
/// see: The plan is written before the session and is immutable after publication
///
/// <b>From 7.8 the plan carries the rule and no price.</b> The entry the strategy states is a flush into
/// the hourly averages and a break of the previous candle's extreme, so the entry price, the stop and
/// the share count are known at the minute of entry and not at 18:30. What is known tonight is written:
/// which rule, the risk budget the size will be taken from, and the ceiling the stop may not exceed,
/// being the tighter of half the name's daily range and 5%, which is tonight's range and so tonight's
/// figure. Until 7.8 this stage sized and wrote the evening's prices, and those plans say so on the row.
/// see: Order prices and the share count resolve at the entry minute
/// see: The entry ceiling is the tighter of half the daily range and 5%
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
/// <b>A setup with no trade geometry gets no plan.</b> Not a plan sized on nought: a give-up
/// distance of nought divides into the risk budget as many times as you like, and the share count
/// that comes back is a number with nothing behind it. Two shapes reach this stage and both are
/// refused, counted apart because only one of them is the defect the 3.15 obligation named. An
/// absent price is the shape migration 031 made expressible. An equal pair is the shape that
/// survived it, where the thrust has not pulled back yet so the entry level and the give-up point
/// are the same price and two of the four columns still state a number. That clause of the order-price
/// decision stands after 7.8; the two clauses placing the prices at 18:30 do not.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
/// see: The order prices are derived from the final pullback session's minutes, not from the screening geometry
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

    /// <summary>
    /// The cap ran and had nobody to keep, which is an ordinary night and not a stage that did not run.
    ///
    /// <b>The row raised at 5.0.</b> The cap writes its decision on candidate rows only, so a night
    /// with no candidate leaves no cap decision anywhere and this stage said the night was never
    /// capped, which is the sentence reserved for a stage that did not run; every recorded night has
    /// passed nought candidates, so every reading said it. The cap's own run row is what tells the
    /// two apart, and it is read rather than inferred from the rows the cap chose not to write.
    /// </summary>
    public const string CapKeptNobody =
        "the cap ran for this session and kept nobody: no setup passed every gating check, so there "
        + "is no candidate list to plan from";

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
                ? flagged.Any(s => s.CappedOut == true)
                    ? AllCappedOut
                    : RunLogger.StageRanOn(connection, SetupCapper.Name, asOf, _options.SessionZone)
                        ? CapKeptNobody
                        : NeverCapped
                : null;

        // The versions this evening fans a plan out to, read from the register as it stood tonight.
        // One row per candidate per version, so `planned` counts plans and `candidatesPlanned`
        // counts candidates, and the two are equal only while one version is live. Reporting one
        // number for both would make a night with two versions read as twice the funnel.
        IReadOnlyList<StoredVariant> live = VariantReader.LiveOn(connection, asOf, _options.SessionZone);

        int planned = 0;
        int candidatesPlanned = 0;
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

            // The ceiling the entry's stop may not exceed, from the name's average daily range as the
            // store held it this evening. A candidate whose range the store does not hold is refused as
            // an absent geometry rather than planned on a stand-in, which cannot happen to a row the
            // detector flagged from that same figure and is counted where it would show if it did.
            // Nothing is refused for its size tonight: the size resolves at the entry minute, so the
            // below-one-share count is written as nought from 7.8 and the sizer carries that refusal.
            decimal? ceiling = CeilingFor(connection, setup, asOf, _options.SessionZone);

            if (ceiling is null)
            {
                absentGeometry++;
                continue;
            }

            foreach (StoredVariant variant in live)
            {
                Insert(connection, transaction, setup, variant, liveSession, ceiling.Value, observedAt);
                planned++;
            }

            candidatesPlanned++;
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
            connection, asOf, liveSession, capped.Count, planned, candidatesPlanned,
            absentGeometry, equalPrices, belowOneShare, outcome, stoppedBecause, observedAt);

        return new PlanRunResult(
            asOf, liveSession, capped.Count, planned, candidatesPlanned, live.Count,
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
    /// The widest stop one candidate's entry may take, as a fraction of the entry price, or null where the
    /// store holds no range for the evening. The range is the average daily range as a fraction of price,
    /// the figure the detector measured the screening distances in.
    /// see: The entry ceiling is the tighter of half the daily range and 5%
    /// </summary>
    private static decimal? CeilingFor(SqliteConnection connection, StoredSetup setup, DateOnly asOf, string sessionZone)
    {
        StoredIndicators? figures = IndicatorDailyReader.Read(connection, setup.Ticker, asOf, asOf, sessionZone);

        return figures is null || figures.AverageDailyRange <= 0m
            ? null
            : EntryRule.CeilingFor(figures.AverageDailyRange);
    }

    /// <summary>
    /// One plan, for one candidate under one version.
    ///
    /// <b>The rule is the same under every version and that is not an oversight.</b> A selection
    /// version changes which stocks are picked and leaves entry and exit alone, so two versions
    /// planning the same candidate on the same evening plan it identically; what differs is which
    /// candidates each has. An execution version would carry a different rule and none is admitted in
    /// this generation.
    /// see: No execution variant is admitted in this generation, and the condition that would reopen it is named
    /// </summary>
    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredSetup setup,
        StoredVariant variant,
        DateOnly liveSession,
        decimal stopCeiling,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only, and nothing in this lab updates a plan. The conflict clause is what makes a
        // rerun write nothing; the key is what makes a second plan for one candidate under one
        // version unexpressible, which is what the setup-keyed table said before the fan-out.
        // see: The plan is written before the session and is immutable after publication
        command.CommandText = """
            INSERT INTO trade_plan (
                plan_id, setup_id, variant_id, as_of, live_session, ticker, direction, entry_rule,
                trigger_price, give_up_price, give_up_distance, shares, stop_ceiling,
                equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
            VALUES (
                @plan_id, @setup_id, @variant_id, @as_of, @live_session, @ticker, @direction, @entry_rule,
                NULL, NULL, NULL, NULL, @stop_ceiling,
                @equity, @risk_fraction, @risk_budget, NULL, @observed_at)
            ON CONFLICT (setup_id, variant_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@plan_id", PlanIdentity.For(setup.SetupId, variant.VariantId));
        command.Parameters.AddWithValue("@variant_id", variant.VariantId);
        command.Parameters.AddWithValue("@setup_id", setup.SetupId);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(setup.AsOf));
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue("@ticker", setup.Ticker);
        command.Parameters.AddWithValue("@direction", setup.Direction);
        command.Parameters.AddWithValue("@entry_rule", EntryRule.FlushReclaim);
        command.Parameters.AddWithValue("@stop_ceiling", StoreText.RatioToStorageText(stopCeiling));
        command.Parameters.AddWithValue("@equity", StoreText.PriceToStorageText(PositionSizing.NotionalEquity));
        command.Parameters.AddWithValue("@risk_fraction", StoreText.RatioToStorageText(PositionSizing.RiskPerTrade));
        command.Parameters.AddWithValue("@risk_budget", StoreText.PriceToStorageText(PositionSizing.RiskBudget));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RecordRun(
        SqliteConnection connection,
        DateOnly asOf,
        DateOnly liveSession,
        int candidates,
        int planned,
        int candidatesPlanned,
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
                session_date, live_session, candidates, planned, candidates_planned,
                refused_absent_geometry, refused_equal_prices, refused_below_one_share,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @live_session, @candidates, @planned, @candidates_planned,
                @refused_absent_geometry, @refused_equal_prices, @refused_below_one_share,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue("@candidates_planned", candidatesPlanned);
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
    // Plans and the candidates behind them, which are the same number only while one version is
    // live. `Planned` counted candidates until the fan-out and counts plans now, so a night with two
    // versions would read as twice the funnel to anybody reading the one figure.
    int Planned,
    int CandidatesPlanned,
    int LiveVersions,
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
