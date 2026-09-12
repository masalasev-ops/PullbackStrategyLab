using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Seats;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A refusal the design requires and a stage that broke arrive at one catch, and 7.15 tells them
/// apart. The first live instance was the pack of Saturday 2026-09-12, the first morning the slot
/// ever ran: it refused because generation 1 has no selection rule written down, recorded a
/// failure, and exited 1, so the scheduler surfaced a red beside a correct outcome.
///
/// <b>Both halves are exercised, because only the pair carries the property.</b> A test of the
/// designed half alone passes just as well against a stage that calls every refusal partial, which
/// is the defect arriving from the other direction.
/// </summary>
public sealed class RefusalIsNotAFailureTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock =
        new(SessionBoundaries.At(new DateOnly(2026, 9, 12), new TimeOnly(8, 20), SessionBoundaries.UsEquities));

    private static readonly DateOnly Saturday = new(2026, 9, 12);

    public RefusalIsNotAFailureTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        using SqliteConnection seed = _connections.OpenWrite();
        TestVersions.SeedBaseline(seed);
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions(string transport = SeatTransport.Subscription) =>
        Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            Researcher = new ResearcherOptions { Transport = transport },
        });

    private ContextPacker Packer() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    private void CloseGenerationZero() =>
        new GenerationCloser(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions())
            .Close("V1", GenerationCloser.GenerationOneDefinition, GenerationCloser.GenerationOneTarget);

    private string RefusedRunOutcome()
    {
        using SqliteConnection reading = _connections.OpenReadOnly();
        using SqliteCommand command = reading.CreateCommand();
        command.CommandText =
            "SELECT outcome FROM pack_run WHERE refused_because IS NOT NULL ORDER BY observed_at DESC LIMIT 1;";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
               ?? "no refused run";
    }

    private int RunRowsFor(string stage)
    {
        using SqliteConnection reading = _connections.OpenReadOnly();
        using SqliteCommand command = reading.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM run_log WHERE stage = @stage;";
        command.Parameters.AddWithValue("@stage", stage);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Generation 1 in force, so the "Rule in force" section has no rule to describe and the pack
    /// refuses. That is the decision working, for as long as the condition holds, so it is recorded
    /// as partial and the stage exits 0.
    /// see: Generation 1 opens with no version admissible in either family, and what reopens each is named
    /// </summary>
    [Fact]
    public void A_refusal_the_design_requires_is_partial_and_the_stage_exits_zero()
    {
        CloseGenerationZero();

        PackResult refused = Packer().Build(Saturday);

        Assert.NotNull(refused.RefusedBecause);
        Assert.Contains("generation 1", refused.RefusedBecause, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Partial, refused.Outcome);
        Assert.Equal("partial", RefusedRunOutcome());

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, Packer().Run([Saturday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)]));
    }

    /// <summary>
    /// A section that cannot read what it reads is a fault: something is wrong and the run stays red
    /// until somebody looks. The store loses the table the loss-taxonomy section reads, which is the
    /// one shape of breakage a test can produce without a stub standing in for the stage.
    /// </summary>
    [Fact]
    public void A_section_that_broke_is_still_a_failure_and_the_stage_exits_one()
    {
        using (SqliteConnection damage = _connections.OpenWrite())
        {
            using SqliteCommand command = damage.CreateCommand();
            command.CommandText = "DROP TABLE loss_class;";
            command.ExecuteNonQuery();
        }

        PackResult broke = Packer().Build(Saturday);

        Assert.NotNull(broke.RefusedBecause);
        Assert.DoesNotContain("generation 1", broke.RefusedBecause, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Failed, broke.Outcome);
        Assert.Equal("failed", RefusedRunOutcome());

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, Packer().Run([Saturday.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)]));
    }

    /// <summary>
    /// The seat cuts the pack itself and refuses to ask when the cut refuses, and until 7.15 it
    /// returned without touching the store, so the week left no `run_log` row at all and read as a
    /// slot that never fired. On the morning this was found the store held two `build-pack` rows
    /// and none of the seat's.
    /// </summary>
    [Fact]
    public void The_seat_records_its_week_even_though_it_asked_nothing()
    {
        CloseGenerationZero();

        var transport = new RefusingTransport();
        var seat = new ResearcherSeat(
            _connections, new RunLogger(_clock, LabOptions(transport.Transport)), _clock,
            LabOptions(transport.Transport), Packer(), [transport]);

        SeatResult result = seat.Ask(Saturday);

        Assert.NotNull(result.PackRefusedBecause);
        Assert.False(transport.WasAsked);
        Assert.Equal(1, RunRowsFor(ResearcherSeat.Name));
    }

    /// <summary>A transport that records whether anything reached it, and refuses if anything did.</summary>
    private sealed class RefusingTransport : IResearchTransport
    {
        public bool WasAsked { get; private set; }

        public string Transport => SeatTransport.Subscription;

        public string ConfiguredModel => "claude-opus-5";

        public SeatAnswer Ask(string body, CancellationToken cancellationToken)
        {
            WasAsked = true;
            throw new InvalidOperationException("nothing should reach the transport when the pack refused");
        }
    }
}
