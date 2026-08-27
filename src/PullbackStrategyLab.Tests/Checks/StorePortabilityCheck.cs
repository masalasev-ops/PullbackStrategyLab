using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No absolute path is written into a database row, so the store stays a directory that can be
/// copied to another machine.
///
/// The hard rule has been in CLAUDE.md since the first day and nothing asserted it. A path in a
/// row is invisible until the store arrives on the other machine, where it resolves to nothing,
/// or worse resolves to something: a Windows path written on one machine and read on macOS is a
/// string that fails, and a path under a home directory that exists on both is a string that
/// quietly points at the wrong file.
///
/// It runs over the store the fixture replay builds, which is a real store with a million rows
/// in it rather than the empty one a migration leaves. A check of this kind over an empty
/// database examines nothing and passes forever, which is the failure the coverage line exists
/// to make visible, and the fixture is what gives it something to look at.
/// </summary>
public sealed partial class StorePortabilityCheck
{
    private readonly ITestOutputHelper _output;

    public StorePortabilityCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// What an absolute path looks like on either machine: a drive letter, or a leading slash
    /// followed by one of the roots a path actually starts at. A bare leading slash would match
    /// every ISO date and every ratio in the store.
    /// </summary>
    [GeneratedRegex(@"(^|\s)([A-Za-z]:[\\/]|/(Users|home|var|tmp|opt|Volumes)/)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();

    [Fact]
    [Trait("check", "store-portability")]
    public void No_row_in_the_store_carries_an_absolute_path()
    {
        var coverage = new CheckCoverage("store-portability", _output);

        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        string store = Path.Combine(RepositoryLayout.Artifacts, "portability.db");
        replay.SnapshotTo(store);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());

        connection.Open();

        var failures = new List<string>();
        long valuesScanned = 0;
        int columnsScanned = 0;
        int tablesScanned = 0;

        foreach (string table in Tables(connection))
        {
            tablesScanned++;

            foreach (string column in TextColumns(connection, table))
            {
                columnsScanned++;

                using SqliteCommand command = connection.CreateCommand();

                // Both names come from the database's own schema rather than from anything a
                // caller supplied.
                command.CommandText = $"SELECT \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL;";

                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    valuesScanned++;

                    if (reader.GetValue(0) is string value && AbsolutePath().IsMatch(value))
                    {
                        failures.Add($"{table}.{column} holds \"{value}\"");
                    }
                }
            }
        }

        coverage
            .Examined("tables in a populated store", tablesScanned)
            .Examined("text columns across them", columnsScanned)
            .Context("stored text values read", checked((int)valuesScanned))
            .NoSourceScan(
                "it reads a store the pipeline actually populated, so what it examines is what was written "
                + "rather than what a statement looks like")
            .Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} row(s) carry an absolute path, so the store is no longer a directory that can be "
            + $"copied to another machine:\n  " + string.Join("\n  ", failures.Take(10)));

        // A scan that found nothing to look at is not a scan that passed.
        Assert.True(valuesScanned > 100_000,
            $"Only {valuesScanned} stored values were read. The fixture replay builds a store with a million rows in "
            + "it, so a number this low means the scan stopped finding columns rather than that the store shrank.");
    }

    private static IReadOnlyList<string> Tables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
             WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
             ORDER BY name;
            """;

        var tables = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>
    /// Every column a path could be written into. TEXT covers it: prices are TEXT in this store
    /// and so is every identifier, and a path stored in an INTEGER column is not a thing that
    /// happens.
    /// </summary>
    private static IReadOnlyList<string> TextColumns(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";

        var columns = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(2).StartsWith("TEXT", StringComparison.OrdinalIgnoreCase))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }
}
