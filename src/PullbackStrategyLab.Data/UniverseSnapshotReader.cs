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
