using System.Globalization;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one crossing between the two worlds, named for what it does. Prices and money are
/// <see cref="decimal"/> in code and TEXT in storage; statistics are <see cref="double"/>.
/// Never REAL for a price or a money value, and no implicit conversion between the two
/// worlds anywhere else.
///
/// Every conversion is invariant-culture. A machine with a comma decimal separator would
/// otherwise write prices no other machine can read back, and the failure looks like bad
/// data rather than like a locale.
/// </summary>
public static class StoreText
{
    /// <summary>ISO-8601 UTC, to the millisecond. Time is UTC in storage, without exception.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public const string DateFormat = "yyyy-MM-dd";

    public static string PriceToStorageText(decimal price) =>
        price.ToString(CultureInfo.InvariantCulture);

    public static decimal StorageTextToPrice(string text) =>
        decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string StatisticToStorageText(double statistic) =>
        statistic.ToString("R", CultureInfo.InvariantCulture);

    public static double StorageTextToStatistic(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static string TimestampToStorageText(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset StorageTextToTimestamp(string text) =>
        DateTimeOffset.ParseExact(text, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string DateToStorageText(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly StorageTextToDate(string text) =>
        DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture);
}
