using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker;
using PullbackStrategyLab.Worker.Seats;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The researcher seat: three transports behind one narrow interface, and what each has to hold.
///
/// <b>Every outcome is exercised, because four outcomes are four populations.</b> A guard over a set
/// is applied to that set, and a proof that ran one branch while the data took another is the eighth
/// failure shape this corpus names. The seat has four: a proposal, an abstention, an answer that
/// could not be read, and a week it could not be asked. All four are proved here over answers
/// written by hand, which is the only way three of them can be reached at all.
///
/// <b>The empty tool set is proved twice and the second proof is the one that matters.</b> A test
/// over the arguments the seat builds says what it asked for. A test over the tool list a session
/// reported says what it got, and that is the half a tool arriving from configuration the seat does
/// not control would break.
/// see: The AI writes only to the proposal store
/// see: The subscription seat is the Claude Code CLI in print mode, and the empty tool set is read back rather than asserted
/// </summary>
public sealed class ResearcherSeatTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 12, 12, 30, 0, TimeSpan.Zero));

    private static readonly DateOnly Today = new(2026, 9, 12);

    public ResearcherSeatTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
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

    private ResearcherSeat Seat(IResearchTransport transport) =>
        new(_connections, new RunLogger(_clock, LabOptions(transport.Transport)), _clock,
            LabOptions(transport.Transport), Packer(), [transport]);

    // ---- the empty tool set, which is the whole of the guard on a hard rule ---------------------

    [Fact]
    public void The_subscription_seat_is_built_with_an_empty_tool_set()
    {
        IReadOnlyList<string> arguments = new SubscriptionTransport(LabOptions()).Arguments();

        // Not --allowedTools, which auto-approves rather than restricts: passing it nothing would
        // leave the full tool set in the model's context and merely unapproved.
        Assert.DoesNotContain("--allowedTools", arguments);

        int at = arguments.ToList().IndexOf("--disallowedTools");
        Assert.True(at >= 0, "the seat must remove every tool from the model's context");
        Assert.Equal(SubscriptionTransport.EmptyToolSet, arguments[at + 1]);

        // No MCP server except those named by --mcp-config, and none is named.
        Assert.Contains("--strict-mcp-config", arguments);

        // The stream format is chosen because it is the only one carrying the session's own tool
        // list, which is what the assertion below reads. A change to `json` would silently remove
        // the guard while leaving every other assertion here passing.
        Assert.Contains("stream-json", arguments);
    }

    [Fact]
    public void A_session_reporting_a_tool_is_refused_rather_than_read()
    {
        SeatAnswer answer = SubscriptionTransport.ReadStream(
            Stream(tools: ["Bash"], result: "{\"outcome\":\"abstained\",\"abstained_because\":\"thin\"}"),
            errors: string.Empty, exitCode: 0, ResearcherOptions.PinnedModel);

        Assert.False(answer.Answered);
        Assert.Contains("reported 1 tool(s)", answer.UnavailableBecause, StringComparison.Ordinal);
        Assert.Contains("Bash", answer.UnavailableBecause, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_reporting_no_tool_list_at_all_is_refused_on_the_same_footing()
    {
        // An unreadable guard is not a passed one. A stream with no init event is the shape a future
        // version of the CLI could produce, and reading the answer anyway would quietly turn the
        // guard off rather than fail.
        SeatAnswer answer = SubscriptionTransport.ReadStream(
            "{\"type\":\"result\",\"result\":\"{}\",\"is_error\":false}",
            errors: string.Empty, exitCode: 0, ResearcherOptions.PinnedModel);

        Assert.False(answer.Answered);
        Assert.Contains("reported no tool list", answer.UnavailableBecause, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_with_an_empty_tool_set_answers_and_reports_what_served_it()
    {
        SeatAnswer answer = SubscriptionTransport.ReadStream(
            Stream(tools: [], result: "a proposal"), errors: string.Empty, exitCode: 0,
            ResearcherOptions.PinnedModel);

        Assert.True(answer.Answered);
        Assert.Equal("a proposal", answer.Text);
        Assert.Equal("claude-opus-5-20260101", answer.ServedModel);
        Assert.Contains(answer.Pins, p => p.Name == SeatPin.ToolsReported && p.Value == "none");
    }

    [Fact]
    public void A_failed_run_is_unavailable_rather_than_an_answer()
    {
        SeatAnswer answer = SubscriptionTransport.ReadStream(
            Stream(tools: [], result: "Credit balance is too low"), errors: string.Empty, exitCode: 1,
            ResearcherOptions.PinnedModel);

        Assert.False(answer.Answered);
        Assert.Equal("Credit balance is too low", answer.UnavailableBecause);
    }

    [Fact]
    public void The_banned_environment_variable_is_absent_from_this_process()
    {
        // The live assertion, which is about the machine the suite runs on rather than about any
        // argument. On the subscription transport its presence silently defeats plan authentication
        // and bills API rates; on the API transport it is a second source for one credential.
        Assert.True(
            string.IsNullOrEmpty(
                Environment.GetEnvironmentVariable(ResearcherOptions.BannedEnvironmentVariable)),
            $"{ResearcherOptions.BannedEnvironmentVariable} is set in this environment and the seat "
            + "refuses to ask with it present.");
    }

    [Fact]
    public void The_seat_refuses_to_run_where_the_banned_variable_is_set_and_no_other_stage_does()
    {
        Assert.NotNull(Program.WhyTheSeatCannotRun(ResearcherSeat.Name, "sk-ant-anything"));
        Assert.Null(Program.WhyTheSeatCannotRun(ResearcherSeat.Name, null));
        Assert.Null(Program.WhyTheSeatCannotRun(ResearcherSeat.Name, "   "));

        // The fault is about which credential answers an ask, so it can only happen on a night the
        // seat is asked. Refusing the bar ingest over it would stop a night's evidence for a
        // variable that has nothing to do with the vendor.
        Assert.Null(Program.WhyTheSeatCannotRun(ContextPacker.Name, "sk-ant-anything"));
    }

    [Fact]
    public void Neither_hosted_transport_sends_a_tools_field()
    {
        // On the API path the property is structural: there is no field to disable and nothing a
        // later edit could re-enable without adding one. The assertion is the absence, which is what
        // such an edit would have to break.
        Assert.DoesNotContain("tools", new ApiTransport(new HttpClient(), LabOptions()).Body("pack").Keys);
        Assert.DoesNotContain("tools", new LocalTransport(new HttpClient(), LabOptions()).Body("pack").Keys);

        Assert.Equal(0, new LocalTransport(new HttpClient(), LabOptions()).Body("pack")["temperature"]);
    }

    // ---- the four outcomes, each over the population it governs ---------------------------------

    [Fact]
    public void A_proposal_is_read_filed_and_carries_its_three_recordings()
    {
        SeatResult result = Ask(new StubTransport(SeatTransport.Subscription, Proposal()));

        Assert.Equal("proposed", result.Outcome);
        Assert.Equal(ResearcherOptions.PinnedModel, result.ConfiguredModel);
        Assert.Equal("claude-opus-5-20260101", result.ServedModel);
        Assert.True(result.CountsTowardHitRate);

        StoredProposal filed = Single();
        Assert.Equal("proposed", filed.Outcome);
        Assert.Equal(SetupDirection.Long, filed.Direction);
        Assert.Equal(SelectionRule.MaximumRetrace, filed.ThresholdName);
        Assert.Equal("subscription", filed.Transport);
        Assert.Equal("claude-opus-5-20260101", filed.ServedModel);
        Assert.Equal(ResearcherSeat.Filed, filed.Status);
    }

    [Fact]
    public void An_abstention_is_a_recorded_result_and_carries_no_change()
    {
        SeatResult result = Ask(new StubTransport(SeatTransport.Subscription,
            """{"outcome":"abstained","abstained_because":"no signal separates outcomes yet"}"""));

        Assert.Equal("abstained", result.Outcome);

        StoredProposal filed = Single();
        Assert.Equal("abstained", filed.Outcome);
        Assert.Null(filed.Direction);
        Assert.Null(filed.ThresholdName);
        Assert.Equal("no signal separates outcomes yet", filed.AbstainedBecause);
    }

    [Fact]
    public void An_answer_that_is_not_the_agreed_document_is_recorded_with_the_text_it_returned()
    {
        SeatResult result = Ask(new StubTransport(SeatTransport.Subscription,
            "I had a look and I think you should loosen the retrace gate a bit."));

        Assert.Equal("unreadable", result.Outcome);
        Assert.NotEmpty(result.Problems);

        StoredProposal filed = Single();
        Assert.Equal("unreadable", filed.Outcome);
        Assert.Contains("loosen the retrace gate", filed.AnswerText, StringComparison.Ordinal);
        Assert.NotNull(filed.AnswerProblems);
    }

    [Fact]
    public void A_week_the_seat_could_not_be_asked_is_a_row_and_not_a_gap()
    {
        SeatResult result = Ask(StubTransport.Unavailable("the subscription has lapsed"));

        Assert.Equal("unavailable", result.Outcome);

        StoredProposal filed = Single();
        Assert.Equal("unavailable", filed.Outcome);
        Assert.Equal("the subscription has lapsed", filed.UnavailableBecause);
        Assert.Null(filed.Direction);
    }

    // ---- the tripwire, scoped to what a proposal rests on ---------------------------------------

    [Fact]
    public void An_abstention_naming_the_planted_null_fails_no_pack_version()
    {
        // The 6.4 finding, reproduced and closed. A model shown the near-empty pack abstained
        // correctly and put `day_of_month` in the gate field because the schema required one; under
        // the tripwire as written that correct abstention failed pack version 1.
        SeatResult result = Ask(new StubTransport(SeatTransport.Subscription,
            $$"""
            {"outcome":"abstained",
             "abstained_because":"the only separation is in {{SignalLibrary.NullControl}}, which is not a mechanism"}
            """));

        Assert.Equal("abstained", result.Outcome);
        Assert.False(result.CitesNullControl);
        Assert.False(result.FailsPackVersion);
        Assert.False(Single().FailsPackVersion);
    }

    [Fact]
    public void A_proposal_resting_on_the_planted_null_fails_the_pack_version()
    {
        SeatResult result = Ask(new StubTransport(SeatTransport.Subscription,
            Proposal(signals: [SignalLibrary.NullControl])));

        Assert.True(result.CitesNullControl);
        Assert.True(result.FailsPackVersion);
        Assert.True(Single().FailsPackVersion);
    }

    [Fact]
    public void A_stopgap_resting_on_the_planted_null_fails_no_version_and_counts_toward_nothing()
    {
        // A competent reader citing a meaningless signal indicts the pack; a weaker reader citing it
        // indicts itself, and the rule as written cannot tell the two apart.
        SeatResult result = Ask(new StubTransport(SeatTransport.Local,
            Proposal(signals: [SignalLibrary.NullControl])));

        Assert.True(result.CitesNullControl);
        Assert.False(result.FailsPackVersion);
        Assert.False(result.CountsTowardHitRate);

        StoredProposal filed = Single();
        Assert.False(filed.FailsPackVersion);
        Assert.False(filed.CountsTowardHitRate);
    }

    [Fact]
    public void A_proposal_moving_a_threshold_the_null_control_is_frozen_against_rests_on_it()
    {
        // The second half of the tripwire's subject: the signals a proposal cites, and the signals
        // the threshold it moves is replayed against. A proposal citing nothing while moving a
        // threshold read off the control is resting on it just as squarely.
        var document = new ProposalDocument
        {
            Outcome = ProposalDocument.Proposed,
            Change = new ProposedChange(
                SetupDirection.Long, "dip-shape", SelectionRule.MaximumRetrace, 0.5m, 0.45m),
        };

        SelectionRule rule = SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.5m) with
        {
            Thresholds =
            [
                new(SelectionRule.MaximumRetrace, "dip-shape", 0.5m, ThresholdFamily.Selection,
                    [SignalLibrary.NullControl]),
            ],
        };

        Assert.Contains(SignalLibrary.NullControl, document.SignalsRestedOn(rule));
        Assert.True(PackVersions.CitesTheNullControl(document.SignalsRestedOn(rule)));
    }

    [Fact]
    public void The_local_transport_is_the_only_one_excluded_from_the_hit_rate()
    {
        Assert.True(SeatTransport.CountsTowardHitRate(SeatTransport.Subscription));
        Assert.True(SeatTransport.CountsTowardHitRate(SeatTransport.Api));
        Assert.False(SeatTransport.CountsTowardHitRate(SeatTransport.Local));
    }

    // ---- the store's own clauses ----------------------------------------------------------------

    [Fact]
    public void The_store_refuses_an_abstention_that_carries_a_change()
    {
        Assert.Throws<SqliteException>(() => Insert(
            outcome: "abstained", abstainedBecause: "thin", gate: "dip-shape"));
    }

    [Fact]
    public void The_store_refuses_a_stopgap_that_fails_a_version_or_counts_toward_the_hit_rate()
    {
        Assert.Throws<SqliteException>(() => Insert(
            outcome: "unavailable", unavailableBecause: "off", transport: "local", countsTowardHitRate: 1));

        Assert.Throws<SqliteException>(() => Insert(
            outcome: "unavailable", unavailableBecause: "off", transport: "local",
            countsTowardHitRate: 0, failsPackVersion: 1));
    }

    [Fact]
    public void The_store_refuses_a_version_failed_by_a_week_with_no_proposal_in_it()
    {
        Assert.Throws<SqliteException>(() => Insert(
            outcome: "unreadable", answerProblems: "prose", citesNullControl: 1, failsPackVersion: 1));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>One ask over a store holding a signal library and a cut pack.</summary>
    private SeatResult Ask(IResearchTransport transport)
    {
        new SignalAdmissionTest(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions())
            .Admit(Today);

        return Seat(transport).Ask(Today);
    }

    /// <summary>A well-formed proposal against the rule the baseline actually holds.</summary>
    private static string Proposal(IReadOnlyList<string>? signals = null)
    {
        decimal inForce = SelectionRule.Long.Value(SelectionRule.MaximumRetrace);

        var document = new
        {
            outcome = "proposed",
            family = "selection",
            change = new
            {
                direction = SetupDirection.Long,
                gate = "dip-shape",
                threshold_name = SelectionRule.MaximumRetrace,
                from = inForce,
                to = inForce - 0.05m,
            },
            mechanism = "a shallower dip keeps the thrust intact, so the entry is nearer the trend",
            evidence_setup_ids = new[] { "AAPL-2026-08-20-long" },
            evidence_signals = signals ?? ["retrace_depth"],
            refutation = "the shallower band does no better over a full quarter",
            observations_to_settle = 400,
        };

        return JsonSerializer.Serialize(document);
    }

    /// <summary>One CLI stream, with whatever tool list and result the test needs.</summary>
    private static string Stream(IReadOnlyList<string> tools, string result)
    {
        string init = JsonSerializer.Serialize(new
        {
            type = "system",
            subtype = "init",
            model = "claude-opus-5-20260101",
            tools,
        });

        string final = JsonSerializer.Serialize(new { type = "result", result, is_error = false });

        return init + "\n" + final + "\n";
    }

    private StoredProposal Single()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<StoredProposal> filed = ProposalReader.Read(connection, Today, SessionBoundaries.UsEquities);
        return Assert.Single(filed);
    }

    /// <summary>A row written straight at the store, so a CHECK clause is what refuses it.</summary>
    private void Insert(
        string outcome,
        string transport = "subscription",
        int countsTowardHitRate = 1,
        string? gate = null,
        string? abstainedBecause = null,
        string? unavailableBecause = null,
        string? answerProblems = null,
        int citesNullControl = 0,
        int failsPackVersion = 0)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = """
                INSERT OR IGNORE INTO pack_version
                    (version, fingerprint, sections, signals_screened, signals_screened_count,
                     correction_form, correction_level, family_wise_threshold, model_identifier, created_at)
                VALUES (1, 'f', 's', 'x', 1, 'benjamini-hochberg', '0.05', '0.05', 'm', '2026-09-12T12:00:00Z')
                """;
            version.ExecuteNonQuery();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO proposal
                (proposal_id, as_of, pack_version, pack_digest, transport, configured_model,
                 counts_toward_hit_rate, outcome, gate, abstained_because, unavailable_because,
                 answer_problems, cites_null_control, fails_pack_version, status, observed_at)
            VALUES
                (@id, '2026-09-12', 1, 'd', @transport, 'm', @counts, @outcome, @gate,
                 @abstained, @unavailable, @problems, @cites, @fails, 'filed', '2026-09-12T12:00:00Z')
            """;

        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@transport", transport);
        command.Parameters.AddWithValue("@counts", countsTowardHitRate);
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@gate", (object?)gate ?? DBNull.Value);
        command.Parameters.AddWithValue("@abstained", (object?)abstainedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@unavailable", (object?)unavailableBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@problems", (object?)answerProblems ?? DBNull.Value);
        command.Parameters.AddWithValue("@cites", citesNullControl);
        command.Parameters.AddWithValue("@fails", failsPackVersion);

        command.ExecuteNonQuery();
    }

    /// <summary>A seat that answers whatever the test hands it, so every branch has a population.</summary>
    private sealed class StubTransport : IResearchTransport
    {
        private readonly string? _text;
        private readonly string? _because;

        public StubTransport(string transport, string text)
        {
            Transport = transport;
            _text = text;
        }

        private StubTransport(string transport, string? text, string? because)
        {
            Transport = transport;
            _text = text;
            _because = because;
        }

        public static StubTransport Unavailable(string because) =>
            new(SeatTransport.Subscription, null, because);

        public string Transport { get; }

        public string ConfiguredModel => ResearcherOptions.PinnedModel;

        public SeatAnswer Ask(string pack, CancellationToken cancellationToken) =>
            _because is not null
                ? SeatAnswer.Unavailable(Transport, ConfiguredModel, _because)
                : SeatAnswer.Answer(Transport, ConfiguredModel, "claude-opus-5-20260101", _text!);
    }
}
