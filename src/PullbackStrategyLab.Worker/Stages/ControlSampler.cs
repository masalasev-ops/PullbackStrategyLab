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
        IReadOnlyList<ControlMatching.Candidate> pool = Pool(connection, asOf, drawnAt, flagged, _options.SessionZone);
        IReadOnlyList<ControlMatching.Candidate> moodPool = MoodPool(connection, asOf, drawnAt, _options.SessionZone);
        var figures = Figures(connection, asOf, drawnAt, _options.SessionZone);

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
                    // Two pools, and which one a set draws from is the whole of the ruling. The
                    // loose set matches on liquidity and daily range, both properties of the name
                    // rather than of the session, so it has nothing to gain from reaching across
                    // nights and would pay the same cost for it. Keeping one set within the night
                    // also keeps a within-night comparison on the scoreboard beside the
                    // across-session one, which is what makes the cost readable rather than assumed.
                    // see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
                    IReadOnlyList<ControlMatching.Draw> drawn = ControlMatching.Nearest(
                        subject, isTight ? moodPool : pool, MeasurementParameters.ControlsPerSet, isTight);

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
        SqliteConnection connection, DateOnly asOf, DateTimeOffset drawnAt, IReadOnlySet<string> flagged,
        string sessionZone) =>
        [.. Figures(connection, asOf, drawnAt, sessionZone).Values.Where(c => !flagged.Contains(c.Ticker))];

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
        SqliteConnection connection, DateOnly asOf, DateTimeOffset drawnAt, string sessionZone)
    {
        var figures = new Dictionary<string, ControlMatching.Candidate>(StringComparer.Ordinal);

        // Read once for the session rather than once per name. The mood is a property of the
        // session, which is the whole reason it could not be a dimension inside one night, so a
        // lookup per row would issue one query per candidate: the tight pool spans every session
        // sharing the mood, so that is a query per name per session and it grows with the record.
        string? mood = Mood(connection, asOf);

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
        command.Parameters.AddWithValue("@drawn_at", StoreText.EndOfSession(asOf, sessionZone));

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
                reader.IsDBNull(3) ? null : reader.GetString(3),
                asOf,
                mood);
        }

        return figures;
    }

    /// <summary>
    /// The candidates the tight set may draw from: every session at or before the as-of that
    /// carries the same market mood, and on each of those sessions the names that cleared the
    /// liquidity floor and were not flagged on that session.
    ///
    /// <b>This is what makes the mood a dimension rather than a formality.</b> Within one night the
    /// mood is a property of the session, so matching on it excludes nothing; the tight set was
    /// declared to match on the trend ladder and the mood and had only ever matched on the ladder.
    /// The operator ruled the dimension is kept and made real.
    /// see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
    ///
    /// <b>Not flagged is decided on the candidate's own session, not on tonight's.</b> A control that
    /// was itself a setup is not a control, and "was it flagged" is a question about the night it is
    /// drawn from. Excluding tonight's flagged names from a pool spanning two years would exclude
    /// the wrong rows in both directions: it would drop names that were ordinary on the session
    /// being drawn from, and admit names that were flagged on it.
    ///
    /// <b>Bounded at the as-of and per session at that session's own end of day.</b> Every session in
    /// the pool is at or before the setup's, so nothing here can see a bar the lab could not have
    /// had; and each session's figures are read on the same terms the loose pool reads tonight's,
    /// which is the end of that session's own day rather than the run instant, because TierClassifier
    /// writes the ladder grade as a later observation of the same session.
    /// see: A reader's signature does not establish point-in-time; the query does
    ///
    /// <b>It grows with the record and is not bounded by a lookback.</b> A lookback would be a new
    /// authored number and the decision names none; the ruling says any session sharing the mood.
    /// The pool is built once per night rather than per setup, the arithmetic is in memory and
    /// costs no vendor call, and the live store gains one session a night. If this becomes the
    /// stage's cost rather than a rounding, the fix is a decision about how far back a control may
    /// be drawn from, not a constant quietly added here.
    /// </summary>
    private static IReadOnlyList<ControlMatching.Candidate> MoodPool(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset drawnAt, string sessionZone)
    {
        if (Mood(connection, asOf) is not string mood)
        {
            // The night has no label, so no session can be said to share it. An unlabelled night
            // draws no tight controls rather than drawing from every session: matching on an unknown
            // is the comparison true by construction that this whole change exists to remove.
            return [];
        }

        var sessions = new List<DateOnly>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT as_of FROM regime_daily
                 WHERE as_of <= @as_of AND label = @label
                 ORDER BY as_of
                """;
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@label", mood);

            using SqliteDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
            }
        }

        var pool = new List<ControlMatching.Candidate>();

        foreach (DateOnly session in sessions)
        {
            IReadOnlySet<string> flaggedThen = FlaggedOn(connection, session);

            foreach (ControlMatching.Candidate candidate in
                Figures(connection, session, drawnAt, sessionZone).Values)
            {
                if (!flaggedThen.Contains(candidate.Ticker))
                {
                    pool.Add(candidate);
                }
            }
        }

        return pool;
    }

    /// <summary>The names either detector flagged on one session, which may not be controls for it.</summary>
    private static IReadOnlySet<string> FlaggedOn(SqliteConnection connection, DateOnly session)
    {
        var flagged = new HashSet<string>(StringComparer.Ordinal);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM setup WHERE as_of = @as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(session));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            flagged.Add(reader.GetString(0));
        }

        return flagged;
    }

    /// <summary>
    /// One session's market-mood label, or null where the night was never labelled.
    ///
    /// <b>Read as a value and never compared against a named one.</b> Nothing in this stage asks
    /// which mood a session is in; it asks whether two sessions carry the same one. The decision
    /// that the label filters nothing in the baseline is about the baseline choosing stocks, and
    /// this is the measurement choosing what to compare them against.
    /// see: The market-mood label is recorded on every setup and filters nothing in the baseline
    /// </summary>
    private static string? Mood(SqliteConnection connection, DateOnly asOf) =>
        RegimeReader.Read(connection, asOf)?.Label;

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
                (control_id, setup_id, control_ticker, control_set, match_quality, rank, drawn_at,
                 control_as_of)
            VALUES (@control_id, @setup_id, @control_ticker, @control_set, @match_quality, @rank,
                    @drawn_at, @control_as_of)
            ON CONFLICT (control_id) DO NOTHING
            """;

        command.Parameters.AddWithValue("@control_id", $"{setupId}-{controlSet}-{draw.Ticker}");
        command.Parameters.AddWithValue("@setup_id", setupId);
        command.Parameters.AddWithValue("@control_ticker", draw.Ticker);
        command.Parameters.AddWithValue("@control_set", controlSet);
        command.Parameters.AddWithValue("@match_quality", JsonSerializer.Serialize(draw.MatchQuality, MatchJson));
        command.Parameters.AddWithValue("@rank", draw.Rank);
        command.Parameters.AddWithValue("@drawn_at", StoreText.TimestampToStorageText(drawnAt));

        // The control's own session, which is no longer the setup's for the tight set. The forward
        // fill measures a control's outcome over its own bars from this date, and the ATR it is
        // expressed in is read on this date.
        command.Parameters.AddWithValue("@control_as_of", StoreText.DateToStorageText(draw.AsOf));

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
