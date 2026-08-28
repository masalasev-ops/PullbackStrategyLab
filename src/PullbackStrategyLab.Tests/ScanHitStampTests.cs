using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// `scan_hit` carries an observation stamp, and a row without one is refused by history rather than
/// visible to it.
///
/// <b>The gap this closes.</b> Every other table feeding a point-in-time read carried a stamp, so a
/// read could at worst fail to bound one. `scan_hit` had none, which is a different thing: a hit
/// inserted for a past session was invisible to every bound the lab has, and a cluster count derived
/// afterwards would have counted it without any read being able to say so.
///
/// <b>Why a null is refused rather than admitted.</b> A row with no stamp has no provenance, and the
/// two honest answers about it are that the session it is dated for may use it and that no other
/// session may. Admitting it everywhere would let a row of unknown origin into a historical read,
/// which is the thing the whole rule exists to stop; refusing it everywhere would leave a session
/// unable to see the hits it recorded itself.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class ScanHitStampTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 8, 27);
    private static readonly DateOnly Later = new(2026, 8, 31);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public ScanHitStampTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A hit with no stamp is admitted by a read of its own session and refused by a read of a later
    /// one.
    ///
    /// Both halves in one test, because either alone is satisfied by a bound that is simply always
    /// on or always off.
    /// </summary>
    [Fact]
    public void An_unstamped_hit_is_admitted_by_its_own_session_and_refused_by_a_later_one()
    {
        Hit("AAA", "gainer", observedAt: null);

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Single(ScanHitReader.Read(connection, Session, "gainer"));
        Assert.Empty(ScanHitReader.ForTicker(connection, "AAA", Later, Session));

        // And its own session still finds it through the window read, so the refusal is about the
        // reading session rather than about which method asked.
        Assert.Single(ScanHitReader.ForTicker(connection, "AAA", Session, Session));
    }

    /// <summary>
    /// A stamped hit is visible to a later session, which is what the column is for: a row with
    /// provenance is history rather than a row nobody can place.
    /// </summary>
    [Fact]
    public void A_stamped_hit_is_visible_to_a_later_session()
    {
        Hit("AAA", "gainer", observedAt: "2026-08-27T22:10:03.959Z");

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Single(ScanHitReader.ForTicker(connection, "AAA", Later, Session));
    }

    /// <summary>
    /// A hit stamped after its own session's end of day is invisible to that session, which is the
    /// ordinary point-in-time property the column now makes assertable.
    ///
    /// This is the case the table could not express at all before: a rerun of `scans` for a past
    /// date wrote rows indistinguishable from the originals.
    /// </summary>
    [Fact]
    public void A_hit_stamped_after_its_own_session_is_invisible_to_it()
    {
        // 05:00Z the next morning is 01:00 Eastern, past the end of the session's own day.
        Hit("AAA", "gainer", observedAt: "2026-08-28T05:00:00.000Z");

        using SqliteConnection connection = _connections.OpenReadOnly();

        Assert.Empty(ScanHitReader.Read(connection, Session, "gainer"));

        // And a later session sees it, so the row is not simply lost.
        Assert.Single(ScanHitReader.ForTicker(connection, "AAA", Later, Session));
    }

    /// <summary>
    /// The backfill reads the instant the `scans` run recorded, and only where that run's own row
    /// count matches the hits it is being matched to.
    ///
    /// <b>The obligation that raised this said the rows would need an instant nobody recorded.</b>
    /// That was wrong, and the difference matters: reading an instant across from `run_log` is not
    /// the same act as choosing one. The count condition is what makes it a match rather than an
    /// association, and both directions of it are asserted here.
    /// </summary>
    [Fact]
    public void The_backfill_takes_the_run_instant_only_where_the_row_count_matches()
    {
        Hit("AAA", "gainer", observedAt: null);
        Hit("BBB", "gainer", observedAt: null);

        // A run that says it wrote three rows against two hits is not the run that wrote them.
        Run("scans", "2026-08-27T22:10:03.506Z", "2026-08-27T22:10:03.959Z", "clean", rowsWritten: 3);
        Assert.Equal(2, Unstamped());

        Execute("DELETE FROM run_log");
        Run("scans", "2026-08-27T22:10:03.506Z", "2026-08-27T22:10:03.959Z", "clean", rowsWritten: 2);
        Assert.Equal(0, Unstamped());

        Assert.Equal("2026-08-27T22:10:03.959Z", StampOf("AAA"));
    }

    /// <summary>A partial walk cannot stamp rows it never reached, so only a clean run matches.</summary>
    [Fact]
    public void A_run_that_did_not_finish_does_not_stamp_anything()
    {
        Hit("AAA", "gainer", observedAt: null);
        Run("scans", "2026-08-27T22:10:03.506Z", "2026-08-27T22:10:03.959Z", "failed", rowsWritten: 1);

        Assert.Equal(1, Unstamped());
    }

    /// <summary>
    /// The migration's own backfill statement, run against whatever the store holds now.
    ///
    /// The statement is repeated here rather than shared, because a migration is a fact about a
    /// version of the store and a test that imported it would stop asserting the text that ran.
    /// </summary>
    private int Unstamped()
    {
        Execute(
            "UPDATE scan_hit\n"
            + "   SET observed_at = (\n"
            + "       SELECT r.ended_at\n"
            + "         FROM run_log r\n"
            + "        WHERE r.stage = 'scans'\n"
            + "          AND r.outcome = 'clean'\n"
            + "          AND r.ended_at IS NOT NULL\n"
            + "          AND date(r.started_at, '-5 hours') = scan_hit.as_of\n"
            + "          AND r.rows_written = (SELECT COUNT(*) FROM scan_hit h WHERE h.as_of = scan_hit.as_of))\n"
            + " WHERE observed_at IS NULL");

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scan_hit WHERE observed_at IS NULL";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string? StampOf(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT observed_at FROM scan_hit WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return command.ExecuteScalar() as string;
    }

    private void Hit(string ticker, string scan, string? observedAt)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using SqliteCommand security = connection.CreateCommand();
        security.CommandText =
            "INSERT INTO security (ticker, name, exchange, type, first_seen) "
            + "VALUES (@t, @t, 'US', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING";
        security.Parameters.AddWithValue("@t", ticker);
        security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Session));
        security.ExecuteNonQuery();

        using SqliteCommand hit = connection.CreateCommand();
        hit.CommandText =
            "INSERT INTO scan_hit (as_of, ticker, scan, magnitude, rank, observed_at) "
            + "VALUES (@d, @t, @s, '1.0', 1, @o)";
        hit.Parameters.AddWithValue("@d", StoreText.DateToStorageText(Session));
        hit.Parameters.AddWithValue("@t", ticker);
        hit.Parameters.AddWithValue("@s", scan);
        hit.Parameters.AddWithValue("@o", (object?)observedAt ?? DBNull.Value);
        hit.ExecuteNonQuery();
    }

    private void Run(string stage, string started, string ended, string outcome, int rowsWritten)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO run_log (run_id, stage, started_at, ended_at, outcome, rows_written) "
            + "VALUES (@id, @stage, @started, @ended, @outcome, @rows)";
        command.Parameters.AddWithValue("@id", $"{stage}-{started}");
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue("@started", started);
        command.Parameters.AddWithValue("@ended", ended);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@rows", rowsWritten);
        command.ExecuteNonQuery();
    }

    private void Execute(string sql)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
