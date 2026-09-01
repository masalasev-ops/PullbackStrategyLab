using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The bid-ask spread for the night's capped names, twice inside the session they trade in.
///
/// <b>The lab's second unrecoverable input, and the harder of the two.</b> A minute bar can be
/// bought for some days after its session; a quote cannot be bought at all once the instant has
/// passed, because the vendor publishes no history of the book. A session sampled nought times has
/// no spread for ever.
/// see: Spread is captured intraday from day one
///
/// <b>What it is captured for is entry slippage at 4.7</b>, and that is stated here because until
/// this checkpoint the store had no reader anywhere in the solution. A capture spending 120
/// unrecoverable calls a session on an input nothing consumes is one nobody can justify, so the
/// reader is named at the capture rather than discovered at the consumer. The fraction charged, and
/// whether it is symmetric between the two directions, are 4.7's to decide and nothing here
/// computes a slippage figure.
///
/// <b>It runs inside the session and reads the previous evening's rows.</b> The same offset the
/// minute bars settled, arrived at from the other side: detection runs at 18:20 on the evening of
/// N-1 and the plan is written at 18:30 for session N, so a stage running inside session N is
/// sampling the names whose plans are live in the session it is running in.
/// see: Minute bars are fetched for the session a plan was live in, never the session it was written on
///
/// <b>The capped sixty, not every flagged name</b>, which is the one population difference between
/// this stage and the minute-bar fetch beside it. It is what the budget was built on and it is a
/// narrower set than the one whose bars are bought; the consequence, that a phase-5 version
/// selecting a name outside the cap will have that name's minutes and not its spread, is a carried
/// obligation due at 4.7 rather than a thing this stage decides.
/// </summary>
public sealed class SpreadSnapshotter
{
    public const string Name = "spreads";

    /// <summary>
    /// The first sample, taken after the open has settled.
    ///
    /// <b>What it is for.</b> A pullback triggers when price crosses a level, and that happens
    /// disproportionately in the first hour, so the first sample has to describe the part of the
    /// session most entries are taken in. It cannot be taken at the open itself: the opening auction
    /// and the minutes after it carry the widest and least representative quotes of the day, and a
    /// spread measured there describes an event rather than the name.
    /// </summary>
    public static readonly TimeOnly AfterOpenSample = new(10, 15);

    /// <summary>
    /// The second sample, taken before the close begins.
    ///
    /// <b>What it is for.</b> A name whose spread widened through the session is invisible to one
    /// morning reading, and widening through the day is exactly the property that decides whether a
    /// tight stop is meaningful. Late enough to see it, early enough to be outside the closing
    /// auction, which distorts the book in the same way the opening one does.
    /// </summary>
    public static readonly TimeOnly BeforeCloseSample = new(15, 45);

    /// <summary>
    /// <b>Why two samples and not one.</b> One quote cannot be checked. A stale quote, a locked or
    /// crossed book and a one-off blowout all look exactly like a normal row, and nothing on that
    /// row would say which it was. Two independent observations of one name on one day give the fill
    /// model something to disagree with, and the disagreement is itself the finding: a name whose
    /// spread doubles across a session is a name no single figure describes.
    ///
    /// <b>Why two and not three.</b> A third costs sixty more unrecoverable calls every session for
    /// the life of the lab, and it buys the shape of the intraday spread curve rather than a check
    /// on its level. 4.7 charges a spread; it does not integrate a curve. If the two samples turn
    /// out to disagree often enough that no level is usable, that is an argument for a third made
    /// from the record rather than in advance of it.
    /// </summary>
    public static readonly IReadOnlyList<(string Pass, TimeOnly At)> Samples =
    [
        (AfterOpenPass, AfterOpenSample),
        (BeforeClosePass, BeforeCloseSample),
    ];

    /// <summary>The stored name of the first sample. A name rather than an index (see: Headings carry no numbers, and anchors are slugs).</summary>
    public const string AfterOpenPass = "after_open";

    /// <summary>The stored name of the second.</summary>
    public const string BeforeClosePass = "before_close";

    /// <summary>Recorded on a session before which nothing had been flagged.</summary>
    public const string NoPriorSession = "no prior session has flagged setups";

    /// <summary>Recorded when the day's call ceiling stopped the pass part way through.</summary>
    public const string CeilingReached = "the daily call ceiling was reached";

