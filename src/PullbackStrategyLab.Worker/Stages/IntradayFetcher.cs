using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Minute bars for every flagged setup, after the close.
///
/// <b>The only unrecoverable input the lab has.</b> The vendor's minute history reaches back a
/// bounded number of days, so a session not captured inside that window cannot be bought afterwards
/// at any price. Everything else this lab fetches can be re-asked for: daily history arrives whole
/// on every request, a symbol list is regenerated nightly, and a fundamentals lookup is the same
/// answer next week. That is why this stage leads phase 4 rather than the watchlist, which loses
/// nothing by waiting an evening.
/// see: Minute bars are fetched for every flagged setup, not only the planned ones
///
/// <b>Every flagged setup, not the capped sixty.</b> A variant that selects a name the baseline
/// passed on must still be resolvable, and a name whose bars were never bought is a name no variant
/// can ever be scored on. The capped set is a publishing decision taken after detection; this is the
/// population that decides what can be measured for ever.
/// </summary>
public sealed class IntradayFetcher
{
    public const string Name = "intraday-bars";

    /// <summary>
    /// How many sessions the fetch buys, ending at the session whose bars it is for.
    ///
    /// <b>Twenty-seven is derived rather than chosen.</b> A swing sits the thrust span plus the two
    /// to seven bar pullback back, so <c>gainer</c> and <c>gapper</c> put it 3 to 8 sessions back
    /// and <c>leader</c> and <c>laggard</c> 22 to 27. Twenty-seven reaches both scan families, so a
    /// name whose window is filled is anchorable whichever scan flagged it. Eight was refused for
    /// reaching the first family only, which would put nights carrying short rows that run the full
    /// disjunction beside short rows that cannot, and a count whose population changes partway is
    /// not a count. The vendor's 120 days were refused as history behind the anchor that nothing
    /// reads.
    /// see: The intraday fetch buys the twenty-seven session anchor window, and the count starts on the first night it runs at that width
    ///
    /// <b>It costs no extra vendor call.</b> The vendor charges per request and the window is a
    /// query parameter, so a wide night and a narrow night cost the same against the daily ceiling.
    /// What it costs is disk, once per name: 1.14 to 2.15 million rows on the first fill at 272
    /// bytes a row, being 310 to 585 MB, after which that name costs one session a night because
    /// <see cref="IntradayBarReader.IsStoredUnchanged"/> writes nothing for a bar already held.
    ///
    /// The figure itself is <see cref="ScanSpans.AnchorWindowSessions"/>, which derives it from the
    /// scan spans and the pullback's maximum length rather than holding a literal, and it lives in
    /// Core because the read surface reports a night's width against it and cannot see the Worker.
    /// </summary>
    public static int AnchorWindowSessions => ScanSpans.AnchorWindowSessions;

    /// <summary>
    /// What one row of this table spans, and the two facts about the series beside it.
    ///
    /// Constants rather than literals at the insert, because they are written on every row and read
    /// by every bound: a value spelled differently in two places would split one series into two
    /// populations that no reader could reconcile.
    /// </summary>
    public const string MinuteInterval = "1m";

    /// <summary>A bar inside the exchange's regular session.</summary>
    public const string RegularWindow = "regular";

    /// <summary>A bar outside it, before the open or after the close.</summary>
    public const string ExtendedWindow = "extended";

