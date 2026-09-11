using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The stop and the size of every entry the resolver found, resolved at the entry minute, from 7.8.
///
/// <b>Between the resolver and the gate, and a stage of its own for that reason.</b> The resolver says
/// when the rule took an entry; the gate places orders and is their only writer; the size has to exist
/// before the gate runs and cannot be the gate's own, because a gate that sized would be recomputing
/// the thing it is meant to cap. So the size is resolved here, into `entry_resolution`, and the gate
/// reads it and enforces the risk budget against it.
/// see: Order prices and the share count resolve at the entry minute
/// see: RiskGate is the sole writer of orders, for both directions and every version
///
/// <b>It walks the session again, to the minute the resolver named and no further.</b> The rule is
/// forward-only, so the stop it hands back is the session's extreme as of the entry minute. The walk
/// is the resolver's own rule over the same minutes, and a walk that took its entry in a different
/// minute from the resolver's is refused with the disagreement named rather than sized on either.
///
/// <b>Three refusals and each is a row.</b> The chase filter, a stop wider than the ceiling the plan
/// carries, and a budget that buys under one share at the stop are refusals of the entry rather than
/// caps, so no order follows them and the row says which it was.
/// see: The stop switches to the entry candle at more than 2%, and 5% as the switch is fourth-hand
/// see: The entry ceiling is the tighter of half the daily range and 5%
/// </summary>
public sealed class EntrySizer
{
    public const string Name = "size-entries";

    /// <summary>No plan carrying the rule triggered in this session, which is most nights.</summary>
    public const string NothingToSize = "no plan carrying the entry rule triggered in this session";

    /// <summary>The walk took its entry in a different minute from the resolver's.</summary>
    public const string Disagrees = "the entry rule walked again takes its entry in a different minute from the resolver's";

