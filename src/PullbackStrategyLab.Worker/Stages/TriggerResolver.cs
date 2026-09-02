using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Whether each plan resting in a session was touched, and in which minute.
///
/// <b>The day is walked one minute at a time and the walk is the component.</b>
/// <see cref="SessionReplayClock"/> hands out ascending minutes and nothing else, so this stage
/// cannot see a minute later than the one it is evaluating. The restriction is easy to state and
/// easy to break, which is why it is a type with a single-pass walk rather than a comment in a loop.
/// see: Trades are resolved by replaying minute bars after the close, not by watching live
///
/// <b>One clock for the session, every name at once.</b> The contention rule fills the earliest
/// trigger and blocks the later ones, so which name fired first is a comparison across names and not
/// a property of any one of them. A clock per name would answer each name correctly and leave the
/// ordering to be reconstructed by whoever needed it, which is a second implementation of the one
/// thing 4.6 has to get right.
/// see: Plans are resting orders and fills go in time order when the caps bind
///
/// <b>Touched, not closed through</b>, which was answered at 4.15 and is applied here per direction:
/// a minute whose high reaches the trigger long, whose low reaches it short, with no margin. The
/// predicate is <see cref="TriggerTouch"/> in Core rather than an inequality in this file, because it
/// decides the order of a session as well as its outcomes.
/// see: The trigger is touched, not closed through
///
/// <b>The pairing is asserted fail-closed and is not restated.</b> A plan written on the evening of
/// N is live in the next session, so a plan resolved against a session at or before its own date
/// would be resolved against the prices it was computed from.
/// <see cref="IntradayFetcher.Pairing"/> already refuses that construction and this stage forms one
/// per plan rather than writing the comparison a second time. It throws rather than returning no
/// fill, because no fill and cannot-resolve are the conflation this whole stage is arranged around.
/// see: Minute bars are fetched for the session a plan was live in, never the session it was written on
///
/// <b>Three outcomes, and the third is the one the corpus keeps losing.</b> A plan whose name traded
/// and never reached its trigger did not fire. A plan whose session or whose name holds no stored
/// minute was never asked, and it is recorded as unresolvable with the reason rather than as a
/// quiet no. The second reading is what a holiday looks like: `live_session` is the next weekday and
/// nothing in this lab knows whether that weekday trades, so about nine evenings a year a plan rests
/// in a day that never opened.
/// see: A session is a date the store holds minutes for, and no calendar is authored here
///
/// <b>A session with resting plans and no minutes is a partial run, not a clean one.</b> That is the
/// figure a person reads on the morning it happens: a blind night reported as clean is a night the
/// build is green about and the lab was down for, which is the shape that cost this lab its second
/// evening of evidence.
/// </summary>
public sealed class TriggerResolver
{
    public const string Name = "resolve-triggers";

    /// <summary>No plan was live in this session, which is most nights and is not a fault.</summary>
    public const string NoPlansResting = "no plan was live in this session";

    /// <summary>
    /// The session holds no regular-session minute at all: a holiday, or a fetch that did not run.
    /// </summary>
    public const string SessionHeldNoMinutes =
        "the store holds no regular-session minute for this session, so no plan resting in it could be asked";

