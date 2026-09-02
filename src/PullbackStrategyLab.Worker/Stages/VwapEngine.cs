using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The two volume-weighted average prices, computed over the minutes the fetch stored an hour
/// earlier and spending no vendor call of its own.
///
/// <b>The session average is no longer stored, and 4.7 is where that was decided.</b> It was written
/// onto every stored minute from 4.4 and the obligation raised at the same checkpoint said a reader
/// had to be named or the column had to stop being written. 4.7 was the checkpoint it fell due at,
/// on the reasoning that the fill model was its most likely reader, and the fill model does not read
/// it: a fill is the resting price plus the captured spread, and no rule in this lab compares a
/// price against a session average. Nothing else in the corpus reads it either, through phase 6.
///
/// <b>It stopped rather than being kept, because it is derivable and the anchored average is not.</b>
/// A running session average is a sum over the session's own stored minutes in order, so anything
/// that wants one computes it from `intraday_bar` through <see cref="VolumeWeightedAverage.Running"/>
/// at the moment it is wanted. That is the ruling this stage already took over the day's high and
/// low and WatchlistPublisher took over a watchlist table. The anchored average is a different case
/// and stays: it needs a swing nothing else resolves, and it is not recoverable from one session.
/// see: The session average is derived when it is wanted and is not stored on a bar
///
/// <b>What it bought is the last exception to a hard rule.</b> `intraday_bar.vwap_session` was the
/// one declared update against a bar table anywhere in this store, and `bar-append-only` carried it
/// by table, column and component. With the write gone the rule reads as it is written, with nothing
/// after the comma.
///
/// <b>The anchored average is the third clause of `reached-ceiling`</b>, deferred since 2.7 and the
/// disjunct 423 of the 432 short calibration rows reaching that gate are refused for want of. It is
/// anchored at the swing the thrust ran from, which `ShortSetupDetector.AnchorSessionOf` names and
/// this stage prices.
/// see: The anchored average price is anchored at the swing the thrust ran from
///
/// <b>It is not what empties the short funnel, and 4.4 measured that rather than assuming it.</b>
/// Given the clause its maximum, so that every one of those 432 rows is admitted, the short funnel
/// still ends at 4 survivors over the 602 calibration sessions, median nought a night. The gate that
/// binds is `exit-tight`, at 0.93% over the 431 short rows that then reach it against 1.51% over
/// 1,981 long rows on the same sessions: a comparable per-row rate, so what the short side is short
/// of is rows reaching the gate rather than a gate set too strict. This stage still has to be right,
/// and the reason is the verdict it decides rather than a funnel it was never going to refill.
///
/// <b>It reads the store and does not fetch, and that is a decision with a cost.</b> The vendor
/// holds minute bars well past any anchor this lab would ask about: one call on 2026-09-01 for
/// `intraday/AAPL.US` over 2026-05-05 to 2026-08-31 returned 78,662 bars across 82 sessions, and the
/// vendor's own 422 states the per-request window as 120 days. So the anchor is inside the vendor's
/// reach and outside the store's, because IntradayFetcher buys one session a night per flagged name
/// and a swing sits three to twenty-seven sessions back. Widening that window is free in vendor
/// calls, since the cost is charged per request and not per session, and expensive in rows; it is
/// the fetch's decision rather than this stage's, and it is carried as an obligation rather than
/// taken here.
///
/// <b>So an unreachable anchor is recorded, not skipped.</b> A row with a null value and a reason is
/// how a night says it had an anchor and could not price it, which is a different fact from a night
/// that anchored nothing and from a night nobody ran.
/// see: A gate handed an absent or degenerate quantity fails rather than passing
///
/// <b>The day's high and low are not stored, and the catalogue's description of this component names
/// them.</b> They are <c>MAX(high)</c> and <c>MIN(low)</c> over the session's stored minutes, so a
/// column here would be a second statement of something already in the store that could disagree
/// with it, which is the ruling WatchlistPublisher took at 4.1 for the same reason. The two averages
/// are different: neither is recoverable from a single row, both need the whole run in order, and
/// the anchored one needs a swing nothing else resolves.
///
/// <b>And the session average has no named reader anywhere in the corpus</b>, which is the shape 4.3
/// found for the spread and fixed by naming entry slippage at the capture. It is carried as an
/// obligation rather than settled here: this one spends no vendor call, so the argument that a
/// capture nobody consumes cannot be justified is weaker, but a column written every night that
/// nothing reads is still a column somebody will eventually take for evidence.
/// </summary>
public sealed class VwapEngine
{
    public const string Name = "vwap";

