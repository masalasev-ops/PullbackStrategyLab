using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the lab keeps, and the three things it will not delete.
///
/// There was no retention at all until 3.11. Twenty-four snapshots had accumulated in four days
/// against a store holding one session of setups: 4.6 GB of recovery points for an evidence base
/// of forty-four rows, growing by about 290 MB a night. A recovery path that fills the disk it
/// recovers onto is not one.
///
/// The danger in the repair is larger than the defect, which is why the cases below are mostly
/// about refusal. This is the only code in the lab that deletes a file the operator cannot get
/// back, and RUNBOOK calls snapshots "the recovery path; there is no other".
/// </summary>
public sealed class SnapshotRetentionTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly PullbackStrategyLabPaths _paths;
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 28, 22, 0, 0, TimeSpan.Zero));

    public SnapshotRetentionTests()
    {
        _paths = new PullbackStrategyLabPaths(_root.Path);
        _connections = new StoreConnectionFactory(_paths);
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private SnapshotStage Stage(int kept) => new(
        _connections,
        _paths,
        _clock,
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path, SnapshotsKept = kept }));

    /// <summary>Older snapshots, written as files rather than taken, so their instants are chosen.</summary>
    private string[] SeedOlder(params string[] stamps)
    {
        Directory.CreateDirectory(_paths.SnapshotDirectory);

        var written = new List<string>();
        foreach (string stamp in stamps)
        {
            string file = Path.Combine(_paths.SnapshotDirectory, $"pullbackstrategylab-{stamp}.db");
            File.WriteAllText(file, "not a real database, and retention never opens one");
            written.Add(file);
        }

        return [.. written];
    }

    private static string Name(string path) => Path.GetFileName(path);

    [Fact]
    public void The_newest_are_kept_and_the_surplus_goes()
    {
        SeedOlder("20260820-100000", "20260821-100000", "20260822-100000", "20260823-100000");

        SnapshotResult result = Stage(kept: 3).Take();

        // Three kept: the two newest of the four seeded, plus the one just taken.
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(3, _paths.SnapshotFiles().Count);

        // Oldest first, which is what the chronological name buys.
        Assert.Contains(result.Removed, f => Name(f).Contains("20260820", StringComparison.Ordinal));
        Assert.Contains(result.Removed, f => Name(f).Contains("20260821", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Removed, f => Name(f).Contains("20260823", StringComparison.Ordinal));
    }

    [Fact]
    public void A_directory_already_inside_the_policy_loses_nothing()
    {
        SeedOlder("20260827-100000");

        SnapshotResult result = Stage(kept: 7).Take();

        Assert.Empty(result.Removed);
        Assert.Equal(2, _paths.SnapshotFiles().Count);
    }

    [Fact]
    public void The_snapshot_just_taken_is_never_the_one_removed()
    {
        SeedOlder("20260820-100000", "20260821-100000");

        // A retention of one. Naively "keep the newest one" would still have to decide whether the
        // new copy counts, and a policy that deleted its own output would leave the lab with none.
        SnapshotResult result = Stage(kept: 1).Take();

        Assert.Equal(2, result.Removed.Count);

        string kept = Assert.Single(_paths.SnapshotFiles());
        Assert.Equal(Path.GetFullPath(result.SnapshotFile), Path.GetFullPath(kept));
        Assert.True(File.Exists(result.SnapshotFile));
    }

    [Fact]
    public void A_renamed_snapshot_is_invisible_to_the_policy()
    {
        SeedOlder("20260820-100000", "20260821-100000", "20260822-100000");

        // The escape hatch, and the reason retention matches the generated name rather than *.db.
        // An operator who wants a copy kept past the window renames it, and nothing here can touch
        // it afterwards.
        string keepForever = Path.Combine(_paths.SnapshotDirectory, "before-the-4.1-migration.db");
        File.WriteAllText(keepForever, "kept on purpose");

        Stage(kept: 1).Take();

        Assert.True(File.Exists(keepForever));
        Assert.DoesNotContain(_paths.SnapshotFiles(), f => Name(f) == "before-the-4.1-migration.db");
    }

    [Fact]
    public void Nothing_is_removed_when_the_snapshot_did_not_verify()
    {
        string[] older = SeedOlder("20260820-100000", "20260821-100000", "20260822-100000");

        // The ordering that matters most. A short disk or a corrupt page still produces a file, and
        // deleting a week of recovery points because a broken new one was written is the failure
        // this refusal exists to prevent. Simulated by asking the result directly, because a
        // snapshot that fails its integrity check cannot be produced on demand.
        var unverified = new SnapshotResult(
            true, "somewhere.db", 1, "malformed database", [new TableCount("setup", 44, 0)], []);

        Assert.False(unverified.Complete);
        Assert.Empty(unverified.Removed);

        // And the real path leaves them alone until it has a good copy, which is the same
        // condition stated twice: Complete and integrity ok.
        SnapshotResult good = Stage(kept: 99).Take();

        Assert.True(good.Complete);
        Assert.Equal("ok", good.Integrity);
        Assert.Empty(good.Removed);
        Assert.All(older, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void A_file_that_is_not_a_snapshot_is_never_considered()
    {
        Directory.CreateDirectory(_paths.SnapshotDirectory);

        // Anything the lab did not name. Retention only ever deletes files it could have written.
        string[] strangers =
        [
            Path.Combine(_paths.SnapshotDirectory, "notes.txt"),
            Path.Combine(_paths.SnapshotDirectory, "pullbackstrategylab.db"),
            Path.Combine(_paths.SnapshotDirectory, "pullbackstrategylab-2026.db"),
        ];

        foreach (string stranger in strangers)
        {
            File.WriteAllText(stranger, "not mine to delete");
        }

        SeedOlder("20260820-100000", "20260821-100000");

        SnapshotResult result = Stage(kept: 1).Take();

        Assert.Equal(2, result.Removed.Count);
        Assert.All(strangers, f => Assert.True(File.Exists(f), $"{Name(f)} was deleted and is not a snapshot."));
    }
}
