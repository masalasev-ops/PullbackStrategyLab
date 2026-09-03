using Microsoft.Data.Sqlite;

using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored corporate actions are read, and the one place the rebuild demand they
/// create is answered.
///
/// Actions are append-only and read exactly as bars are: every read takes an as-of date, only
/// observations made by the end of that date are visible, and within a grain the latest such
/// observation wins. Vendors restate corporate actions, and a restatement arriving on Thursday
/// must not change what Monday's replay sees.
///
/// There is no overload without an as-of date. A read that could omit it would compile, run,
/// and answer with a ratio the lab did not have.
/// </summary>
public sealed class CorporateActionReader
{
    private readonly StoreConnectionFactory _connections;

    public CorporateActionReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Every action for one ticker effective on or before <paramref name="asOf"/>, as it was
    /// last observed by the end of that date, oldest first.
    /// </summary>
    public IReadOnlyList<StoredCorporateAction> Read(string ticker, DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, asOf, sessionZone);
    }

    public static IReadOnlyList<StoredCorporateAction> Read(SqliteConnection connection, string ticker, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.ticker, a.effective_date, a.type, a.ratio, a.observed_at
              FROM corporate_action a
             WHERE a.ticker = @ticker
               AND a.effective_date <= @as_of
               AND a.observed_at <= @observed_before
               AND a.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM corporate_action l
                      WHERE l.ticker = a.ticker
                        AND l.effective_date = a.effective_date
                        AND l.type = a.type
                        AND l.observed_at <= @observed_before)
             ORDER BY a.effective_date, a.type;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", EndOf(asOf, sessionZone));

        return ReadAll(command);
    }

    /// <summary>
    /// Every action on one effective date as last observed at or before
    /// <paramref name="observedBefore"/>. What the ingestor compares tonight's bulk response
    /// against, so a rerun writes nothing and a restatement writes one row.
    ///
    /// The bound is an instant rather than a date, and the distinction is the one that bit the
    /// bar ingestor. The ingestor asks "has the vendor changed anything since we last looked",
    /// so its bound is now; a signal asks "what did the lab know on the night", so its bound is
    /// that night. Passing the effective date as both makes a backfilled date look unobserved
    /// to the run that just wrote it.
    ///
    /// Keyed on ticker and type together, because a stock can pay a dividend and split on the
    /// same day and the grain says those are two actions.
    /// </summary>
    public static IReadOnlyDictionary<string, StoredCorporateAction> ReadDate(
        SqliteConnection connection,
        DateOnly effectiveDate,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.ticker, a.effective_date, a.type, a.ratio, a.observed_at
              FROM corporate_action a
             WHERE a.effective_date = @effective_date
               AND a.observed_at = (
                     SELECT MAX(l.observed_at)
                       FROM corporate_action l
                      WHERE l.ticker = a.ticker
                        AND l.effective_date = a.effective_date
                        AND l.type = a.type
                        AND l.observed_at <= @observed_before);
            """;
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(effectiveDate));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        return ReadAll(command).ToDictionary(a => Key(a.Ticker, a.Type), StringComparer.Ordinal);
    }

    /// <summary>The dictionary key <see cref="ReadDate"/> returns: the grain minus the date it was read for.</summary>
    public static string Key(string ticker, CorporateActionType type) => ticker + "/" + type.ToStorageText();

    private static IReadOnlyList<StoredCorporateAction> ReadAll(SqliteCommand command)
    {
        var actions = new List<StoredCorporateAction>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            actions.Add(new StoredCorporateAction(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                CorporateActionTypeText.FromStorageText(reader.GetString(2)),
                StoreText.StorageTextToRatio(reader.GetString(3)),
                StoreText.StorageTextToTimestamp(reader.GetString(4))));
        }

        return actions;
    }

    internal static string EndOf(DateOnly date, string sessionZone) => StoreText.EndOfSession(date, sessionZone);
}

/// <summary>
/// Which stocks may not have their averages computed, and as of when.
///
/// A demand with no rebuilt_at is a stock whose calculations refuse to run. Failing loudly is
/// the point: an unprocessed action corrupts every moving average a stock has at once and the
/// result looks entirely reasonable, so the alternative to a loud refusal is a plausible wrong
/// number rather than an obvious one.
/// see: An unprocessed corporate action of any kind blocks calculation, not only a split
///
/// A demand is keyed on the action as observed, so a restated ratio arrives as a second demand
/// rather than as a failed attempt to reopen the first.
/// see: A rebuild demand is keyed on the action as observed, and a restated action raises a new one
///
/// Point in time like everything else. Asked as of a night a demand was outstanding, the answer
/// is that the stock was blocked, even if it has been satisfied since.
/// </summary>
public sealed class IndicatorRebuildReader
{
    private readonly StoreConnectionFactory _connections;

    public IndicatorRebuildReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public IReadOnlyList<RebuildDemand> Open(DateOnly asOf, string sessionZone)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Open(connection, asOf, sessionZone);
    }

    public static IReadOnlyList<RebuildDemand> Open(SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Outstanding as of that night: observed by then, and either never satisfied or
        // satisfied afterwards. The second half is what makes the answer a replay rather than
        // a status.
        command.CommandText = """
            SELECT ticker, effective_date, type, observed_at, rebuilt_at
              FROM indicator_rebuild
             WHERE observed_at <= @observed_before
               AND (rebuilt_at IS NULL OR rebuilt_at > @observed_before)
             ORDER BY ticker, effective_date, type, observed_at;
            """;
        command.Parameters.AddWithValue("@observed_before", CorporateActionReader.EndOf(asOf, sessionZone));

        var demands = new List<RebuildDemand>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            demands.Add(new RebuildDemand(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                CorporateActionTypeText.FromStorageText(reader.GetString(2)),
                StoreText.StorageTextToTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : StoreText.StorageTextToTimestamp(reader.GetString(4))));
        }

        return demands;
    }

    /// <summary>The tickers a calculation must refuse to run for on that date, and nothing else.</summary>
    public static IReadOnlySet<string> BlockedTickers(SqliteConnection connection, DateOnly asOf, string sessionZone) =>
        Open(connection, asOf, sessionZone).Select(d => d.Ticker).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// When each ticker's whole series was last re-observed on one basis.
///
/// Read rather than inferred, and the inference is worth naming because it is the obvious thing
/// to reach for and it fails in both directions (see: A rebuild is satisfied by a recorded refetch, not by inferring one from what changed).
/// </summary>
public static class HistoryRefetchReader
{
    /// <summary>
    /// The latest refetch of each ticker made at or before <paramref name="observedBefore"/>.
    /// One query for the whole universe, because the engine asks it of every member a night.
    /// </summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> LatestByTicker(SqliteConnection connection, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, MAX(refetched_at)
              FROM history_refetch
             WHERE refetched_at <= @observed_before
             GROUP BY ticker;
            """;
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            latest[reader.GetString(0)] = StoreText.StorageTextToTimestamp(reader.GetString(1));
        }

        return latest;
    }
}

