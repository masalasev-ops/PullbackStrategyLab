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
        Console.WriteLine(result.Taken
            ? string.Create(CultureInfo.InvariantCulture, $"snapshot-db: wrote {result.SnapshotFile} ({result.Bytes} bytes)")
            : $"snapshot-db: nothing to snapshot, no store at {result.SnapshotFile}");
        return 0;
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
            return new SnapshotResult(false, _connections.StoreFile, 0);
        }

        _paths.EnsureDirectories();
        string destination = _paths.SnapshotFile(_clock.UtcNow);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO @destination;";
        command.Parameters.AddWithValue("@destination", destination);
        command.ExecuteNonQuery();

        return new SnapshotResult(true, destination, new FileInfo(destination).Length);
    }
}

public sealed record SnapshotResult(bool Taken, string SnapshotFile, long Bytes);
