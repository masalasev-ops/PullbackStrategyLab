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

            // All three conditions, not just Taken.
            //
            // Taken is set for any completed VACUUM INTO, whatever the copy turned out to hold, so
            // the guard proved a file had been written and not that it was usable. SnapshotStage's
            // own Run already exits non-zero on `Complete && Integrity == "ok"` and this did not,
            // which meant the one operation that can lose a store in a way no later run recovers
            // from was proceeding on an unverified backup. A short disk or a corrupt page produces
            // a file, an integrity check that answers something other than ok, and mismatched row
            // counts, and none of the three stopped the migration.
            if (!snapshot.Taken)
            {
                Console.Error.WriteLine("migrate: refusing to run without a successful snapshot.");
                return 1;
            }

            if (!string.Equals(snapshot.Integrity, "ok", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"migrate: refusing to run. The snapshot at {snapshot.SnapshotFile} answered "
                    + $"PRAGMA integrity_check with \"{snapshot.Integrity}\", so it is not a copy anything "
                    + "could be restored from.");
                return 1;
            }

            if (!snapshot.Complete)
            {
                string[] short_ = [.. snapshot.Counts.Where(c => !c.Matches).Select(c => c.ToString())];
                Console.Error.WriteLine(
                    $"migrate: refusing to run. The snapshot at {snapshot.SnapshotFile} does not hold every "
                    + $"row the store does, in {short_.Length} table(s): {string.Join(", ", short_)}. An "
                    + "integrity check proves the file is not corrupt, not that it is complete.");
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
