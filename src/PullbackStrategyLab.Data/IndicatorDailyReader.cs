using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one way computed indicators are read, on the same terms as every other store here:
/// append-only, keyed with the instant of the computation, and a read takes the latest
/// computation at or before its as-of date.
///
/// That is what lets a rebuild reach the rows it invalidates. A ticker recomputed after a
/// corporate action is honoured gains a second row for each affected date, and a replay of a
/// night before the rebuild still returns the numbers the lab acted on, wrong ones included.
///
/// Every read takes an as-of date and there is no overload that does not, for the reason the
/// bar reader gives: a read that could omit it would compile, run, and answer with figures the
/// lab could not have had.
/// </summary>
public sealed class IndicatorDailyReader
{
    private readonly StoreConnectionFactory _connections;

    public IndicatorDailyReader(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>
    /// One ticker's indicators for one session, as they stood at the end of
    /// <paramref name="asOf"/>, or null if nothing had been computed by then.
    /// </summary>
    public StoredIndicators? Read(string ticker, DateOnly session, DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return Read(connection, ticker, session, asOf);
    }

    public static StoredIndicators? Read(SqliteConnection connection, string ticker, DateOnly session, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                   dollar_volume_median_20, range_avg_20, ladder_grade
              FROM indicator_daily
             WHERE ticker = @ticker
               AND as_of = @session
               AND computed_at <= @computed_before
             ORDER BY computed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@computed_before", EndOf(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>
    /// The latest computation of one ticker's session, whenever it was made. What the engine
    /// compares a fresh calculation against, so a rerun that produces identical figures writes
    /// nothing and a rebuild that produces different ones writes a row.
    /// </summary>
    public static StoredIndicators? Latest(SqliteConnection connection, string ticker, DateOnly session)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                   dollar_volume_median_20, range_avg_20, ladder_grade
              FROM indicator_daily
             WHERE ticker = @ticker AND as_of = @session
             ORDER BY computed_at DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static StoredIndicators Map(SqliteDataReader reader) => new(
        reader.GetString(0),
        StoreText.StorageTextToDate(reader.GetString(1)),
        StoreText.StorageTextToTimestamp(reader.GetString(2)),
        StoreText.StorageTextToPrice(reader.GetString(3)),
        StoreText.StorageTextToPrice(reader.GetString(4)),
        StoreText.StorageTextToPrice(reader.GetString(5)),
        StoreText.StorageTextToPrice(reader.GetString(6)),
        StoreText.StorageTextToRatio(reader.GetString(7)),
        StoreText.StorageTextToPrice(reader.GetString(8)),
        StoreText.StorageTextToPrice(reader.GetString(9)),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string EndOf(DateOnly date) => StoreText.DateToStorageText(date) + "T23:59:59.999Z";
}

/// <summary>
/// One computation of one ticker's session. The daily range is a fraction rather than a
/// percentage, and the ladder grade is null until TierClassifier writes a later observation
/// carrying it.
/// </summary>
public sealed record StoredIndicators(
    string Ticker,
    DateOnly AsOf,
    DateTimeOffset ComputedAt,
    decimal EmaShort,
    decimal EmaMedium,
    decimal EmaLong,
    decimal AverageTrueRange,
    decimal AverageDailyRange,
    decimal DollarVolumeMedian,
    decimal RangeAverage,
    string? LadderGrade) : IIndicatorFigures
{
    /// <summary>True when the two computations produced the same figures, whatever their computed_at says.</summary>
    public bool SameFigures(IIndicatorFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return EmaShort == other.EmaShort
            && EmaMedium == other.EmaMedium
            && EmaLong == other.EmaLong
            && AverageTrueRange == other.AverageTrueRange
            && AverageDailyRange == other.AverageDailyRange
            && DollarVolumeMedian == other.DollarVolumeMedian
            && RangeAverage == other.RangeAverage;
    }
}

/// <summary>
/// The seven computed figures, without the row they belong to. Shared between the engine that
/// produces them and the reader that compares against them, so the comparison cannot go out of
/// step with the calculation.
/// </summary>
public interface IIndicatorFigures
{
    decimal EmaShort { get; }

    decimal EmaMedium { get; }

    decimal EmaLong { get; }

    decimal AverageTrueRange { get; }

    decimal AverageDailyRange { get; }

    decimal DollarVolumeMedian { get; }

    decimal RangeAverage { get; }
}
