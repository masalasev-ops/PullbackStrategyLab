using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The fixture's night detected under generation 2, from 7.19: generation 1 closed through the act with
/// generation 2's baseline, then the detectors, which require the daily range generation 1 recorded.
/// see: The baseline is written clause by clause from SOURCES.md, and a figure he states screens even where he qualifies it
/// </summary>
public sealed class GenerationTwoNightTests : IClassFixture<GenerationTwoNightTests.Night>
{
    private readonly Night _night;

    public GenerationTwoNightTests(Night night) => _night = night;

    /// <summary>One replay for the whole class, because each costs a pipeline run.</summary>
    public sealed class Night : IDisposable
    {
        public Night()
        {
            Replay = new PhaseReplay(RepositoryLayout.Fixtures);
            Result = Replay.RunTheGenerationTwoNight();
        }

        public PhaseReplay Replay { get; }

        public SwitchNight Result { get; }

        public void Dispose() => Replay.Dispose();
    }

    /// <summary>
    /// The act closes generation 1 and the night is generation 2's, and the recording floor has not
    /// moved: the same 16 long and 8 short names the switch night records are recorded, the slower ones
    /// among them failing `moves-enough` rather than going unrecorded.
    /// </summary>
    [Fact]
    public void Generation_two_is_in_force_and_records_the_names_generation_one_recorded()
    {
        using SqliteConnection connection = _night.Replay.OpenStore();
        DateOnly night = _night.Replay.AsOf;

        Assert.Null(_night.Result.Closure.RefusedBecause);
        Assert.Equal(1, _night.Result.Closure.ClosedGeneration);
        Assert.Equal(2, LongSetupDetector.GenerationInForce(connection, night, SessionBoundaries.UsEquities));

        IReadOnlyList<StoredSetup> setups = SetupReader.Read(connection, night);
        Assert.All(setups, s => Assert.Equal(2, s.Generation));
        Assert.Equal(16, setups.Count(s => s.Direction == SetupDirection.Long));
        Assert.Equal(8, setups.Count(s => s.Direction == SetupDirection.Short));
    }

    /// <summary>
    /// Every stored row's pass is generation 2's rule over its own vector, no name passes that failed the
    /// daily range, and a name that passed generation 1's rule and failed the daily range is stored
    /// failing. Stated over the rows the night recorded, both sides, never pooled.
    /// </summary>
    [Fact]
    public void A_row_passes_only_where_generation_twos_rule_passes_it_so_a_name_failing_the_daily_range_does_not()
    {
        using SqliteConnection connection = _night.Replay.OpenStore();

        foreach (string direction in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            StoredSetup[] rows = [.. SetupReader.Read(connection, _night.Replay.AsOf).Where(s => s.Direction == direction)];

            foreach (StoredSetup row in rows)
            {
                (string Name, bool Passed)[] verdicts = Verdicts(row.CheckResults);

                Assert.Equal(SetupChecks.GatingFailures(verdicts, 2) == 0, row.PassedAll);

                if (row.PassedAll)
                {
                    Assert.Contains(("moves-enough", true), verdicts);
                }

                if (SetupChecks.GatingFailures(verdicts, 1) == 0 && verdicts.Contains(("moves-enough", false)))
                {
                    Assert.False(row.PassedAll, $"{row.SetupId} failed the daily range and was stored passing");
                }
            }
        }
    }

    private static (string Name, bool Passed)[] Verdicts(string checkResults)
    {
        using JsonDocument document = JsonDocument.Parse(checkResults);
        return [.. document.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("passed").GetBoolean()))];
    }
}
