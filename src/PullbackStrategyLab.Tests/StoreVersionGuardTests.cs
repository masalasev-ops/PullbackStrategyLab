using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A stage refusing to run against a store at a version other than the one this build carries.
///
/// <b>What it is for.</b> On 2026-08-28 migrations 031 and 032 landed and <c>data/live</c> was never
/// migrated. detect-long, vectorize, controls and cap each died on
/// <c>no such column: degraded_because</c>, one slot after the next, and the night produced no
/// setups at all against inputs that were entirely clean. Every message named a column, which says
/// what broke and not why. Nothing in the lab compared the store's version against the code's, so
/// the first thing to notice was a raw SQLite error at 18:20 with nobody watching, and the next
/// morning's band read clean.
/// </summary>
public sealed class StoreVersionGuardTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public StoreVersionGuardTests() =>
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));

    public void Dispose() => _root.Dispose();

    /// <summary>Stands the store up at a version, the way the store on 2026-08-28 stood at 30.</summary>
    private void MigrateThrough(int version)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        new MigrationRunner(_connections).Apply(connection, version);
    }

    [Fact]
    public void A_store_one_migration_behind_the_build_refuses_the_stage_and_names_both_versions()
    {
        int needed = MigrationRunner.LatestVersion;
        MigrateThrough(needed - 1);

        string? why = Program.WhyThisStageCannotRun(LongSetupDetector.Name, _connections);

        Assert.NotNull(why);

        // Both numbers, because "the store is behind" is a sentence an operator cannot act on and
        // "the store is at 30 and this build needs 32" is one they can.
        Assert.Contains((needed - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), why, StringComparison.Ordinal);
        Assert.Contains(needed.ToString(System.Globalization.CultureInfo.InvariantCulture), why, StringComparison.Ordinal);
        Assert.Contains("tools/migrate", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_at_the_version_the_build_carries_runs()
    {
        MigrateThrough(MigrationRunner.LatestVersion);

        // The other direction, so "always refuses" is not what passes the test above.
        Assert.Null(Program.WhyThisStageCannotRun(LongSetupDetector.Name, _connections));
    }

    [Fact]
    public void A_store_ahead_of_the_build_is_refused_as_well_and_says_which_way_round_it_is()
    {
        MigrateThrough(MigrationRunner.LatestVersion);

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA user_version = {MigrationRunner.LatestVersion + 1};";
            command.ExecuteNonQuery();
        }

        string? why = Program.WhyThisStageCannotRun(LongSetupDetector.Name, _connections);

        Assert.NotNull(why);

        // An older binary against a migrated store is the same fault with the sign changed, and it
        // is quieter: the columns are all there and one of them may not mean what this build reads.
        Assert.Contains("newer build", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_store_that_does_not_exist_yet_is_not_behind_anything()
    {
        Assert.False(_connections.StoreExists);

        // migrate creates it. A guard that refused here would refuse a first run on a new machine,
        // which is the move procedure's step 6.
        Assert.Null(Program.WhyThisStageCannotRun(LongSetupDetector.Name, _connections));
    }

    [Theory]
    [InlineData(MigrateStage.Name)]
    [InlineData(SnapshotStage.Name)]
    [InlineData("list-stages")]
    public void The_three_stages_that_may_run_at_any_version_do(string stage)
    {
        MigrateThrough(MigrationRunner.LatestVersion - 1);

        // migrate is the repair. snapshot-db is the recovery path and the RUNBOOK runs it before
        // every migration, so refusing it would refuse the one command standing between a store
        // that is behind and a store that cannot be put back. list-stages reads nothing.
        Assert.Null(Program.WhyThisStageCannotRun(stage, _connections));
    }

    [Fact]
    public void Every_other_stage_is_guarded()
    {
        MigrateThrough(MigrationRunner.LatestVersion - 1);

        string[] guarded = [.. Program.StageNames
            .Where(n => !Program.RunsWhateverVersionTheStoreIsAt.Contains(n, StringComparer.Ordinal))];

        // Stated in advance rather than derived from the run: the exempt list holds three names and
        // two of them are stages, so the guarded set is every stage but those two.
        Assert.Equal(Program.StageNames.Count - 2, guarded.Length);

        foreach (string stage in guarded)
        {
            Assert.NotNull(Program.WhyThisStageCannotRun(stage, _connections));
        }
    }

    [Fact]
    public void The_version_the_build_needs_is_the_last_migration_rather_than_the_count_of_them()
    {
        IReadOnlyList<Migration> all = MigrationRunner.All();

        // The two agree only while the numbering has no gap, and what the guard compares is what a
        // migrated store's user_version will read, which is the last number applied.
        Assert.Equal(all[^1].Number, MigrationRunner.LatestVersion);
    }
}
