using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The switch night, from 7.11: the fixture's night replayed with generation 1's baseline registered
/// before its detectors, and the readers the switch reaches.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
/// </summary>
public sealed class SwitchNightTests : IClassFixture<SwitchNightTests.Night>
{
    private readonly Night _night;

    public SwitchNightTests(Night night) => _night = night;

    /// <summary>One switched replay for the whole class, because each costs a pipeline run.</summary>
    public sealed class Night : IDisposable
    {
        public Night()
        {
            Replay = new PhaseReplay(RepositoryLayout.Fixtures);
            Result = Replay.RunTheSwitchNight();
        }

        public PhaseReplay Replay { get; }

        public SwitchNight Result { get; }

        public void Dispose() => Replay.Dispose();
    }

    /// <summary>
    /// Generation 1's verdicts go to `setup` and generation 0's to its companion, from one evidence,
    /// and a name both flag is joined back from the two.
    /// </summary>
    [Fact]
    public void A_switch_night_writes_generation_one_to_setup_and_generation_zero_beside_it()
    {
        using SqliteConnection connection = _night.Replay.OpenStore();
        DateOnly night = _night.Replay.AsOf;

        Assert.Equal(0, _night.Result.Closure.ClosedGeneration);
        Assert.Equal(1, LongSetupDetector.GenerationInForce(connection, night, SessionBoundaries.UsEquities));

        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, night);
        Assert.All(setups, s => Assert.Equal(1, s.Generation));
        Assert.Equal(16, setups.Count(s => s.Direction == SetupDirection.Long));
        Assert.Equal(8, setups.Count(s => s.Direction == SetupDirection.Short));

        Assert.Equal(1, GenerationComparisonReader.GenerationZeroFlagged(connection, SetupDirection.Long, night, SessionBoundaries.UsEquities));
        Assert.Equal(1, GenerationComparisonReader.GenerationZeroFlagged(connection, SetupDirection.Short, night, SessionBoundaries.UsEquities));

