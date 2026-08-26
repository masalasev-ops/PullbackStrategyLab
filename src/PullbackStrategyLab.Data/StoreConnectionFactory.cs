using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;

namespace PullbackStrategyLab.Data;

/// <summary>
/// The one place the store is opened, and the one place the four pragmas from SCHEMA's
/// "Store configuration" are set. They are set at open rather than at create, because
/// three of the four are per connection and two of those are silently off by default:
/// a connection that skipped them behaves like one that did until the night it does not.
///
/// One writer, one connection. The Worker is the sole writer by design, and SQLite makes
/// that a practical requirement rather than a stylistic one. A second writing connection
/// produces intermittent lock failures that look like load problems and are not.
/// </summary>
public sealed class StoreConnectionFactory
{
    /// <summary>
    /// Brief contention retries rather than throwing. Zero is SQLite's default and it is
    /// the wrong default here: the nightly job writes for hours while a page may be reading.
    /// </summary>
    public const int BusyTimeoutMilliseconds = 5000;

    private readonly PullbackStrategyLabPaths _paths;

    public StoreConnectionFactory(PullbackStrategyLabPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string StoreFile => _paths.StoreFile;

    public bool StoreExists => File.Exists(_paths.StoreFile);

    /// <summary>The writing connection. Only the Worker opens one.</summary>
    public SqliteConnection OpenWrite()
    {
        _paths.EnsureDirectories();
        SqliteConnection connection = Open(SqliteOpenMode.ReadWriteCreate);

        // Write-ahead logging is a property of the database file rather than of the
        // connection, so it is set here and persists. Without it a reader blocks a writer
        // and evening runs throw spurious lock errors.
        Execute(connection, "PRAGMA journal_mode = WAL;");
        return connection;
    }

    /// <summary>
    /// The reading connection. The Api opens the file read-only, which is what keeps the
    /// one-writer rule from depending on everyone remembering it.
    /// </summary>
    public SqliteConnection OpenReadOnly() => Open(SqliteOpenMode.ReadOnly);

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.StoreFile,
            Mode = mode,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Per connection, and two of the three are off by default and silently so.
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Reads a pragma back, so a test can assert what the connection actually reports.</summary>
    public static string ReadPragma(SqliteConnection connection, string pragmaName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SqliteIdentifier.Validate(pragmaName);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragmaName};";
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }
}
