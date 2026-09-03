using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The win rate perfect foresight could have reached, per direction, recomputed weekly.
///
/// <b>Weekly rather than nightly, and it is not in the nightly table.</b> The bound moves with the
/// population rather than with a session, and a figure recomputed every night over one more row
/// than yesterday invites reading noise as movement.
///
/// <b>What it answers.</b> Achieved 25% against a bound of 50% means half the available room is
/// unused and better selection has somewhere to go. Achieved 25% against a bound of 28% means the
/// stop is too tight for these names and no selection change can help: the loop should be pointed at
/// the exit rule instead. Those are opposite conclusions from the same win rate, which is why the
/// bound is computed rather than assumed.
/// see: The win-rate ceiling is computed from the outcome distribution, never assumed
/// see: The ceiling is computed from the path, not from the terminal return
/// </summary>
public sealed class CeilingCalculator
{
    public const string Name = "ceiling";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public CeilingCalculator(
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

        CeilingResult result = Compute(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, at {MeasurementParameters.ScoringHorizonSessions} sessions");

        foreach ((string direction, int subjects, decimal bound, decimal achieved) in result.Bounds)
        {
            Console.WriteLine(
                $"{Name}: {direction} over {subjects} subject(s), bound {bound:P1}, achieved {achieved:P1}, "
                + $"gap {bound - achieved:P1}");
        }

        if (result.Bounds.Count == 0)
        {
            Console.WriteLine($"{Name}: no closed subjects yet, so no bound. That is not a bound of nought");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One week's bound, per direction, over every subject whose scoring horizon has closed.</summary>
    public CeilingResult Compute(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "ceiling_bound");

        DateTimeOffset computedAt = _clock.UtcNow;
        var bounds = new List<(string Direction, int Subjects, decimal Bound, decimal Achieved)>();

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string direction in new[] { "long", "short" })
            {
                IReadOnlyList<WinRateCeiling.Subject> subjects =
                    Closed(connection, direction, asOf, computedAt, _options.SessionZone);

                WinRateCeiling.Bound? bound = WinRateCeiling.Of(subjects);

                if (bound is null)
                {
                    // Nothing has closed on this side yet. No row rather than a row of noughts: a
                    // ceiling of nought reads on a scoreboard as "selection has no room", and what
                    // it would mean is "nobody has measured anything yet".
                    continue;
                }

                bounds.Add((direction, bound.Subjects, bound.Ceiling, bound.Achieved));
                Insert(connection, transaction, asOf, direction, bound, computedAt);
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new CeilingResult(asOf, bounds, summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// Every setup on one side whose scoring horizon has closed, with the four figures the bound
    /// needs.
    ///
    /// The excursion comes from `forward_return` in ATR, the give-up distance from `setup` in daily
    /// ranges, and the two prices that convert between them from `indicator_daily`. Reading all four
    /// together here is what lets the conversion happen once, in
    /// <see cref="WinRateCeiling.Survived"/>, rather than at each call site.
    ///
    /// <b>A setup with no give-up distance is not in the population.</b> The column is nullable from
    /// 031, and a setup whose geometry the detector could not compute has no stop, so there is no
    /// trade for a ceiling to be a ceiling of. Excluded rather than judged as not having survived,
    /// which is what `Survived` does with a subject it cannot measure: that treatment is right for a
    /// subject whose volatility figures are missing and wrong for one that was never a trade, and
    /// counting it as stopped out would push the bound down for a row that should not be in it.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    private static IReadOnlyList<WinRateCeiling.Subject> Closed(
        SqliteConnection connection, string direction, DateOnly asOf, DateTimeOffset computedAt, string sessionZone)
    {
        var subjects = new List<WinRateCeiling.Subject>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.setup_id, f.return_signed, f.mae_atr, s.stop_distance_ranges,
                   i.atr_14, i.adr_20, b.close
              FROM setup s
              JOIN forward_return f
                ON f.subject_id = s.setup_id AND f.subject_kind = 'setup'
               AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
              JOIN indicator_daily i
                ON i.ticker = s.ticker AND i.as_of = s.as_of
               AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                     WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                       AND c.computed_at <= @end_of_day)
              JOIN daily_bar b
                ON b.ticker = s.ticker AND b.bar_date = s.as_of
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= @end_of_day)
             WHERE s.direction = @direction AND s.as_of <= @as_of
               AND s.stop_distance_ranges IS NOT NULL
               -- A row whose excursions could not be measured carries none, with the reason on
               -- the row, from 050. It is out of the population on the same terms as a setup with
               -- no give-up distance: there is no path in the subject's own range for a ceiling
               -- to be a ceiling of, and reading the absence as nought adverse would count it as
               -- having survived.
               AND f.mae_atr IS NOT NULL
             ORDER BY s.setup_id
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));
        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            // Each column through the crossing named for what it carries: the close and the ATR
            // are prices, and the return, the excursion, the daily range and the give-up distance
            // are ratios. The values are identical either way and the naming is the whole point,
            // because a crossing named for what it carries is what stops a percentage being
            // written where a fraction was meant; until 5.8 every one of these went through the
            // price crossing.
            decimal close = StoreText.StorageTextToPrice(reader.GetString(6));
            decimal adr = StoreText.StorageTextToRatio(reader.GetString(5));

            subjects.Add(new WinRateCeiling.Subject(
                reader.GetString(0),
                direction,
                StoreText.StorageTextToRatio(reader.GetString(1)),
                StoreText.StorageTextToRatio(reader.GetString(2)),
                StoreText.StorageTextToPrice(reader.GetString(4)),
                // The daily range as a price, which is what the give-up distance is a multiple of.
                adr * close,
                StoreText.StorageTextToRatio(reader.GetString(3))));
        }

        return subjects;
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly asOf,
        string direction,
        WinRateCeiling.Bound bound,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // A week recomputed replaces its own row and no other. A later week is a new row and the
        // old one stays, because the gap narrowing over time is what a reader is looking at.
        command.CommandText = """
            INSERT INTO ceiling_bound
                (as_of, direction, horizon_days, subjects, bound, achieved, computed_at)
            VALUES (@as_of, @direction, @horizon, @subjects, @bound, @achieved, @computed_at)
            ON CONFLICT (as_of, direction) DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@subjects", bound.Subjects);
        command.Parameters.AddWithValue("@bound", StoreText.RatioToStorageText(bound.Ceiling));
        command.Parameters.AddWithValue("@achieved", StoreText.RatioToStorageText(bound.Achieved));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        command.ExecuteNonQuery();
    }
}

/// <summary>One week's bounds, per direction.</summary>
public sealed record CeilingResult(
    DateOnly AsOf,
    IReadOnlyList<(string Direction, int Subjects, decimal Bound, decimal Achieved)> Bounds,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
