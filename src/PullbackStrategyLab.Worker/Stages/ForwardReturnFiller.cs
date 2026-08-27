using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// What every flagged setup did over the next 1, 3, 5 and 10 sessions, traded or not.
///
/// <b>The clock the whole project runs on starts here.</b> Phase 3's answers need accumulated
/// outcomes and nothing substitutes for elapsed time, so a night not spent filling is a night the
/// lab never gets back.
/// see: Forward returns are recorded for every flagged setup, traded or not
///
/// <b>This is the one stage that reads bars dated after its subject's own date, by design, and it
/// is the sharpest point-in-time case in the system.</b> Every other read in the lab is bounded so
/// that a row observed after the as-of is invisible. This one must see the future of a setup, or it
/// has nothing to measure. The resolution is that <b>the fill's as-of is the fill date, not the
/// setup date</b>: the stage answers "what can the lab know today", the row carries `filled_at`
/// saying when that was, and a reader bounded on it sees exactly what was knowable when it asked.
/// Backdating the row to the night that flagged the setup is what would break the property, because
/// then a replay of that night would find an outcome the night could not have had.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Written once per subject per horizon and never revised.</b> A horizon that has elapsed has one
/// answer; a restated bar arriving later is a correction to the market's record and not a licence to
/// rewrite an outcome the lab already acted on. The store's own key refuses the second write.
/// </summary>
public sealed class ForwardReturnFiller
{
    public const string Name = "forward-returns";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ForwardReturnFiller(
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

        FillResult result = Fill(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Subjects} subject(s) considered");
        Console.WriteLine($"{Name}: {result.Written} outcome(s) written, {result.NotYetElapsed} horizon(s) not yet elapsed");
        Console.WriteLine($"{Name}: {result.AcrossAHoliday} landed on a session later than the calendar horizon");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// One fill pass. Every setup whose horizon has elapsed and whose outcome is not already
    /// recorded gets a row; everything else is left for a later night.
    /// </summary>
    public FillResult Fill(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "forward_return");

        DateTimeOffset filledAt = _clock.UtcNow;

        IReadOnlyList<Subject> subjects = Subjects(connection, asOf, filledAt);
        int written = 0;
        int notYetElapsed = 0;
        int acrossAHoliday = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (Subject subject in subjects)
            {
                IReadOnlyList<ForwardOutcome.Bar> path = Path(connection, subject, asOf, filledAt);

                foreach (int horizon in ForwardOutcome.Horizons)
                {
                    ForwardOutcome.Outcome? outcome =
                        ForwardOutcome.Of(path, horizon, subject.IsLong, subject.AverageTrueRange);

                    if (outcome is null)
                    {
                        notYetElapsed++;
                        continue;
                    }

                    // Where a naive calendar step lands, which is what the horizon would have been
                    // over an unbroken run of trading days. Stored beside the session actually used
                    // so a follow-up that crossed a weekend or a holiday says so rather than being
                    // silently later than it claims.
                    DateOnly intended = subject.AsOf.AddDays(horizon);

                    if (intended != outcome.ActualDate)
                    {
                        acrossAHoliday++;
                    }

                    written += Insert(connection, transaction, subject, horizon, intended, outcome, filledAt);
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new FillResult(
            asOf, subjects.Count, written, notYetElapsed, acrossAHoliday,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// The subjects owed an outcome: every setup the lab has flagged, with the ATR it was flagged
    /// against.
    ///
    /// Bounded on the fill instant rather than on any setup's own date, which is what makes the read
    /// point-in-time: the question is what the lab can measure today.
    /// </summary>
    private static IReadOnlyList<Subject> Subjects(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset filledAt)
    {
        var subjects = new List<Subject>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.setup_id, s.as_of, s.ticker, s.direction, i.atr_14
              FROM setup s
              LEFT JOIN indicator_daily i
                ON i.ticker = s.ticker AND i.as_of = s.as_of
               AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                     WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                       AND c.computed_at <= @filled_at)
             WHERE s.as_of <= @as_of
             ORDER BY s.setup_id
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            subjects.Add(new Subject(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? 0m : StoreText.StorageTextToPrice(reader.GetString(4))));
        }

        return subjects;
    }

    /// <summary>
    /// One subject's own bars from its as-of session forward, on the adjusted basis.
    ///
    /// Adjusted throughout, because a return read across a split on the raw basis is a collapse. The
    /// observation bound is the fill instant, so a correction the lab has not yet seen cannot change
    /// an outcome it is about to write.
    /// </summary>
    private static IReadOnlyList<ForwardOutcome.Bar> Path(
        SqliteConnection connection, Subject subject, DateOnly asOf, DateTimeOffset filledAt)
    {
        var path = new List<ForwardOutcome.Bar>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.bar_date, b.high, b.low, b.close, b.adj_close
              FROM daily_bar b
             WHERE b.ticker = @ticker
               AND b.bar_date >= @from
               AND b.bar_date <= @to
               AND b.observed_at <= @filled_at
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= @filled_at)
             ORDER BY b.bar_date
            """;
        command.Parameters.AddWithValue("@ticker", subject.Ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(subject.AsOf));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            decimal close = StoreText.StorageTextToPrice(reader.GetString(3));
            decimal adjusted = StoreText.StorageTextToPrice(reader.GetString(4));
            decimal factor = close == 0m ? 1m : adjusted / close;

            path.Add(new ForwardOutcome.Bar(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToPrice(reader.GetString(1)) * factor,
                StoreText.StorageTextToPrice(reader.GetString(2)) * factor,
                adjusted));
        }

        return path;
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Subject subject,
        int horizon,
        DateOnly intended,
        ForwardOutcome.Outcome outcome,
        DateTimeOffset filledAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Never revised. A horizon that has elapsed has one answer, and a restated bar arriving
        // later corrects the market's record rather than licensing a rewrite of an outcome already
        // acted on. The key refuses the second write rather than this method remembering to.
        command.CommandText = """
            INSERT INTO forward_return
                (subject_id, subject_kind, horizon_days, intended_date, actual_date,
                 return_signed, mfe_atr, mae_atr, filled_at)
            VALUES (@subject_id, @subject_kind, @horizon_days, @intended_date, @actual_date,
                    @return_signed, @mfe_atr, @mae_atr, @filled_at)
            ON CONFLICT (subject_id, subject_kind, horizon_days) DO NOTHING
            """;

        command.Parameters.AddWithValue("@subject_id", subject.SubjectId);
        command.Parameters.AddWithValue("@subject_kind", "setup");
        command.Parameters.AddWithValue("@horizon_days", horizon);
        command.Parameters.AddWithValue("@intended_date", StoreText.DateToStorageText(intended));
        command.Parameters.AddWithValue("@actual_date", StoreText.DateToStorageText(outcome.ActualDate));
        command.Parameters.AddWithValue("@return_signed", StoreText.RatioToStorageText(outcome.ReturnSigned));
        command.Parameters.AddWithValue(
            "@mfe_atr", StoreText.RatioToStorageText(outcome.MaximumFavourableExcursion ?? 0m));
        command.Parameters.AddWithValue(
            "@mae_atr", StoreText.RatioToStorageText(outcome.MaximumAdverseExcursion ?? 0m));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        return command.ExecuteNonQuery();
    }

    private sealed record Subject(
        string SubjectId, DateOnly AsOf, string Ticker, string Direction, decimal AverageTrueRange)
    {
        public bool IsLong => string.Equals(Direction, "long", StringComparison.Ordinal);
    }
}

/// <summary>What one fill pass did.</summary>
public sealed record FillResult(
    DateOnly AsOf,
    int Subjects,
    int Written,
    int NotYetElapsed,
    int AcrossAHoliday,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
