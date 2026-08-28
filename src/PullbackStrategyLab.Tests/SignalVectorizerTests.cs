using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Checks;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The frozen signal row: written once and never updated.
///
/// That property is the whole reason the row exists, and it is the one a test has to hold rather
/// than a comment. A signal whose value could be revised would leave every later replay comparing
/// against something the night never knew, and nothing about the store would look wrong.
///
/// These are not the checkpoint's verification, which is the fixture diff. They are the property.
/// </summary>
public sealed class SignalVectorizerTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 26, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly AsOf = new(2026, 8, 26);

    public SignalVectorizerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private SignalVectorizer Stage() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the property ------------------------------------------------------------------------

    [Fact]
    public void A_second_run_over_the_same_night_writes_nothing()
    {
        Seed("SMCI", "long");

        VectorizeResult first = Stage().Vectorize(AsOf);
        VectorizeResult second = Stage().Vectorize(AsOf);

        Assert.True(first.Written > 0, "the first run froze nothing, so the second proves nothing");
        Assert.Equal(0, second.Written);
        Assert.Equal(first.Written, second.AlreadyFrozen);
        Assert.Equal(0, second.RowsWritten);
    }

    [Fact]
    public void A_frozen_value_is_not_revised_when_the_bars_move_underneath_it()
    {
        // The case the write-once rule exists for, and the only one that distinguishes it from a
        // rerun writing the same numbers. A vendor restatement arrives as a later bar observation,
        // so the signal a rerun would compute is genuinely different. It must not be written.
        Seed("SMCI", "long");
        Stage().Vectorize(AsOf);

        string before = ValueOf("SMCI-long", "close_adjusted");

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            Restate(connection, "SMCI", AsOf, 400m);
        }

        VectorizeResult rerun = Stage().Vectorize(AsOf);

        Assert.Equal(0, rerun.Written);
        Assert.Equal(before, ValueOf("SMCI-long", "close_adjusted"));
    }

    [Fact]
    public void The_store_refuses_a_second_write_of_the_same_signal()
    {
        // Belt and braces, and deliberately so. The stage checks what is already frozen before it
        // writes, and the key makes a mistake in that check a failure rather than a duplicate row
        // nobody notices. A property held only by the code that checks it is held once.
        Seed("SMCI", "long");
        Stage().Vectorize(AsOf);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES ('SMCI-long', 'close_adjusted', '999', '2026-08-26T22:00:00.000Z')
            """;

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Nothing_in_the_shipped_source_updates_a_frozen_signal()
    {
        // The other direction, and the one a behavioural test cannot reach: a future stage could
        // add an UPDATE and every test above would still pass, because none of them runs it.
        SourceWrite[] writes =
        [
            .. SourceWrites.InProductionSource.Where(w =>
                string.Equals(w.Table, "setup_signal", StringComparison.Ordinal)
                && w.Operation == StoreOperation.Update)
        ];

        Assert.True(writes.Length == 0,
            "setup_signal is written once and never updated: " + string.Join(", ", writes.Select(w => w.ToString())));
    }

    // ---- the library, and what it does not cover yet ------------------------------------------

    [Fact]
    public void Every_active_signal_is_either_frozen_or_names_the_checkpoint_that_supplies_it()
    {
        // The partition that stops a signal being declared in SCHEMA and quietly produced by
        // nothing. A signal in neither list is one nobody noticed had no producer, which is the
        // library's own version of a check that examines less than it claims.
        IReadOnlyList<string> active = SignalLibrary.ActiveNames;
        var frozen = new HashSet<string>(SignalVectorizer.Frozen, StringComparer.Ordinal);
        var awaiting = new HashSet<string>(SignalVectorizer.AwaitingCheckpoint.Keys, StringComparer.Ordinal);

        string[] orphans = [.. active.Where(name => !frozen.Contains(name) && !awaiting.Contains(name)).Order(StringComparer.Ordinal)];
        Assert.True(orphans.Length == 0,
            $"{orphans.Length} active signal(s) in SCHEMA.md are neither frozen by SignalVectorizer nor awaiting a "
            + "checkpoint: " + string.Join(", ", orphans));

        string[] invented = [.. frozen.Concat(awaiting).Where(name => !active.Contains(name)).Order(StringComparer.Ordinal)];
        Assert.True(invented.Length == 0,
            $"{invented.Length} signal(s) the vectorizer names are not active in SCHEMA.md's library: "
            + string.Join(", ", invented));

        Assert.Equal(active.Count, frozen.Count + awaiting.Count);
    }

    [Fact]
    public void Every_awaiting_signal_names_a_checkpoint_that_exists_and_has_not_landed()
    {
        // The same rule an out-of-scope coverage item obeys as of 2.2. A signal deferred to a
        // checkpoint that has landed is one that checkpoint shipped without coming back to.
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();

        var deferrals = SignalVectorizer.AwaitingCheckpoint
            .Select(entry => new CheckCoverage.Deferred(
                $"signal {entry.Key}",
                1,
                CheckCoverage.OutOfScopeReason.UntilCheckpoint(entry.Value, "the store it reads arrives there")))
            .ToArray();

        IReadOnlyList<string> problems =
            CheckCoverage.DeferralProblems("the signal library", deferrals, schedule.Exists, schedule.HasLanded);

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    [Fact]
    public void A_signal_the_history_cannot_support_is_absent_rather_than_zero()
    {
        // One bar is not enough for an average, and a zero would be a number a rule could be
        // built on. Absent says the lab did not know, which is the true statement.
        Seed("TINY", "long", bars: 1);

        VectorizeResult result = Stage().Vectorize(AsOf);

        Assert.True(result.Absent > 0);
        Assert.DoesNotContain(Frozen("TINY-long"), name => name == "ema_gap_21_50_avg_20");
        Assert.Contains(Frozen("TINY-long"), name => name == "trigger_price");
    }

    [Fact]
    public void A_setup_whose_geometry_is_absent_freezes_no_geometry_signal()
    {
        // The defect this is named for, as a case. Until 031 the three geometry columns were NOT
        // NULL, the detector wrote nought where it had recorded no quantity, and this stage froze
        // the nought into setup_signal, which is written once and never updated. The fixture's own
        // 2026-08-24-INTC-short carried `exit-tight` failed with value null and a frozen
        // stop_distance_ranges of 0.0000 on the same row, on the same night.
        //
        // Every assertion below fails if the guards in Values() are removed: the three signals come
        // back as "0.00", "0.00" and "0.000000", and trigger_distance_ranges comes back as a real
        // number computed from a trigger that does not exist.
        Seed("FLAT", "long", geometry: false);

        VectorizeResult result = Stage().Vectorize(AsOf);

        IReadOnlyList<string> frozen = Frozen("FLAT-long");

        Assert.DoesNotContain(frozen, name => name == "trigger_price");
        Assert.DoesNotContain(frozen, name => name == "stop_price");
        Assert.DoesNotContain(frozen, name => name == "stop_distance_ranges");
        Assert.DoesNotContain(frozen, name => name == "trigger_distance_ranges");

        // Absent rather than merely missing: the stage counts what it could not compute, so a
        // signal that vanished for some other reason would not look like this.
        Assert.True(result.Absent >= 4);

        // And the rest of the row is still frozen, so this is a setup the stage processed rather
        // than one it skipped.
        Assert.Contains(frozen, name => name == "close_adjusted");
    }

    // ---- helpers -----------------------------------------------------------------------------

    private IReadOnlyList<string> Frozen(string setupId)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return [.. SetupSignalReader.NamesFor(connection, setupId).Order(StringComparer.Ordinal)];
    }

    private string ValueOf(string setupId, string signal)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return SetupSignalReader.Read(connection, AsOf)
            .Single(s => s.SetupId == setupId && s.SignalName == signal)
            .Value;
    }

    private static void Restate(SqliteConnection connection, string ticker, DateOnly barDate, decimal close)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @p, @p, @p, @p, @p, 1000000, '2026-08-27T00:00:00.000Z')
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
        command.Parameters.AddWithValue("@p", StoreText.PriceToStorageText(close));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A security, a run of bars and one setup, written by hand.
    ///
    /// The detectors do not exist until 2.6, so the setup rows here are authored rather than
    /// produced. That is the honest shape for this checkpoint: the property under test is the row,
    /// not what decided it.
    /// </summary>
    private void Seed(string ticker, string direction, int bars = 200, bool geometry = true)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
                """;
            security.Parameters.AddWithValue("@ticker", ticker);
            security.ExecuteNonQuery();
        }

        DateOnly date = AsOf.AddDays(-bars);
        for (int i = 0; i < bars; i++)
        {
            date = date.AddDays(1);
            decimal close = 100m + i;

            using SqliteCommand bar = connection.CreateCommand();
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@ticker, @bar_date, @open, @high, @low, @close, @close, 2000000, @observed_at)
                """;
            bar.Parameters.AddWithValue("@ticker", ticker);
            bar.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(i == bars - 1 ? AsOf : date));
            bar.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(close - 1m));
            bar.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(close + 2m));
            bar.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(close - 2m));
            bar.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
            bar.Parameters.AddWithValue("@observed_at", "2026-08-26T20:00:00.000Z");
            bar.ExecuteNonQuery();
        }

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = geometry
            ? """
              INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                                 trigger_price, stop_price, stop_distance_ranges)
              VALUES (@setup_id, @as_of, @ticker, @direction, '{}', 1, '120.50', '118.00', '0.4200')
              """
            : """
              INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                                 trigger_price, stop_price, stop_distance_ranges)
              VALUES (@setup_id, @as_of, @ticker, @direction, '{}', 0, NULL, NULL, NULL)
              """;
        setup.Parameters.AddWithValue("@setup_id", $"{ticker}-{direction}");
        setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        setup.Parameters.AddWithValue("@ticker", ticker);
        setup.Parameters.AddWithValue("@direction", direction);
        setup.ExecuteNonQuery();
    }
}
