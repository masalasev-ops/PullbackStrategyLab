using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Holds proposals, sends each kind where it goes, and records what became of it.
///
/// <b>The two kinds are two kinds and not one.</b> A rule change uses signals that exist and goes
/// straight to replay screening and then to a paired test; a signal request says the opposite, that
/// the model cannot separate two setups with what it has and can name what would, and that is a
/// build task rather than a variant. Without the second the loop plateaus within months, because
/// there are only so many ways to rearrange ten checks.
/// see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal
///
/// <b>It moves a status and nothing else.</b> The proposal's own columns are the seat's and are
/// never edited here: what a week's answer said is a fact about that week and rewriting it would put
/// a second statement about one ask into the row that holds the first. This stage is `proposal`'s
/// declared updater of one column.
/// see: The AI writes only to the proposal store
///
/// <b>An abstention is a result and reaches a state of its own.</b> Folding it in with a week the
/// seat could not be asked would put a considered decline and an outage in one bucket, and telling
/// those apart is the whole reason the seat records four outcomes rather than two. A version that
/// never abstains has learned to always find something, which is a warning rather than a triumph,
/// and it is only readable if abstentions are counted as the results they are.
/// see: Abstention is a valid recorded proposal outcome
/// </summary>
public sealed class ProposalRegistry
{
    public const string Name = "screen-proposals";

    /// <summary>A rule change replay let through, which is much weaker than saying it is any good.</summary>
    public const string Screened = "screened";

    /// <summary>A rule change replay killed, or one the register would not take as a version.</summary>
    public const string Discarded = "discarded";

    /// <summary>An abstention: a recorded result that needs no screen.</summary>
    public const string Recorded = "recorded";

    /// <summary>A signal request: work for a person, not a variant.</summary>
    public const string BuildTask = "build-task";

    /// <summary>A week with no answer in it: neither a result nor work.</summary>
    public const string Unactionable = "unactionable";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;
    private readonly ReplayHarness _harness;

    public ProposalRegistry(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options,
        ReplayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
        _harness = harness;
    }

    /// <summary><c>screen-proposals [as-of]</c>.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        RegistryResult result = Register(asOf);

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, {result.Filed} filed proposal(s) read");

        // Every disposition on its own line and no total. There is no figure here that adds a
        // screened rule change to a signal request, because the two go to different places and a
        // sum of them would describe neither.
        Console.WriteLine(
            $"{Name}: {result.Screened} survived the screen, {result.Discarded} killed by it, "
            + $"{result.Inconclusive} inconclusive and still filed");
        Console.WriteLine(
            $"{Name}: {result.BuildTasks} signal request(s), {result.Abstentions} abstention(s), "
            + $"{result.Unactionable} week(s) with no answer");

