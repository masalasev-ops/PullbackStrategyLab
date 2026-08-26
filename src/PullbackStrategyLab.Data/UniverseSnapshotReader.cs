using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Who was listed on a given night, and who is listed now. Two different questions, and keeping
/// them apart is the point of this class rather than an accident of its shape.
///
/// <see cref="Members"/> reads the nightly snapshot, which is what makes a replay free of
/// survivorship bias: a name delisted since is simply absent from the night it was not listed on.
/// Every nightly stage reads this one.
///
/// <see cref="CurrentMembers"/> reads membership as it stands today, and exactly one caller may
/// use it: a calibration run, which walks history the lab was not running for and has no snapshot
/// to read. Those rows carry survivorship bias by construction, which is why they go to
/// `calibration_setup` and why nothing downstream reads them.
/// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
/// </summary>
public static class UniverseSnapshotReader
{
    /// <summary>The names listed on one night, in ticker order. Point in time by construction.</summary>
    public static IReadOnlyList<string> Members(SqliteConnection connection, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM universe_snapshot WHERE as_of = @as_of ORDER BY ticker";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        return Read(command);
    }

    /// <summary>
    /// The names listed today, for a calibration run and nothing else.
    ///
    /// Named for what it is rather than offered as a fallback when a snapshot is missing. A stage
    /// that silently fell back to this on a night with no snapshot would produce a reconstructed
    /// answer that looks exactly like a real one, which is the failure the whole snapshot exists to
    /// prevent.
    /// </summary>
    public static IReadOnlyList<string> CurrentMembers(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM universe_member WHERE removed_on IS NULL ORDER BY ticker";

        return Read(command);
    }

    /// <summary>
    /// The names listed today that the store holds at least <paramref name="sessions"/> sessions of
    /// history for, at or before <paramref name="asOf"/>.
    ///
    /// What a calibration run walks, and the count is not a detail. A member the store holds one bar
    /// for produces no evidence on any session, because every figure the detector reads needs the
    /// warm-up behind it. The nightly bulk ingest stores the whole market's closes for the evening it
    /// runs, so in the golden fixture seven thousand two hundred names have a bar and thirty have a
    /// history: a run that walked "members with any bar at all" would walk all seven thousand, take
    /// two orders of magnitude longer, and count exactly the same setups.
    ///
    /// It is also the honest denominator. The distribution is read as a rate per name so it survives
    /// a change of universe, and a rate over seven thousand names when thirty could ever have flagged
    /// is a number about the fixture's symbol list rather than about the thresholds.
    /// </summary>
    public static IReadOnlyList<string> CurrentMembersWithHistory(
        SqliteConnection connection,
        DateOnly asOf,
        int sessions,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessions);

        using SqliteCommand command = connection.CreateCommand();

        // The observation instant is the caller's rather than derived from the as-of date, on the
        // same terms as the reader that takes both. A backfill acquires a name's whole history in
        // one evening, so every bar of 2024 was observed in 2026 and an instant taken from the
        // as-of date would see none of it. Derived here, this read answered "no member has any
        // history" over a store of one and a half million bars, and the run it fed reported six
        // hundred sessions and nought setups.
        command.CommandText = """
            SELECT m.ticker
              FROM universe_member m
              JOIN daily_bar b ON b.ticker = m.ticker
             WHERE m.removed_on IS NULL
               AND b.bar_date <= @as_of
               AND b.observed_at <= @observed_before
             GROUP BY m.ticker
            HAVING COUNT(DISTINCT b.bar_date) >= @sessions
             ORDER BY m.ticker
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));
        command.Parameters.AddWithValue("@sessions", sessions);

        return Read(command);
    }

    private static IReadOnlyList<string> Read(SqliteCommand command)
    {
        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }
}