    /// <summary>No session before this one flagged anything, so no plan was live in it.</summary>
    public const string NoPriorSession =
        "no session before this one flagged a setup, so no plan was live in this session";

    /// <summary>The fetch stored nothing for this name and session.</summary>
    public const string NoMinuteBars = "the store holds no minute bars for this name and session";

    /// <summary>
    /// The ordinary reason an anchor cannot be priced, and it is a fact about the store rather than
    /// about the vendor.
    /// </summary>
    public const string AnchorNotStored =
        "the store holds no minute bars for the anchor session, so the average cannot start where the rule says";

    /// <summary>Minutes back to the anchor exist and none of them traded a share.</summary>
    public const string NoVolumeFromTheAnchor =
        "no volume traded from the anchor forward, so there is no volume-weighted price";

    /// <summary>The setup carries no thrust, so there is no move and no swing to anchor at.</summary>
    public const string NoAnchor = "the setup records no thrust, so there is no swing to anchor at";

    /// <summary>The anchor is a swing high on the short side. Named for what was measured.</summary>
    public const string SwingHigh = "swing-high";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public VwapEngine(
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

    public Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly sessionDate = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        VwapRunResult result = Compute(sessionDate);

        Console.WriteLine(
            $"{Name}: session {result.SessionDate:yyyy-MM-dd}, "
            + (result.SetupAsOf is DateOnly asOf
                ? $"the names flagged on the evening of {asOf:yyyy-MM-dd}"
                : "no prior session has flagged setups, so nothing was priced"));
        Console.WriteLine(
            $"{Name}: {result.Names} flagged name(s) whose minutes the fetch bought");
        Console.WriteLine(
            $"{Name}: {result.AnchorsAsked} anchor(s) asked, {result.AnchorsPriced} priced, "
            + $"{result.AnchorsAsked - result.AnchorsPriced} out of the store's reach");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} row(s) written"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return Task.FromResult(result.Outcome == RunOutcome.Failed ? 1 : 0);
    }

    /// <summary>
    /// Price one session's stored minutes and every anchor the evening's setups name.
    ///
    /// <paramref name="sessionDate"/> is the session the bars are for, which on a scheduled run is
    /// the session that has just closed and whose minutes landed at 20:30.
    /// </summary>
    public VwapRunResult Compute(DateOnly sessionDate)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "intraday_bar", "anchored_vwap", "vwap_run");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly? setupAsOf = IntradayFetcher.PreviousFlaggedSession(connection, sessionDate);

        // The same offset the fetch settled and the spread capture carries. This stage prices the
        // bars that fetch stored, so it inherits the pairing rather than deciding one of its own.
        // see: Minute bars are fetched for the session a plan was live in, never the session it was written on
        if (setupAsOf is null)
        {
            RecordRun(connection, sessionDate, sessionDate, 0, 0, 0, RunOutcome.Clean, NoPriorSession, observedAt);
            RunSummary nothing = run.Complete(RunOutcome.Clean);

            return new VwapRunResult(
                sessionDate, null, 0, 0, 0, nothing.RowsWritten, RunOutcome.Clean, NoPriorSession);
        }

        IntradayFetcher.Pairing pairing = IntradayFetcher.Pairing.Of(sessionDate, setupAsOf.Value);

        // Every flagged name, which is the population whose minutes the fetch bought. It is counted
        // and no longer walked: the session average this stage used to write onto each of their
        // minutes stopped being written at 4.7, and the anchors below are drawn from the short
        // setups rather than from this list.
        IReadOnlyList<string> names = IntradayFetcher.FlaggedNames(connection, pairing.SetupAsOf);

        int asked = 0;
        int anchored = 0;

        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (ShortSetupRow row in ShortSetups(connection, transaction, pairing.SetupAsOf))
        {
            asked++;

            if (PriceAnchor(connection, transaction, row, pairing, observedAt))
            {
                anchored++;
            }
        }

        transaction.Commit();

        // Clean whatever the anchors did. An anchor out of the store's reach is the expected state
        // for a long time and is not a degraded run: nothing was asked of the vendor, nothing
        // failed, and the rows say exactly what could not be reached. A run that called this partial
        // would report every night as partial until the store had accumulated years of minutes,
        // which is a signal that means nothing.
        RunSummary summary = run.Complete(RunOutcome.Clean);
        RecordRun(
            connection, sessionDate, pairing.SetupAsOf, names.Count, asked, anchored,
            RunOutcome.Clean, null, observedAt);

        return new VwapRunResult(
            sessionDate, pairing.SetupAsOf, names.Count, asked, anchored,
            summary.RowsWritten, RunOutcome.Clean, null);
    }

    /// <summary>
    /// Price one short setup's anchor, and record the row whether or not a level came out.
    ///
    /// Returns whether a level was written, which is what the night's counts separate: an anchor
    /// asked and an anchor priced are two figures because the gap between them is the whole state of
    /// the third clause.
    /// </summary>
    private static bool PriceAnchor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ShortSetupRow row,
        IntradayFetcher.Pairing pairing,
        DateTimeOffset observedAt)
    {
        if (row.ThrustScan is null || row.ThrustSession is not DateOnly thrustSession)
        {
            Record(connection, transaction, row.Ticker, pairing.SetupAsOf, null, pairing, null, 0, 0, NoAnchor, observedAt);
            return false;
        }

        IReadOnlyList<StoredDailyBar> daily = DailyBarReader.Read(
            connection, row.Ticker, pairing.SetupAsOf, ShortSetupDetector.HistorySessions);

        if (ShortSetupDetector.AnchorSessionOf(daily, row.ThrustScan, thrustSession) is not DateOnly anchorSession)
        {
            Record(connection, transaction, row.Ticker, pairing.SetupAsOf, null, pairing, null, 0, 0, NoAnchor, observedAt);
            return false;
        }

        // Every stored regular-session minute from the anchor session to the session just priced.
        // A run with a hole in it is still a run over what the store holds, and the bar count on the
        // row is what makes a thin one visible; refusing it outright would drop a level computed
        // over most of the window because one session's fetch was partial.
        var minutes = new List<VolumeWeightedAverage.Minute>();
        DateTimeOffset? anchorAt = null;

        foreach (DateOnly session in SessionsWithBars(connection, transaction, row.Ticker, anchorSession, pairing.SessionDate))
        {
            IReadOnlyList<StoredIntradayBar> bars = IntradayBarReader.Read(
                connection, row.Ticker, session, pairing.SessionDate, regularOnly: true);

            if (session == anchorSession)
            {
                // The minute the swing high traded in, which is what "anchored to the last swing
                // high" names. Earliest where the high is touched more than once, because the anchor
                // is where the move began and a later equal high is a retest of it.
                StoredIntradayBar? peak = bars
                    .OrderByDescending(b => b.High)
                    .ThenBy(b => b.OpenedAt)
                    .FirstOrDefault();

                if (peak is null)
                {
                    Record(
                        connection, transaction, row.Ticker, pairing.SetupAsOf, anchorSession, pairing,
                        null, 0, 0, AnchorNotStored, observedAt);
                    return false;
                }

                anchorAt = peak.OpenedAt;
            }

            minutes.AddRange(bars.Select(b =>
                new VolumeWeightedAverage.Minute(b.OpenedAt, b.High, b.Low, b.Close, b.Volume)));
        }

        if (anchorAt is not DateTimeOffset anchor)
        {
            Record(
                connection, transaction, row.Ticker, pairing.SetupAsOf, anchorSession, pairing,
                null, 0, 0, AnchorNotStored, observedAt);
            return false;
        }

        VolumeWeightedAverage.Minute[] fromAnchor = [.. minutes.Where(m => m.OpenedAt >= anchor)];
        decimal? value = VolumeWeightedAverage.Of(fromAnchor);
        long volume = fromAnchor.Sum(m => m.Volume);

        Record(
            connection, transaction, row.Ticker, pairing.SetupAsOf, anchorSession, pairing,
            value, fromAnchor.Length, volume, value is null ? NoVolumeFromTheAnchor : null, observedAt,
            anchor);

        return value is not null;
    }

    /// <summary>
    /// The sessions between the anchor and the priced session that this name has stored minutes
    /// for, in order.
    ///
    /// Read as the sessions the store holds rather than as a calendar walk, so a market holiday, a
    /// halted name and a night the fetch could not reach all read the same way: a session with no
    /// bars contributes nothing and is not a gap anybody has to classify.
    /// </summary>
    private static IReadOnlyList<DateOnly> SessionsWithBars(
        SqliteConnection connection, SqliteTransaction transaction, string ticker, DateOnly from, DateOnly to)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT session_date
              FROM intraday_bar
             WHERE ticker = @ticker
               AND session_date >= @from
               AND session_date <= @to
               AND observed_at <= @observed_before
             ORDER BY session_date;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(to));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(to, SessionBoundaries.UsEquities));

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return sessions;
    }

    /// <summary>The evening's short setups, which are the only ones with a ceiling clause to feed.</summary>
    private static IReadOnlyList<ShortSetupRow> ShortSetups(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ticker, thrust_scan, thrust_session
              FROM setup
             WHERE as_of = @as_of
               AND direction = @direction
             ORDER BY ticker;
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", SetupDirection.Short);

        var rows = new List<ShortSetupRow>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new ShortSetupRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : StoreText.StorageTextToDate(reader.GetString(2))));
        }

        return rows;
    }

    private static void Record(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ticker,
        DateOnly setupAsOf,
        DateOnly? anchorSession,
        IntradayFetcher.Pairing pairing,
        decimal? value,
        int bars,
        long volume,
        string? absentBecause,
        DateTimeOffset observedAt,
        DateTimeOffset? anchorAt = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO anchored_vwap
                (ticker, anchor_session, anchor_ts, anchor_kind, through_session, setup_as_of,
                 value, bars, volume, absent_because, observed_at)
            VALUES (@ticker, @anchor_session, @anchor_ts, @anchor_kind, @through_session, @setup_as_of,
                    @value, @bars, @volume, @absent_because, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        // A setup with no swing has no anchor session, and the row still exists to say so. The
        // setup's own session stands in, which is the only date the row is about, and
        // `absent_because` is what a reader takes the meaning from.
        command.Parameters.AddWithValue(
            "@anchor_session", StoreText.DateToStorageText(anchorSession ?? setupAsOf));
        command.Parameters.AddWithValue(
            "@anchor_ts",
            anchorAt is DateTimeOffset at ? StoreText.TimestampToStorageText(at) : DBNull.Value);
        command.Parameters.AddWithValue("@anchor_kind", SwingHigh);
        command.Parameters.AddWithValue("@through_session", StoreText.DateToStorageText(pairing.SessionDate));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(setupAsOf));
        command.Parameters.AddWithValue(
            "@value", value is decimal price ? StoreText.PriceToStorageText(price) : DBNull.Value);
        command.Parameters.AddWithValue("@bars", bars);
        command.Parameters.AddWithValue("@volume", volume);
        command.Parameters.AddWithValue("@absent_because", (object?)absentBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RecordRun(
        SqliteConnection connection,
        DateOnly sessionDate,
        DateOnly setupAsOf,
        int names,
        int anchorsAsked,
        int anchorsPriced,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vwap_run
                (session_date, setup_as_of, names,
                 anchors_asked, anchors_priced, outcome, stopped_because, observed_at)
            VALUES (@session_date, @setup_as_of, @names,
                    @anchors_asked, @anchors_priced, @outcome, @stopped_because, @observed_at);
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(setupAsOf));
        command.Parameters.AddWithValue("@names", names);
        command.Parameters.AddWithValue("@anchors_asked", anchorsAsked);
        command.Parameters.AddWithValue("@anchors_priced", anchorsPriced);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private sealed record ShortSetupRow(string Ticker, string? ThrustScan, DateOnly? ThrustSession);
}

/// <summary>What one night's engine did, as the stage reports it.</summary>
public sealed record VwapRunResult(
    DateOnly SessionDate,
    DateOnly? SetupAsOf,
    int Names,
    int AnchorsAsked,
    int AnchorsPriced,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause);
