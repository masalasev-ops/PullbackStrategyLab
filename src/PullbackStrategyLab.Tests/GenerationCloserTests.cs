using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The act that closes a generation, from 7.6: what it writes on each row of the generation in
/// force, what it leaves alone, and what the store refuses around it.
///
/// <b>Authored, on the register's own footing.</b> The live register holds one baseline and nothing
/// else, and no generation has ever been closed, so the cases are written to sit on either side of the
/// properties under test.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class GenerationCloserTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 9, 11);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Evening, new TimeOnly(19, 0), SessionBoundaries.UsEquities));

    public GenerationCloserTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private GenerationCloser Closer() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    /// <summary>A baseline, two open versions, one accepted and one rejected, all in generation 0.</summary>
    private void SeedGenerationZero()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        TestVersions.SeedBaseline(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES
                ('V-open-long', 0, 'selection', 'a', 't', 1802, 'effective_paired_setup_observations',
                 'open', NULL, '2026-09-01T22:00:00.000Z', 'long', 'dip-shape', 'maximum-retrace', '0.40', '0.50'),
                ('V-open-short', 0, 'selection', 'b', 't', 1802, 'effective_paired_setup_observations',
                 'open', NULL, '2026-09-01T22:00:00.000Z', 'short', 'bounce-shape', 'maximum-retrace', '0.40', '0.30'),
                ('V-accepted', 0, 'selection', 'c', 't', 1802, 'effective_paired_setup_observations',
                 'accepted', '2026-09-05T01:00:00.000Z', '2026-09-01T22:00:00.000Z', 'long', 'dip-shape', 'maximum-retrace', '0.40', '0.45'),
                ('V-rejected', 0, 'selection', 'd', 't', 1802, 'effective_paired_setup_observations',
                 'rejected', '2026-09-05T01:00:00.000Z', '2026-09-01T22:00:00.000Z', 'long', 'dip-shape', 'maximum-retrace', '0.40', '0.35');
            """;
        command.ExecuteNonQuery();
    }

    private StoredVariant Read(string variantId)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return VariantReader.RegisteredBy(connection, Evening, SessionBoundaries.UsEquities)
            .Single(v => v.VariantId == variantId);
    }

    [Fact]
    public void Closing_writes_unresolved_on_every_open_version_and_retired_on_the_baseline_and_opens_the_next()
    {
        SeedGenerationZero();

        GenerationClosure closure = Closer().Close("V-next", "the next rule", "the reference");

        Assert.True(closure.Written);
        Assert.Null(closure.RefusedBecause);
        Assert.Equal(0, closure.ClosedGeneration);
        Assert.Equal(["V-open-long", "V-open-short"], closure.Unresolved.Order(StringComparer.Ordinal));
        Assert.Equal(2, closure.SettledKept);

        Assert.Equal(VariantStatus.Unresolved, Read("V-open-long").Status);
        Assert.Equal(VariantStatus.Unresolved, Read("V-open-short").Status);
        Assert.Equal(VariantStatus.Retired, Read(TestVersions.Baseline).Status);
        Assert.Equal(_clock.UtcNow, Read(TestVersions.Baseline).ResolvedAt);

        // The settled versions keep the answer they were measured to, and the date they got it.
        Assert.Equal(VariantStatus.Accepted, Read("V-accepted").Status);
        Assert.Equal(VariantStatus.Rejected, Read("V-rejected").Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero), Read("V-accepted").ResolvedAt);

        StoredVariant next = Read("V-next");
        Assert.Equal(1, next.Generation);
        Assert.Equal(VariantFamily.Baseline, next.Family);
        Assert.Equal(VariantStatus.Open, next.Status);
        Assert.Equal(1802, next.MinimumSample);

        // And the night fans out to the new generation alone.
        using SqliteConnection read = _connections.OpenReadOnly();
        StoredVariant live = Assert.Single(VariantReader.LiveOn(read, Evening, SessionBoundaries.UsEquities));
        Assert.Equal("V-next", live.VariantId);
    }

    /// <summary>
    /// The close is read as of its own evening, so a replay of the evening before still sees the old
    /// generation live, which is the point-in-time property the register already had.
    /// </summary>
    [Fact]
    public void An_evening_before_the_close_still_fans_out_to_the_generation_it_had()
    {
        SeedGenerationZero();
        Closer().Close("V-next", "the next rule", "the reference");

        using SqliteConnection read = _connections.OpenReadOnly();
        IReadOnlyList<StoredVariant> before = VariantReader.LiveOn(read, Evening.AddDays(-1), SessionBoundaries.UsEquities);

        Assert.All(before, v => Assert.Equal(0, v.Generation));
        Assert.Contains(before, v => v.VariantId == TestVersions.Baseline);
    }

    [Fact]
    public void A_second_close_closes_the_generation_then_in_force_and_leaves_the_first_as_it_was()
    {
        SeedGenerationZero();
        Closer().Close("V-next", "the next rule", "the reference");

        _clock.Advance(TimeSpan.FromDays(1));
        GenerationClosure second = Closer().Close("V-after", "the rule after that", "the reference");

        Assert.Equal(1, second.ClosedGeneration);
        Assert.Empty(second.Unresolved);
        Assert.Equal("V-next", second.RetiredBaseline);

        using SqliteConnection read = _connections.OpenReadOnly();
        IReadOnlyList<StoredVariant> all = VariantReader.RegisteredBy(read, Evening.AddDays(1), SessionBoundaries.UsEquities);
        Assert.Equal(VariantStatus.Retired, all.Single(v => v.VariantId == TestVersions.Baseline).Status);
        Assert.Equal(VariantStatus.Retired, all.Single(v => v.VariantId == "V-next").Status);
        Assert.Equal(2, all.Single(v => v.VariantId == "V-after").Generation);
    }

    [Fact]
    public void A_dry_run_says_what_it_would_close_and_writes_nothing()
    {
        SeedGenerationZero();

        GenerationClosure dry = Closer().Close("V-next", "the next rule", "the reference", dryRun: true);

        Assert.False(dry.Written);
        Assert.Equal(2, dry.Unresolved.Count);
        Assert.Equal(VariantStatus.Open, Read(TestVersions.Baseline).Status);
        Assert.Equal(VariantStatus.Open, Read("V-open-long").Status);

        using SqliteConnection read = _connections.OpenReadOnly();
        Assert.DoesNotContain(
            VariantReader.RegisteredBy(read, Evening, SessionBoundaries.UsEquities), v => v.VariantId == "V-next");
    }

    [Fact]
    public void An_empty_register_has_no_generation_to_close_and_is_refused()
    {
        GenerationClosure refused = Closer().Close("V-next", "the next rule", "the reference");

        Assert.False(refused.Written);
        Assert.Equal(GenerationCloser.NothingToClose, refused.RefusedBecause);
        Assert.Equal(1, Closer().Run(["V-next", GenerationCloser.DefinitionFlag, "d", GenerationCloser.TargetFlag, "t"]));
    }

    [Fact]
    public void A_next_baseline_named_after_a_registered_version_is_refused_and_nothing_is_closed()
    {
        SeedGenerationZero();

        GenerationClosure refused = Closer().Close("V-open-long", "the next rule", "the reference");

        Assert.NotNull(refused.RefusedBecause);
        Assert.Equal(VariantStatus.Open, Read(TestVersions.Baseline).Status);
        Assert.Equal(VariantStatus.Open, Read("V-open-short").Status);
    }

    [Fact]
    public void The_verb_wants_a_name_a_definition_and_a_target_and_exits_two_without_them()
    {
        SeedGenerationZero();

        Assert.Equal(2, Closer().Run(["V-next"]));
        Assert.Equal(2, Closer().Run([GenerationCloser.DefinitionFlag, "d", GenerationCloser.TargetFlag, "t"]));
        Assert.Equal(VariantStatus.Open, Read(TestVersions.Baseline).Status);

        Assert.Equal(0, Closer().Run(["V-next", GenerationCloser.DefinitionFlag, "d", GenerationCloser.TargetFlag, "t"]));
        Assert.Equal(VariantStatus.Retired, Read(TestVersions.Baseline).Status);
    }

    /// <summary>
    /// The store holds `retired` to the baseline and the baseline to open or retired, so neither of
    /// the two readings the status exists to keep apart can be written by hand either.
    /// </summary>
    [Fact]
    public void The_store_refuses_retired_on_a_version_and_a_settled_answer_on_a_baseline()
    {
        SeedGenerationZero();

        using SqliteConnection connection = _connections.OpenWrite();

        SqliteException versionRetired = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE variant SET status = 'retired', resolved_at = '2026-09-11T23:00:00.000Z' WHERE variant_id = 'V-open-long';"));
        Assert.Contains("CHECK constraint failed", versionRetired.Message, StringComparison.Ordinal);

        SqliteException baselineAccepted = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE variant SET status = 'accepted', resolved_at = '2026-09-11T23:00:00.000Z' WHERE variant_id = 'V0';"));
        Assert.Contains("CHECK constraint failed", baselineAccepted.Message, StringComparison.Ordinal);

        SqliteException baselineUnresolved = Assert.Throws<SqliteException>(() => Execute(connection,
            "UPDATE variant SET status = 'unresolved', resolved_at = '2026-09-11T23:00:00.000Z' WHERE variant_id = 'V0';"));
        Assert.Contains("CHECK constraint failed", baselineUnresolved.Message, StringComparison.Ordinal);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
