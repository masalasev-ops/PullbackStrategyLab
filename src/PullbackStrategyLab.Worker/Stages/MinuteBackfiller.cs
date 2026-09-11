using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// One-minute history for the flagged calibration rows, bought once, from 7.7.
///
/// <b>It is the first time the execution family has a history to be screened over.</b> Until 7.0
/// the corpus held that the vendor sold no minute history, and the 7.0(a) probe read 959 bars for a
/// session of 2024, so the calibration rows 7.9 measures the sourced entry rule over can have their
/// minutes. This stage buys them, window by window, as <see cref="MinuteBackfillPlan"/> lays them out.
///
/// <b>Outside the nightly ceiling, and every call recorded.</b> A one-time operation is not the
/// evening's job, so its calls are counted on its own run row and the day's total does not see them.
/// see: A one-time backfill is outside the nightly ceiling, whether it buys daily history or minutes
///
/// <b>The minutes land in `calibration_minute_bar` and never in `intraday_bar`.</b> The live capture
/// table's population is the forward nights the execution family's reopening condition counts, and a
/// backfilled minute sitting there would be counted as a night the lab captured. Two populations and
/// two tables, on the grounds `calibration_setup` is a table rather than a flag.
///
/// <b>A window already bought is not bought again</b>, so a run the operator stops part way resumes
/// where it stopped rather than paying twice. The rows its plan leaves short of warm-up are written
/// with their shortfall on every run that finds them, so the report 7.9 reads names them.
///
/// <b>An operator verb and never a slot.</b> It spends thousands of calls once and then has nothing
/// to do, which is a decision a person takes on a day they choose.
/// </summary>
public sealed class MinuteBackfiller
{
    public const string Name = "backfill-minutes";

