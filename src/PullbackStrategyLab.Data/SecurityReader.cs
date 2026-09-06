using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The attributes SectorResolver caches on a name: its sector, its industry and its market
/// capitalisation.
///
/// <b>Read as of a date, like everything else here, and the date it is read against moved at
/// 6.1.</b> The three values sit in mutable columns rather than in an append-only table, so there
/// is no history to walk back through. What there is instead is <c>sector_resolved_at</c>, the
/// instant the lookup was made, and every reader here treated that as the instant the fact became
/// true.
///
/// <b>For an attribute looked up lazily those are two different things, and that is the whole of
/// the correction.</b> A bar's observation is dated by the market, so the two coincide. A company's
/// sector was true for years before anyone asked, so a name resolved on the 28th was invisible to
/// the session of the 27th although the sector it carries was as true on the 27th as it is now.
/// `security` is the only table in the store with that shape, and it has no session-date column at
/// all, which is the conflation rather than a consequence of it.
///
/// <b>So the attribute is asserted from <c>first_seen</c>, the first session the lab had any reason
/// to hold it.</b> That is the cheapest of the three bases the plan priced and the one that claims
/// the least: every resolvable row carries such a date, nothing is re-captured, and the claim made
/// is only that the lab had heard of the name. The vendor's own validity date is truer and costs a
/// re-capture; asserting the attribute from the beginning of the record is cheaper still and least
/// honest, because it would make a name resolved next year visible to a session two years ago.
/// Both are refused by name rather than left unmentioned.
/// see: The lazily-resolved attribute is asserted from the first session the lab had reason to hold it, and the correction runs with the signal backfill
///
/// <b>What this changes about a reconstructed session is worth stating.</b> It used to see no
/// resolved attribute at all, because none of them had been resolved yet, and that was the honest
/// answer under the old basis. Under this one it sees an attribute wherever the lab had already seen
/// the name, which for a reconstructed 2024 session is nothing, because <c>first_seen</c> is a date
/// in the lab's own life. The calibration table's reason for existing is unchanged.
///
/// <b>And <c>first_seen</c> is weaker for a delisted name than for a listed one.</b> For a survivor
/// it is the night the lab first saw it trading; for a delisted name the vendor publishes no
/// delisting date, so it is the night the delisted list was first read, which is a fact about the
/// lab. A delisted name is never flagged, so nothing downstream reads an attribute through this
/// bound for one, and the weakness is recorded rather than relied on.
/// </summary>
public static class SecurityReader
{
    /// <summary>
    /// One name's market capitalisation as the lab may assert it for <paramref name="asOf"/>, or
    /// null where the name was not yet known or nothing has been resolved.
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
               AND first_seen <= @asserted_from
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@asserted_from", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? StoreText.StorageTextToPrice(reader.GetString(0)) : null;
    }

    /// <summary>One name's industry on the same basis, or null.</summary>
    public static string? Industry(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT industry FROM security
             WHERE ticker = @ticker
               AND industry IS NOT NULL
               AND first_seen <= @asserted_from
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@asserted_from", StoreText.DateToStorageText(asOf));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }
}
