using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
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
            RecordFetch(connection, sessionDate, null, 0, 0, 0, 0, RunOutcome.Clean, NoPriorSession, observedAt);
            RunSummary empty = run.Complete(RunOutcome.Clean);

            return new IntradayFetchResult(
                sessionDate, null, 0, 0, 0, 0, 0, empty.RowsWritten, empty.CallsUsed,
                RunOutcome.Clean, NoPriorSession);
        }

        Pairing pairing = Pairing.Of(sessionDate, setupAsOf.Value);

        IReadOnlyList<string> names = FlaggedNames(connection, pairing.SetupAsOf);
        (DateTimeOffset from, DateTimeOffset to) = WindowOf(sessionDate, _options.SessionZone);

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

                Insert(connection, transaction, sessionDate, bar, _options.SessionZone, observedAt);
                written++;
            }

            transaction.Commit();
        }

        RunOutcome outcome = stoppedBecause is null ? RunOutcome.Clean : RunOutcome.Partial;
        RecordFetch(
            connection, sessionDate, pairing.SetupAsOf, names.Count, fetched, empties, written,
            outcome, stoppedBecause, observedAt);

        RunSummary summary = run.Complete(outcome);

        return new IntradayFetchResult(
            sessionDate, pairing.SetupAsOf, names.Count, fetched, empties, written, unchanged,
            summary.RowsWritten, summary.CallsUsed, outcome, stoppedBecause);
    }

    /// <summary>Recorded on the first night, when nothing had been flagged before the session.</summary>
    public const string NoPriorSession = "no prior session has flagged setups";

    /// <summary>Recorded when the day's call ceiling stopped the walk part way through.</summary>
    public const string CeilingReached = "the daily call ceiling was reached";

    /// <summary>
    /// The whole of a session date in the trading zone, local midnight to local midnight.
    ///
    /// Wider than the regular session on purpose. An extended-hours minute is exactly as
    /// unrecoverable as a regular one, so the fetch takes whatever the vendor holds and every bar is
    /// labelled with the session window it fell in. Narrowing here would throw away data that cannot
    /// be re-bought in order to avoid storing rows nothing currently reads.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) WindowOf(DateOnly sessionDate, string zone) =>
        (SessionBoundaries.At(sessionDate, TimeOnly.MinValue, zone),
         SessionBoundaries.At(sessionDate.AddDays(1), TimeOnly.MinValue, zone));

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

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly sessionDate,
        VendorIntradayBar bar,
        string zone,
        DateTimeOffset observedAt)
    {
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
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intraday_fetch (
                session_date, setup_as_of, requested, fetched, empty, bars_written,
                outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @setup_as_of, @requested, @fetched, @empty, @bars_written,
                @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;
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
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    string? StoppedBecause);
