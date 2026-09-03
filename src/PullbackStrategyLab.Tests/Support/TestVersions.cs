using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The baseline a test store needs before a plan can exist in it.
///
/// <b>Here rather than in each test file, because the alternative is seven copies of one row.</b>
/// From 5.1 a plan belongs to a version and the store's foreign key says so, so any test that seeds
/// a plan seeds a version first. Every test that does this wants the same thing: one baseline, in
/// the generation the register starts at.
///
/// <b>It is not VariantAdmitter and does not pretend to be.</b> The admitter is the component under
/// test wherever registration is what is being tested; this is a fixture, and it writes the row
/// directly so that a test of the broker is not also a test of the admitter. Where the two disagree
/// about a column, the admitter is right and this is the thing to fix.
/// </summary>
public static class TestVersions
{
    /// <summary>The identifier every fixture uses, matching the baseline the lab registers.</summary>
    public const string Baseline = "V0";

    /// <summary>
    /// Writes the baseline if it is not already there, and answers its identifier.
    ///
    /// Idempotent, because a test that seeds two plans should not have to know whether the first
    /// call already ran.
    /// </summary>
    public static string SeedBaseline(SqliteConnection connection, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
            VALUES (
                @variant_id, 0, @family, @definition, @target,
                @minimum_sample, @minimum_sample_unit, @status, NULL, @created_at)
            ON CONFLICT (variant_id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@variant_id", Baseline);
        command.Parameters.AddWithValue("@family", VariantFamily.Baseline);
        command.Parameters.AddWithValue("@definition", "the rule the lab has run every night");
        command.Parameters.AddWithValue("@target", "the reference every version is differenced against");
        command.Parameters.AddWithValue("@minimum_sample", MeasurementParameters.MinimumEffectiveObservations);
        command.Parameters.AddWithValue("@minimum_sample_unit", MinimumSampleUnit.EffectivePairedSetupObservations);
        command.Parameters.AddWithValue("@status", VariantStatus.Open);
        command.Parameters.AddWithValue(
            "@created_at",
            StoreText.TimestampToStorageText(createdAt ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        command.ExecuteNonQuery();
        return Baseline;
    }
}