/// <summary>One stored corporate action, as observed. Ratios are decimal in code and TEXT in storage.</summary>
public sealed record StoredCorporateAction(
    string Ticker,
    DateOnly EffectiveDate,
    CorporateActionType Type,
    decimal Ratio,
    DateTimeOffset ObservedAt);

/// <summary>
/// One outstanding rebuild, identified by the action that raised it as that action was
/// observed. A restatement of the same action is a different observation and therefore a
/// different demand.
/// </summary>
public sealed record RebuildDemand(
    string Ticker,
    DateOnly EffectiveDate,
    CorporateActionType Type,
    DateTimeOffset ObservedAt,
    DateTimeOffset? RebuiltAt);

public enum CorporateActionType
{
    /// <summary>A change of share count. Rescales every adjusted close before it.</summary>
    Split,

    /// <summary>Cash per share. Moves every adjusted close before it by a smaller factor, and by one all the same.</summary>
    Dividend,
}

public static class CorporateActionTypeText
{
    public static string ToStorageText(this CorporateActionType type) => type switch
    {
        CorporateActionType.Split => "split",
        CorporateActionType.Dividend => "dividend",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static CorporateActionType FromStorageText(string text) => text switch
    {
        "split" => CorporateActionType.Split,
        "dividend" => CorporateActionType.Dividend,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text,
            "corporate_action.type is 'split' or 'dividend'. The column carries a CHECK saying so, "
            + "so a third value here means the store was written by something other than this build."),
    };
}
