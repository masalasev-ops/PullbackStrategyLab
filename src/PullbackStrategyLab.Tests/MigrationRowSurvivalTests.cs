using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A hand-written table rebuild moves rows from an old table to a new one and drops the old
/// one. SCHEMA requires every rebuild to carry a row-survival test asserting the count before
/// and after, and the reason is that the failure mode is silence: a copy that misses rows
/// leaves a store that opens, queries and reports perfectly well with less in it than it had.
///
/// Stated in advance rather than derived from the result. "The count is the same afterwards"
/// is satisfied by a rebuild that dropped everything on both sides, so the counts are named as
/// numbers here and the rows are named as rows.
/// </summary>
public sealed class MigrationRowSurvivalTests
{
    private const int BeforeTheRekey = 4;

    [Fact]
    public void Migration_005_rebuilds_both_tables_and_loses_no_row()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        var runner = new MigrationRunner(factory);

        using SqliteConnection connection = factory.OpenWrite();
        MigrationResult before = runner.Apply(connection, throughVersion: BeforeTheRekey);
        Assert.Equal(BeforeTheRekey, before.ToVersion);

        // Three actions on the shape 004 declared, and two demands, one of them already
        // satisfied. The satisfied one is the row that matters: a rebuild that dropped
        // rebuilt_at would silently reopen a rebuild that had been done.
        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES
                ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01'),
                ('BBB', 'BBB', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO corporate_action (ticker, effective_date, type, ratio, observed_at) VALUES
                ('AAA', '2026-08-24', 'split',    '4',    '2026-08-25T21:30:00.000Z'),
                ('AAA', '2026-08-24', 'dividend', '0.11', '2026-08-25T21:30:00.000Z'),
                ('BBB', '2026-08-20', 'split',    '2',    '2026-08-21T21:30:00.000Z');

            INSERT INTO indicator_rebuild (ticker, effective_date, requested_at, rebuilt_at) VALUES
                ('AAA', '2026-08-24', '2026-08-25T21:30:00.000Z', NULL),
                ('BBB', '2026-08-20', '2026-08-21T21:30:00.000Z', '2026-08-21T22:00:00.000Z');
            """);

        Assert.Equal(3, Count(connection, "corporate_action"));
        Assert.Equal(2, Count(connection, "indicator_rebuild"));

        MigrationResult after = runner.Apply(connection);
        Assert.Equal(MigrationRunner.All().Count, after.ToVersion);
        Assert.Contains("005-action-as-observed.sql", after.Applied);

        Assert.Equal(3, Count(connection, "corporate_action"));
        Assert.Equal(2, Count(connection, "indicator_rebuild"));

        // The demands carry the type and the observation of the action that raised them, taken
        // from the action rather than invented, and the satisfied one is still satisfied.
        IReadOnlyList<RebuildDemand> open = IndicatorRebuildReader.Open(connection, new DateOnly(2026, 8, 26));
        RebuildDemand only = Assert.Single(open);
        Assert.Equal("AAA", only.Ticker);
        Assert.Equal(CorporateActionType.Split, only.Type);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 21, 30, 0, TimeSpan.Zero), only.ObservedAt);

        // And every demand still names an action that exists, which is the property the
        // migration's join has to preserve.
        Assert.Equal(0L, Scalar(connection, """
            SELECT COUNT(*)
              FROM indicator_rebuild r
             WHERE NOT EXISTS (
                     SELECT 1 FROM corporate_action a
                      WHERE a.ticker = r.ticker
                        AND a.effective_date = r.effective_date
                        AND a.type = r.type
                        AND a.observed_at = r.observed_at);
            """));
    }

    [Fact]
    public void The_rekeyed_action_table_accepts_a_second_observation_of_the_same_action()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(factory).Apply();

        using SqliteConnection connection = factory.OpenWrite();
        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO corporate_action (ticker, effective_date, type, ratio, observed_at) VALUES
                ('AAA', '2026-08-24', 'split', '4', '2026-08-25T21:30:00.000Z'),
                ('AAA', '2026-08-24', 'split', '5', '2026-08-26T21:30:00.000Z');
            """);

        // Which 004 could not do at all. The primary key was the action, so the restatement had
        // nowhere to go and the store kept a factor the vendor no longer publishes.
        Assert.Equal(2, Count(connection, "corporate_action"));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Count(SqliteConnection connection, string table)
    {
        SqliteIdentifier.Validate(table);
        return Convert.ToInt32(
            Scalar(connection, $"SELECT COUNT(*) FROM {table};"),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
