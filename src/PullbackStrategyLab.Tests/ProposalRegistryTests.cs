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
/// The registry: the two kinds sent where each goes, and what a screen may say about a proposal.
///
/// <b>Every disposition is exercised, because five dispositions are five populations.</b> A rule
/// change that a screen could not judge, one it killed, one it let through, a signal request, an
/// abstention and a week with no answer are six different facts about a week, and a test that ran
/// one branch while the data took another is the eighth failure shape this corpus names.
///
/// <b>The screen is the real harness over a real store on every one of them.</b> What is authored is
/// the proposal, because a proposal is what a model said and no fixture can hold one; everything
/// downstream is shipped code.
/// see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal
/// see: Replay screens proposals and the forward paired test admits them
/// </summary>
public sealed class ProposalRegistryTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 12, 12, 30, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 9, 12);

    public ProposalRegistryTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
        SeedPackVersion();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private ProposalRegistry Registry() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions(),
            new ReplayHarness(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions()));

    // ---- the two kinds go to two places ---------------------------------------------------------

    [Fact]
    public void A_signal_request_is_a_build_task_and_never_reaches_the_screen()
    {
        File("req-1", "requested", requestedSignal: "pullback_volume_ratio", requestedAxis: "volume",
            mechanism: "healthy pullbacks happen on drying volume",
            evidence: "a-1,a-2");

        RegistryResult result = Registry().Register(Today);

        Assert.Equal(1, result.BuildTasks);
        Assert.Equal(0, result.Screened);
        Assert.Equal(0, result.Discarded);
        Assert.Equal(ProposalRegistry.BuildTask, StatusOf("req-1"));

        // The library is a hard ceiling on the proposal space and a request is what lifts it. There
        // is nothing to replay, because the signal it asks for does not exist yet.
        Assert.Empty(Screens());
    }

    [Fact]
    public void An_abstention_is_recorded_as_a_result_and_a_week_with_no_answer_is_not()
    {
        File("abs-1", "abstained", abstainedBecause: "no signal separates outcomes yet");
        File("out-1", "unavailable", unavailableBecause: "the subscription has lapsed");

        RegistryResult result = Registry().Register(Today);

        Assert.Equal(1, result.Abstentions);
        Assert.Equal(1, result.Unactionable);

        // The two reach different states, which is the whole reason the seat records five outcomes
        // rather than two: a considered decline and an outage are opposite facts about one empty
        // week, and a version that never abstains is a warning only if abstentions are counted.
        Assert.Equal(ProposalRegistry.Recorded, StatusOf("abs-1"));
        Assert.Equal(ProposalRegistry.Unactionable, StatusOf("out-1"));
        Assert.Empty(Screens());
    }

    // ---- what a screen may say --------------------------------------------------------------------

    [Fact]
    public void A_screen_over_a_population_that_selects_nothing_says_nothing_and_leaves_it_filed()
    {
        // The commonest case this lab can produce: the funnel passes a median of nought candidates a
        // night, so the baseline selects nothing and a candidate selecting nothing too has said
        // nothing rather than changed nothing.
        File("rule-1", "proposed");

        RegistryResult result = Registry().Register(Today);

        Assert.Equal(1, result.Inconclusive);
        Assert.Equal(0, result.Discarded);
        Assert.Equal(0, result.Screened);

        // Still filed, deliberately: it is read again as the store grows, and a proposal settled by
        // a screen that said nothing would be settled by a reading that never took place.
        Assert.Equal(ResearcherSeat.Filed, StatusOf("rule-1"));

        // And the screen is recorded whatever it said, which is the record that the evidence still
        // cannot separate anything.
        Assert.Equal(ReplayResult.Inconclusive, Assert.Single(Screens()));
    }

    [Fact]
    public void A_screen_is_recorded_against_its_proposal_with_the_population_it_read()
    {
        File("rule-1", "proposed");

        Registry().Register(Today);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT proposal_id, window_id, direction, verdict, refused_because
              FROM replay_result
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal("rule-1", reader.GetString(0));

        // Null rather than a sentinel: no holdout window has matured, and a screen over a window and
        // a screen over the accumulated store are different screens.
        Assert.True(reader.IsDBNull(1));

        Assert.Equal(SetupDirection.Long, reader.GetString(2));
        Assert.Equal(ReplayResult.Inconclusive, reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public void A_re_screen_is_a_second_reading_and_not_a_collision()
    {
        // The grain SCHEMA declared was proposal and window, which would have made this a primary
        // key collision against a writer with no update path at all: the second reading could not
        // have been recorded and the first would have gone on reading as current.
        File("rule-1", "proposed");

        Registry().Register(Today);

        // Still filed, because the screen was inconclusive, so the registry reads it again.
        _clock.Advance(TimeSpan.FromMinutes(7));
        Registry().Register(Today);

        Assert.Equal(2, Screens().Count);
    }

    [Fact]
    public void A_proposal_is_dispositioned_once_and_is_not_read_again()
    {
        File("abs-1", "abstained", abstainedBecause: "thin");

        Assert.Equal(1, Registry().Register(Today).Abstentions);

        _clock.Advance(TimeSpan.FromMinutes(7));

        // Nothing left at filed, so the second run has no queue.
        RegistryResult second = Registry().Register(Today);

        Assert.Equal(0, second.Filed);
        Assert.Equal(0, second.Abstentions);
    }

    // ---- what the store refuses -------------------------------------------------------------------

    [Fact]
    public void A_screen_has_no_verdict_that_means_admitted()
    {
        // Held by the store rather than by the stage. Replay is free and free tests are how you
        // overfit: only the forward paired test says a proposal is worth keeping.
        File("rule-1", "proposed");

        Assert.Throws<SqliteException>(() => InsertScreen("rule-1", verdict: "admitted"));
    }

    [Fact]
    public void The_store_refuses_a_refusal_that_read_rows_and_a_screen_that_carries_a_reason()
    {
        File("rule-1", "proposed");

        Assert.Throws<SqliteException>(() =>
            InsertScreen("rule-1", verdict: ReplayResult.Refused, sessionsRead: 3, because: "no"));

        Assert.Throws<SqliteException>(() =>
            InsertScreen("rule-1", verdict: ReplayResult.Killed, because: "a reason it should not have"));
    }

    [Fact]
    public void The_store_refuses_an_overlap_larger_than_the_sets_it_overlaps()
    {
        File("rule-1", "proposed");

        Assert.Throws<SqliteException>(() =>
            InsertScreen("rule-1", verdict: ReplayResult.Survived, baseline: 2, candidate: 2, both: 3));
    }

    [Fact]
    public void The_store_refuses_a_request_that_carries_a_change_and_a_proposal_that_asks_for_a_signal()
    {
        Assert.Throws<SqliteException>(() =>
            File("bad-1", "requested", requestedSignal: "x", requestedAxis: "volume",
                mechanism: "m", evidence: "a,b", gate: "dip-shape"));

        Assert.Throws<SqliteException>(() =>
            File("bad-2", "proposed", requestedSignal: "x"));
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private void SeedPackVersion()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pack_version
                (version, fingerprint, sections, signals_screened, signals_screened_count,
                 correction_form, correction_level, family_wise_threshold, model_identifier, created_at)
            VALUES (1, 'f', 's', 'x', 1, 'benjamini-hochberg', '0.05', '0.05', 'm', '2026-09-12T00:00:00.000Z')
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>One filed proposal, written straight at the store so a CHECK clause is what refuses it.</summary>
    private void File(
        string id,
        string outcome,
        string? gate = null,
        string? requestedSignal = null,
        string? requestedAxis = null,
        string? mechanism = null,
        string? evidence = null,
        string? abstainedBecause = null,
        string? unavailableBecause = null)
    {
        bool ruleChange = outcome == "proposed";

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO proposal
                (proposal_id, as_of, pack_version, pack_digest, transport, configured_model,
                 counts_toward_hit_rate, outcome, direction, gate, threshold_name, from_value,
                 to_value, family, requested_signal, requested_axis, mechanism, evidence_setup_ids,
                 refutation, observations_to_settle, abstained_because, unavailable_because,
                 cites_null_control, fails_pack_version, status, observed_at)
            VALUES
                (@id, '2026-09-12', 1, 'd', 'subscription', 'm', 1, @outcome, @direction, @gate,
                 @threshold, @from, @to, @family, @requested_signal, @requested_axis, @mechanism,
                 @evidence, @refutation, @observations, @abstained, @unavailable,
                 0, 0, 'filed', '2026-09-12T00:00:00.000Z')
            """;

        decimal inForce = SelectionRule.Long.Value(SelectionRule.MaximumRetrace);

        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@direction", ruleChange ? SetupDirection.Long : (object)DBNull.Value);
        command.Parameters.AddWithValue("@gate",
            gate ?? (ruleChange ? "dip-shape" : (object)DBNull.Value));
        command.Parameters.AddWithValue("@threshold",
            ruleChange ? SelectionRule.MaximumRetrace : (object)DBNull.Value);
        command.Parameters.AddWithValue("@from",
            ruleChange ? StoreText.PriceToStorageText(inForce) : (object)DBNull.Value);
        command.Parameters.AddWithValue("@to",
            ruleChange ? StoreText.PriceToStorageText(inForce - 0.05m) : (object)DBNull.Value);
        command.Parameters.AddWithValue("@family", ruleChange ? "selection" : (object)DBNull.Value);
        command.Parameters.AddWithValue("@requested_signal", (object?)requestedSignal ?? DBNull.Value);
        command.Parameters.AddWithValue("@requested_axis", (object?)requestedAxis ?? DBNull.Value);
        command.Parameters.AddWithValue("@mechanism",
            mechanism ?? (ruleChange ? "a shallower dip keeps the thrust intact" : (object)DBNull.Value));
        command.Parameters.AddWithValue("@evidence", (object?)evidence ?? DBNull.Value);
        command.Parameters.AddWithValue("@refutation",
            ruleChange ? "no better over a quarter" : (object)DBNull.Value);
        command.Parameters.AddWithValue("@observations", ruleChange ? 400 : (object)DBNull.Value);
        command.Parameters.AddWithValue("@abstained", (object?)abstainedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@unavailable", (object?)unavailableBecause ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    private void InsertScreen(
        string proposalId,
        string verdict,
        int sessionsRead = 0,
        string? because = null,
        int baseline = 0,
        int candidate = 0,
        int both = 0)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO replay_result
                (proposal_id, window_id, observed_at, as_of, direction, sessions_read, rows_examined,
                 baseline_selected, candidate_selected, both_selected, candidate_only, baseline_only,
                 unjudgeable, unmeasured_verdicts, disagreements, verdict, refused_because, elapsed_ms)
            VALUES
                (@id, NULL, @observed_at, '2026-09-12', 'long', @sessions, 0,
                 @baseline, @candidate, @both, 0, 0, 0, 0, 0, @verdict, @because, 1)
            """;

        command.Parameters.AddWithValue("@id", proposalId);
        command.Parameters.AddWithValue("@observed_at", $"2026-09-12T12:{Random.Shared.Next(10, 59)}:00.000Z");
        command.Parameters.AddWithValue("@sessions", sessionsRead);
        command.Parameters.AddWithValue("@baseline", baseline);
        command.Parameters.AddWithValue("@candidate", candidate);
        command.Parameters.AddWithValue("@both", both);
        command.Parameters.AddWithValue("@verdict", verdict);
        command.Parameters.AddWithValue("@because", (object?)because ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    private string StatusOf(string proposalId)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();

        return ProposalReader.Read(connection, Today, SessionBoundaries.UsEquities)
            .Single(p => p.ProposalId == proposalId)
            .Status;
    }

    private IReadOnlyList<string> Screens()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT verdict FROM replay_result";

        var verdicts = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            verdicts.Add(reader.GetString(0));
        }

        return verdicts;
    }
}
