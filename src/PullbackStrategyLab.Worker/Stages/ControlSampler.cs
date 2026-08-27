using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The comparison population, drawn nightly, loose and tight.
///
/// <b>Every figure phase 3 produces is a comparison, and this is what it is compared against.</b>
/// Flagged setups returning 2% over ten days is not a result if the whole market returned 2% that
/// fortnight. The loose set matches on liquidity and daily-range decile and measures the whole
/// funnel; the tight set also matches on the trend ladder and answers the question that can
/// embarrass the project, which is whether the pattern is worth anything beyond owning stocks in
/// uptrends.
/// see: Matched control populations are drawn nightly, loose and tight
///
/// <b>At 18:26, before the cap at 18:28.</b> Controls answer for the flagged population rather than
/// for the sixty that survived truncation, and drawing after the cap would compare the kept setups
/// against controls for a different question.
///
/// <b>No vendor call.</b> Everything it reads is already stored, which is why a comparison this
/// good is free and why there is no excuse for not having one.
/// </summary>
public sealed class ControlSampler
{
    public const string Name = "controls";

    private static readonly JsonSerializerOptions MatchJson = new(JsonSerializerDefaults.Web);

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ControlSampler(
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

        ControlResult result = Draw(asOf);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Setups} setup(s), {result.Pool} candidate(s) in the pool");
        Console.WriteLine($"{Name}: {result.Loose} loose and {result.Tight} tight control(s) drawn");
        Console.WriteLine($"{Name}: {result.ShortOfFive} set(s) came up short of {MeasurementParameters.ControlsPerSet}");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One night's draw, over the setups the detectors recorded and before the cap runs.</summary>
    public ControlResult Draw(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "control_setup");

        DateTimeOffset drawnAt = _clock.UtcNow;

        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, asOf);
        var flagged = new HashSet<string>(setups.Select(s => s.Ticker), StringComparer.Ordinal);
        IReadOnlyList<ControlMatching.Candidate> pool = Pool(connection, asOf, drawnAt, flagged);
        var figures = Figures(connection, asOf, drawnAt);

        int loose = 0;
        int tight = 0;
        int shortOfFive = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (StoredSetup setup in setups)
            {
                if (!figures.TryGetValue(setup.Ticker, out ControlMatching.Candidate? subject))
                {
                    // A name with no figures on its own night cannot be matched on them. It is not
                    // an error and it is not silent: the shortfall is counted and reported.
                    shortOfFive += 2;
                    continue;
                }

                foreach ((string set, bool isTight) in new[] { ("loose", false), ("tight", true) })
                {
                    IReadOnlyList<ControlMatching.Draw> drawn = ControlMatching.Nearest(
                        subject, pool, MeasurementParameters.ControlsPerSet, isTight);

                    if (drawn.Count < MeasurementParameters.ControlsPerSet)
                    {
                        shortOfFive++;
                    }

                    foreach (ControlMatching.Draw draw in drawn)
                    {
                        int written = Insert(connection, transaction, setup.SetupId, set, draw, drawnAt);

                        if (isTight)
                        {
                            tight += written;
                        }
                        else
                        {
                            loose += written;
                        }
                    }
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new ControlResult(
            asOf, setups.Count, pool.Count, loose, tight, shortOfFive,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// The names a control may be drawn from: universe members that cleared the liquidity floor on
    /// the night and were not flagged by either detector.
    ///
    /// Not flagged is the whole point. A control that was itself a setup is not a control, and a
    /// pool that quietly admitted them would narrow every comparison toward zero without changing
    /// any number a reader could see.
    /// </summary>
    private static IReadOnlyList<ControlMatching.Candidate> Pool(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset drawnAt, IReadOnlySet<string> flagged) =>
        [.. Figures(connection, asOf, drawnAt).Values.Where(c => !flagged.Contains(c.Ticker))];

    /// <summary>
    /// Every name's matched figures on the night, bounded on the end of the as-of date.
    ///
    /// <b>The end of the date rather than the run instant, and the difference is not pedantry.</b>
    /// TierClassifier writes the ladder grade as a <i>later observation</i> of the same session
    /// rather than updating the row IndicatorEngine wrote, which is what 2.4 decided. Bounded on the
    /// run instant, this read takes the engine's row and every grade comes back null, so the tight
    /// set's ladder filter compares null to null, excludes nothing, and draws exactly the loose set.
    /// It did: the first run of this stage produced identical loose and tight sets for all three
    /// fixture setups, and the two figures agreeing is not something a count would have shown.
    /// `IndicatorDailyReader` bounds on the end of the date for the same reason, and this follows it.
    ///
    /// The liquidity floor is applied here rather than in the pool, because the subject of a draw
    /// has to be readable on the same terms as its candidates: a setup matched against a pool
    /// filtered differently from itself is matched on a dimension nobody stated.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private static IReadOnlyDictionary<string, ControlMatching.Candidate> Figures(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset drawnAt)
    {
        var figures = new Dictionary<string, ControlMatching.Candidate>(StringComparer.Ordinal);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.ticker, i.dollar_volume_median_20, i.adr_20, i.ladder_grade
              FROM indicator_daily i
             WHERE i.as_of = @as_of
               AND i.computed_at <= @drawn_at
               AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                     WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                       AND c.computed_at <= @drawn_at)
             ORDER BY i.ticker
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@drawn_at", $"{asOf:yyyy-MM-dd}T23:59:59.999Z");

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            decimal turnover = reader.IsDBNull(1) ? 0m : StoreText.StorageTextToPrice(reader.GetString(1));

            if (turnover < Core.Detection.LongPullbackRules.LiquidityFloor)
            {
                continue;
            }

            figures[reader.GetString(0)] = new ControlMatching.Candidate(
                reader.GetString(0),
                turnover,
                reader.IsDBNull(2) ? 0m : StoreText.StorageTextToPrice(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }

        return figures;
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string setupId,
        string controlSet,
        ControlMatching.Draw draw,
        DateTimeOffset drawnAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO control_setup
                (control_id, setup_id, control_ticker, control_set, match_quality, rank, drawn_at)
            VALUES (@control_id, @setup_id, @control_ticker, @control_set, @match_quality, @rank, @drawn_at)
            ON CONFLICT (control_id) DO NOTHING
            """;

        command.Parameters.AddWithValue("@control_id", $"{setupId}-{controlSet}-{draw.Ticker}");
        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@control_ticker", draw.Ticker);
        command.Parameters.AddWithValue("@control_set", controlSet);
        command.Parameters.AddWithValue("@match_quality", JsonSerializer.Serialize(draw.MatchQuality, MatchJson));
        command.Parameters.AddWithValue("@rank", draw.Rank);
        command.Parameters.AddWithValue("@drawn_at", StoreText.TimestampToStorageText(drawnAt));

        return command.ExecuteNonQuery();
    }
}

/// <summary>What one night's draw produced.</summary>
public sealed record ControlResult(
    DateOnly AsOf,
    int Setups,
    int Pool,
    int Loose,
    int Tight,
    int ShortOfFive,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
