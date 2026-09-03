using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The register of rule versions: what registering one writes, and what a night reads back.
///
/// <b>Every case here is authored, and that is the only footing available.</b> No version has ever
/// been registered in the live store, so there is no captured population to run these against and
/// there will not be one until the baseline is frozen. The rows below are written to sit either side
/// of the properties under test, which is where every gate boundary in this suite stands.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class VariantRegisterTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 9, 3);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Evening, new TimeOnly(18, 28), SessionBoundaries.UsEquities));

    public VariantRegisterTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private VariantAdmitter Admitter() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    private VariantResolver Resolver() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    // ---- what registration writes --------------------------------------------------------

    /// <summary>
    /// The pre-registration is written at creation, and the minimum comes from the corpus rather
    /// than from whoever ran the command.
    /// </summary>
    [Fact]
    public void The_baseline_is_registered_with_the_derived_minimum_and_its_unit()
    {
        VariantAdmission admitted = Admitter().Admit(
            "V0", VariantFamily.Baseline, "the rule the lab has run every night", "the reference");

        Assert.True(admitted.Written);
        Assert.Equal(MeasurementParameters.MinimumEffectiveObservations, admitted.Variant.MinimumSample);
        Assert.Equal(MinimumSampleUnit.EffectivePairedSetupObservations, admitted.Variant.MinimumSampleUnit);
        Assert.Equal(VariantStatus.Open, admitted.Variant.Status);
        Assert.Equal(0, admitted.Variant.Generation);
        Assert.Null(admitted.Variant.ResolvedAt);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredVariant stored = Assert.Single(
            VariantReader.RegisteredBy(connection, Evening, SessionBoundaries.UsEquities));
        Assert.Equal(1802, stored.MinimumSample);
    }

    /// <summary>
    /// A dry run shows what would be written and writes nothing, which is what an act that cannot be
    /// undone deserves.
    /// </summary>
    [Fact]
    public void A_dry_run_reports_the_row_and_leaves_the_register_empty()
    {
        VariantAdmission dry = Admitter().Admit(
            "V0", VariantFamily.Baseline, "the rule", "the reference", dryRun: true);

        Assert.False(dry.Written);
        Assert.Equal(1802, dry.Variant.MinimumSample);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Empty(VariantReader.RegisteredBy(connection, Evening, SessionBoundaries.UsEquities));
    }

    /// <summary>
    /// The key refuses a second registration rather than the stage remembering to check, which is
    /// what makes the target immutable by any path rather than by this component's good behaviour.
    /// </summary>
    [Fact]
    public void A_second_registration_of_one_identifier_is_refused_by_the_key()
    {
        Admitter().Admit("V0", VariantFamily.Baseline, "the rule", "the reference");

        SqliteException thrown = Assert.Throws<SqliteException>(
            () => Admitter().Admit("V0", VariantFamily.Baseline, "a different rule", "a different target"));

        Assert.Contains("UNIQUE", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A second baseline in one generation is refused by the index, because a difference series with
    /// two things to be measured against measures nothing.
    /// </summary>
    [Fact]
    public void A_second_baseline_in_one_generation_is_refused_by_the_index()
    {
        Admitter().Admit("V0", VariantFamily.Baseline, "the rule", "the reference");

        SqliteException thrown = Assert.Throws<SqliteException>(
            () => Admitter().Admit("V0b", VariantFamily.Baseline, "another rule", "another reference"));

        Assert.Contains("UNIQUE", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A selection version shares the baseline's unit, and the store refuses the pairing that would
    /// make the two families' minima read as comparable.
    /// </summary>
    [Fact]
    public void A_selection_version_carries_the_effective_unit_and_the_store_refuses_the_other_pairing()
    {
        Admitter().Admit("V0", VariantFamily.Baseline, "the rule", "the reference");
        VariantAdmission selection = Admitter().Admit(
            "F1a", VariantFamily.Selection, "widens exit-tight by a tenth of a range", "two points of forward return");

        Assert.Equal(MinimumSampleUnit.EffectivePairedSetupObservations, selection.Variant.MinimumSampleUnit);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand wrong = connection.CreateCommand();
        wrong.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
            VALUES ('F1b', 0, 'selection', 'a rule', 'a target', 200, 'paired_trades', 'open', NULL, '2026-09-03T22:28:00.000Z');
            """;

        SqliteException thrown = Assert.Throws<SqliteException>(() => wrong.ExecuteNonQuery());
        Assert.Contains("CHECK", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- what a night reads back ---------------------------------------------------------

    /// <summary>
    /// A night before the baseline is registered is reported partial and says why, because a lab
    /// that flags and records setups without any version existing is a real state and not an error.
    /// </summary>
    [Fact]
    public void A_night_with_an_empty_register_is_partial_and_names_the_reason()
    {
        VariantResolution resolved = Resolver().Resolve(Evening);

        Assert.Empty(resolved.Live);
        Assert.Equal(RunOutcome.Partial, resolved.Outcome);
        Assert.Equal(VariantResolver.NoVersionsRegistered, resolved.NothingBecause);
        Assert.Equal(0, resolved.RowsWritten);
    }

    /// <summary>
    /// A night with versions and no baseline is a different state from a night with none, and the
    /// two are told apart rather than counted together.
    /// </summary>
    [Fact]
    public void A_register_with_no_baseline_is_partial_for_a_different_reason()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand only = connection.CreateCommand();
            only.CommandText = """
                INSERT INTO variant (
                    variant_id, generation, family, definition, target,
                    minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
                VALUES ('F1a', 0, 'selection', 'a rule', 'a target', 1802,
                        'effective_paired_setup_observations', 'open', NULL, '2026-09-01T22:28:00.000Z');
                """;
            only.ExecuteNonQuery();
        }

        VariantResolution resolved = Resolver().Resolve(Evening);

        Assert.Single(resolved.Live);
        Assert.Equal(RunOutcome.Partial, resolved.Outcome);
        Assert.Equal(VariantResolver.NoBaseline, resolved.NothingBecause);
    }

    /// <summary>
    /// The resolver writes nothing, which is a property of the run record rather than of its
    /// comment: the scope measures rows from the store, so a row would be counted and reported.
    /// </summary>
    [Fact]
    public void A_resolved_night_writes_no_row()
    {
        Admitter().Admit("V0", VariantFamily.Baseline, "the rule", "the reference");

        VariantResolution resolved = Resolver().Resolve(Evening);

        Assert.Equal(RunOutcome.Clean, resolved.Outcome);
        Assert.Null(resolved.NothingBecause);
        Assert.Equal(0, resolved.RowsWritten);
        Assert.Equal("V0", Assert.Single(resolved.Live).VariantId);
    }

    /// <summary>
    /// A version registered after a night is invisible to that night, which is what stops a replay
    /// fanning a plan out to a version the night had never heard of and then differencing it.
    /// </summary>
    [Fact]
    public void A_version_registered_after_a_session_is_invisible_to_it()
    {
        Admitter().Admit("V0", VariantFamily.Baseline, "the rule", "the reference");

        DateOnly before = Evening.AddDays(-1);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Empty(VariantReader.LiveOn(connection, before, SessionBoundaries.UsEquities));
        Assert.Single(VariantReader.LiveOn(connection, Evening, SessionBoundaries.UsEquities));
    }

    /// <summary>
    /// Only the generation in force is fanned out to. An older generation's versions stay readable
    /// and stop being planned against, which is what editing the baseline does to them.
    /// </summary>
    [Fact]
    public void Only_the_generation_in_force_is_live()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand generations = connection.CreateCommand();
            generations.CommandText = """
                INSERT INTO variant (
                    variant_id, generation, family, definition, target,
                    minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
                VALUES
                    ('V0', 0, 'baseline', 'the first rule', 'the reference', 1802,
                     'effective_paired_setup_observations', 'unresolved', '2026-09-02T22:00:00.000Z',
                     '2026-09-01T22:28:00.000Z'),
                    ('V1', 1, 'baseline', 'the rule after the edit', 'the reference', 1802,
                     'effective_paired_setup_observations', 'open', NULL,
                     '2026-09-02T22:28:00.000Z');
                """;
            generations.ExecuteNonQuery();
        }

        using SqliteConnection read = _connections.OpenReadOnly();
        StoredVariant live = Assert.Single(VariantReader.LiveOn(read, Evening, SessionBoundaries.UsEquities));

        Assert.Equal("V1", live.VariantId);
        Assert.Equal(1, live.Generation);
        Assert.Equal(2, VariantReader.RegisteredBy(read, Evening, SessionBoundaries.UsEquities).Count);
    }

    /// <summary>
    /// The identifier of a plan is the setup and the version, and it refuses a component that would
    /// make the two halves unreadable.
    /// </summary>
    [Fact]
    public void A_plan_identifier_is_the_setup_and_the_version_and_refuses_a_separator_inside_either()
    {
        Assert.Equal("2026-09-03-AAPL-long@V0", PlanIdentity.For("2026-09-03-AAPL-long", "V0"));

        Assert.Throws<ArgumentException>(() => PlanIdentity.For("a@b", "V0"));
        Assert.Throws<ArgumentException>(() => PlanIdentity.For("2026-09-03-AAPL-long", "V0@1"));
    }
}
