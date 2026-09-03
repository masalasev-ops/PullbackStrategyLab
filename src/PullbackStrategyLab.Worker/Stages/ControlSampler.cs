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
/// <b>Both sets draw from the setup's own session, and the tight set matches on the trend ladder.</b>
/// The market mood is a property of the session, so a within-night pool holds it fixed at the
/// subject's own value on every row: it is controlled exactly, by construction, and there is nothing
/// left for an exclusion to do. For one day, from 2026-08-30 to 2026-08-31, the tight set reached
/// into other sessions sharing the mood label so that the dimension would exclude rows. What that
/// cost was the cancellation the paired difference exists to produce, measured at about six sevenths
/// of the tight comparison's effective sample, and the ruling was reversed.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
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
        Console.WriteLine(
            $"{Name}: {result.KeptOutForWantOfABar} indicated name(s) kept out of the pool for want of a bar on the session");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One night's draw, over the setups the detectors recorded and before the cap runs.</summary>
    public ControlResult Draw(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        var source = new StoredFigures(connection);
        return Draw(connection, source, asOf, SubjectTables.Evidence);
    }

    /// <summary>
    /// The same draw over either population, on a connection the caller owns.
    ///
    /// <b>One pool serves both sets, and that is the whole of the reversal.</b> The draw took a
    /// `reach` argument for one day, naming the sessions a tight draw could cross into, and a
    /// reconstructed read had to pass the range it was computed over so its figures kept their
    /// population. Neither exists now: a control comes from the subject's own session on both sets,
    /// so there is no range to state and no lookback to bound.
    /// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
    /// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
    /// </summary>
    public ControlResult Draw(
        SqliteConnection connection,
        ISessionFigures source,
        DateOnly asOf,
        SubjectTables tables)
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
                    // One pool, the night's own, for both sets. What separates them is which
                    // dimensions `Nearest` matches on rather than which rows it is handed: the
                    // trend ladder varies across a night's pool and is what makes the tight set
                    // tighter, and the market mood does not vary, which is what makes it controlled.
                    // see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
                    IReadOnlyList<ControlMatching.Draw> drawn = ControlMatching.Nearest(
                        subject, pool, MeasurementParameters.ControlsPerSet, isTight);

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
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean,
            source.KeptOutForWantOfABar(asOf, _options.SessionZone));
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
    RunOutcome Outcome,
    int KeptOutForWantOfABar = 0);
