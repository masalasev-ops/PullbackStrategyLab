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
        var source = new StoredFigures(connection);
        return Draw(connection, source, asOf, SubjectTables.Evidence, reach: null);
    }

    /// <summary>
    /// The same draw over either population, on a connection the caller owns.
    ///
    /// <b><paramref name="reach"/> is the sessions a tight draw may reach across, and it is the
    /// caller's rather than this stage's.</b> Null means every session the seam can answer for,
    /// which is what a forward night has always done. A reconstructed read passes the range it is
    /// computed over, because that range is the population its figures are stated against and not a
    /// lookback. **No bound is added here**: this stage's own source says the fix for its cost is a
    /// decision about how far back a control may be drawn from rather than a constant added
    /// quietly, and that decision has not been taken.
    /// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
    /// </summary>
    public ControlResult Draw(
        SqliteConnection connection,
        ISessionFigures source,
        DateOnly asOf,
        SubjectTables tables,
        IReadOnlySet<DateOnly>? reach)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tables);

        using RunScope run = _runLogger.Begin(connection, Name, tables.Control);

        DateTimeOffset drawnAt = _clock.UtcNow;

        IReadOnlyList<StoredSetup> setups = tables.IsEvidence
            ? SetupReader.Read(connection, asOf)
            : SetupReader.ReadCalibration(connection, asOf);
        var flagged = new HashSet<string>(setups.Select(s => s.Ticker), StringComparer.Ordinal);
        IReadOnlyList<ControlMatching.Candidate> pool = Pool(source, connection, asOf, flagged, _options.SessionZone);
        IReadOnlyList<ControlMatching.Candidate> moodPool =
            MoodPool(source, connection, asOf, _options.SessionZone, tables, reach);
        IReadOnlyDictionary<string, ControlMatching.Candidate> figures =
            source.Candidates(asOf, _options.SessionZone);

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
                        int written = Insert(connection, transaction, setup.SetupId, set, draw, drawnAt, tables);

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
        ISessionFigures source, SqliteConnection connection, DateOnly asOf, IReadOnlySet<string> flagged,
        string sessionZone) =>
        [.. source.Candidates(asOf, sessionZone).Values.Where(c => !flagged.Contains(c.Ticker))];

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
        ISessionFigures source, SqliteConnection connection, DateOnly asOf, string sessionZone,
        SubjectTables tables, IReadOnlySet<DateOnly>? reach)
    {
        if (source.Mood(asOf) is not string mood)
        {
            // The night has no label, so no session can be said to share it. An unlabelled night
            // draws no tight controls rather than drawing from every session: matching on an unknown
            // is the comparison true by construction that this whole change exists to remove.
            return [];
        }

        var sessions = new List<DateOnly>();

        // <b>Which sessions share the mood is the seam's answer, not `regime_daily`'s.</b> A forward
        // night has a stored label per session and the query below is what reads them. A
        // reconstructed session has none and may not be given one, so its mood lives only in the
        // figures the walk computed: reading the table there returns the two forward nights the lab
        // has actually run, which share no mood with a 2026-05 session and produce an empty pool.
        //
        // That is what it did. The first 60-session read drew 46,295 loose controls and **nought**
        // tight ones, and every tight panel came back withheld at nought nights: not a wrong
        // interval, no interval, on the half the whole ruling was about.
        if (reach is not null)
        {
            foreach (DateOnly session in reach.Where(d => d <= asOf).OrderBy(d => d))
            {
                if (string.Equals(source.Mood(session), mood, StringComparison.Ordinal))
                {
                    sessions.Add(session);
                }
            }
        }
        else
        {
            using SqliteCommand command = connection.CreateCommand();
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
            // The caller's range, where it gave one. A session outside it is not a session this
            // read is computed over, so a control drawn from it would be a row from a population
            // the figures do not name.
            if (reach is not null && !reach.Contains(session))
            {
                continue;
            }

            IReadOnlySet<string> flaggedThen = FlaggedOn(connection, session, tables);

            foreach (ControlMatching.Candidate candidate in
                source.Candidates(session, sessionZone).Values)
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
    private static IReadOnlySet<string> FlaggedOn(
        SqliteConnection connection, DateOnly session, SubjectTables tables)
    {
        var flagged = new HashSet<string>(StringComparer.Ordinal);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT ticker FROM {tables.Setup} WHERE as_of = @as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(session));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            flagged.Add(reader.GetString(0));
        }

        return flagged;
    }

    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string setupId,
        string controlSet,
        ControlMatching.Draw draw,
        DateTimeOffset drawnAt,
        SubjectTables tables)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Two literal statements rather than an interpolated table name, so `writer-ownership` can
        // see both writes and attribute both to this stage. An interpolated name matches nothing in
        // its scan, which is a check narrowing itself silently.
        command.CommandText = tables.IsEvidence
            ? """
                INSERT INTO control_setup
                    (control_id, setup_id, control_ticker, control_set, match_quality, rank, drawn_at,
                     control_as_of)
                VALUES (@control_id, @setup_id, @control_ticker, @control_set, @match_quality, @rank,
                        @drawn_at, @control_as_of)
                ON CONFLICT (control_id) DO NOTHING
              """
            : """
                INSERT INTO calibration_control_setup
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
