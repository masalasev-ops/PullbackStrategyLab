using Microsoft.Data.Sqlite;

using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The attributes SectorResolver caches on a name: its sector, its industry and its market
/// capitalisation.
///
/// <b>Read as of a date, like everything else here, and this one is easy to get wrong.</b> The three
/// values sit in mutable columns rather than in an append-only table, so there is no history to walk
/// back through; what there is instead is <c>sector_resolved_at</c>, the instant the lookup was made.
/// A read that ignored it would hand a session in 2024 a market capitalisation resolved in 2026 and
/// answer with a figure the lab could not have had.
/// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
///
/// The consequence is worth stating rather than discovering: a reconstructed historical session sees
/// no resolved attributes at all, because none of them were resolved yet. That is the honest answer
/// and it is why the calibration table exists.
/// </summary>
public static class SecurityReader
{
    /// <summary>
    /// One name's market capitalisation as it stood at the end of <paramref name="asOf"/>, or null
    /// where nothing had been resolved by then.
    ///
    /// Null is not zero and callers may not treat it as a cleared floor. The short side's
    /// <c>tradable-shortable</c> check stands in for borrow availability, which is not in the feed
    /// at all, so a name nobody has looked up is the one place an absent figure must not become a
    /// tradable verdict.
    /// </summary>
    public static decimal? MarketCap(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT market_cap FROM security
             WHERE ticker = @ticker
               AND market_cap IS NOT NULL
               AND sector_resolved_at IS NOT NULL
               AND sector_resolved_at <= @resolved_before
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@resolved_before", EndOf(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? StoreText.StorageTextToPrice(reader.GetString(0)) : null;
    }

    /// <summary>One name's industry as it stood at the end of <paramref name="asOf"/>, or null.</summary>
    public static string? Industry(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT industry FROM security
             WHERE ticker = @ticker
               AND industry IS NOT NULL
               AND sector_resolved_at IS NOT NULL
               AND sector_resolved_at <= @resolved_before
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@resolved_before", EndOf(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static string EndOf(DateOnly date) => StoreText.EndOfSession(date, SessionBoundaries.UsEquities);
}
