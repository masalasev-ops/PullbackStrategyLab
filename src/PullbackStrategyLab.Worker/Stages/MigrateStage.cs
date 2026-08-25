using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Applies outstanding migrations, after taking a snapshot. It refuses to run without a
/// successful snapshot, because a migration is the one operation that can lose a store in
/// a way no later run recovers from.
///
/// The exception is a store that does not exist yet, where there is nothing to snapshot
/// and nothing to lose.
/// </summary>
public sealed class MigrateStage
{
    public const string Name = "migrate";

    private readonly MigrationRunner _migrations;
    private readonly SnapshotStage _snapshot;
    private readonly StoreConnectionFactory _connections;

    public MigrateStage(MigrationRunner migrations, SnapshotStage snapshot, StoreConnectionFactory connections)
    {
        _migrations = migrations;
        _snapshot = snapshot;
        _connections = connections;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (_connections.StoreExists)
        {
            SnapshotResult snapshot = _snapshot.Take();
            if (!snapshot.Taken)
            {
                Console.Error.WriteLine("migrate: refusing to run without a successful snapshot.");
                return 1;
            }

            Console.WriteLine($"migrate: snapshot at {snapshot.SnapshotFile}");
        }
        else
        {
            Console.WriteLine($"migrate: no store at {_connections.StoreFile} yet, so there is nothing to snapshot.");
        }

        MigrationResult result = _migrations.Apply();

        if (result.Applied.Count == 0)
        {
            Console.WriteLine($"migrate: already at version {result.ToVersion}, nothing outstanding.");
            return 0;
        }

        foreach (string applied in result.Applied)
        {
            Console.WriteLine($"migrate: applied {applied}");
        }

        Console.WriteLine($"migrate: version {result.FromVersion} to {result.ToVersion}.");
        return 0;
    }
}
