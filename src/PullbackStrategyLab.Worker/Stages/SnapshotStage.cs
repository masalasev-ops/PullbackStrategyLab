using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Writes a full copy of the store with <c>VACUUM INTO</c>. That folds the write-ahead log
/// in, drops free pages, and produces one consistent file with no siblings to forget.
///
/// Copying the .db alone is the failure this exists to avoid: the most recent writes live
/// in a -wal sibling, so the copy opens cleanly and is missing several nights.
///
/// It also does RUNBOOK's steps 2 and 5, because those are the ones a person does by hand and
/// the ones that fail quietly. The counts are taken from the source before the copy and from the
/// copy afterwards and compared here, and the integrity check runs against the copy. An
/// integrity check proves the file is not corrupt and says nothing about whether it is complete,
/// which is why both are needed and why a snapshot that cannot show both is not a snapshot
/// anybody should move a machine on.
/// </summary>
public sealed class SnapshotStage
{
    public const string Name = "snapshot-db";

    private readonly StoreConnectionFactory _connections;
    private readonly PullbackStrategyLabPaths _paths;
    private readonly IClock _clock;

    public SnapshotStage(StoreConnectionFactory connections, PullbackStrategyLabPaths paths, IClock clock)
    {
        _connections = connections;
        _paths = paths;
        _clock = clock;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        SnapshotResult result = Take();

        if (!result.Taken)
        {
            Console.WriteLine($"snapshot-db: nothing to snapshot, no store at {result.SnapshotFile}");
            return 0;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"snapshot-db: wrote {result.SnapshotFile} ({result.Bytes:N0} bytes)"));
        Console.WriteLine($"snapshot-db: integrity {result.Integrity}");

        foreach (TableCount table in result.Counts)
        {
            string mismatch = table.Matches ? string.Empty : "   MISMATCH";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"snapshot-db:   {table.Table,-22} {table.Source,12:N0} source  {table.Snapshot,12:N0} snapshot{mismatch}"));
        }

        string verdict = result.Complete ? "counts matched" : "COUNTS DID NOT MATCH";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"snapshot-db: {result.Counts.Count} table(s), {result.Counts.Sum(c => c.Source):N0} row(s), {verdict}"));

        return result.Complete && result.Integrity == "ok" ? 0 : 1;
    }

    /// <summary>
    /// Takes the snapshot, or reports that there is no store yet. A store that does not
    /// exist is not a failed snapshot: it is the state a machine is in before its first
    /// migration, and refusing to migrate in that state would make the lab unstartable.
    /// </summary>
    public SnapshotResult Take()
    {
        if (!_connections.StoreExists)
        {
            return new SnapshotResult(false, _connections.StoreFile, 0, "no store", []);
        }

        _paths.EnsureDirectories();
        string destination = _paths.SnapshotFile(_clock.UtcNow);

        using SqliteConnection connection = _connections.OpenWrite();

        // RUNBOOK's step 2, taken before anything is copied, because after the copy there is
        // nothing left to compare against.
        IReadOnlyDictionary<string, long> before = CountEveryTable(connection);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "VACUUM INTO @destination;";
            command.Parameters.AddWithValue("@destination", destination);
            command.ExecuteNonQuery();
        }

        // RUNBOOK's step 5, against the copy rather than against the original.
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        snapshot.Open();

        string integrity;
        using (SqliteCommand command = snapshot.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            integrity = command.ExecuteScalar() as string ?? "unknown";
        }

        IReadOnlyDictionary<string, long> after = CountEveryTable(snapshot);

        TableCount[] counts =
        [
            .. before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(table => new TableCount(
                    table,
                    before.GetValueOrDefault(table, -1),
                    after.GetValueOrDefault(table, -1)))
        ];

        return new SnapshotResult(true, destination, new FileInfo(destination).Length, integrity, counts);
    }

    /// <summary>
    /// A row count for every table the store holds, read from the schema rather than from a list.
    ///
    /// A list kept here would go stale the moment a migration added a table, and a count that
    /// silently omits a table is exactly the failure this step exists to catch: the copy opens
    /// cleanly, every counted table matches, and one nobody counted is empty.
    /// </summary>
    private static IReadOnlyDictionary<string, long> CountEveryTable(SqliteConnection connection)
    {
        var tables = new List<string>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name FROM sqlite_master
                 WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                 ORDER BY name;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();

            // The name comes from sqlite_master rather than from a caller, so it is a name this
            // database already holds. Stated because a SQL string built by concatenation
            // deserves the sentence.
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"SELECT COUNT(*) FROM \"{table}\";");
            counts[table] = (long)(command.ExecuteScalar() ?? 0L);
        }

        return counts;
    }
}

/// <summary>One table, counted on both sides of the copy.</summary>
public sealed record TableCount(string Table, long Source, long Snapshot)
{
    public bool Matches => Source == Snapshot;
}

public sealed record SnapshotResult(
    bool Taken,
    string SnapshotFile,
    long Bytes,
    string Integrity,
    IReadOnlyList<TableCount> Counts)
{
    /// <summary>Every table present on both sides with the same number of rows.</summary>
    public bool Complete => Counts.All(c => c.Matches);
}
