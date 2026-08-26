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
        }

        return new MigrationResult(startingVersion, ReadUserVersion(connection),
            outstanding.Select(m => m.Name).ToArray());
    }

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
