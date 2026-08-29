using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Applies hand-written SQL migrations in numeric order, each in one transaction.
///
/// Migrations are hand-written rather than generated. Generated table rebuilds have twice
/// re-added constraints that a convention here strips, and a rebuild that silently
/// restores a dropped CHECK is the kind of change nothing notices.
///
/// The applied version lives in SQLite's own <c>user_version</c> rather than in a
/// bookkeeping table. A bookkeeping table would be a store, and every store is declared
/// in SCHEMA.md with a writer; user_version is a property of the file and declares nothing.
/// </summary>
public sealed partial class MigrationRunner
{
    private const string ResourcePrefix = "PullbackStrategyLab.Data.Migrations.";

    [GeneratedRegex(@"^(?<number>\d{3})-(?<name>[a-z0-9-]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileName();

    private readonly StoreConnectionFactory _connections;

    public MigrationRunner(StoreConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Opens a writing connection and applies whatever is outstanding.</summary>
    public MigrationResult Apply()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        return Apply(connection);
    }

    /// <summary>
    /// Applies whatever is outstanding, or everything up to <paramref name="throughVersion"/>.
    ///
    /// The bound exists for one caller: the test that asserts a table rebuild does not lose
    /// rows has to stand the store up at the version before the rebuild, put rows in it, and
    /// then step forward. Stopping short is not something a running lab ever does, which is why
    /// the parameter is optional and the default is everything.
    /// </summary>
    public MigrationResult Apply(SqliteConnection connection, int? throughVersion = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int startingVersion = ReadUserVersion(connection);
        List<Migration> outstanding = All()
            .Where(m => m.Number > startingVersion && (throughVersion is null || m.Number <= throughVersion))
            .ToList();

        // Foreign keys off for the length of the run, and every migration checked against them
        // afterwards. This is SQLite's own procedure for a table rebuild and it is not optional:
        // relaxing NOT NULL means creating a new table, copying, dropping the old one and renaming,
        // and DROP TABLE on a parent with child rows present fails outright while enforcement is on.
        //
        // <b>CI could not have found this and did not.</b> tools/ci.* drops the store and migrates an
        // empty one, so every rebuild ran against a table with nothing referencing it. Migration 031
        // rebuilds `setup`, which `setup_signal` and `control_setup` both reference; against the live
        // store, holding 44 setups with 1,406 signals and 440 controls, it failed with
        // "FOREIGN KEY constraint failed" and rolled back. The store sat two migrations behind for a
        // night and four stages died on the column it had not got.
        //
        // The pragma is a no-op inside a transaction, so it cannot live in the migration file and has
        // to be here. What replaces the enforcement is foreign_key_check, run after each migration
        // commits: it reports every orphan in the whole store rather than refusing one statement, so
        // a rebuild that dropped rows some other table pointed at fails here with the rows named.
        bool foreignKeysWereOn = ReadPragmaFlag(connection, "foreign_keys");
        Execute(connection, "PRAGMA foreign_keys = OFF;");

        try
        {
            foreach (Migration migration in outstanding)
            {
                using SqliteTransaction transaction = connection.BeginTransaction();

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    command.ExecuteNonQuery();
                }

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    // user_version takes no parameter. The value is an int parsed from a
                    // filename that matched a three-digit pattern, so there is nothing to inject.
                    command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {migration.Number};");
                    command.ExecuteNonQuery();
                }

                transaction.Commit();

                string[] orphans = ForeignKeyViolations(connection);
                if (orphans.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"Migration '{migration.Name}' left {orphans.Length} foreign key violation(s), so it "
                        + "dropped or rewrote rows another table points at. The migration has committed and the "
                        + "store needs the snapshot taken before it. Violations, as child table, rowid and parent: "
                        + string.Join("; ", orphans.Take(20)));
                }
            }
        }
        finally
        {
            if (foreignKeysWereOn)
            {
                Execute(connection, "PRAGMA foreign_keys = ON;");
            }
        }

        return new MigrationResult(startingVersion, ReadUserVersion(connection),
            outstanding.Select(m => m.Name).ToArray());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool ReadPragmaFlag(SqliteConnection connection, string pragma)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Every orphaned row in the store, as child table, rowid and the parent it points at.
    ///
    /// <c>foreign_key_check</c> rather than the enforcement the migrations run without: it asks the
    /// question of the whole store at once, after the rebuild, which is the shape of answer wanted
    /// here. A constraint refuses one statement; this names the rows.
    /// </summary>
    public static string[] ForeignKeyViolations(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";

        var violations = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            violations.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{reader.GetString(0)} rowid {(reader.IsDBNull(1) ? "null" : reader.GetValue(1))} -> {reader.GetString(2)}"));
        }

        return [.. violations];
    }

    /// <summary>
    /// The version a store sits at once every migration this build carries has been applied.
    ///
    /// The last migration's own number rather than the count of them. The two agree only while the
    /// numbering has no gap, and what a caller wants here is what this build expects to find in a
    /// store, which is a number written in the files rather than an arithmetic fact about how many
    /// of them there are.
    /// </summary>
    public static int LatestVersion => All()[^1].Number;

    public static int ReadUserVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Every migration this build carries, in the order it applies them.</summary>
    public static IReadOnlyList<Migration> All()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<Migration>();

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string fileName = resource[ResourcePrefix.Length..];
            Match match = MigrationFileName().Match(fileName);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Migration '{fileName}' is not named NNN-lowercase-name.sql. The number is what orders them, " +
                    "so a name that does not carry one would apply in whatever order the assembly happened to list it.");
            }

            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration resource '{resource}' could not be opened.");
            using var reader = new StreamReader(stream);

            migrations.Add(new Migration(
                int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture),
                fileName,
                reader.ReadToEnd()));
        }

        List<Migration> ordered = migrations.OrderBy(m => m.Number).ToList();

        int[] duplicates = ordered.GroupBy(m => m.Number).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Two migrations share the number {string.Join(", ", duplicates)}. One of them would never apply.");
        }

        return ordered;
    }
}

public sealed record Migration(int Number, string Name, string Sql);

public sealed record MigrationResult(int FromVersion, int ToVersion, IReadOnlyList<string> Applied);