    /// <summary>The risk budget buys under one share at the stop the entry resolved.</summary>
    public const string BelowOneShare = "the risk budget buys under one share at the stop the entry resolved";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public EntrySizer(
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

        EntrySizeResult result = Size(sessionDate);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: session of {result.SessionDate:yyyy-MM-dd}, {result.Entries} entr{(result.Entries == 1 ? "y" : "ies")} to size"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {result.Sized} sized, {result.Refused} refused, {result.AlreadyResolved} already resolved"));
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>Resolve the stop and the size of every entry the resolver took in <paramref name="sessionDate"/>.</summary>
    public EntrySizeResult Size(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "entry_resolution");

        DateTimeOffset observedAt = run.StartedAt;
        string zone = _options.SessionZone;

        if (TradeChainWindow.Closed(observedAt, sessionDate, zone) is string closed)
        {
            RunSummary refused = run.Complete(RunOutcome.Failed);
            return new EntrySizeResult(sessionDate, 0, 0, 0, 0, refused.RowsWritten, RunOutcome.Failed, closed);
        }

        Dictionary<string, StoredTriggerResolution> touched = TriggerResolutionReader
            .ForLiveSession(connection, sessionDate, sessionDate, zone)
            .Where(r => string.Equals(r.Outcome, "touched", StringComparison.Ordinal) && r.TouchedAt is not null)
            .ToDictionary(r => r.PlanId, StringComparer.Ordinal);

        HashSet<string> resolved = [.. EntryResolutionReader
            .ForLiveSession(connection, sessionDate, sessionDate, zone)
            .Select(r => r.PlanId)];

        CommittedTradePlan[] entries =
        [
            .. TradePlanReader.CommittedForLiveSession(connection, sessionDate, sessionDate, zone)
                .Where(p => p.CarriesTheRule && touched.ContainsKey(p.PlanId)),
        ];

        if (entries.Length == 0)
        {
            RunSummary nothing = run.Complete(RunOutcome.Clean);
            return new EntrySizeResult(sessionDate, 0, 0, 0, 0, nothing.RowsWritten, RunOutcome.Clean, NothingToSize);
        }

        CommittedTradePlan[] toSize = [.. entries.Where(p => !resolved.Contains(p.PlanId))];

        // One walk of the session for every name, each plan's watch fed until it takes its entry.
        var watches = toSize.ToDictionary(
            p => p.PlanId,
            p => new EntryWatch(
                p.Direction,
                IntradayBarReader.HourlyClosesBefore(connection, p.Ticker, sessionDate, sessionDate, zone),
                sessionDate,
                zone),
            StringComparer.Ordinal);

        string[] names = [.. toSize.Select(p => p.Ticker).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        SessionReplayClock clock = SessionReplayClock.ForSession(connection, names, sessionDate, sessionDate, zone);

        foreach (ReplayMinute minute in clock.Walk())
        {
            foreach (CommittedTradePlan plan in toSize)
            {
                if (minute.Of(plan.Ticker) is StoredIntradayBar bar)
                {
                    watches[plan.PlanId].Observe(bar.OpenedAt, bar.High, bar.Low, bar.Close);
                }
            }
        }

        int sized = 0;
        int refusedCount = 0;
        int disagreements = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (CommittedTradePlan plan in toSize)
            {
                StoredTriggerResolution trigger = touched[plan.PlanId];
                EntryPoint? entry = watches[plan.PlanId].Entry;

                if (entry is null || entry.Minute != trigger.TouchedAt)
                {
                    // Nothing to write a row about when the walk found no entry at all: the row's entry
                    // columns are required, and inventing them would be worse than the partial run.
                    disagreements++;

                    if (entry is not null)
                    {
                        Insert(connection, transaction, plan, entry, new EntryStop.Resolution(null, null, null, null, Disagrees), null, observedAt);
                        refusedCount++;
                    }

                    continue;
                }

                decimal ceiling = plan.StopCeiling!.Value;
                EntryStop.Resolution stop = EntryStop.Resolve(plan.Direction, entry, ceiling);
                int? shares = null;

                if (stop.RefusedBecause is null)
                {
                    int count = PositionSizing.SharesFor(stop.Distance!.Value, plan.RiskBudget);

                    if (count < 1)
                    {
                        stop = stop with { RefusedBecause = BelowOneShare };
                    }
                    else
                    {
                        shares = count;
                    }
                }

                Insert(connection, transaction, plan, entry, stop, shares, observedAt);

                if (shares is null)
                {
                    refusedCount++;
                }
                else
                {
                    sized++;
                }
            }

            transaction.Commit();
        }

        RunOutcome outcome = disagreements > 0 ? RunOutcome.Partial : RunOutcome.Clean;
        RunSummary summary = run.Complete(outcome);

        return new EntrySizeResult(
            sessionDate, entries.Length, sized, refusedCount, entries.Length - toSize.Length,
            summary.RowsWritten, outcome, disagreements > 0 ? Disagrees : null);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CommittedTradePlan plan,
        EntryPoint entry,
        EntryStop.Resolution stop,
        int? shares,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. A resolution is a statement about a minute that has passed, and nothing revises
        // one; a rerun meets the key and writes nothing.
        command.CommandText = """
            INSERT INTO entry_resolution (
                plan_id, setup_id, variant_id, live_session, ticker, direction,
                entry_minute, candle_minutes, level, level_value, armed_at, entry_price,
                session_extreme, candle_extreme, stop_ceiling, stop_basis, stop_price,
                stop_distance, stop_fraction, shares, risk_budget, risk_at_stake,
                refused_because, observed_at)
            VALUES (
                @plan_id, @setup_id, @variant_id, @live_session, @ticker, @direction,
                @entry_minute, @candle_minutes, @level, @level_value, @armed_at, @entry_price,
                @session_extreme, @candle_extreme, @stop_ceiling, @stop_basis, @stop_price,
                @stop_distance, @stop_fraction, @shares, @risk_budget, @risk_at_stake,
                @refused_because, @observed_at)
            ON CONFLICT (plan_id) DO NOTHING;
            """;

        // A refused entry keeps its stop where it has one, so the row says how wide the stop it refused
        // was; one refused by the chase filter or a disagreement never reached a stop.
        bool hasStop = stop.Stop is not null;

        command.Parameters.AddWithValue("@plan_id", plan.PlanId);
        command.Parameters.AddWithValue("@setup_id", plan.SetupId);
        command.Parameters.AddWithValue("@variant_id", plan.VariantId);
        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(plan.LiveSession));
        command.Parameters.AddWithValue("@ticker", plan.Ticker);
        command.Parameters.AddWithValue("@direction", plan.Direction);
        command.Parameters.AddWithValue("@entry_minute", StoreText.TimestampToStorageText(entry.Minute));
        command.Parameters.AddWithValue("@candle_minutes", entry.CandleMinutes);
        command.Parameters.AddWithValue("@level", entry.Level);
        command.Parameters.AddWithValue("@level_value", StoreText.PriceToStorageText(entry.LevelValue));
        command.Parameters.AddWithValue("@armed_at", StoreText.TimestampToStorageText(entry.ArmedAt));
        command.Parameters.AddWithValue("@entry_price", StoreText.PriceToStorageText(entry.Price));
        command.Parameters.AddWithValue("@session_extreme", StoreText.PriceToStorageText(entry.SessionExtreme));
        command.Parameters.AddWithValue("@candle_extreme", StoreText.PriceToStorageText(entry.CandleExtreme));
        command.Parameters.AddWithValue("@stop_ceiling", StoreText.RatioToStorageText(plan.StopCeiling!.Value));
        command.Parameters.AddWithValue("@stop_basis", hasStop ? (object)stop.Basis! : DBNull.Value);
        command.Parameters.AddWithValue("@stop_price", hasStop ? (object)StoreText.PriceToStorageText(stop.Stop!.Value) : DBNull.Value);
        command.Parameters.AddWithValue("@stop_distance", hasStop ? (object)StoreText.PriceToStorageText(stop.Distance!.Value) : DBNull.Value);
        command.Parameters.AddWithValue("@stop_fraction", hasStop ? (object)StoreText.RatioToStorageText(stop.Fraction!.Value) : DBNull.Value);
        command.Parameters.AddWithValue("@shares", shares is int count ? (object)count : DBNull.Value);
        command.Parameters.AddWithValue("@risk_budget", StoreText.PriceToStorageText(plan.RiskBudget));
        command.Parameters.AddWithValue(
            "@risk_at_stake",
            shares is int held && hasStop ? (object)StoreText.PriceToStorageText(PositionSizing.RiskAtStake(held, stop.Distance!.Value)) : DBNull.Value);
        command.Parameters.AddWithValue("@refused_because", (object?)stop.RefusedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

/// <summary>What one run of the sizer resolved.</summary>
public sealed record EntrySizeResult(
    DateOnly SessionDate,
    int Entries,
    int Sized,
    int Refused,
    int AlreadyResolved,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause);
