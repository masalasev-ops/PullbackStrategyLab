using System.Globalization;
using PullbackStrategyLab.Core.Time;

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

    /// <summary>
    /// A ratio, which is decimal in code and TEXT in storage like a price and for the same
    /// reason: a split of 3 for 2 is 1.5 exactly in decimal and is not in binary floating
    /// point, and a factor that is a hair under scales a whole price history a hair under.
    ///
    /// Named separately from a price rather than folded into it. Ratios are stored as
    /// fractions, never percentages, and a crossing named for what it carries is what stops
    /// 6.8 being written where 0.068 was meant.
    /// </summary>
    public static string RatioToStorageText(decimal ratio) =>
        ratio.ToString(CultureInfo.InvariantCulture);

    public static decimal StorageTextToRatio(string text) =>
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

    /// <summary>
    /// The last instant of a session date, in the form an observation stamp is stored in.
    ///
    /// <b>This is the point-in-time bound, and it is the only correct way to build one.</b> Appending
    /// <c>T23:59:59.999Z</c> to the date closes an Eastern session at 19:59:59 Eastern through
    /// daylight time and 18:59:59 through standard time, so every stage running after the close
    /// writes rows its own session cannot read, and the truncation point moves an hour twice a year.
    /// The zone is named at every call site rather than defaulted, because a bound whose zone is
    /// invisible is how the literal survived twelve sites in the first place.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    public static string EndOfSession(DateOnly sessionDate, string ianaZoneId) =>
        TimestampToStorageText(SessionBoundaries.EndOfSession(sessionDate, ianaZoneId));
}
