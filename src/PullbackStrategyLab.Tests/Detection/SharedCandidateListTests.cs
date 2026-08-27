using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// The cap is applied to one shared candidate list, never per version.
///
/// <b>Asserted now, while there are no versions, because it is unassertable once there are.</b> When
/// several versions pick from the same night, a cap applied per version leaves their disagreements
/// unscoreable: two versions that chose differently would each be capped against their own list, so
/// a name one of them dropped and the other kept would be indistinguishable from a name the cap
/// removed. By the time that shows up, the record it destroyed cannot be reconstructed, and the
/// paired comparison the whole project rests on is the thing that quietly stopped being paired.
/// see: Versions select from one shared nightly candidate list rather than each re-scanning
/// see: The nightly cap is 60, split forty long and twenty short, unused slots released
///
/// The property is held by the schema rather than by the stage's good behaviour, which is what makes
/// it survive a rewrite: `setup` carries no column naming a version, so a per-version rank is not
/// expressible without a migration, and a migration that added one would fail here.
/// </summary>
public sealed class SharedCandidateListTests
{
    /// <summary>Words that would make a column belong to one version rather than to the night.</summary>
    private static readonly string[] PerVersion = ["version", "variant", "account"];

    [Fact]
    public void The_setup_table_has_no_column_that_would_make_a_rank_belong_to_one_version()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        using SqliteConnection connection = replay.OpenStore();
        string[] columns = [.. Columns(connection, SetupReader.SetupTable)];

        // Stated in advance. A table name that stopped resolving would give an empty column list,
        // which contains nothing forbidden and would pass.
        Assert.True(columns.Length >= 10,
            $"`{SetupReader.SetupTable}` reports {columns.Length} column(s), so the lookup stopped matching.");

        string[] offending =
        [
            .. columns.Where(c => PerVersion.Any(w => c.Contains(w, StringComparison.OrdinalIgnoreCase))),
        ];

        Assert.True(offending.Length == 0,
            $"`{SetupReader.SetupTable}` carries {string.Join(", ", offending)}. A rank that belongs to one version "
            + "is a cap applied per version, and the disagreements between versions stop being scoreable.");
    }

    /// <summary>
    /// And the read the capper makes is one night, whole.
    ///
    /// The other half of the same property: a schema with nowhere to put a version still admits a
    /// capper that reads a subset. `SetupReader.Read` takes a connection and a date and nothing else,
    /// which is the signature that makes a per-version read impossible to write.
    /// </summary>
    [Fact]
    public void The_capper_reads_a_night_by_date_and_by_nothing_else()
    {
        string source = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "SetupCapper.cs"));

        Assert.Contains("SetupReader.Read(connection, asOf)", source, StringComparison.Ordinal);

        // The reader's own signature, so the assertion above is not merely about today's call site.
        // No public read of the evidence store takes a name of any kind: the calibration table is
        // reached through a differently named method rather than through a parameter, precisely so
        // that a caller cannot pass "the rows belonging to version three" and have it compile.
        System.Reflection.MethodInfo[] reads =
        [
            .. typeof(SetupReader).GetMethods()
                .Where(m => string.Equals(m.Name, nameof(SetupReader.Read), StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(reads);
        Assert.All(reads, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(string)));
    }

    private static IEnumerable<string> Columns(SqliteConnection connection, string table)
    {
        SqliteIdentifier.Validate(table);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(1);
        }
    }
}
