using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The store as it is actually opened. Two of the four pragmas are off by default in SQLite
/// and silently so, which is why they are asserted from the connection rather than from the
/// source that sets them.
/// </summary>
public sealed class StoreTests
{
    [Fact]
    public void The_open_connection_reports_the_four_pragmas_from_schema()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));

        using SqliteConnection connection = factory.OpenWrite();

        Assert.Equal("wal", StoreConnectionFactory.ReadPragma(connection, "journal_mode"), ignoreCase: true);
        Assert.Equal("1", StoreConnectionFactory.ReadPragma(connection, "foreign_keys"));

        // NORMAL is 1 in the pragma's own numbering, and busy_timeout reports milliseconds.
        Assert.Equal("1", StoreConnectionFactory.ReadPragma(connection, "synchronous"));
        Assert.Equal(
            StoreConnectionFactory.BusyTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StoreConnectionFactory.ReadPragma(connection, "busy_timeout"));
    }

    [Fact]
    public void The_store_is_created_under_the_configured_data_root_and_nowhere_else()
    {
        using var root = new TemporaryDirectory();
        var paths = new PullbackStrategyLabPaths(root.Path);
        var factory = new StoreConnectionFactory(paths);

        Assert.False(factory.StoreExists);

        using (factory.OpenWrite())
        {
        }

        Assert.True(File.Exists(paths.StoreFile));
        Assert.Equal(PullbackStrategyLabPaths.StoreFileName, Path.GetFileName(paths.StoreFile));
    }

    [Fact]
    public void Migrations_apply_in_order_and_are_idempotent()
    {
        using var root = new TemporaryDirectory();
        var runner = new MigrationRunner(new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path)));

        MigrationResult first = runner.Apply();
        Assert.Equal(0, first.FromVersion);
        Assert.NotEmpty(first.Applied);
        Assert.Equal(MigrationRunner.All().Count, first.ToVersion);

        // Re-running applies nothing. Every stage is idempotent for its date, and the migration
        // runner is the first place that has to be true.
        MigrationResult second = runner.Apply();
        Assert.Empty(second.Applied);
        Assert.Equal(first.ToVersion, second.ToVersion);
    }

    [Fact]
    public void Every_migration_is_numbered_and_the_numbers_are_unique()
    {
        IReadOnlyList<Migration> migrations = MigrationRunner.All();

        Assert.NotEmpty(migrations);
        Assert.Equal(migrations.Count, migrations.Select(m => m.Number).Distinct().Count());
        Assert.Equal(migrations.OrderBy(m => m.Number).Select(m => m.Name), migrations.Select(m => m.Name));
    }

    [Fact]
    public void The_read_only_connection_cannot_write()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(factory).Apply();

        using SqliteConnection connection = factory.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO run_log (run_id, stage, started_at) VALUES ('x', 'y', 'z');";

        // The Api opens the file read-only, which is what keeps the one-writer rule from
        // depending on everyone remembering it.
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }
}
