using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored spreads are read, on the same terms as the bar readers: every read takes an
/// as-of date, only observations made by the end of that date are visible, and within one name's
/// pass the latest such observation wins.
///
/// <b>Its reader is entry slippage at 4.7</b>, which is what this capture exists for. Nothing here
/// computes a slippage figure; it answers what the book looked like, and what fraction of it a fill
/// is charged is the fill model's.
///
/// <b>It refuses a session nobody sampled, and that is the point of the class.</b> A session with
/// no pass row is not a session with a spread of nought, and a fill model charging nothing on such a
/// session would produce an encouraging figure that means nothing. "No spread" and "never sampled"
/// are different answers and only the second stops anything, which is the same shape the minute-bar
/// pairing already uses one table over.
/// </summary>
public sealed class SpreadSnapshotReader
{
    private readonly StoreConnectionFactory _connections;

    public SpreadSnapshotReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>One name's spreads for one session, as last observed by the end of the as-of.</summary>
    public SessionSpread Read(string ticker, DateOnly sessionDate, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, sessionDate, asOf);
    }

    /// <summary>The same read from a connection the caller already holds.</summary>
    public static SessionSpread Read(
        SqliteConnection connection, string ticker, DateOnly sessionDate, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        SamplingOf(connection, sessionDate, asOf).ThrowIfNothingWasSampled(sessionDate);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT pass, bid, ask, spread_bps, quote_lag_seconds, absent_because, snapshot_ts
              FROM spread_snapshot s
             WHERE s.ticker = @ticker
               AND s.session_date = @session_date
               AND s.observed_at <= @observed_before
               AND s.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM spread_snapshot l
                      WHERE l.ticker = s.ticker
                        AND l.session_date = s.session_date
                        AND l.pass = s.pass
                        AND l.observed_at <= @observed_before)
             ORDER BY s.pass;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, Core.Time.SessionBoundaries.UsEquities));

        var samples = new List<SpreadSample>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            samples.Add(new SpreadSample(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : StoreText.StorageTextToPrice(reader.GetString(1)),
                reader.IsDBNull(2) ? null : StoreText.StorageTextToPrice(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                StoreText.StorageTextToTimestamp(reader.GetString(6))));
        }

        return new SessionSpread(ticker, sessionDate, samples, PassesOf(connection, sessionDate, asOf));
    }

    /// <summary>
    /// How many of the session's two passes ran, by name, as observed by the end of the as-of.
    ///
    /// Read from <c>spread_pass</c> rather than from the snapshots, because a pass that ran and
    /// quoted nothing wrote no snapshot and still happened. Counting distinct passes among the
    /// stored rows would call that session unsampled, which is the one thing this class exists to
    /// tell apart.
    /// </summary>
    public static IReadOnlyList<string> PassesOf(
        SqliteConnection connection, DateOnly sessionDate, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT pass
              FROM spread_pass
             WHERE session_date = @session_date
               AND observed_at <= @observed_before
             ORDER BY pass;
            """;
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(sessionDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, Core.Time.SessionBoundaries.UsEquities));

        var passes = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            passes.Add(reader.GetString(0));
        }

        return passes;
    }

    /// <summary>
    /// What a session's sampling looked like, without reading any one name's book.
    ///
    /// Separate from <see cref="Read"/> so the morning read can ask whether last night's session was
    /// sampled at all without naming a stock, which is the question the failure behaviour is about.
    /// </summary>
    public static SessionSampling SamplingOf(
        SqliteConnection connection, DateOnly sessionDate, DateOnly asOf) =>
        new(sessionDate, PassesOf(connection, sessionDate, asOf));
}

/// <summary>
/// Which of a session's two passes ran. The one fact the missed-snapshot behaviour turns on.
/// </summary>
public sealed record SessionSampling(DateOnly SessionDate, IReadOnlyList<string> Passes)
{
    /// <summary>Both passes ran. The design, and the only state that is not a shortfall.</summary>
    public bool IsComplete => Passes.Count == 2;

    /// <summary>
    /// One of the two ran. The session has a spread and it rests on a single observation, so it can
    /// neither be checked against a second reading nor say anything about how the book moved through
    /// the day. Degraded rather than failed: one sample is worth more than none and nothing later
    /// can raise it to two.
    /// </summary>
    public bool IsDegraded => Passes.Count == 1;

    /// <summary>Neither ran. There is no spread for this session and there never will be.</summary>
    public bool IsUnsampled => Passes.Count == 0;

    /// <summary>
    /// Refuses a session nobody sampled.
    ///
    /// Fail-closed on the grounds every unrecoverable input in this lab is: a reader that answered
    /// with an empty result would be indistinguishable from a name that had no book, and a fill
    /// model cannot tell the two apart either. A degraded session does not throw, because one sample
    /// is a real answer and the caller is told it is one of two.
    /// </summary>
    public void ThrowIfNothingWasSampled(DateOnly sessionDate)
    {
        if (IsUnsampled)
        {
            throw new InvalidOperationException(
                $"No spread pass was recorded for the session of {sessionDate:yyyy-MM-dd}, so this session has no "
                + "spread and cannot be given one: a quote is not purchasable after its instant has passed. A session "
                + "sampled nought times is a hole in the evidence rather than a session whose spreads were nought, and "
                + "answering with an empty result would let a fill be charged no slippage on a session nobody measured.");
        }
    }
}

/// <summary>One name's spreads for one session, with how many passes the session got.</summary>
public sealed record SessionSpread(
    string Ticker,
    DateOnly SessionDate,
    IReadOnlyList<SpreadSample> Samples,
    IReadOnlyList<string> Passes)
{
    /// <summary>The session's sampling, so a caller reads one object rather than two.</summary>
    public SessionSampling Sampling => new(SessionDate, Passes);

    /// <summary>
    /// The samples that carry a usable two-sided book. A name the vendor answered with one side is
    /// stored, and it is not a spread.
    /// </summary>
    public IReadOnlyList<SpreadSample> Usable =>
        [.. Samples.Where(s => s.SpreadBasisPoints is not null)];
}

/// <summary>One pass's observation of one name's book.</summary>
public sealed record SpreadSample(
    string Pass,
    decimal? Bid,
    decimal? Ask,
    double? SpreadBasisPoints,
    int? QuoteLagSeconds,
    string? AbsentBecause,
    DateTimeOffset SnapshotAt);