    /// <summary>Recorded against a name the vendor answered with no usable book.</summary>
    public const string NoBook = "the vendor answered with no usable two-sided quote";

    private readonly IMarketDataVendor _vendor;
    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SpreadSnapshotter(
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

        string? passArgument = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)
            && (a == AfterOpenPass || a == BeforeClosePass));

        if (passArgument is null)
        {
            Console.Error.WriteLine(
                $"{Name}: name the pass, {AfterOpenPass} or {BeforeClosePass}. "
                + "The two are different samples of the same session and a row cannot say which it was without being told.");
            return 2;
        }

        string? date = args.FirstOrDefault(a =>
            a.Length == 10 && a[4] == '-' && !a.StartsWith("--", StringComparison.Ordinal));

        DateOnly sessionDate = date is not null
            ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        SpreadPassResult result = await SnapshotAsync(sessionDate, passArgument, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"{Name}: session {result.SessionDate:yyyy-MM-dd}, pass {result.Pass}, "
            + (result.SetupAsOf is DateOnly asOf
                ? $"the names capped on the evening of {asOf:yyyy-MM-dd}"
                : "no prior session has flagged setups, so nothing was asked for"));
        Console.WriteLine(
            $"{Name}: {result.Requested} name(s) asked, {result.Answered} answered, {result.Quoted} quoted "
            + $"on both sides, {result.Unquoted} without a usable book");
        Console.WriteLine(
            $"{Name}: {result.Outcome.ToStorageText()}, {result.CallsUsed} calls, {result.RowsWritten} rows"
            + (result.StoppedBecause is null ? string.Empty : $", stopped because {result.StoppedBecause}"));

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Take one pass of one session's spreads.
    ///
    /// <paramref name="sessionDate"/> is the session being traded, which on a scheduled run is the
    /// session the stage is running inside.
    /// </summary>
    public async Task<SpreadPassResult> SnapshotAsync(
        DateOnly sessionDate, string pass, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pass);

        if (pass != AfterOpenPass && pass != BeforeClosePass)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pass), pass, $"A pass is {AfterOpenPass} or {BeforeClosePass}.");
        }

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "spread_snapshot");

        DateTimeOffset observedAt = run.StartedAt;
        DateOnly? setupAsOf = IntradayFetcher.PreviousFlaggedSession(connection, sessionDate);

        // Nothing was flagged before this session, so no plan is live in it and there is no name
        // whose entry cost this pass would be describing. Recorded as a pass of nothing rather than
        // skipped, for the reason the whole table exists: a session with no row is a session nobody
        // sampled, and that has to stay distinguishable from one that ran and asked for nothing.
        if (setupAsOf is null)
        {
            RecordPass(connection, sessionDate, null, pass, 0, 0, 0, 0, 0, RunOutcome.Clean, NoPriorSession, observedAt);
            RunSummary nothing = run.Complete(RunOutcome.Clean);

            return new SpreadPassResult(
                sessionDate, null, pass, 0, 0, 0, 0, 0, nothing.CallsUsed, RunOutcome.Clean, NoPriorSession);
        }

        IntradayFetcher.Pairing pairing = IntradayFetcher.Pairing.Of(sessionDate, setupAsOf.Value);

        IReadOnlyList<string> names = CappedNames(connection, pairing.SetupAsOf);

        int answered = 0;
        int quoted = 0;
        int unquoted = 0;
        int written = 0;
        string? stoppedBecause = null;

        for (int at = 0; at < names.Count;)
        {
            // <b>The last batch is trimmed to what the ceiling can still cover.</b> A batch is
            // charged whole, so a fixed twenty asked against fifteen remaining is refused entire and
            // fifteen names go unbought with the budget to buy them sitting there. For a recoverable
            // input that is a rounding error and tomorrow fixes it; for this one those fifteen
            // spreads are gone for good. So the batch is the smaller of the batch size and what the
            // remainder pays for, and the pass stops only when the remainder pays for nothing.
            int affordable = run.CallsRemaining / EodhdClient.UsQuoteCost;
            int take = Math.Min(EodhdClient.UsQuoteBatchSize, Math.Min(affordable, names.Count - at));

            if (take <= 0)
            {
                stoppedBecause = CeilingReached;
                break;
            }

            string[] batch = [.. names.Skip(at).Take(take)];
            at += take;

            VendorResult<IReadOnlyList<VendorQuote>> answer = await _vendor
                .GetQuotesAsync(batch, run, cancellationToken).ConfigureAwait(false);

            if (answer.BudgetExhausted)
            {
                // The designed behaviour at the ceiling, and reachable even after the trim above
                // because another stage may spend between the read of the remainder and the request.
                // The names not reached are `requested` against `answered`, which is why the first
                // is what was asked for rather than what came back.
                stoppedBecause = CeilingReached;
                break;
            }

            using SqliteTransaction transaction = connection.BeginTransaction();

            foreach (VendorQuote quote in answer.Require())
            {
                answered++;

                if (quote.IsUsable)
                {
                    quoted++;
                }
                else
                {
                    unquoted++;
                }

                Insert(connection, transaction, sessionDate, pairing.SetupAsOf, pass, quote, observedAt);
                written++;
            }

            transaction.Commit();
        }

        RunOutcome outcome = stoppedBecause is null ? RunOutcome.Clean : RunOutcome.Partial;
        RecordPass(
            connection, sessionDate, pairing.SetupAsOf, pass, names.Count, answered, quoted, unquoted,
            written, outcome, stoppedBecause, observedAt);

        RunSummary summary = run.Complete(outcome);

        return new SpreadPassResult(
            sessionDate, pairing.SetupAsOf, pass, names.Count, answered, quoted, unquoted, written,
            summary.CallsUsed, outcome, stoppedBecause);
    }

    /// <summary>
    /// The distinct capped names of one evening, in ticker order.
    ///
    /// <b>Capped, which is <c>capped_out = 0</c> and not the whole flagged set.</b> These are the
    /// night's published candidates, the population the 120-call figure is built on, and the ones a
    /// plan is written for in phase 4.
    ///
    /// Distinct, because a name capped long and short is one name to quote and two rows to price.
    /// Asking twice would spend the call twice and store one book against one key.
    /// </summary>
    public static IReadOnlyList<string> CappedNames(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT ticker FROM setup WHERE as_of = @as_of AND capped_out = 0 ORDER BY ticker";
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
    /// The spread in basis points of the mid, or null where either side is missing or the book is
    /// crossed.
    ///
    /// <b>Computed once here rather than by each reader.</b> It rests on a choice of denominator,
    /// and a reader taking the mid while another took the last trade would produce two figures
    /// carrying one name. The mid, because the spread is the round trip a marketable order pays and
    /// the mid is the price it is paying it around.
    ///
    /// A statistic, so it is a double and stored in a REAL column, which is the one place in this
    /// table where the prices rule points the other way.
    /// </summary>
    public static double? SpreadBasisPoints(VendorQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        if (!quote.IsUsable)
        {
            return null;
        }

        decimal bid = quote.Bid!.Value;
        decimal ask = quote.Ask!.Value;

        return (double)((ask - bid) / ((ask + bid) / 2m)) * 10_000d;
    }

    /// <summary>
    /// How stale the quote was when the lab took it, in seconds, measured from the <b>older</b> of
    /// the two sides.
    ///
    /// The older side, because a spread is only as fresh as its stalest half: an ask stamped a
    /// second ago against a bid stamped four minutes ago is a four-minute-old spread whatever the
    /// ask says. Null where either stamp is missing, on the same grounds as everything else in this
    /// table: a lag of nought would say the quote was live.
    ///
    /// <b>Recorded rather than corrected for.</b> The feed is delayed by design and the delay is the
    /// vendor's to change, so the lag is a stored fact per row that 4.7 can bound on or exclude,
    /// instead of a constant this stage subtracts and a later reader has to know about.
    /// see: A delayed quote records its own lag rather than being corrected for it
    /// </summary>
    public static int? QuoteLagSeconds(VendorQuote quote, DateTimeOffset takenAt)
    {
        ArgumentNullException.ThrowIfNull(quote);

        if (quote.BidAt is not DateTimeOffset bidAt || quote.AskAt is not DateTimeOffset askAt)
        {
            return null;
        }

        DateTimeOffset older = bidAt < askAt ? bidAt : askAt;
        return (int)Math.Round((takenAt - older).TotalSeconds, MidpointRounding.AwayFromZero);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly sessionDate,
        DateOnly setupAsOf,
        string pass,
        VendorQuote quote,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO spread_snapshot (
                ticker, session_date, setup_as_of, pass, snapshot_ts,
                bid, ask, bid_size, ask_size, bid_ts, ask_ts,
                last_trade, last_trade_ts, spread_bps, quote_lag_seconds, absent_because, observed_at)
            VALUES (
                @ticker, @session_date, @setup_as_of, @pass, @snapshot_ts,
                @bid, @ask, @bid_size, @ask_size, @bid_ts, @ask_ts,
                @last_trade, @last_trade_ts, @spread_bps, @quote_lag_seconds, @absent_because, @observed_at)
            ON CONFLICT (ticker, session_date, pass, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@ticker", quote.Ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@setup_as_of", StoreText.DateToStorageText(setupAsOf));
        command.Parameters.AddWithValue("@pass", pass);
        command.Parameters.AddWithValue("@snapshot_ts", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@bid", Price(quote.Bid));
        command.Parameters.AddWithValue("@ask", Price(quote.Ask));
        command.Parameters.AddWithValue("@bid_size", (object?)quote.BidSize ?? DBNull.Value);
        command.Parameters.AddWithValue("@ask_size", (object?)quote.AskSize ?? DBNull.Value);
        command.Parameters.AddWithValue("@bid_ts", Stamp(quote.BidAt));
        command.Parameters.AddWithValue("@ask_ts", Stamp(quote.AskAt));
        command.Parameters.AddWithValue("@last_trade", Price(quote.LastTrade));
        command.Parameters.AddWithValue("@last_trade_ts", Stamp(quote.LastTradeAt));
        command.Parameters.AddWithValue("@spread_bps", (object?)SpreadBasisPoints(quote) ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@quote_lag_seconds", (object?)QuoteLagSeconds(quote, observedAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("@absent_because", quote.IsUsable ? DBNull.Value : NoBook);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }

    private static object Price(decimal? value) =>
        value is decimal price ? StoreText.PriceToStorageText(price) : DBNull.Value;

    private static object Stamp(DateTimeOffset? value) =>
        value is DateTimeOffset instant ? StoreText.TimestampToStorageText(instant) : DBNull.Value;

    /// <summary>
    /// What one pass did, written whatever the outcome.
    ///
    /// <b>This row is the whole of how a missed snapshot is detectable.</b> A stage that never ran
    /// cannot record that it never ran, so absence is the only signal there is, and absence is only
    /// readable because a pass that does run always writes. One row for a session is a session
    /// sampled once; two is the design; none is a hole that no later call can fill.
    /// </summary>
    private static void RecordPass(
        SqliteConnection connection,
        DateOnly sessionDate,
        DateOnly? setupAsOf,
        string pass,
        int requested,
        int answered,
        int quoted,
        int unquoted,
        int rowsWritten,
        RunOutcome outcome,
        string? stoppedBecause,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO spread_pass (
                session_date, setup_as_of, pass, requested, answered, quoted, unquoted,
                rows_written, outcome, stopped_because, observed_at)
            VALUES (
                @session_date, @setup_as_of, @pass, @requested, @answered, @quoted, @unquoted,
                @rows_written, @outcome, @stopped_because, @observed_at)
            ON CONFLICT (session_date, pass, observed_at) DO NOTHING;
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue(
            "@setup_as_of",
            setupAsOf is DateOnly asOf ? StoreText.DateToStorageText(asOf) : StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@pass", pass);
        command.Parameters.AddWithValue("@requested", requested);
        command.Parameters.AddWithValue("@answered", answered);
        command.Parameters.AddWithValue("@quoted", quoted);
        command.Parameters.AddWithValue("@unquoted", unquoted);
        command.Parameters.AddWithValue("@rows_written", rowsWritten);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped_because", (object?)stoppedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.ExecuteNonQuery();
    }
}

/// <summary>What one pass did, as the stage reports it.</summary>
public sealed record SpreadPassResult(
    DateOnly SessionDate,
    DateOnly? SetupAsOf,
    string Pass,
    int Requested,
    int Answered,
    int Quoted,
    int Unquoted,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    string? StoppedBecause);