    /// <summary>
    /// The basis these prices are on. Raw, because a minute bar is what a trade actually gets and
    /// the vendor publishes no adjusted intraday series. Recorded per row rather than assumed, so a
    /// capture taken on another basis is visible instead of being mixed in.
    /// </summary>
    public const string RawBasis = "raw";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public IntradayFetcher(
        IMarketDataVendor vendor,
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _vendor = vendor;
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly sessionDate = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        IntradayFetchResult result = await FetchAsync(sessionDate, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"{Name}: session {result.SessionDate:yyyy-MM-dd}, "
            + (result.SetupAsOf is DateOnly asOf
                ? $"resolving setups flagged {asOf:yyyy-MM-dd}"
                : "no prior session has flagged setups, so nothing was asked for"));
        Console.WriteLine(
            $"{Name}: {result.Requested} name(s) asked, {result.Fetched} answered, {result.Empty} returned nothing, "
            + $"{result.BarsWritten} bar(s) written, {result.Unchanged} already stored unchanged");

        // The width, on its own line and on every night. Short's twenty-session count starts on the
        // first night this reads the full window, so a reader of the night's log can see which
        // nights counted without dating the run against a commit.
        Console.WriteLine(
            $"{Name}: window {result.WindowSessions} session(s) of {AnchorWindowSessions}"
            + (result.WindowSessions < AnchorWindowSessions
                ? ", which is what the store holds rather than what the window asks for"
                : ", the full anchor window"));
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Fetch and store one session's minute bars, for the setups whose plans were live in it.
    ///
    /// <paramref name="sessionDate"/> is the session the bars belong to, which on a nightly run is
    /// the session that has just closed.
    /// </summary>
    public async Task<IntradayFetchResult> FetchAsync(
        DateOnly sessionDate, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "intraday_bar");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly? setupAsOf = PreviousFlaggedSession(connection, sessionDate);

        // The first night, and it is a real state rather than a failure. Nothing was flagged before
        // this session, so no plan was live in it and there is nothing these bars would resolve.
        // Recorded as a fetch of nothing rather than skipped, because a night with no row is
        // indistinguishable from a night the scheduler never fired.
        if (setupAsOf is null)
        {
            RecordFetch(connection, sessionDate, null, 0, 0, 0, 0, 0, 0, RunOutcome.Clean, NoPriorSession, observedAt);
            RunSummary empty = run.Complete(RunOutcome.Clean);

            return new IntradayFetchResult(
                sessionDate, null, 0, 0, 0, 0, 0, 0, empty.RowsWritten, empty.CallsUsed,
                RunOutcome.Clean, NoPriorSession);
        }

        Pairing pairing = Pairing.Of(sessionDate, setupAsOf.Value);

        IReadOnlyList<string> names = FlaggedNames(connection, pairing.SetupAsOf);
        IReadOnlyList<DateOnly> window = AnchorWindow(connection, sessionDate, observedAt);
        (DateTimeOffset from, DateTimeOffset to) = WindowOf(window, sessionDate, _options.SessionZone);

        int fetched = 0;
        int empties = 0;
        int written = 0;
        int unchanged = 0;
        string? stoppedBecause = null;

        foreach (string ticker in names)
        {
            VendorResult<IReadOnlyList<VendorIntradayBar>> answer = await _vendor
                .GetIntradayAsync(ticker, from, to, run, cancellationToken).ConfigureAwait(false);

            if (answer.BudgetExhausted)
            {
                // The designed behaviour at the ceiling: stop, record what was reached, and let the
                // row say how far it got. The names not reached are named by arithmetic against
                // `requested`, which is why that column is the count asked for rather than the count
                // answered.
                stoppedBecause = CeilingReached;
                break;
            }

            fetched++;
            IReadOnlyList<VendorIntradayBar> bars = answer.Require();

            if (bars.Count == 0)
            {
                // A name the vendor holds no minutes for on this session. Counted rather than
                // treated as a failure: a halted name and a name outside the vendor's intraday
                // window both look like this, and both are facts about the night.
                empties++;
                continue;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            foreach (VendorIntradayBar bar in bars)
            {
                if (IntradayBarReader.IsStoredUnchanged(connection, transaction, ticker, bar, observedAt))
                {
                    unchanged++;
                    continue;
                }

                Insert(connection, transaction, bar, _options.SessionZone, observedAt);
                written++;
            }

            transaction.Commit();
        }

        // What the night's asking left the store holding for this window, which is the quantity the
        // outcome turns on. Written plus unchanged rather than written alone: a rerun over minutes
        // the store already has writes nought bars and has lost nothing, so `written == 0` is not
        // by itself a shortfall. Nought here with names answered is, because it says every name the
        // vendor was asked about came back with no minutes at all.
        int stored = written + unchanged;
        string? shortfall = stoppedBecause ?? NothingBought(fetched, stored);
        RunOutcome outcome = shortfall is null ? RunOutcome.Clean : RunOutcome.Partial;

        RecordFetch(
            connection, sessionDate, pairing.SetupAsOf, names.Count, fetched, empties, written,
            stored, window.Count, outcome, shortfall, observedAt);

        RunSummary summary = run.Complete(outcome);

        return new IntradayFetchResult(
            sessionDate, pairing.SetupAsOf, names.Count, fetched, empties, written, unchanged,
            window.Count, summary.RowsWritten, summary.CallsUsed, outcome, shortfall);
    }

    /// <summary>
    /// Why a night that spent calls has nothing to show for them, or null where it has.
    ///
    /// <b>The stage that could end a night having bought nothing and call it clean.</b> On
    /// 2026-09-04 it asked 92 names, all 92 answered with nothing, 460 calls were spent, 0 bars
    /// were written and the run recorded `clean` with no reason. The outcome was
    /// <c>stoppedBecause is null ? Clean : Partial</c>, which is the identical idiom
    /// <c>TriggerResolver</c> uses; the difference is that <c>TriggerResolver</c> sets its reason
    /// when a session held no minutes, while this stage set one only from the call ceiling, so its
    /// outcome was unconditional on what it stored. Every other stage in the lab reports partial on
    /// this shape.
    ///
    /// <b>Two shapes of nothing and they are not the same night.</b> A night that asked nothing is
    /// the first night or a night with no prior flagged session, and it is clean because there was
    /// nothing to lose. A night that asked and was answered with no minutes at all has spent calls
    /// on names the vendor holds nothing for, which is either a halt, a horizon that has moved past
    /// the session, or a fault, and the row says which shape it was rather than which cause. The
    /// cause is not knowable from here and is not claimed.
    /// </summary>
    public static string? NothingBought(int fetched, int stored) =>
        fetched > 0 && stored == 0 ? BoughtNothing : null;

    /// <summary>Recorded on the first night, when nothing had been flagged before the session.</summary>
    public const string NoPriorSession = "no prior session has flagged setups";

    /// <summary>Recorded when the day's call ceiling stopped the walk part way through.</summary>
    public const string CeilingReached = "the daily call ceiling was reached";

    /// <summary>
    /// Recorded when every name the vendor was asked about answered with no minutes at all, so the
    /// night spent calls and the store holds nothing for the window it bought.
    /// </summary>
    public const string BoughtNothing =
        "every name answered with no minutes, so the calls were spent and nothing was bought";

    /// <summary>
    /// The sessions this night buys, oldest first: the anchor window's width, ending at
    /// <paramref name="sessionDate"/>.
    ///
    /// <b>Read from the store rather than counted off the calendar, because the lab authors no
    /// trading calendar.</b> Twenty-seven sessions back is not thirty-nine days back: weekends and
    /// holidays move it, and a fixed calendar width would buy a different number of sessions in
    /// every month of the year. <c>daily_bar</c> is the record of which days actually traded, on the
    /// same terms both detectors and <c>SessionFigures</c> already walk it, so the window is the
    /// sessions the lab knows about and the count it records is a fact rather than an aim.
    ///
    /// <b>Bounded on the run's own instant, like every other read this stage makes.</b> A session
    /// the store learned about after the run began is not one this run buys.
    ///
    /// It returns fewer than <see cref="AnchorWindowSessions"/> where the store holds fewer, which
    /// it did for the whole of the lab's first year, and it returns the session itself where the
    /// store holds no daily bar for it at all. The width is written onto the fetch row either way,
    /// so a short window is legible as short instead of being inferred from a date.
    /// </summary>
    public static IReadOnlyList<DateOnly> AnchorWindow(
        SqliteConnection connection, DateOnly sessionDate, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE bar_date <= @through
               AND observed_at <= @observed_before
             ORDER BY bar_date DESC
             LIMIT @width;
            """;
        command.Parameters.AddWithValue("@through", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));
        command.Parameters.AddWithValue("@width", AnchorWindowSessions);

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        // The session itself, where the store holds no daily bar at or before it. The fetch still
        // has a night to buy and the alternative is an empty range, which would ask the vendor for
        // nothing and record it as a night that bought nothing.
        if (sessions.Count == 0)
        {
            return [sessionDate];
        }

        sessions.Reverse();
        return sessions;
    }

    /// <summary>
    /// The instants bounding <paramref name="window"/>, local midnight to local midnight.
    ///
    /// Wider than the regular session on purpose. An extended-hours minute is exactly as
    /// unrecoverable as a regular one, so the fetch takes whatever the vendor holds and every bar is
    /// labelled with the session window it fell in. Narrowing here would throw away data that cannot
    /// be re-bought in order to avoid storing rows nothing currently reads.
    ///
    /// <paramref name="sessionDate"/> closes the range rather than the window's own last session,
    /// so a window whose newest stored session is older than the night being bought still reaches
    /// the night being bought. The two differ on exactly the evening this stage runs, because the
    /// daily bars for the session that has just closed land at 18:00 and this runs at 20:30 against
    /// whatever the store has.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) WindowOf(
        IReadOnlyList<DateOnly> window, DateOnly sessionDate, string zone)
    {
        ArgumentNullException.ThrowIfNull(window);

        DateOnly from = window.Count > 0 && window[0] < sessionDate ? window[0] : sessionDate;

        return (SessionBoundaries.At(from, TimeOnly.MinValue, zone),
                SessionBoundaries.At(sessionDate.AddDays(1), TimeOnly.MinValue, zone));
    }

    /// <summary>
    /// The most recent session strictly before <paramref name="sessionDate"/> that flagged anything,
    /// or null where none has.
    ///
    /// Strictly before, in the statement rather than in a caller's check, so the pairing cannot be
    /// formed wrongly by a caller that forgot. <see cref="Pairing.Of"/> asserts the same thing again
    /// on the value this returns, because a guard in the query is a guard one rewrite away from
    /// being lost and the property is worth stating twice.
    /// </summary>
    public static DateOnly? PreviousFlaggedSession(SqliteConnection connection, DateOnly sessionDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(as_of) FROM setup WHERE as_of < @session";
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(sessionDate));

        object? value = command.ExecuteScalar();

        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? StoreText.StorageTextToDate(text)
            : null;
    }

    /// <summary>
    /// The distinct names flagged on one session, in ticker order.
    ///
    /// Distinct, because a name flagged long and short on the same night is one name to buy minutes
    /// for and two rows to resolve. Asking twice would spend the call twice and store the same bars
    /// against the same key.
    /// </summary>
    public static IReadOnlyList<string> FlaggedNames(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT ticker FROM setup WHERE as_of = @as_of ORDER BY ticker";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// One minute bar, labelled with the session it traded in rather than with the session the
    /// fetch was for.
    ///
    /// <b>The two were the same figure until the window widened and they are not now.</b> The stage
    /// bought one session a night, so every bar it stored belonged to the night it was buying and
    /// the fetch's session was a correct label by accident. A twenty-seven session window returns
    /// bars from twenty-seven different sessions in one answer, and stamping all of them with the
    /// night the fetch ran would put every anchor's minutes under the wrong day: the reader bounds
    /// on <c>session_date</c>, so the anchored average would find the whole window under one date
    /// and no minutes at all under the session it was anchored to.
    ///
    /// The session is the bar's own local calendar day, which is also what decides whether it fell
    /// inside the regular session, so both come from the one conversion rather than from a
    /// parameter that could disagree with it.
    /// </summary>
    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VendorIntradayBar bar,
        string zone,
        DateTimeOffset observedAt)
    {
        DateOnly sessionDate = SessionBoundaries.SessionDateOf(bar.OpenedAt, zone);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Insert only. Nothing in this lab updates or deletes a bar, and the same named check that
        // watches the daily and index bars watches this table.
        command.CommandText = """
            INSERT INTO intraday_bar (
                ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                open, high, low, close, volume, observed_at)
            VALUES (
                @ticker, @bar_ts, @session_date, @interval_code, @session_window, @price_basis,
                @open, @high, @low, @close, @volume, @observed_at)
            ON CONFLICT (ticker, bar_ts, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@ticker", bar.Ticker);
        command.Parameters.AddWithValue("@bar_ts", StoreText.TimestampToStorageText(bar.OpenedAt));
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@interval_code", MinuteInterval);
        command.Parameters.AddWithValue(
            "@session_window",
            SessionBoundaries.IsRegularSession(bar.OpenedAt, sessionDate, zone) ? RegularWindow : ExtendedWindow);
        command.Parameters.AddWithValue("@price_basis", RawBasis);
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(bar.Open));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(bar.High));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(bar.Low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(bar.Close));
        command.Parameters.AddWithValue("@volume", bar.Volume);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// What the night's fetch did, written whatever the outcome.
    ///
    /// <b>The shortfall is recorded here and not on the setup rows.</b> A stage that stops at the
    /// ceiling marks what it could not reach, and the obvious place is `setup.degraded_because`.
    /// That column is written once by the detector that inserts the row, and `setup` has one
    /// declared writer per operation; an update from here would be a second writer on a table whose
    /// rows the corpus forbids rewriting. The count asked for and the count answered are both on this
    /// row, so which names went unfetched is a join rather than an edit.
    /// </summary>
    private static void RecordFetch(
        SqliteConnection connection,
        DateOnly sessionDate,
        DateOnly? setupAsOf,
        int requested,
        int fetched,
        int empty,
        int barsWritten,
        int stored,
        int windowSessions,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intraday_fetch (
                session_date, setup_as_of, requested, fetched, empty, bars_written, stored,
                window_sessions, outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @setup_as_of, @requested, @fetched, @empty, @bars_written, @stored,
                @window_sessions, @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@stored", stored);
        command.Parameters.AddWithValue("@window_sessions", windowSessions);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@setup_as_of",
            setupAsOf is DateOnly asOf ? StoreText.DateToStorageText(asOf) : StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@requested", requested);
        command.Parameters.AddWithValue("@fetched", fetched);
        command.Parameters.AddWithValue("@empty", empty);
        command.Parameters.AddWithValue("@bars_written", barsWritten);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    // Declared last, and the placement is forced rather than stylistic. `writer-ownership`
    // attributes a write to the nearest type declaration above it rather than to the type whose
    // braces enclose it, which is the defect raised at 3.13 and carried to 4.6. With this record
    // declared above them, both inserts below were attributed to `Pairing` and the check reported
    // that IntradayFetcher issues no statement SCHEMA declares for it. Moving the declaration is a
    // workaround and is written down as one: the repair is in the check, and this is a second
    // instance for the row that already asks for it.
    /// <summary>
    /// The session a fetch is for, paired with the session whose setups it resolves.
    ///
    /// <b>This type exists so the pairing can be refused rather than assumed.</b> The stage runs at
    /// 20:30 because minute bars publish two to three hours after the close, while detection runs at
    /// 18:20 and the plan is written at 18:30, both for the <i>next</i> session. So on the evening of
    /// session N the bars stored are session N's and the setups they resolve are the ones flagged on
    /// the evening of N-1. Fetching session N's bars against setups flagged on session N would pair
    /// a plan with the session it was written on, which is a plan resolved against prices it was
    /// computed from.
    /// see: Minute bars are fetched for the session a plan was live in, never the session it was written on
    ///
    /// It refuses rather than returning nothing, on the same shape a point-in-time read uses: no
    /// fill and cannot-pair are different answers, and a stage that returned an empty set for the
    /// second would look like a quiet night.
    /// </summary>
    public sealed record Pairing(DateOnly SessionDate, DateOnly SetupAsOf)
    {
        public static Pairing Of(DateOnly sessionDate, DateOnly setupAsOf) =>
            setupAsOf < sessionDate
                ? new Pairing(sessionDate, setupAsOf)
                : throw new InvalidOperationException(
                    $"Minute bars for the session of {sessionDate:yyyy-MM-dd} cannot resolve setups flagged on "
                    + $"{setupAsOf:yyyy-MM-dd}. A plan is written on the evening of one session for the next, so the "
                    + "setups a session's bars resolve are always flagged strictly before it. Pairing a session with "
                    + "its own setups would resolve a plan against the prices it was computed from.");
    }
}

/// <summary>What one night's fetch did, as the stage reports it.</summary>
public sealed record IntradayFetchResult(
    DateOnly SessionDate,
    DateOnly? SetupAsOf,
    int Requested,
    int Fetched,
    int Empty,
    int BarsWritten,
    int Unchanged,
    int WindowSessions,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    string? StoppedBecause)
{
    /// <summary>What the night's asking left the store holding, which is what its outcome turns on.</summary>
    public int Stored => BarsWritten + Unchanged;
}
