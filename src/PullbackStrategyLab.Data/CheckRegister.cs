using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Data;

/// <summary>
/// Sole writer of <c>check_definition</c>, both operations: which checks each side's detector ran,
/// and the sessions each was in force for.
///
/// <b>What it exists for.</b> <c>check-completeness</c> claimed from 2026-08-26 that every setup row
/// records a result for every check defined at its date, and compared every row against the list the
/// build carries today. A check added to a detector would have read every historical row as missing
/// it. The register is what says which list a row was written under, so a row is held to the list of
/// its own night.
///
/// <b>Written by the detectors on the night they run, from the list they run.</b> A check the list
/// holds and the register does not is introduced on that session; a check the register holds and the
/// list does not is retired on it. So the register moves exactly when the detector's list does, and a
/// generation switch retiring one gate set and bringing in another is recorded by the first night of
/// the new one rather than by anybody remembering to.
///
/// <b>The first registration of a side reaches back.</b> When the register holds nothing for a side,
/// the list is introduced on the earliest session the store already holds a setup of that side for,
/// rather than on tonight: the rows already there were written by a detector that had no register to
/// write to, and the list it ran is the one being registered now. Dating it tonight would read every
/// one of them as recording checks that were not yet defined.
/// see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
/// </summary>
public sealed class CheckRegister
{
    private readonly IClock _clock;

    public CheckRegister(IClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Registers the list one side's detector runs on one session. Returns how many checks were
    /// introduced and how many retired, which is nought and nought on every ordinary night.
    /// </summary>
    public (int Introduced, int Retired) Register(
        SqliteConnection connection, string direction, IReadOnlyList<string> checks, DateOnly session)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(checks);

        string now = StoreText.TimestampToStorageText(_clock.UtcNow);
        IReadOnlyList<string> inForce = InForce(connection, direction, now);

        string introducedOn = inForce.Count == 0 && !EverRegistered(connection, direction, now)
            ? EarliestSetup(connection, direction, now) is DateOnly earliest && earliest < session
                ? StoreText.DateToStorageText(earliest)
                : StoreText.DateToStorageText(session)
            : StoreText.DateToStorageText(session);

        int introduced = 0;
        int retired = 0;

        foreach (string check in checks.Where(c => !inForce.Contains(c, StringComparer.Ordinal)))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO check_definition (check_name, direction, introduced_on, observed_at)
                VALUES (@name, @direction, @introduced_on, @now)
                ON CONFLICT DO NOTHING;
                """;
            command.Parameters.AddWithValue("@name", check);
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@introduced_on", introducedOn);
            command.Parameters.AddWithValue("@now", now);
            introduced += command.ExecuteNonQuery();
        }

        foreach (string check in inForce.Where(c => !checks.Contains(c, StringComparer.Ordinal)))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE check_definition
                   SET retired_on = @session, retired_observed_at = @now
                 WHERE check_name = @name AND direction = @direction AND retired_on IS NULL
                   AND introduced_on < @session;
                """;
            command.Parameters.AddWithValue("@name", check);
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
            command.Parameters.AddWithValue("@now", now);
            retired += command.ExecuteNonQuery();
        }

        return (introduced, retired);
    }

    /// <summary>
    /// The checks one side's detector was running for a session, as far as the store could say at
    /// <paramref name="observedBefore"/>: introduced on or before the session and not retired by it.
    ///
    /// <b>Bounded on both stamps.</b> A check registered after the instant is not yet in the list, and
    /// a retirement recorded after it has not happened yet, so a read for an old instant sees the
    /// register as it stood then.
    /// </summary>
    public static IReadOnlyList<string> DefinedOn(
        SqliteConnection connection, string direction, DateOnly session, DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT check_name
              FROM check_definition
             WHERE direction = @direction
               AND introduced_on <= @session
               AND observed_at <= @observed_before
               AND NOT (retired_on IS NOT NULL
                        AND retired_on <= @session
                        AND retired_observed_at <= @observed_before)
             ORDER BY check_name;
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));

        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>The checks in force today on one side, which is what a night's list is compared with.</summary>
    private static IReadOnlyList<string> InForce(SqliteConnection connection, string direction, string now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT check_name FROM check_definition
             WHERE direction = @direction AND retired_on IS NULL AND observed_at <= @now
             ORDER BY check_name;
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@now", now);

        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static bool EverRegistered(SqliteConnection connection, string direction, string now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM check_definition WHERE direction = @direction AND observed_at <= @now;";
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@now", now);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static DateOnly? EarliestSetup(SqliteConnection connection, string direction, string now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT MIN(as_of) FROM setup
             WHERE direction = @direction AND (corrected_at IS NULL OR corrected_at <= @now);
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@now", now);
        object? value = command.ExecuteScalar();
        return value is string text ? StoreText.StorageTextToDate(text) : null;
    }
}
