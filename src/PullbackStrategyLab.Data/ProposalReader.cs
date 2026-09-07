using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What the researcher returned, week by week.
///
/// <b>Every read is bounded on the instant the answer was filed.</b> The scoreboard's third band
/// reads proposal hit rate by pack version, and a figure that included a proposal made after the
/// session being looked at would be a figure about a population that did not exist. That is the
/// point-in-time rule at its sharpest, because the confounder is the reader rather than the price.
/// see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version
/// </summary>
public static class ProposalReader
{
    /// <summary>
    /// Every proposal filed at or before <paramref name="asOf"/>, oldest first.
    ///
    /// Ordered by the instant with the id as the tiebreak, so a week asked twice reads in the order
    /// it was asked and two reads over one store state agree.
    /// </summary>
    public static IReadOnlyList<StoredProposal> Read(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionZone);

        string bound = StoreText.EndOfSession(asOf, sessionZone);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.proposal_id, p.as_of, p.pack_version, p.pack_digest, p.transport,
                   p.configured_model, p.served_model, p.counts_toward_hit_rate, p.outcome,
                   p.direction, p.gate, p.threshold_name, p.from_value, p.to_value, p.family,
                   p.mechanism, p.evidence_setup_ids, p.evidence_signals, p.refutation,
                   p.observations_to_settle, p.abstained_because, p.unavailable_because,
                   p.answer_problems, p.answer_text, p.pins, p.cites_null_control,
                   p.fails_pack_version, p.status, p.observed_at
              FROM proposal p
             WHERE p.observed_at <= @bound
             ORDER BY p.observed_at, p.proposal_id
            """;

        command.Parameters.AddWithValue("@bound", bound);

        var proposals = new List<StoredProposal>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            proposals.Add(new StoredProposal(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7) == 1,
                reader.GetString(8))
            {
                Direction = reader.IsDBNull(9) ? null : reader.GetString(9),
                Gate = reader.IsDBNull(10) ? null : reader.GetString(10),
                ThresholdName = reader.IsDBNull(11) ? null : reader.GetString(11),
                From = reader.IsDBNull(12) ? null : StoreText.StorageTextToPrice(reader.GetString(12)),
                To = reader.IsDBNull(13) ? null : StoreText.StorageTextToPrice(reader.GetString(13)),
                Family = reader.IsDBNull(14) ? null : reader.GetString(14),
                Mechanism = reader.IsDBNull(15) ? null : reader.GetString(15),
                EvidenceSetupIds = reader.IsDBNull(16) ? null : reader.GetString(16),
                EvidenceSignals = reader.IsDBNull(17) ? null : reader.GetString(17),
                Refutation = reader.IsDBNull(18) ? null : reader.GetString(18),
                ObservationsToSettle = reader.IsDBNull(19) ? null : reader.GetInt32(19),
                AbstainedBecause = reader.IsDBNull(20) ? null : reader.GetString(20),
                UnavailableBecause = reader.IsDBNull(21) ? null : reader.GetString(21),
                AnswerProblems = reader.IsDBNull(22) ? null : reader.GetString(22),
                AnswerText = reader.IsDBNull(23) ? null : reader.GetString(23),
                Pins = reader.IsDBNull(24) ? null : reader.GetString(24),
                CitesNullControl = reader.GetInt32(25) == 1,
                FailsPackVersion = reader.GetInt32(26) == 1,
                Status = reader.GetString(27),
                ObservedAt = StoreText.StorageTextToTimestamp(reader.GetString(28)),
            });
        }

        return proposals;
    }

    /// <summary>
    /// The most recent week the seat could not be asked, or null where the last ask succeeded.
    ///
    /// <b>This is what the status band reads.</b> A seat that cannot ask is a fact about the running
    /// lab rather than about the build, and nothing in the verification harness reaches the running
    /// lab; recorded and not shown would mean the operator learns of it a quarter later from a gap
    /// in the proposal record.
    /// see: The seat runs on the subscription against claude-opus-5, and the API path stays live for the day the subscription stops
    /// see: Every phase ends in a generated phase report, not in a page somebody looks at
    /// </summary>
    public static StoredProposal? LatestUnavailable(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        IReadOnlyList<StoredProposal> filed = Read(connection, asOf, sessionZone);

        // The latest ask, and it is reported only where that ask is the one that failed. A refusal
        // three weeks back that a later week answered over is history rather than a live warning,
        // and a band that went on showing it would be a band nobody reads.
        StoredProposal? latest = filed.Count == 0 ? null : filed[^1];

        return latest?.Outcome is "unavailable" ? latest : null;
    }
}

/// <summary>
/// One filed answer: a proposal, an abstention, an unreadable answer or a week with no ask in it.
///
/// The four outcomes are four shapes and the nullable fields are how the record says which: a
/// proposal carries the five change fields and an abstention carries none of them, which the store
/// holds as CHECK clauses rather than leaving to whoever writes the row.
/// </summary>
public sealed record StoredProposal(
    string ProposalId,
    DateOnly AsOf,
    int PackVersion,
    string PackDigest,
    string Transport,
    string ConfiguredModel,
    string? ServedModel,
    bool CountsTowardHitRate,
    string Outcome)
{
    public string? Direction { get; init; }
    public string? Gate { get; init; }
    public string? ThresholdName { get; init; }
    public decimal? From { get; init; }
    public decimal? To { get; init; }
    public string? Family { get; init; }
    public string? Mechanism { get; init; }
    public string? EvidenceSetupIds { get; init; }
    public string? EvidenceSignals { get; init; }
    public string? Refutation { get; init; }
    public int? ObservationsToSettle { get; init; }
    public string? AbstainedBecause { get; init; }
    public string? UnavailableBecause { get; init; }
    public string? AnswerProblems { get; init; }
    public string? AnswerText { get; init; }
    public string? Pins { get; init; }
    public bool CitesNullControl { get; init; }
    public bool FailsPackVersion { get; init; }
    public string Status { get; init; } = "filed";
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>How the week reads in a run line and on the band.</summary>
    public string Describe() => Outcome switch
    {
        "proposed" => $"{Direction} {ThresholdName} from {From} to {To}",
        "abstained" => $"abstained, {AbstainedBecause}",
        "unavailable" => $"not asked, {UnavailableBecause}",
        _ => $"unreadable, {AnswerProblems}",
    };
}