    /// <summary>The session traded and this name has no stored minute in it.</summary>
    public const string NameHeldNoMinutes =
        "the store holds no regular-session minute for this name in this session";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public TriggerResolver(
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

        TriggerRunResult result = Resolve(sessionDate);

        Console.WriteLine(
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, "
            + (result.SetupAsOf is null
                ? "no plan resting"
                : $"plans written on the evening of {result.SetupAsOf:yyyy-MM-dd}"));
        Console.WriteLine(
            $"{Name}: walked {result.MinutesWalked} minute(s) across {result.NamesWalked} name(s)");
        Console.WriteLine(
            $"{Name}: {result.Plans} plan(s), {result.Touched} touched, "
            + $"{result.NotTouched} not touched, {result.Unresolvable} unresolvable");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Resolve every plan resting in <paramref name="sessionDate"/> against that session's stored
    /// minutes.
    ///
    /// Idempotent: the insert takes the store's own key and does nothing on conflict, so a rerun
    /// writes no row. A resolution is a statement about a session that has closed, and nothing in
    /// this lab revises one.
    /// </summary>
    public TriggerRunResult Resolve(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "trigger_resolution", "trigger_run");

        DateTimeOffset observedAt = run.StartedAt;

        // What was resting when this session opened, which is the question `live_session` was stored
        // to answer rather than one derived by stepping a calendar back over a weekend.
        IReadOnlyList<StoredTradePlan> plans =
            TradePlanReader.ForLiveSession(connection, sessionDate, sessionDate);

        if (plans.Count == 0)
        {
            RecordRun(
                connection, sessionDate, null, 0, 0, 0, 0, 0, 0,
                RunOutcome.Clean, NoPlansResting, observedAt);

            RunSummary nothing = run.Complete(RunOutcome.Clean);

            return new TriggerRunResult(
                sessionDate, null, 0, 0, 0, 0, 0, 0,
                nothing.RowsWritten, RunOutcome.Clean, NoPlansResting);
        }

        // Fail-closed, per plan, before anything is walked. The type refuses the construction, so a
        // plan dated on or after the session it is resting in stops the night rather than resolving
        // to no fill. Formed for every plan rather than for the first: a store holding one bad row
        // among good ones is the case a check of the first would pass.
        DateOnly setupAsOf = plans[0].AsOf;

        foreach (StoredTradePlan plan in plans)
        {
            IntradayFetcher.Pairing.Of(sessionDate, plan.AsOf);

            if (plan.AsOf > setupAsOf)
            {
                setupAsOf = plan.AsOf;
            }
        }

        string[] names = [.. plans.Select(p => p.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        SessionReplayClock clock = SessionReplayClock.ForSession(connection, names, sessionDate, sessionDate);

        var touchedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var minutesOf = new Dictionary<string, int>(StringComparer.Ordinal);

        // One pass over the session. Each plan is decided by the first minute that reaches its
        // trigger, so a plan already touched is skipped rather than overwritten: the earliest touch
        // is the one the contention rule fills on, and a later minute must not be able to move it.
        foreach (ReplayMinute minute in clock.Walk())
        {
            // Counted per name and not per plan, because two plans on one name walk the same
            // minutes: counting inside the plan loop would report a name that traded 390 minutes as
            // having traded 780 and would say so only on the nights two versions selected it.
            foreach (string ticker in names)
            {
                if (minute.Bars.ContainsKey(ticker))
                {
                    minutesOf[ticker] = minutesOf.GetValueOrDefault(ticker) + 1;
                }
            }

            foreach (StoredTradePlan plan in plans)
            {
                if (touchedAt.ContainsKey(plan.SetupId) || minute.Of(plan.Ticker) is not StoredIntradayBar bar)
                {
                    continue;
                }

                if (TriggerTouch.Reached(plan.Direction, plan.TriggerPrice, bar.High, bar.Low))
                {
                    touchedAt[plan.SetupId] = minute.OpenedAt;
                }
            }
        }

        int touched = 0;
        int notTouched = 0;
        int unresolvable = 0;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (StoredTradePlan plan in plans)
        {
            // How many minutes this plan was asked over, which is the name's count and is stored on
            // every row: a resolution that says nothing about what it walked cannot be told apart
            // from one taken over a session the store barely holds.
            int walked = minutesOf.GetValueOrDefault(plan.Ticker);

            string outcome;
            DateTimeOffset? at = null;
            string? because = null;

            if (touchedAt.TryGetValue(plan.SetupId, out DateTimeOffset when))
            {
                outcome = "touched";
                at = when;
                touched++;
            }
            else if (clock.Minutes == 0)
            {
                outcome = "unresolvable";
                because = SessionHeldNoMinutes;
                unresolvable++;
            }
            else if (walked == 0)
            {
                outcome = "unresolvable";
                because = NameHeldNoMinutes;
                unresolvable++;
            }
            else
            {
                outcome = "not_touched";
                notTouched++;
            }

            Insert(connection, transaction, plan, outcome, at, walked, because, observedAt);
        }

        transaction.Commit();

        // A session with plans resting in it and no minutes to ask them against is a night this lab
        // was blind on, and it is reported as partial on the morning it happens rather than as a
        // clean night on which nothing triggered. A name missing from a session that otherwise
        // traded is the same fault one name wide, so it carries the same outcome.
        string? stoppedBecause = clock.Minutes == 0
            ? SessionHeldNoMinutes
            : unresolvable > 0
                ? NameHeldNoMinutes
                : null;

        RunOutcome outcome_ = stoppedBecause is null ? RunOutcome.Clean : RunOutcome.Partial;
        RunSummary summary = run.Complete(outcome_);

        RecordRun(
            connection, sessionDate, setupAsOf, plans.Count, touched, notTouched, unresolvable,
            minutesOf.Count, clock.Minutes, outcome_, stoppedBecause, observedAt);

        return new TriggerRunResult(
            sessionDate, setupAsOf, plans.Count, touched, notTouched, unresolvable,
            minutesOf.Count, clock.Minutes, summary.RowsWritten, outcome_, stoppedBecause);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredTradePlan plan,
        string outcome,
        DateTimeOffset? touchedAt,
        int minutesWalked,
        string? unresolvedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. A session that has closed does not change, and a rerun of the same evening
        // finds its own rows and writes none.
        command.CommandText = """
            INSERT INTO trigger_resolution (
                setup_id, live_session, ticker, direction, outcome,
                touched_at, minutes_walked, unresolved_because, observed_at)
            VALUES (
                @setup_id, @live_session, @ticker, @direction, @outcome,
                @touched_at, @minutes_walked, @unresolved_because, @observed_at)
            ON CONFLICT (setup_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(plan.LiveSession));
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue(
            "@touched_at",
            touchedAt is null ? DBNull.Value : StoreText.TimestampToStorageText(touchedAt.Value));
        command.Parameters.AddWithValue("@minutes_walked", minutesWalked);
        command.Parameters.AddWithValue("@unresolved_because", (object?)unresolvedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RecordRun(
        SqliteConnection connection,
        DateOnly sessionDate,
        DateOnly? setupAsOf,
        int plans,
        int touched,
        int notTouched,
        int unresolvable,
        int namesWalked,
        int minutesWalked,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO trigger_run (
                session_date, setup_as_of, plans, touched, not_touched, unresolvable,
                names_walked, minutes_walked, outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @setup_as_of, @plans, @touched, @not_touched, @unresolvable,
                @names_walked, @minutes_walked, @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@setup_as_of", setupAsOf is null ? DBNull.Value : StoreText.DateToStorageText(setupAsOf.Value));
        command.Parameters.AddWithValue("@plans", plans);
        command.Parameters.AddWithValue("@touched", touched);
        command.Parameters.AddWithValue("@not_touched", notTouched);
        command.Parameters.AddWithValue("@unresolvable", unresolvable);
        command.Parameters.AddWithValue("@names_walked", namesWalked);
        command.Parameters.AddWithValue("@minutes_walked", minutesWalked);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

/// <summary>What one run of the resolver walked and what it decided.</summary>
public sealed record TriggerRunResult(
    DateOnly SessionDate,
    DateOnly? SetupAsOf,
    int Plans,
    int Touched,
    int NotTouched,
    int Unresolvable,
    int NamesWalked,
    int MinutesWalked,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause);