        foreach (string line in result.Lines)
        {
            Console.WriteLine($"{Name}: {line}");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Every proposal still at <c>filed</c>, sent where its kind goes.
    ///
    /// <b>Dispositioned once, with one deliberate exception.</b> A proposal leaves <c>filed</c> for
    /// every disposition a screen can reach, so a stage run twice on one evening does the work once,
    /// and a week with no answer in it is moved off the queue rather than read again for ever. The
    /// exception is a screen that read rows and separated none: that screen said nothing, so the
    /// proposal stays filed and is read again as the store grows.
    /// </summary>
    public RegistryResult Register(DateOnly asOf)
    {
        IReadOnlyList<StoredProposal> filed;

        using (SqliteConnection reading = _connections.OpenReadOnly())
        {
            filed = [.. ProposalReader.Read(reading, asOf, _options.SessionZone)
                .Where(p => p.Status == ResearcherSeat.Filed)];
        }

        var lines = new List<string>();
        var dispositions = new List<(string ProposalId, string Status)>();
        int screened = 0, discarded = 0, buildTasks = 0, abstentions = 0, unactionable = 0;
        int inconclusive = 0;

        foreach (StoredProposal proposal in filed)
        {
            switch (proposal.Outcome)
            {
                case "proposed":
                    // The screen is the harness's write and the status is this stage's. Both happen
                    // for one proposal and neither writes the other's table.
                    ReplayResult result = _harness.ScreenProposal(proposal, asOf);
                    lines.Add(result.Describe());

                    // **An inconclusive screen leaves the proposal filed, deliberately.** Over a
                    // population where neither rule selected anything the screen has said nothing,
                    // and a proposal dispositioned on a screen that said nothing would be settled by
                    // a reading that never took place. It is read again as the store grows, and the
                    // result row written each time is the record that the evidence still cannot
                    // separate anything, on the same terms a pack cut in a week with nothing to say
                    // is the record that there was nothing to say.
                    if (result.Verdict == ReplayResult.Inconclusive)
                    {
                        inconclusive++;
                        break;
                    }

                    bool killed = result.Verdict != ReplayResult.Survived;
                    dispositions.Add((proposal.ProposalId, killed ? Discarded : Screened));

                    if (killed)
                    {
                        discarded++;
                    }
                    else
                    {
                        screened++;
                    }

                    break;

                case "requested":
                    lines.Add(
                        $"{proposal.ProposalId}: a build task, \"{proposal.RequestedSignal}\" on the "
                        + $"{proposal.RequestedAxis} axis, from {Pairs(proposal)} setup(s) it could not separate");
                    dispositions.Add((proposal.ProposalId, BuildTask));
                    buildTasks++;
                    break;

                case "abstained":
                    lines.Add($"{proposal.ProposalId}: abstained, {proposal.AbstainedBecause}");
                    dispositions.Add((proposal.ProposalId, Recorded));
                    abstentions++;
                    break;

                default:
                    lines.Add($"{proposal.ProposalId}: {proposal.Outcome}, nothing to screen");
                    dispositions.Add((proposal.ProposalId, Unactionable));
                    unactionable++;
                    break;
            }
        }

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "proposal");

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach ((string proposalId, string status) in dispositions)
            {
                MoveStatus(connection, transaction, proposalId, status);
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new RegistryResult(
            asOf, filed.Count, screened, discarded, inconclusive, buildTasks, abstentions,
            unactionable, lines, summary.RowsWritten, RunOutcome.Clean);
    }

    private static int Pairs(StoredProposal proposal) =>
        proposal.EvidenceSetupIds is null
            ? 0
            : proposal.EvidenceSetupIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The one column this stage writes.
    ///
    /// <b>Guarded on the status it is moving from, not only on the id.</b> Two runs of an evening
    /// would otherwise re-disposition a proposal a screen had already settled, and the second write
    /// would look exactly like the first while resting on a read taken before it.
    /// </summary>
    private static void MoveStatus(
        SqliteConnection connection, SqliteTransaction transaction, string proposalId, string status)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE proposal
               SET status = @status
             WHERE proposal_id = @proposal_id
               AND status = 'filed'
            """;

        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@proposal_id", proposalId);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// What one run of the registry did.
///
/// The dispositions are separate fields with no total, which is the two-kinds rule made structural:
/// there is no field for a figure over a screened rule change and a signal request together, and
/// nowhere for one to go.
/// see: Proposals come in two kinds, rule changes over existing signals and requests for a new signal
/// </summary>
public sealed record RegistryResult(
    DateOnly AsOf,
    int Filed,
    int Screened,
    int Discarded,

    /// <summary>
    /// Screens that read rows and separated none, which leave their proposal filed.
    ///
    /// Counted apart from the two that settle a proposal, because a screen that said nothing is not
    /// a screen that found nothing.
    /// </summary>
    int Inconclusive,

    int BuildTasks,
    int Abstentions,
    int Unactionable,
    IReadOnlyList<string> Lines,
    int RowsWritten,
    RunOutcome Outcome);
