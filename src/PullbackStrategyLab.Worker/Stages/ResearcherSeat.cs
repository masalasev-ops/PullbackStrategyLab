using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Asks the model once a week and files the answer.
///
/// <b>It writes to the proposal store and to nothing else.</b> That is a hard rule and it is held
/// by `writer-ownership` rather than described here: nothing this stage can reach scores its own
/// output, and the AI's output is a document a person reads.
/// see: The AI writes only to the proposal store
///
/// <b>It cuts the pack it asks against rather than reading a previous cut.</b> The pack body is not
/// stored, only its digest, so a seat reading last night's row would be asking against a document it
/// had to rebuild anyway. Cutting it here means the digest on the proposal is the digest of the body
/// actually shown, which is what makes "this proposal was made against this pack" checkable rather
/// than asserted. It is byte-stable, so the cut is the same document the packer's own slot produced.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
///
/// <b>A week with no rule change has three causes and they are three outcomes.</b> A seat that could
/// not be asked, a seat whose answer is not the agreed document, and a seat that read the evidence
/// and declined are different facts about the lab. A fifth outcome from 6.6 is a signal request,
/// which is not a week with no proposal at all: it is the second kind
/// (see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal). One column reading "no proposal" for all three
/// would be the silence the loop exists to break, and the first of them is a fact about the running
/// lab rather than about the build, so it is also shown on the morning it happens.
/// see: Abstention is a valid recorded proposal outcome
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class ResearcherSeat
{
    public const string Name = "ask-researcher";

    /// <summary>What a proposal's status is when it is filed, and the only one this stage writes.</summary>
    public const string Filed = "filed";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;
    private readonly ContextPacker _packer;
    private readonly IReadOnlyList<IResearchTransport> _transports;

    public ResearcherSeat(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options,
        ContextPacker packer,
        IEnumerable<IResearchTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transports);

        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
        _packer = packer;
        _transports = [.. transports];
    }

    /// <summary><c>ask-researcher [as-of]</c>.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        SeatResult result = Ask(asOf);

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, transport {result.Transport}, "
            + $"model {result.ConfiguredModel}, served {result.ServedModel ?? "unreported"}");

        if (result.PackRefusedBecause is string noPack)
        {
            Console.WriteLine($"{Name}: nothing was asked, no pack was built, {noPack}");
        }
        else
        {
            Console.WriteLine(
                $"{Name}: pack version {result.PackVersion}, digest {result.PackDigest[..Math.Min(12, result.PackDigest.Length)]}");
            Console.WriteLine($"{Name}: outcome {result.Outcome}");
        }

        if (result.MatchedEarlierCut == false)
        {
            Console.WriteLine(
                $"{Name}: this cut does not match the one the pack slot made this morning, so the "
                + "packer is not byte-stable over the live store");
        }

        if (result.UnavailableBecause is string why)
        {
            Console.WriteLine($"{Name}: the seat could not be asked, {why}");
        }

        foreach (string problem in result.Problems)
        {
            Console.WriteLine($"{Name}: the answer is not a proposal, {problem}");
        }

        if (result.CitesNullControl)
        {
            Console.WriteLine(
                result.FailsPackVersion
                    ? $"{Name}: the proposal rests on the planted null, so pack version {result.PackVersion} FAILS"
                    : $"{Name}: the answer names the planted null and does not rest on it, so no version fails");
        }

        if (!result.CountsTowardHitRate)
        {
            Console.WriteLine(
                $"{Name}: a stopgap seat, so this answer is excluded from the pack-version hit rate");
        }

        RunOutcome outcome = result.Outcome is "proposed" or "abstained"
            ? RunOutcome.Clean
            : RunOutcome.Partial;

        Console.WriteLine($"{Name}: {outcome.ToStorageText()}, {result.RowsWritten} rows");

        return 0;
    }

    /// <summary>One week's ask, from the cut to the filed row.</summary>
    public SeatResult Ask(DateOnly asOf)
    {
        IResearchTransport transport = Selected();

        // What the packer's own slot cut this morning, read before this stage cuts again. The two
        // digests are compared below and the comparison is the point of reading it.
        string? cutEarlier;

        using (SqliteConnection reading = _connections.OpenReadOnly())
        {
            cutEarlier = PackVersionReader.LatestRun(reading, asOf, _options.SessionZone)?.BodyDigest;
        }

        // Cut first, and refuse to ask at all if the pack could not be built. A model shown a pack
        // missing a section would be judged under a correction computed over signals that section
        // never screened.
        PackResult pack = _packer.Build(asOf);

        if (pack.RefusedBecause is string refused)
        {
            return SeatResult.NoPack(transport, asOf, refused);
        }

        string body = pack.Pack.Render();

        SeatAnswer answer = transport.Ask(body, CancellationToken.None);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "proposal");

        SeatResult result = Read(answer, pack, run.StartedAt) with
        {
            // **Byte-stability, observed in the running lab rather than claimed of the build.** The
            // packer's slot cut this pack an ask earlier over the same store state, and this stage
            // cut it again; the two digests agree or the packer is not byte-stable here, whatever
            // the golden fixture says. A green report is a statement about the build and never about
            // the lab, so the one place this property can be seen live is the one place it is read.
            // see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
            MatchedEarlierCut = cutEarlier is null ? null : cutEarlier == pack.BodyDigest,
        };

        using (SqliteTransaction write = connection.BeginTransaction())
        {
            Insert(connection, write, result);
            write.Commit();
        }

        RunSummary summary = run.Complete(
            result.Outcome is "proposed" or "abstained" ? RunOutcome.Clean : RunOutcome.Partial);

        return result with { RowsWritten = summary.RowsWritten };
    }

    /// <summary>
    /// The transport configuration selects, or a refusal naming what is configured.
    ///
    /// Every one of the three is constructed whether or not it is selected, which is the provision
    /// the operator ruled for: the day the subscription stops is a configuration value, and a path
    /// that only existed when chosen would be a path nobody could switch to in a hurry.
    /// </summary>
    public IResearchTransport Selected() =>
        _transports.FirstOrDefault(t =>
            string.Equals(t.Transport, _options.Researcher.Transport, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"the researcher transport is configured as \"{_options.Researcher.Transport}\" and the "
            + $"seat holds {string.Join(", ", _transports.Select(t => t.Transport))}.");

    /// <summary>
    /// What one answer came to, before anything is written.
    ///
    /// Pure and public, so every branch is proved over answers written by hand rather than over the
    /// one branch a live call happens to take. The four outcomes are four populations and a proof
    /// that exercised one of them would be a guard over a set it never saw.
    /// </summary>
    public static SeatResult Read(SeatAnswer answer, PackResult pack, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(pack);

        var seed = new SeatResult
        {
            ProposalId = ProposalId(pack.AsOf, observedAt, answer.Transport),
            AsOf = pack.AsOf,
            PackVersion = pack.Version,
            PackDigest = pack.BodyDigest,
            Transport = answer.Transport,
            ConfiguredModel = answer.ConfiguredModel,
            ServedModel = answer.ServedModel,
            CountsTowardHitRate = SeatTransport.CountsTowardHitRate(answer.Transport),
            Pins = answer.Pins,
            AnswerText = answer.Text,
            ObservedAt = observedAt,
            Outcome = "unavailable",
        };

        if (!answer.Answered)
        {
            return seed with { UnavailableBecause = answer.UnavailableBecause };
        }

        ProposalParse parse = ProposalDocument.Parse(answer.Text);

        if (!parse.Read)
        {
            return seed with { Outcome = "unreadable", Problems = parse.Problems };
        }

        ProposalDocument document = parse.Document!;
        IReadOnlyList<string> problems = document.Problems(SelectionRule.Long, SelectionRule.Short);

        if (problems.Count > 0)
        {
            return seed with { Outcome = "unreadable", Problems = problems };
        }

        // The tripwire, over what the proposal rests on rather than over the document. An abstention
        // rests on nothing and returns an empty set, so a correct abstention that named the control
        // in its reason cannot fail a version.
        // see: One meaningless signal is planted in the conditional tables
        SelectionRule rule = document.Change is null
            ? SelectionRule.Long
            : SelectionRule.For(document.Change.Direction);

        bool cites = PackVersions.CitesTheNullControl(document.SignalsRestedOn(rule));

        return seed with
        {
            // The document's own outcome rather than a two-way split on abstention. It was that
            // split until 6.6 added the second kind, and a signal request would have been filed as
            // a rule change carrying none of the five change fields, which the store refuses.
            Outcome = document.Outcome,
            Document = document,
            CitesNullControl = cites,

            // A stopgap citing the control indicts itself rather than the pack, and the rule cannot
            // tell a weak reader from a rummaging one, so the version is untouched.
            // see: A local stopgap seat is recorded, and it is excluded from the pack-version hit rate
            FailsPackVersion = cites
                && !document.IsAbstention
                && SeatTransport.CountsTowardHitRate(answer.Transport),
        };
    }

    /// <summary>
    /// The proposal's identity: the week, the instant and the transport.
    ///
    /// The instant is in it because a week can be asked twice, and the transport because a week
    /// asked on two transports is two answers from two readers and not one answer twice.
    /// </summary>
    public static string ProposalId(DateOnly asOf, DateTimeOffset observedAt, string transport) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{asOf:yyyy-MM-dd}-{transport}-{observedAt.ToUnixTimeSeconds()}");

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, SeatResult result)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO proposal
                (proposal_id, as_of, pack_version, pack_digest, transport, configured_model,
                 served_model, counts_toward_hit_rate, outcome, direction, gate, threshold_name,
                 from_value, to_value, family, mechanism, evidence_setup_ids, evidence_signals,
                 refutation, observations_to_settle, abstained_because, unavailable_because,
                 answer_problems, answer_text, pins, cites_null_control, fails_pack_version,
                 status, observed_at, requested_signal, requested_axis)
            VALUES
                (@proposal_id, @as_of, @pack_version, @pack_digest, @transport, @configured_model,
                 @served_model, @counts_toward_hit_rate, @outcome, @direction, @gate, @threshold_name,
                 @from_value, @to_value, @family, @mechanism, @evidence_setup_ids, @evidence_signals,
                 @refutation, @observations_to_settle, @abstained_because, @unavailable_because,
                 @answer_problems, @answer_text, @pins, @cites_null_control, @fails_pack_version,
                 @status, @observed_at, @requested_signal, @requested_axis)
            """;

        ProposalDocument? document = result.Document;
        ProposedChange? change = document?.Change;

        command.Parameters.AddWithValue("@proposal_id", result.ProposalId);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(result.AsOf));
        command.Parameters.AddWithValue("@pack_version", result.PackVersion);
        command.Parameters.AddWithValue("@pack_digest", result.PackDigest);
        command.Parameters.AddWithValue("@transport", result.Transport);
        command.Parameters.AddWithValue("@configured_model", result.ConfiguredModel);
        command.Parameters.AddWithValue("@served_model", (object?)result.ServedModel ?? DBNull.Value);
        command.Parameters.AddWithValue("@counts_toward_hit_rate", result.CountsTowardHitRate ? 1 : 0);
        command.Parameters.AddWithValue("@outcome", result.Outcome);
        command.Parameters.AddWithValue("@direction", (object?)change?.Direction ?? DBNull.Value);
        command.Parameters.AddWithValue("@gate", (object?)change?.Gate ?? DBNull.Value);
        command.Parameters.AddWithValue("@threshold_name", (object?)change?.ThresholdName ?? DBNull.Value);
        command.Parameters.AddWithValue("@from_value",
            change is null ? DBNull.Value : StoreText.PriceToStorageText(change.From));
        command.Parameters.AddWithValue("@to_value",
            change is null ? DBNull.Value : StoreText.PriceToStorageText(change.To));
        command.Parameters.AddWithValue("@family", (object?)document?.Family ?? DBNull.Value);
        command.Parameters.AddWithValue("@mechanism", (object?)document?.Mechanism ?? DBNull.Value);
        command.Parameters.AddWithValue("@evidence_setup_ids",
            document is null || document.EvidenceSetupIds.Count == 0
                ? DBNull.Value
                : string.Join(",", document.EvidenceSetupIds));
        command.Parameters.AddWithValue("@evidence_signals",
            document is null || document.EvidenceSignals.Count == 0
                ? DBNull.Value
                : string.Join(",", document.EvidenceSignals));
        command.Parameters.AddWithValue("@refutation", (object?)document?.Refutation ?? DBNull.Value);
        command.Parameters.AddWithValue("@observations_to_settle",
            (object?)document?.ObservationsToSettle ?? DBNull.Value);
        command.Parameters.AddWithValue("@abstained_because",
            (object?)document?.AbstainedBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@unavailable_because",
            (object?)result.UnavailableBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@answer_problems",
            result.Problems.Count == 0 ? DBNull.Value : string.Join("; ", result.Problems));
        command.Parameters.AddWithValue("@answer_text", (object?)result.AnswerText ?? DBNull.Value);
        command.Parameters.AddWithValue("@pins",
            result.Pins.Count == 0
                ? DBNull.Value
                : string.Join("\n", result.Pins.Select(p => $"{p.Name}={p.Value}")));
        command.Parameters.AddWithValue("@cites_null_control", result.CitesNullControl ? 1 : 0);
        command.Parameters.AddWithValue("@fails_pack_version", result.FailsPackVersion ? 1 : 0);
        command.Parameters.AddWithValue("@status", Filed);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(result.ObservedAt));
        command.Parameters.AddWithValue("@requested_signal", (object?)document?.RequestedSignal ?? DBNull.Value);
        command.Parameters.AddWithValue("@requested_axis", (object?)document?.RequestedAxis ?? DBNull.Value);

        command.ExecuteNonQuery();
    }
}

/// <summary>
/// What one week's ask came to.
///
/// <b>There is no field for a partial proposal.</b> The document is present exactly on the two
/// outcomes that produced one, and the reasons are separate fields, so nothing here can hold half an
/// answer and be read later as a whole one.
/// </summary>
public sealed record SeatResult
{
    public string ProposalId { get; init; } = string.Empty;
    public DateOnly AsOf { get; init; }
    public int PackVersion { get; init; }
    public string PackDigest { get; init; } = string.Empty;

    public required string Transport { get; init; }
    public required string ConfiguredModel { get; init; }
    public string? ServedModel { get; init; }
    public bool CountsTowardHitRate { get; init; }

    /// <summary>One of proposed, abstained, unavailable and unreadable.</summary>
    public required string Outcome { get; init; }

    public ProposalDocument? Document { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = [];
    public string? UnavailableBecause { get; init; }
    public string? AnswerText { get; init; }
    public IReadOnlyList<SeatPin> Pins { get; init; } = [];

    public bool CitesNullControl { get; init; }
    public bool FailsPackVersion { get; init; }

    /// <summary>Why no pack was cut, on the weeks nothing was asked at all.</summary>
    public string? PackRefusedBecause { get; init; }

    /// <summary>
    /// Whether this cut matched the one the packer's own slot made this morning, or null where there
    /// was none to compare against.
    ///
    /// Null and false are different answers and only one of them is a fault: a week where the packer
    /// slot did not run has nothing to compare, and a week where the two disagree is a packer that is
    /// not byte-stable over the live store.
    /// </summary>
    public bool? MatchedEarlierCut { get; init; }

    public DateTimeOffset ObservedAt { get; init; }
    public int RowsWritten { get; init; }

    /// <summary>A week where the pack could not be built, so the seat was never asked.</summary>
    public static SeatResult NoPack(IResearchTransport transport, DateOnly asOf, string because)
    {
        ArgumentNullException.ThrowIfNull(transport);

        return new SeatResult
        {
            AsOf = asOf,
            Transport = transport.Transport,
            ConfiguredModel = transport.ConfiguredModel,
            Outcome = "unavailable",
            PackRefusedBecause = because,
            UnavailableBecause = $"no pack was built, {because}",
            CountsTowardHitRate = SeatTransport.CountsTowardHitRate(transport.Transport),
        };
    }
}
