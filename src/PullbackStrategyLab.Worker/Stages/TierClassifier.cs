using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The ladder grade on every universe member: rising, falling, or mixed where it is neither.
///
/// The three are a partition rather than a filter with a leftover bucket, and every name carries
/// exactly one. Mixed is most names on most days and is a grade in its own right: a name with no
/// grade would be indistinguishable from one the stage never reached.
///
/// <b>It writes a later observation rather than updating the row IndicatorEngine wrote.</b>
/// `indicator_daily` is append-only, so there is nothing to update: this stage writes a second
/// observation of the same session carrying the grade, and copies the seven computed figures
/// forward with it. That duplication is the price of the row being a complete observation rather
/// than a fragment, and a reader taking the latest row gets an answer instead of assembling one
/// from two. SCHEMA declares the two inserters and states how they are disjoint, which is why
/// writer-ownership passes without carrying an exception.
///
/// No vendor call. The grade is a comparison between four numbers already stored.
/// </summary>
public sealed class TierClassifier
{
    public const string Name = "tiers";

    /// <summary>Price above the 9-day, 9 above 21, 21 above 50. The working definition of an uptrend.</summary>
    public const string Rising = "rising";

    /// <summary>Every one of those reversed. The working definition of a downtrend.</summary>
    public const string Falling = "falling";

    /// <summary>Neither. A grade rather than a gap, and the commonest of the three.</summary>
    public const string Mixed = "mixed";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public TierClassifier(
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

        TierResult result = Classify(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Members} member(s), {result.Graded} graded");
        Console.WriteLine($"{Name}: {result.Rising} rising, {result.Mixed} mixed, {result.Falling} falling");
        Console.WriteLine($"{Name}: {result.AlreadyGraded} already graded, {result.NoIndicators} with no indicator row, {result.NoBar} with no bar, {result.Collided} refused by the store");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    public TierResult Classify(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "indicator_daily");

        DateTimeOffset computedAt = run.StartedAt;
        IReadOnlyList<string> members = UniverseSnapshotReader.Members(connection, asOf);

        int graded = 0;
        int alreadyGraded = 0;
        int noIndicators = 0;
        int noBar = 0;
        int collided = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Rising] = 0,
            [Mixed] = 0,
            [Falling] = 0,
        };

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (string ticker in members)
            {
                StoredIndicators? indicators = IndicatorDailyReader.Latest(connection, ticker, asOf);

                if (indicators is null || indicators.AsOf != asOf)
                {
                    // No figures for this session. The engine refuses for a name short of its
                    // warm-up or carrying an open rebuild demand, and a grade computed on figures
                    // from an older session would be a statement about the wrong night.
                    noIndicators++;
                    continue;
                }

                if (indicators.LadderGrade is not null)
                {
                    alreadyGraded++;
                    continue;
                }

                StoredDailyBar? bar = DailyBarReader.Latest(connection, ticker, asOf, EndOf(asOf));
                if (bar is null || bar.BarDate != asOf)
                {
                    noBar++;
                    continue;
                }

                string grade = Grade(bar.AdjustedClose, indicators);

                // Strictly after the observation it copies forward, never at the same instant.
                //
                // This row is a later observation of the same session, and the key is
                // (ticker, as_of, computed_at): writing at the instant the engine wrote would
                // collide with it, and the collision is silent because the insert says DO NOTHING.
                // Found in the replay, where a fixed clock gives every stage the same instant and
                // this stage reported thirty grades while writing no rows at all. In production the
                // wall clock happens to move between stages, which means the defect would have sat
                // there until the first night two stages ran inside the same millisecond.
                DateTimeOffset writtenAt = computedAt > indicators.ComputedAt
                    ? computedAt
                    : indicators.ComputedAt.AddMilliseconds(1);

                int written = Insert(connection, transaction, ticker, asOf, writtenAt, indicators, grade);

                if (written == 0)
                {
                    // The store refused the row and the stage would otherwise have counted a grade
                    // it did not write. Loud rather than absorbed: a grade nothing stored is a name
                    // every later stage reads as ungraded.
                    collided++;
                    continue;
                }

                counts[grade]++;
                graded += written;
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new TierResult(
            asOf, members.Count, graded, alreadyGraded, noIndicators, noBar, collided,
            counts[Rising], counts[Mixed], counts[Falling], summary.RowsWritten, RunOutcome.Clean);
    }

    /// <summary>
    /// The grade, from the close and the three averages.
    ///
    /// Pure and separated from the run, so the partition can be proved against figures written by
    /// hand. The property worth proving is that the three are exhaustive and exclusive: a name
    /// graded both ways, or neither, would be a hole in something every later stage reads.
    /// </summary>
    public static string Grade(decimal close, IIndicatorFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        if (close > figures.EmaShort && figures.EmaShort > figures.EmaMedium && figures.EmaMedium > figures.EmaLong)
        {
            return Rising;
        }

        if (close < figures.EmaShort && figures.EmaShort < figures.EmaMedium && figures.EmaMedium < figures.EmaLong)
        {
            return Falling;
        }

        // Everything else, including every equality. Two averages exactly equal is a real state on
        // a flat series and it is neither a rise nor a fall, so the strict comparisons above are
        // strict on purpose rather than by oversight.
        return Mixed;
    }

    /// <summary>
    /// The last instant of a session, in the session's own zone.
    ///
    /// <b>This was a DateTimeOffset built on TimeSpan.Zero until 3.10.</b> That closes an Eastern
    /// session at 19:59:59 Eastern in summer and 18:59:59 in winter, so a stage running in its own
    /// evening slot sat inside the bound and a night that ran late fell outside it, silently and
    /// differently either side of the clock change. The 3.9 pass closed twelve sites of the same
    /// defect written as a string concatenation and left this one, because the guard it added reads
    /// for the concatenation and a constructor is not one.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    private DateTimeOffset EndOf(DateOnly session) =>
        SessionBoundaries.EndOfSession(session, _options.SessionZone);

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateOnly asOf,
        DateTimeOffset computedAt,
        StoredIndicators figures,
        string grade)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20, ladder_grade)
            VALUES (@ticker, @as_of, @computed_at, @ema_9, @ema_21, @ema_50, @atr_14, @adr_20, @dollar_volume_median_20, @range_avg_20, @ladder_grade)
            ON CONFLICT (ticker, as_of, computed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));
        command.Parameters.AddWithValue("@ema_9", StoreText.PriceToStorageText(figures.EmaShort));
        command.Parameters.AddWithValue("@ema_21", StoreText.PriceToStorageText(figures.EmaMedium));
        command.Parameters.AddWithValue("@ema_50", StoreText.PriceToStorageText(figures.EmaLong));
        command.Parameters.AddWithValue("@atr_14", StoreText.PriceToStorageText(figures.AverageTrueRange));
        command.Parameters.AddWithValue("@adr_20", StoreText.RatioToStorageText(figures.AverageDailyRange));
        command.Parameters.AddWithValue("@dollar_volume_median_20", StoreText.PriceToStorageText(figures.DollarVolumeMedian));
        command.Parameters.AddWithValue("@range_avg_20", StoreText.PriceToStorageText(figures.RangeAverage));
        command.Parameters.AddWithValue("@ladder_grade", grade);

        return command.ExecuteNonQuery();
    }
}

/// <summary>What one grading run did, with the three grades counted separately.</summary>
public sealed record TierResult(
    DateOnly AsOf,
    int Members,
    int Graded,
    int AlreadyGraded,
    int NoIndicators,
    int NoBar,
    int Collided,
    int Rising,
    int Mixed,
    int Falling,
    int RowsWritten,
    RunOutcome Outcome);
