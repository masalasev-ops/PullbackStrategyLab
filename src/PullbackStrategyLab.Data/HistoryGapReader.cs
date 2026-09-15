using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The sessions a stock's stored daily history is missing that nothing has asked the vendor for
/// since. From 7.18.
///
/// <b>A session is a day the index history holds.</b> The trackers' whole histories are refetched
/// every night, so a day they hold is a day the market traded, whatever happened on the nights in
/// between. A stock's own bars cannot say that about themselves: a day the store never asked for
/// looks exactly like a day nothing traded, which is how 2026-09-08 and 2026-09-11 went missing for
/// all but 68 names and the averages of 2026-09-14 were computed across both without a word.
/// see: A session is a date the store holds minutes for, and no calendar is authored here
///
/// <b>A session is missing only between two of the stock's own bars.</b> A day before its first
/// bar is a stock that had not listed or had not yet been fetched, which is a different question
/// with a different answer, and a day after its last is one the night has not reached.
///
/// <b>And a session the vendor was asked for and did not return is not missing.</b> The per-ticker
/// refetch asks for a stock's whole series through its own date, so a day on or before the latest
/// refetch's end that is still absent is one the vendor holds nothing for: a halt, or a thin listing
/// that did not trade. Asking again changes nothing, and would cost a call a night for as long as
/// the day stayed inside the window and refuse the stock's averages for the same stretch.
/// see: A stock's history is made whole before its averages are computed, and an average across a missing session is refused
/// </summary>
public static class HistoryGapReader
{
    /// <summary>
    /// Every stock with at least one missing session among the last <paramref name="sessions"/> the
    /// index history holds up to <paramref name="asOf"/>, and the sessions, oldest first. A stock with
    /// none is absent rather than present with an empty list.
    ///
    /// <b>The window is sessions the market held, not bars the stock has,</b> and for the averages
    /// that is exact rather than close: a stock missing nothing among the last hundred and fifty
    /// sessions has its last hundred and fifty bars on exactly those sessions, so a gap further back
    /// is outside every window the engine reads.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<DateOnly>> Missing(
        SqliteConnection connection,
        IReadOnlyList<string> indexSymbols,
        DateOnly asOf,
        int sessions,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(indexSymbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessions);

        DateOnly[] held = HeldSessions(connection, indexSymbols, asOf, sessions, observedBefore);

        if (held.Length == 0)
        {
            // No index history, so no day can be placed and nothing can be called missing. A store
            // before its first index ingest, or a test that seeds none.
            return new Dictionary<string, IReadOnlyList<DateOnly>>(StringComparer.Ordinal);
        }

        Dictionary<string, HashSet<DateOnly>> stored = StoredDates(connection, held[0], asOf, observedBefore);
        Dictionary<string, DateOnly> askedThrough = AskedThrough(connection, observedBefore);

        var missing = new Dictionary<string, IReadOnlyList<DateOnly>>(StringComparer.Ordinal);

        foreach ((string ticker, HashSet<DateOnly> dates) in stored)
        {
            DateOnly first = dates.Min();
            DateOnly last = dates.Max();
            bool asked = askedThrough.TryGetValue(ticker, out DateOnly through);

            DateOnly[] gaps =
                [.. held.Where(h => h > first && h < last && !dates.Contains(h) && (!asked || h > through))];

            if (gaps.Length > 0)
            {
                missing[ticker] = gaps;
            }
        }

        return missing;
    }

    /// <summary>
    /// The last <paramref name="sessions"/> days any tracker holds a bar for, oldest first. One read a
    /// tracker rather than one over all of them, and the union taken here: the latest of each
    /// tracker's own last N days covers the latest N of the union.
    /// </summary>
    private static DateOnly[] HeldSessions(
        SqliteConnection connection, IReadOnlyList<string> indexSymbols, DateOnly asOf, int sessions, DateTimeOffset observedBefore)
    {
        var days = new HashSet<DateOnly>();

        foreach (string symbol in indexSymbols)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT bar_date
                  FROM index_bar
                 WHERE symbol = @symbol
                   AND bar_date <= @as_of
                   AND observed_at <= @observed_before
                 ORDER BY bar_date DESC
                 LIMIT @sessions;
                """;
            command.Parameters.AddWithValue("@symbol", symbol);
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));
            command.Parameters.AddWithValue("@sessions", sessions);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                days.Add(StoreText.StorageTextToDate(reader.GetString(0)));
            }
        }

        return [.. days.OrderDescending().Take(sessions).Order()];
    }

    /// <summary>Every stock's stored bar dates inside the window, as observed by the bound.</summary>
    private static Dictionary<string, HashSet<DateOnly>> StoredDates(
        SqliteConnection connection, DateOnly from, DateOnly asOf, DateTimeOffset observedBefore)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ticker, bar_date
              FROM daily_bar
             WHERE bar_date >= @from
               AND bar_date <= @as_of
               AND observed_at <= @observed_before;
            """;
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var stored = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string ticker = reader.GetString(0);
            if (!stored.TryGetValue(ticker, out HashSet<DateOnly>? dates))
            {
                dates = [];
                stored[ticker] = dates;
            }

            dates.Add(StoreText.StorageTextToDate(reader.GetString(1)));
        }

        return stored;
    }

    /// <summary>The end date of each stock's latest refetch, which is how far the vendor has been asked.</summary>
    private static Dictionary<string, DateOnly> AskedThrough(SqliteConnection connection, DateTimeOffset observedBefore)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ticker, MAX(to_date)
              FROM history_refetch
             WHERE refetched_at <= @observed_before
             GROUP BY ticker;
            """;
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var through = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            through[reader.GetString(0)] = StoreText.StorageTextToDate(reader.GetString(1));
        }

        return through;
    }
}
