using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the front page reads, from 7.20.
///
/// Three properties, and the third is the one this file exists for. The two sides are counted
/// apart. The funnel's widest rung is derived from the store rather than copied out of a log. And
/// the generation is taken from the rows of the evening being asked about rather than from the
/// variant register, which records only a baseline's current status.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class LabExperimentTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public LabExperimentTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private ExperimentResponse Read(DateOnly asOf) =>
        LabExperiment.Read(_connections, asOf, SessionBoundaries.UsEquities);

    private void Execute(string sql)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The security row a setup's foreign key needs, written once per ticker.</summary>
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    private void Security(string ticker)
    {
        if (_known.Add(ticker))
        {
            Execute($"""
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES ('{ticker}', '{ticker}', 'NASDAQ', 'Common Stock', '2026-08-01');
                """);
        }
    }

    /// <summary>One recorded setup, above the floor, with the generation that scored it.</summary>
    private void Setup(string asOf, string ticker, string direction, bool passedAll, int generation)
    {
        Security(ticker);
        Execute($"""
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all, generation)
            VALUES ('{asOf}-{ticker}-{direction}', '{asOf}', '{ticker}', '{direction}', '[]',
                    {(passedAll ? 1 : 0)}, {generation.ToString(CultureInfo.InvariantCulture)});
            """);
    }

    /// <summary>One name that missed the recording floor, which is the other half of "examined".</summary>
    private void BelowFloor(string asOf, string ticker, string direction, int generation)
    {
        Security(ticker);
        Execute($"""
            INSERT INTO below_floor (as_of, ticker, direction, generation, check_results, failed_floor, observed_at)
            VALUES ('{asOf}', '{ticker}', '{direction}', {generation.ToString(CultureInfo.InvariantCulture)},
                    '[]', 'uptrend', '{asOf}T22:30:00.000Z');
            """);
    }

    /// <summary>
    /// The two sides are counted apart, and no field of the answer holds their sum.
    ///
    /// Different counts on each side on purpose: a store with the same number on both would pass a
    /// reader that answered one side twice.
    /// </summary>
    [Fact]
    public void Each_side_is_counted_apart_and_nothing_holds_their_sum()
    {
        Setup("2026-08-27", "AAA", "long", passedAll: true, generation: 1);
        Setup("2026-08-27", "BBB", "long", passedAll: false, generation: 1);
        Setup("2026-08-27", "CCC", "long", passedAll: false, generation: 1);
        Setup("2026-08-27", "DDD", "short", passedAll: false, generation: 1);

        ExperimentResponse answer = Read(new DateOnly(2026, 8, 27));

        Assert.NotNull(answer.Long);
        Assert.NotNull(answer.Short);
        Assert.Equal(3, answer.Long!.PatternsRecorded);
        Assert.Equal(1, answer.Short!.PatternsRecorded);
        Assert.Equal(1, answer.Long.Funnel.Passed);
        Assert.Equal(0, answer.Short.Funnel.Passed);

        // The sum appears nowhere. Asserted over the record's own properties rather than by reading
        // the page, because a reader that offered a total would offer it to every surface at once.
        Assert.DoesNotContain(
            typeof(ExperimentResponse).GetProperties(),
            p => p.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)
                 || p.Name.Contains("Both", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Examined is the rows recorded plus the rows that missed the floor, so the widest rung is the
    /// store's own arithmetic rather than a figure copied from the night's log.
    /// </summary>
    [Fact]
    public void Examined_is_what_was_recorded_plus_what_missed_the_floor()
    {
        Setup("2026-08-27", "AAA", "long", passedAll: true, generation: 1);
        Setup("2026-08-27", "BBB", "long", passedAll: false, generation: 1);
        BelowFloor("2026-08-27", "CCC", "long", generation: 1);
        BelowFloor("2026-08-27", "DDD", "long", generation: 1);
        BelowFloor("2026-08-27", "EEE", "long", generation: 1);

        ExperimentFunnel funnel = Read(new DateOnly(2026, 8, 27)).Long!.Funnel;

        Assert.Equal(5, funnel.Examined);
        Assert.Equal(2, funnel.Recorded);
        Assert.Equal(1, funnel.Passed);
    }

    /// <summary>
    /// The generation is the one the evening's own rows carry, and stays so after the register has
    /// moved on.
    ///
    /// <b>This is the fault the first draft of this reader shipped with.</b> It read the generation
    /// from <c>variant</c>, which holds a baseline's current status and no history of it. On the
    /// morning generation 2 opened, the same query asked for the evening before found no open
    /// baseline at that date and answered generation 0, on an evening whose every row carries 1.
    /// The store below is exactly that state: generation 1's baseline retired, generation 2's open
    /// and created after the evening being asked about.
    /// </summary>
    [Fact]
    public void The_generation_is_the_one_the_evenings_rows_carry_after_the_register_has_moved_on()
    {
        Setup("2026-09-15", "AAA", "long", passedAll: true, generation: 1);
        Setup("2026-09-15", "BBB", "short", passedAll: false, generation: 1);

        Execute("""
            INSERT INTO variant (variant_id, generation, family, definition, target, minimum_sample,
                                 minimum_sample_unit, status, resolved_at, created_at)
            VALUES ('V1', 1, 'baseline', 'generation 1', 'the reference', 1, 'effective_paired_setup_observations',
                    'retired', '2026-09-16T04:13:12.523Z', '2026-09-12T04:53:08.870Z'),
                   ('V2', 2, 'baseline', 'generation 2', 'the reference', 1, 'effective_paired_setup_observations',
                    'open', NULL, '2026-09-16T04:13:12.523Z');
            """);

        Assert.Equal(1, Read(new DateOnly(2026, 9, 15)).LatestEveningGeneration);
    }

    /// <summary>
    /// The evenings are the ones the store holds a detection for, not the weekdays between the
    /// first and the last. The gap is what the front page's warning is about, and it is only
    /// visible because both the count and the two dates are stated.
    /// </summary>
    [Fact]
    public void The_evenings_counted_are_the_ones_recorded_rather_than_the_span()
    {
        Setup("2026-08-27", "AAA", "long", passedAll: false, generation: 1);
        Setup("2026-09-15", "BBB", "long", passedAll: false, generation: 1);

        ExperimentResponse answer = Read(new DateOnly(2026, 9, 15));

        Assert.Equal(2, answer.Evenings);
        Assert.Equal("2026-08-27", answer.FirstEvening);
        Assert.Equal("2026-09-15", answer.LatestEvening);
    }

    /// <summary>An evening later than the one asked for is invisible, on the point-in-time rule.</summary>
    [Fact]
    public void An_evening_after_the_date_asked_for_is_not_counted()
    {
        Setup("2026-08-27", "AAA", "long", passedAll: false, generation: 1);
        Setup("2026-09-15", "BBB", "long", passedAll: false, generation: 1);

        ExperimentResponse answer = Read(new DateOnly(2026, 8, 31));

        Assert.Equal(1, answer.Evenings);
        Assert.Equal("2026-08-27", answer.LatestEvening);
        Assert.Equal(1, answer.Long!.PatternsRecorded);
    }

    /// <summary>A store with no detection in it says so rather than answering with noughts.</summary>
    [Fact]
    public void A_store_with_no_evening_says_so_rather_than_answering_with_noughts()
    {
        ExperimentResponse answer = Read(new DateOnly(2026, 8, 27));

        Assert.NotNull(answer.Absent);
        Assert.Equal(0, answer.Evenings);
    }
}