        Assert.Equal(
            ["HOOD-long", "INTC-short"],
            GenerationComparisonReader.BothFlagged(connection, night, SessionBoundaries.UsEquities).Select(p => $"{p.Ticker}-{p.Direction}"));
    }

    /// <summary>
    /// Generation 0's companion row carries generation 0's verdicts, so exit-tight is there, and the
    /// setup row beside it carries generation 1's, so it is not and weekly-trend is.
    /// </summary>
    [Fact]
    public void Each_table_carries_its_own_generations_gate_list()
    {
        using SqliteConnection connection = _night.Replay.OpenStore();

        using SqliteCommand zero = connection.CreateCommand();
        zero.CommandText = "SELECT check_results FROM setup_generation_zero WHERE ticker = 'HOOD' AND direction = 'long'";
        string companion = (string)zero.ExecuteScalar()!;

        using SqliteCommand one = connection.CreateCommand();
        one.CommandText = "SELECT check_results FROM setup WHERE ticker = 'HOOD' AND direction = 'long'";
        string record = (string)one.ExecuteScalar()!;

        Assert.Contains("\"exit-tight\"", companion, StringComparison.Ordinal);
        Assert.DoesNotContain("\"weekly-trend\"", companion, StringComparison.Ordinal);
        Assert.Contains("\"weekly-trend\"", record, StringComparison.Ordinal);
        Assert.DoesNotContain("\"exit-tight\"", record, StringComparison.Ordinal);
    }

    /// <summary>
    /// A night detected before generation 1 was registered keeps generation 0 when it is detected again
    /// afterwards. The fixture's own run registers generation 1 that evening once every figure is read, so
    /// a rerun of its night is the case: the first run's rows decide the night, and the rerun writes no
    /// companion row and no generation 1 vector beside them.
    /// </summary>
    [Fact]
    public void A_night_keeps_the_generation_its_first_detection_scored_it_under()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        using (SqliteConnection before = replay.OpenStore())
        {
            Assert.Equal(1, VariantReader.BaselineOn(before, replay.AsOf, SessionBoundaries.UsEquities)!.Generation);
        }

        replay.DetectLong();
        replay.DetectShort();

        using SqliteConnection connection = replay.OpenStore();
        Assert.Equal(0, LongSetupDetector.GenerationInForce(connection, replay.AsOf, SessionBoundaries.UsEquities));
        Assert.All(SetupReader.Read(connection, replay.AsOf), s => Assert.Equal(0, s.Generation));

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM setup_generation_zero) + (SELECT COUNT(*) FROM below_floor WHERE generation = 1)";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    /// <summary>A name that missed generation 1's floor on the switch night is kept under generation 1, with generation 1's floor.</summary>
    [Fact]
    public void The_below_floor_record_is_generation_ones_from_the_switch_night()
    {
        using SqliteConnection connection = _night.Replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(generation = 1) FROM below_floor WHERE direction = 'long'";
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();

        Assert.Equal(7186L, reader.GetInt64(0));
        Assert.Equal(7186L, reader.GetInt64(1));
    }

    /// <summary>
    /// The harness replays generation 0's rule and reads generation 0's rows alone, so a switch night
    /// of generation 1's rows gives it nothing to disagree about rather than a disagreement per row.
    /// </summary>
    [Fact]
    public void The_harness_reads_no_generation_one_row()
    {
        ReplayScreening screening = _night.Replay.ScreenTheBaseline(SetupDirection.Long);

        Assert.Equal(0, screening.RowsExamined);
        Assert.Empty(screening.Disagreements);
    }

    /// <summary>
    /// A generation 1 row failing only a clause generation 1 records and never requires has passed
    /// everything under its own gate set, and would read one gate short under generation 0's.
    /// </summary>
    [Fact]
    public void A_rows_outcome_is_read_under_its_own_generations_gate_set()
    {
        (string, bool)[] checks = [("tradable", true), ("moves-enough", false), ("weekly-trend", true)];

        Assert.True(SetupOutcomes.Matches(SetupOutcomes.PassedEverything, checks, generation: 1));
        Assert.True(SetupOutcomes.Matches(SetupOutcomes.FailedOnlyOne, checks, generation: 0));
    }

    /// <summary>
    /// A generation 1 candidate with no evening geometry, being a thrust that has not pulled back yet,
    /// is ranked after every candidate that has one rather than dropped from the cap, which under
    /// generation 0 could not arise because `exit-tight` failed on an absent distance.
    /// </summary>
    [Fact]
    public void A_candidate_with_no_give_up_distance_is_ranked_last_rather_than_dropped()
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        using (SqliteConnection connection = connections.OpenWrite())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen) VALUES
                    ('NEAR', 'NEAR', 'NASDAQ', 'Common Stock', '2026-07-01'),
                    ('NONE', 'NONE', 'NASDAQ', 'Common Stock', '2026-07-01');
                INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all, stop_distance_ranges, generation) VALUES
                    ('2026-09-14-NEAR-long', '2026-09-14', 'NEAR', 'long', '[]', 1, '0.300000', 1),
                    ('2026-09-14-NONE-long', '2026-09-14', 'NONE', 'long', '[]', 1, NULL, 1);
                """;
            command.ExecuteNonQuery();
        }

        IOptions<PullbackStrategyLabOptions> options =
            Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = root.Path });
        var clock = new FixedClock(SessionBoundaries.At(new DateOnly(2026, 9, 14), new TimeOnly(18, 28), SessionBoundaries.UsEquities));

        CapResult capped = new SetupCapper(connections, new RunLogger(clock, options), clock, options).Cap(new DateOnly(2026, 9, 14));
        Assert.Equal(2, capped.Candidates);

        using SqliteConnection read = connections.OpenReadOnly();
        IReadOnlyList<StoredSetup> setups = SetupReader.Read(read, new DateOnly(2026, 9, 14));
        Assert.Equal(1, setups.Single(s => s.Ticker == "NEAR").Rank);
        Assert.Equal(2, setups.Single(s => s.Ticker == "NONE").Rank);
    }

    /// <summary>
    /// Once generation 1 is in force no selection version is admitted, because the only selection rule
    /// written down is generation 0's, and the pack refuses rather than asking against it.
    /// see: Generation 1 opens with no version admissible in either family, and what reopens each is named
    /// </summary>
    [Fact]
    public void Generation_one_admits_no_selection_version_and_builds_no_pack()
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        using (SqliteConnection connection = connections.OpenWrite())
        {
            TestVersions.SeedBaseline(connection);
        }

        IOptions<PullbackStrategyLabOptions> options =
            Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = root.Path });
        var clock = new FixedClock(SessionBoundaries.At(new DateOnly(2026, 9, 14), new TimeOnly(18, 0), SessionBoundaries.UsEquities));

        new GenerationCloser(connections, new RunLogger(clock, options), clock, options).Close(
            "V1", GenerationCloser.GenerationOneDefinition, GenerationCloser.GenerationOneTarget);

        var admitter = new VariantAdmitter(connections, new RunLogger(clock, options), clock, options);

        Assert.Equal(1, admitter.Run(["V1-dip", "--family", "selection", "--direction", "long", "--threshold", "maximum-retrace", "--value", "0.5"]));

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => admitter.Admit(
            "V1-dip", VariantFamily.Selection, "a move", null));
        Assert.Equal(VariantAdmitter.GenerationOneSelectionRefused, refused.Message);

        PackResult pack = new ContextPacker(connections, new RunLogger(clock, options), clock, options).Build(new DateOnly(2026, 9, 14));
        Assert.NotNull(pack.RefusedBecause);
        Assert.Contains("generation 1", pack.RefusedBecause, StringComparison.Ordinal);
    }
}
