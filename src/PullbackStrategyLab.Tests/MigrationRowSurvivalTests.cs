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
    private const int BeforeTheIndicatorRekey = 8;
    private const int BeforeTheGeometryRebuild = 30;

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

    [Fact]
    public void Migration_009_rebuilds_the_indicator_table_and_loses_no_row()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        var runner = new MigrationRunner(factory);

        using SqliteConnection connection = factory.OpenWrite();
        MigrationResult before = runner.Apply(connection, throughVersion: BeforeTheIndicatorRekey);
        Assert.Equal(BeforeTheIndicatorRekey, before.ToVersion);

        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES
                ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01'),
                ('BBB', 'BBB', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO indicator_daily
                (ticker, as_of, ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20, ladder_grade)
            VALUES
                ('AAA', '2026-08-24', '10', '11', '12', '1.5', '0.02', '1000000', '0.3', 'rising'),
                ('AAA', '2026-08-25', '10', '11', '12', '1.5', '0.02', '1000000', '0.3', NULL),
                ('BBB', '2026-08-25', '20', '21', '22', '2.5', '0.03', '2000000', '0.6', 'falling');
            """);

        Assert.Equal(3, Count(connection, "indicator_daily"));

        MigrationResult after = runner.Apply(connection);
        Assert.Contains("009-indicator-as-computed.sql", after.Applied);
        Assert.Equal(3, Count(connection, "indicator_daily"));

        // Every row keeps its figures and its grade, and gains a computed_at inside its own
        // session: visible from that session onward, and behind any real computation made later.
        // 028 moves it from midnight UTC, which is the previous Eastern session, to 05:00Z, which
        // is inside this one on either side of the clock change.
        StoredIndicators row = Assert.IsType<StoredIndicators>(
            IndicatorDailyReader.Read(connection, "AAA", new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 24)));

        Assert.Equal(10m, row.EmaShort);
        Assert.Equal("rising", row.LadderGrade);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 5, 0, 0, TimeSpan.Zero), row.ComputedAt);

        // And a read as of the day before the session sees nothing, which is the point of the
        // column: the values were not available before the evening that produced them. This is the
        // assertion 028 exists for. Under the UTC bound it passed against a stamp in the wrong
        // session, because the bound was wrong by the same offset and the two cancelled.
        Assert.Null(IndicatorDailyReader.Read(connection, "AAA", new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 23)));
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

    [Fact]
    public void Migration_031_rebuilds_both_setup_tables_and_loses_no_row()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        var runner = new MigrationRunner(factory);

        using SqliteConnection connection = factory.OpenWrite();
        MigrationResult before = runner.Apply(connection, throughVersion: BeforeTheGeometryRebuild);
        Assert.Equal(BeforeTheGeometryRebuild, before.ToVersion);

        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES
                ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01'),
                ('BBB', 'BBB', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all, rank, capped_out,
                 trigger_price, stop_price, stop_distance_ranges, agreement, agreement_note,
                 thrust_scan, thrust_session)
            VALUES
                ('a', '2026-08-24', 'AAA', 'long',  '[]', 1, 3, 0, '120.50', '118.00', '0.4200',
                 'agree', 'looks right', 'gainer', '2026-08-21'),
                ('b', '2026-08-24', 'BBB', 'short', '[]', 0, NULL, NULL, '85.14', '85.14', '0',
                 NULL, NULL, NULL, NULL);

            INSERT INTO calibration_setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES
                ('c', '2026-08-20', 'AAA', 'long', '[]', 1, '99.00', '97.00', '0.5000');
            """);

        Assert.Equal(2, Count(connection, "setup"));
        Assert.Equal(1, Count(connection, "calibration_setup"));

        MigrationResult after = runner.Apply(connection);
        Assert.Contains("031-setup-geometry-absent.sql", after.Applied);

        Assert.Equal(2, Count(connection, "setup"));
        Assert.Equal(1, Count(connection, "calibration_setup"));

        // Every column travels, not only the three the rebuild is about. A rebuild that dropped a
        // column would leave a store that opens and queries perfectly well with less in it, and the
        // two thrust columns were missing from the first draft of this migration.
        StoredSetup kept = SetupReader.Read(connection, new DateOnly(2026, 8, 24))
            .Single(x => x.SetupId == "a");

        Assert.Equal(120.50m, kept.TriggerPrice);
        Assert.Equal(118.00m, kept.StopPrice);
        Assert.Equal(0.4200m, kept.StopDistanceRanges);
        Assert.Equal(3, kept.Rank);
        Assert.Equal("agree", kept.Agreement);
        Assert.Equal("looks right", kept.AgreementNote);

        // The flattened row is copied verbatim rather than reinterpreted. Its stop_distance_ranges
        // is the literal 0 the old columns forced, and turning that into NULL here would be
        // reconstructing a detector's decision from a sentinel.
        StoredSetup flattened = SetupReader.Read(connection, new DateOnly(2026, 8, 24))
            .Single(x => x.SetupId == "b");

        Assert.Equal(0m, flattened.StopDistanceRanges);

        // And the column can now hold what it could not before.
        Execute(connection, """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES ('d', '2026-08-25', 'BBB', 'short', '[]', 0, NULL, NULL, NULL);
            """);

        StoredSetup absent = SetupReader.Read(connection, new DateOnly(2026, 8, 25)).Single();

        Assert.Null(absent.TriggerPrice);
        Assert.Null(absent.StopPrice);
        Assert.Null(absent.StopDistanceRanges);
    }

    /// <summary>
    /// The same rebuild against a store where something points at the table being rebuilt.
    ///
    /// <b>The test above passes on an empty neighbourhood and that is the whole gap.</b> It seeds
    /// <c>setup</c> and <c>calibration_setup</c> and nothing else, so <c>DROP TABLE setup</c> drops a
    /// table with no child rows and foreign key enforcement has nothing to refuse. <c>tools/ci.*</c>
    /// drops the store and migrates an empty one, so it could not have found this either.
    ///
    /// Against the live store on 2026-08-29, holding 44 setups with 1,406 signals and 440 controls,
    /// migration 031 failed with "FOREIGN KEY constraint failed" and rolled back. It had never been
    /// applied to a store with rows in it and it could not be. The store stayed two migrations
    /// behind, four stages died on the column it had not got, and the night produced nothing.
    ///
    /// So the population here is the one that matters: both children of <c>setup</c> carry rows, the
    /// rebuild has to succeed, and nothing may be orphaned by it.
    /// </summary>
    [Fact]
    public void Migration_031_rebuilds_a_setup_table_that_other_tables_point_at()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        var runner = new MigrationRunner(factory);

        using SqliteConnection connection = factory.OpenWrite();
        runner.Apply(connection, throughVersion: BeforeTheGeometryRebuild);

        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES
                ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01'),
                ('CCC', 'CCC', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES ('a', '2026-08-24', 'AAA', 'long', '[]', 1, '120.50', '118.00', '0.4200');

            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at) VALUES
                ('a', 'stop_distance_ranges', '0.4200', '2026-08-24T22:40:00.000Z'),
                ('a', 'ema_21_distance',      '0.0130', '2026-08-24T22:40:00.000Z');

            INSERT INTO control_setup
                (control_id, setup_id, control_ticker, control_set, match_quality, rank, drawn_at)
            VALUES ('a-loose-1', 'a', 'CCC', 'loose', 'rising', 1, '2026-08-24T22:45:00.000Z');
            """);

        Assert.Equal(2, Count(connection, "setup_signal"));
        Assert.Equal(1, Count(connection, "control_setup"));

        MigrationResult after = runner.Apply(connection);
        Assert.Contains("031-setup-geometry-absent.sql", after.Applied);

        // The rebuild ran, and the rows that pointed at the dropped table still point at rows that
        // exist. foreign_key_check reads the whole store, so it answers for every table at once.
        Assert.Equal(1, Count(connection, "setup"));
        Assert.Equal(2, Count(connection, "setup_signal"));
        Assert.Equal(1, Count(connection, "control_setup"));
        Assert.Empty(MigrationRunner.ForeignKeyViolations(connection));
    }

    /// <summary>
    /// Enforcement is put back after a run, so the store the lab then writes through is the store
    /// SCHEMA describes.
    ///
    /// The guard the rebuild needs is scoped to the migration run and to nothing else. A connection
    /// left with foreign keys off would accept an orphan on every insert after it, silently, which
    /// is a worse fault than the one turning them off repairs.
    /// </summary>
    [Fact]
    public void Foreign_keys_are_enforced_again_once_the_migrations_have_run()
    {
        using var root = new TemporaryDirectory();
        var factory = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));

        using SqliteConnection connection = factory.OpenWrite();
        new MigrationRunner(factory).Apply(connection);

        Assert.Equal("1", StoreConnectionFactory.ReadPragma(connection, "foreign_keys"));

        // And it bites: a setup naming a ticker no security row holds is refused.
        SqliteException refused = Assert.Throws<SqliteException>(() => Execute(connection, """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES ('z', '2026-08-24', 'NOPE', 'long', '[]', 1, NULL, NULL, NULL);
            """));

        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.OrdinalIgnoreCase);
    }
}