    /// <summary>Says what would be bought and what it would cost, and spends nothing.</summary>
    public const string DryRunFlag = "--dry-run";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public MinuteBackfiller(
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

        bool dryRun = args.Contains(DryRunFlag, StringComparer.Ordinal);
        MinuteBackfillResult result = await BackfillAsync(dryRun, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {result.Rows} flagged calibration row(s) over {result.Names} name(s), {result.Windows} window(s) of "
            + $"{MinuteBackfillPlan.WindowDays} days, {result.Windows * EodhdClient.IntradayCost} call(s) to buy them all"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {result.ShortRows} row(s) short of the {MinuteBackfillPlan.WarmupSessions}-session warm-up, written with their shortfall"));

        if (dryRun)
        {
            Console.WriteLine($"{Name}: nothing bought, {DryRunFlag} given.");
            return 0;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {result.WindowsBought} window(s) bought, {result.WindowsAlreadyHeld} already held, "
            + $"{result.BarsReturned} bar(s) returned over {result.SessionsAnswered} session(s), {result.BarsWritten} written"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls outside the daily ceiling"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>Lays the windows out over the store's calibration rows, and buys the ones not yet held.</summary>
    public async Task<MinuteBackfillResult> BackfillAsync(bool dryRun = false, CancellationToken cancellationToken = default)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(
            connection, Name, CallCounting.OutsideTheDailyCeiling,
            "calibration_minute_bar", "calibration_minute_window", "calibration_minute_shortfall");

        DateTimeOffset observedAt = run.StartedAt;
        IReadOnlyList<DateOnly> sessions = Sessions(connection, observedAt);
        IReadOnlyList<MinuteBackfillPlan.Row> rows = FlaggedCalibrationRows(connection, sessions);
        MinuteBackfillPlan.Plan plan = MinuteBackfillPlan.Of(rows, sessions);

        int names = rows.Select(r => r.Ticker).Distinct(StringComparer.Ordinal).Count();

        if (dryRun)
        {
            run.Complete(RunOutcome.Clean);
            return new MinuteBackfillResult(rows.Count, names, plan.Windows.Count, plan.Short.Count, 0, 0, 0, 0, 0, 0, RunOutcome.Clean);
        }

        foreach (MinuteBackfillPlan.Shortfall shortfall in plan.Short)
        {
            RecordShortfall(connection, shortfall, observedAt);
        }

        int bought = 0;
        int held = 0;
        int returned = 0;
        int written = 0;
        int answered = 0;

        foreach (MinuteBackfillPlan.Window window in plan.Windows)
        {
            if (AlreadyBought(connection, window))
            {
                held++;
                continue;
            }

            (DateTimeOffset from, DateTimeOffset to) = (
                SessionBoundaries.At(window.From, TimeOnly.MinValue, _options.SessionZone),
                SessionBoundaries.At(window.To.AddDays(1), TimeOnly.MinValue, _options.SessionZone));

            int callsBefore = run.CallsUsed;
            VendorResult<IReadOnlyList<VendorIntradayBar>> answer = await _vendor
                .GetIntradayAsync(window.Ticker, from, to, run, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<VendorIntradayBar> bars = answer.Require();
            int windowWritten = 0;

            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                foreach (VendorIntradayBar bar in bars)
                {
                    windowWritten += Insert(connection, transaction, bar, _options.SessionZone, observedAt);
                }

                int sessionsAnswered = bars
                    .Select(b => SessionBoundaries.SessionDateOf(b.OpenedAt, _options.SessionZone))
                    .Distinct()
                    .Count();

                RecordWindow(connection, transaction, window, bars.Count, windowWritten, sessionsAnswered,
                    run.CallsUsed - callsBefore, observedAt);

                transaction.Commit();
                answered += sessionsAnswered;
            }

            bought++;
            returned += bars.Count;
            written += windowWritten;
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new MinuteBackfillResult(
            rows.Count, names, plan.Windows.Count, plan.Short.Count, bought, held, returned, answered, written,
            summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// Every session the store holds a daily bar for, as known at the run's own instant. The lab
    /// authors no calendar, so this is what a session is, on the terms the intraday fetch counts its
    /// anchor window.
    /// </summary>
    public static IReadOnlyList<DateOnly> Sessions(SqliteConnection connection, DateTimeOffset observedBefore)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE observed_at <= @observed_before
             ORDER BY bar_date;
            """;
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var sessions = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return sessions;
    }

    /// <summary>
    /// Every calibration row, which is every name a historical walk recorded, with the session its plan
    /// would have been live in.
    ///
    /// <b>Flagged is recorded, on the terms the nightly fetch buys minutes for every setup row rather
    /// than the capped sixty.</b> The entry session is the first session the store holds after the
    /// row's own, and the next weekday where the store holds none, which is the plan's own rule for its
    /// live session on an evening that cannot see the next day.
    /// </summary>
    private static IReadOnlyList<MinuteBackfillPlan.Row> FlaggedCalibrationRows(
        SqliteConnection connection, IReadOnlyList<DateOnly> sessions)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setup_id, ticker, as_of FROM calibration_setup ORDER BY as_of, ticker, direction;";

        DateOnly[] traded = [.. sessions];
        var rows = new List<MinuteBackfillPlan.Row>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateOnly asOf = StoreText.StorageTextToDate(reader.GetString(2));
            rows.Add(new MinuteBackfillPlan.Row(reader.GetString(0), reader.GetString(1), EntrySessionFor(asOf, traded)));
        }

        return rows;
    }

    /// <summary>
    /// The session a calibration row's plan would have been live in: the first session the store holds
    /// after the row's own, or the next weekday where it holds none. Shared with the measurement that
    /// resolves the entry over the minutes this stage bought, so the two agree on which session a row is.
    /// </summary>
    public static DateOnly EntrySessionFor(DateOnly flagged, IReadOnlyList<DateOnly> traded)
    {
        ArgumentNullException.ThrowIfNull(traded);

        DateOnly[] sessions = traded as DateOnly[] ?? [.. traded];
        int found = Array.BinarySearch(sessions, flagged);
        int next = found >= 0 ? found + 1 : ~found;
        return next < sessions.Length ? sessions[next] : PlanBuilder.NextWeekday(flagged);
    }

    private static bool AlreadyBought(SqliteConnection connection, MinuteBackfillPlan.Window window)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM calibration_minute_window
             WHERE ticker = @ticker AND window_to = @window_to AND window_from = @window_from;
            """;
        command.Parameters.AddWithValue("@ticker", window.Ticker);
        command.Parameters.AddWithValue("@window_to", StoreText.DateToStorageText(window.To));
        command.Parameters.AddWithValue("@window_from", StoreText.DateToStorageText(window.From));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// One minute, into the research table. Insert only, labelled with the session it traded in and the
    /// window of that session it fell in, on exactly the terms the nightly fetch labels its own.
    /// </summary>
    private static int Insert(
        SqliteConnection connection, SqliteTransaction transaction, VendorIntradayBar bar, string zone, DateTimeOffset observedAt)
    {
        DateOnly sessionDate = SessionBoundaries.SessionDateOf(bar.OpenedAt, zone);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calibration_minute_bar (
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
        command.Parameters.AddWithValue("@interval_code", IntradayFetcher.MinuteInterval);
        command.Parameters.AddWithValue(
            "@session_window",
            SessionBoundaries.IsRegularSession(bar.OpenedAt, sessionDate, zone)
                ? IntradayFetcher.RegularWindow
                : IntradayFetcher.ExtendedWindow);
        command.Parameters.AddWithValue("@price_basis", IntradayFetcher.RawBasis);
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(bar.Open));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(bar.High));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(bar.Low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(bar.Close));
        command.Parameters.AddWithValue("@volume", bar.Volume);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        return command.ExecuteNonQuery();
    }

    private static void RecordWindow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MinuteBackfillPlan.Window window,
        int barsReturned,
        int barsWritten,
        int sessionsAnswered,
        int callsUsed,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO calibration_minute_window (
                ticker, window_from, window_to, rows_served, bars_returned, bars_written,
                sessions_answered, calls_used, observed_at)
            VALUES (
                @ticker, @window_from, @window_to, @rows_served, @bars_returned, @bars_written,
                @sessions_answered, @calls_used, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", window.Ticker);
        command.Parameters.AddWithValue("@window_from", StoreText.DateToStorageText(window.From));
        command.Parameters.AddWithValue("@window_to", StoreText.DateToStorageText(window.To));
        command.Parameters.AddWithValue("@rows_served", window.Rows.Count);
        command.Parameters.AddWithValue("@bars_returned", barsReturned);
        command.Parameters.AddWithValue("@bars_written", barsWritten);
        command.Parameters.AddWithValue("@sessions_answered", sessionsAnswered);
        command.Parameters.AddWithValue("@calls_used", callsUsed);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static void RecordShortfall(SqliteConnection connection, MinuteBackfillPlan.Shortfall shortfall, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calibration_minute_shortfall (
                setup_id, ticker, entry_session, window_from, warmup_sessions, warmup_wanted, observed_at)
            VALUES (
                @setup_id, @ticker, @entry_session, @window_from, @warmup_sessions, @warmup_wanted, @observed_at)
            ON CONFLICT (setup_id, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@setup_id", shortfall.Row.SetupId);
        command.Parameters.AddWithValue("@ticker", shortfall.Row.Ticker);
        command.Parameters.AddWithValue("@entry_session", StoreText.DateToStorageText(shortfall.Row.EntrySession));
        command.Parameters.AddWithValue("@window_from", StoreText.DateToStorageText(shortfall.WindowFrom));
        command.Parameters.AddWithValue("@warmup_sessions", shortfall.WarmupSessionsHeld);
        command.Parameters.AddWithValue("@warmup_wanted", MinuteBackfillPlan.WarmupSessions);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

/// <summary>What one backfill run laid out, bought and wrote, or would have.</summary>
public sealed record MinuteBackfillResult(
    int Rows,
    int Names,
    int Windows,
    int ShortRows,
    int WindowsBought,
    int WindowsAlreadyHeld,
    int BarsReturned,
    int SessionsAnswered,
    int BarsWritten,
    int CallsUsed,
    RunOutcome Outcome);
