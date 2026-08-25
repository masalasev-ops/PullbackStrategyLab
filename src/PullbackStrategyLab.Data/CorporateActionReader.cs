using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way stored corporate actions are read, and the one place the rebuild demand they
/// create is answered.
///
/// Every read takes an as-of date, for the same reason every bar read does: a split observed
/// on Thursday did not exist on Wednesday, and a replay that saw it would be answering with
/// knowledge the lab did not have. There is no overload without one.
/// </summary>
public sealed class CorporateActionReader
{
    private readonly StoreConnectionFactory _connections;

    public CorporateActionReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// Every action for one ticker effective on or before <paramref name="asOf"/> and observed
    /// by the end of that date, oldest first.
    /// </summary>
    public IReadOnlyList<StoredCorporateAction> Read(string ticker, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, asOf);
    }

    public static IReadOnlyList<StoredCorporateAction> Read(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, effective_date, type, ratio, observed_at
              FROM corporate_action
             WHERE ticker = @ticker
               AND effective_date <= @as_of
               AND observed_at <= @observed_before
             ORDER BY effective_date, type;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", EndOf(asOf));

        return ReadAll(command);
    }

    /// <summary>
    /// Every action already stored for one effective date, whatever its type. What the ingestor
    /// compares tonight's bulk response against, so a rerun writes nothing.
    ///
    /// Keyed on ticker and type together, because a stock can pay a dividend and split on the
    /// same day and the grain says those are two rows.
    /// </summary>
    public static IReadOnlyDictionary<string, StoredCorporateAction> ReadDate(SqliteConnection connection, DateOnly effectiveDate)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, effective_date, type, ratio, observed_at
              FROM corporate_action
             WHERE effective_date = @effective_date;
            """;
        command.Parameters.AddWithValue("@effective_date", StoreText.DateToStorageText(effectiveDate));

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

    internal static string EndOf(DateOnly date) => StoreText.DateToStorageText(date) + "T23:59:59.999Z";
}

/// <summary>
/// Which stocks may not have their averages computed, and as of when.
///
/// A demand row with no rebuilt_at is a stock whose calculations refuse to run. Failing loudly
/// is the point: a split corrupts every moving average a stock has at once and the result
/// looks entirely reasonable, so the alternative to a loud refusal is a plausible wrong number
/// rather than an obvious one.
///
/// Point in time like everything else. Asked as of a night the split was outstanding, the
/// answer is that the stock was blocked, even if it has been rebuilt since.
/// </summary>
public sealed class IndicatorRebuildReader
{
    private readonly StoreConnectionFactory _connections;

    public IndicatorRebuildReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public IReadOnlyList<RebuildDemand> Pending(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Pending(connection, asOf);
    }

    public static IReadOnlyList<RebuildDemand> Pending(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();

        // Outstanding as of that night: requested by then, and either never rebuilt or rebuilt
        // afterwards. The second half is what makes the answer a replay rather than a status.
        command.CommandText = """
            SELECT ticker, effective_date, requested_at, rebuilt_at
              FROM indicator_rebuild
             WHERE requested_at <= @observed_before
               AND (rebuilt_at IS NULL OR rebuilt_at > @observed_before)
             ORDER BY ticker, effective_date;
            """;
        command.Parameters.AddWithValue("@observed_before", CorporateActionReader.EndOf(asOf));

        var demands = new List<RebuildDemand>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            demands.Add(new RebuildDemand(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                StoreText.StorageTextToTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : StoreText.StorageTextToTimestamp(reader.GetString(3))));
        }

        return demands;
    }

    /// <summary>The tickers a calculation must refuse to run for on that date, and nothing else.</summary>
    public static IReadOnlySet<string> BlockedTickers(SqliteConnection connection, DateOnly asOf) =>
        Pending(connection, asOf).Select(d => d.Ticker).ToHashSet(StringComparer.Ordinal);
}

/// <summary>One stored corporate action. Ratios are decimal in code and TEXT in storage.</summary>
public sealed record StoredCorporateAction(
    string Ticker,
    DateOnly EffectiveDate,
    CorporateActionType Type,
    decimal Ratio,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// True when this action changes the scale of every adjusted close before it, which is what
    /// forces a rebuild. A one-for-one split is a vendor artefact rather than an event and
    /// rescales nothing, so it is excluded here rather than filtered by whoever reads this.
    /// </summary>
    public bool RescalesHistory => Type == CorporateActionType.Split && Ratio != 1m;
}

/// <summary>One outstanding rebuild, and the split that demanded it.</summary>
public sealed record RebuildDemand(
    string Ticker,
    DateOnly EffectiveDate,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RebuiltAt);

public enum CorporateActionType
{
    /// <summary>A change of share count. Rescales every adjusted close before it.</summary>
    Split,

    /// <summary>Cash per share. Stored, and does not demand a rebuild in this build.</summary>
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
