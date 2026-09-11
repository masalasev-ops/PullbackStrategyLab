using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Checks;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The register of which checks each side ran on which night, and the comparison it makes possible.
///
/// <b>7.2's done condition is the first test.</b> A gate added to a list leaves no historical row
/// reading as missing, because each row is held to the list in force on its own night. The same rows
/// held to the latest list, which is what `check-completeness` did until 7.2, read the earlier one as
/// missing the gate added since, and the test asserts that as well so the difference is on the record
/// rather than argued.
///
/// Population: stores these tests migrate and authored lists of made-up check names. Nothing here
/// depends on the detectors' own lists.
/// </summary>
public sealed class CheckRegisterTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 9, 14);
    private static readonly DateOnly Tuesday = new(2026, 9, 15);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public CheckRegisterTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_gate_added_to_the_list_leaves_no_historical_row_reading_as_missing()
    {
        string[] monday = ["dip-shape", "trigger-near"];
        string[] tuesday = ["dip-shape", "trigger-near", "weekly-screen"];

        using SqliteConnection connection = _connections.OpenWrite();

        Register(connection, Monday, monday);
        Register(connection, Tuesday, tuesday);

        IReadOnlyList<string> heldOnMonday = CheckRegister.DefinedOn(connection, "long", Monday, Later);
        IReadOnlyList<string> heldOnTuesday = CheckRegister.DefinedOn(connection, "long", Tuesday, Later);

        Assert.Equal(monday.Order(StringComparer.Ordinal), heldOnMonday);
        Assert.Equal(tuesday.Order(StringComparer.Ordinal), heldOnTuesday);

        // Each night's row against its own night's list: nothing missing, nothing extra.
        Assert.Empty(CheckCompletenessCheck.RowProblems("monday-row", monday, heldOnMonday));
        Assert.Empty(CheckCompletenessCheck.RowProblems("tuesday-row", tuesday, heldOnTuesday));

        // And Monday's row against the latest list, which is the comparison made of every row until
        // 7.2: it reads as missing the gate that did not exist on its night.
        string problem = Assert.Single(CheckCompletenessCheck.RowProblems("monday-row", monday, tuesday));
        Assert.Contains("weekly-screen", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_removed_from_the_list_is_retired_on_the_night_it_stops_running()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Register(connection, Monday, ["dip-shape", "exit-tight"]);
        (int introduced, int retired) = Register(connection, Tuesday, ["dip-shape"]);

        Assert.Equal(0, introduced);
        Assert.Equal(1, retired);
        Assert.Equal(["dip-shape", "exit-tight"], CheckRegister.DefinedOn(connection, "long", Monday, Later));
        Assert.Equal(["dip-shape"], CheckRegister.DefinedOn(connection, "long", Tuesday, Later));
    }

    [Fact]
    public void An_ordinary_night_registering_the_same_list_changes_nothing()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Register(connection, Monday, ["dip-shape", "trigger-near"]);

        Assert.Equal((0, 0), Register(connection, Tuesday, ["dip-shape", "trigger-near"]));
    }

    [Fact]
    public void A_retirement_recorded_after_an_instant_is_invisible_to_a_read_at_that_instant()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Register(connection, Monday, ["dip-shape", "exit-tight"]);
        Register(connection, Tuesday, ["dip-shape"]);

        // As the store stood on Monday evening, before Tuesday's detector ran, exit-tight was still in
        // force for Tuesday; the retirement had not been written.
        DateTimeOffset mondayEvening = At(Monday);
        Assert.Equal(["dip-shape", "exit-tight"], CheckRegister.DefinedOn(connection, "long", Tuesday, mondayEvening));
    }

    [Fact]
    public void The_first_registration_of_a_side_reaches_back_to_the_rows_the_store_already_holds()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Execute(connection, """
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2026-08-01');

            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES ('2026-08-25-AAA-long', '2026-08-25', 'AAA', 'long', '[]', 0);
            """);

        Register(connection, Monday, ["dip-shape"]);

        Assert.Equal(["dip-shape"], CheckRegister.DefinedOn(connection, "long", new DateOnly(2026, 8, 25), Later));
        Assert.Empty(CheckRegister.DefinedOn(connection, "long", new DateOnly(2026, 8, 24), Later));

        // The other side has no rows, so its first list is introduced on its own night.
        Register(connection, Monday, ["bounce-shape"], "short");
        Assert.Empty(CheckRegister.DefinedOn(connection, "short", new DateOnly(2026, 8, 25), Later));
    }

    [Fact]
    public void A_gate_retired_and_brought_back_is_a_second_row_rather_than_a_rewritten_one()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        Register(connection, Monday, ["dip-shape", "cluster"]);
        Register(connection, Tuesday, ["dip-shape"]);
        Register(connection, Tuesday.AddDays(1), ["dip-shape", "cluster"]);

        Assert.Equal(["cluster", "dip-shape"], CheckRegister.DefinedOn(connection, "long", Monday, Later));
        Assert.Equal(["dip-shape"], CheckRegister.DefinedOn(connection, "long", Tuesday, Later));
        Assert.Equal(["cluster", "dip-shape"], CheckRegister.DefinedOn(connection, "long", Tuesday.AddDays(1), Later));
    }

    private static readonly DateTimeOffset Later = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(DateOnly session) =>
        new(session.Year, session.Month, session.Day, 22, 30, 0, TimeSpan.Zero);

    private static (int, int) Register(SqliteConnection connection, DateOnly session, string[] checks, string direction = "long") =>
        new CheckRegister(new FixedClock(At(session))).Register(connection, direction, checks, session);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
