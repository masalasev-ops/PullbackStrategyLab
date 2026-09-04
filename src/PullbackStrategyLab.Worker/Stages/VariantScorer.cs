using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Differences each version against the baseline, night by night.
///
/// <b>The comparison is paired on the night, and that is the whole design.</b> Baseline and version
/// see the same candidate list on the same night against the same market, so what is measured is
/// the difference between two selections out of one population rather than two runs in two months.
/// Unpaired, a small improvement needs thousands of observations; paired it needs hundreds
/// (see: Versions select from one shared nightly candidate list rather than each re-scanning).
///
/// <b>One family is scored and the other is named unreachable.</b> A selection version is scored on
/// the forward return of what it selected against what the baseline selected. An execution version
/// is scored on R, R needs fills, and no execution version is admitted in this generation, so this
/// stage has no execution path rather than a path nothing reaches: neither route by which such a
/// version earns its place is open, and the condition that would reopen them is named on the
/// decision (see: No execution variant is admitted in this generation, and the condition that would
/// reopen it is named). <b>The minimum sample that would be scored against is a row count until a
/// trade exists</b> to measure the trade-level design effect over, and the conversion waits on the
/// same condition (see: The execution minimum is 200 paired trades and its conversion waits on a
/// trade existing).
///
/// <b>Long and short are scored apart and never added.</b> A version is one side's, because a
/// threshold belongs to one side's gate list, and a row of this table is one side's night. Pooling
/// would let a version that helps one side and hurts the other read as no difference at all
/// (see: Long and short are never pooled into one figure).
///
/// <b>A version selecting outside the night's capped sixty is refused a fill, and the score says
/// so.</b> The spread capture stays at the capped sixty, so a name past the cap has minutes and no
/// book and the gate refuses it: that is a recorded fact about the version rather than a silent
/// absence, and it is what makes a version scoring poorly because it selected outside the cap
/// distinguishable from one scoring poorly on its merits. Both sides carry the count, because the
/// baseline's own selections past the sixtieth rank are refused on the same terms
/// (see: The spread capture stays at the capped sixty, and a version selecting outside it is scored
/// as refused).
///
/// <b>It scores a night once, when the night's horizon has closed, and never rewrites it.</b> A
/// figure recomputed as returns dribble in would be a figure over a population that changed after
/// it was read, which is the defect the corpus's population rule is about.
/// </summary>
public sealed class VariantScorer
{
    public const string Name = "score-variants";

    /// <summary>Recorded where the register holds no version to difference against the baseline.</summary>
    public const string NoVersions =
        "the register holds no version other than the baseline for this session, so there is nothing to "
        + "difference against it. That is the state after the freeze and before the first proposal, and it "
        + "is not an error";

    /// <summary>Recorded where versions exist and the generation in force has no baseline.</summary>
    public const string NoBaseline =
        "versions are registered and none of them is this generation's baseline, so there is nothing for a "
        + "difference to be measured against";

    /// <summary>What a scored night says when it has no figure because neither rule picked anything.</summary>
    public const string NeitherSideSelected =
        "neither the baseline nor the version selected a name on this night, so there are two empty "
        + "populations and no difference between them";

    /// <summary>What a scored night says when one side picked names and none of them has an outcome.</summary>
    public const string NoOutcomes =
        "the horizon closed and no name either side selected carries a forward return, so a mean over "
        + "either population would be a mean over nothing";

    /// <summary>What a scored night says when one of the two sides picked nothing.</summary>
    public const string OneSideEmpty =
        "one of the two rules selected nothing on this night, so a difference of means would be a figure "
        + "over one population presented as a comparison of two";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public VariantScorer(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        VariantScoring scoring = Score(asOf);

        Console.WriteLine(
            $"{Name}: session {scoring.AsOf:yyyy-MM-dd}, {scoring.VersionsLive} version(s) live, "
            + $"{scoring.VersionsScored} scored");
        Console.WriteLine(
            $"{Name}: {scoring.NightsScored} night(s) scored, {scoring.NightsWaiting} still inside a horizon");
        Console.WriteLine(
            $"{Name}: {scoring.Longs} long row(s), {scoring.Shorts} short row(s), never added");

        if (scoring.Unscoreable > 0)
        {
            Console.WriteLine(
                $"{Name}: {scoring.Unscoreable} setup(s) the frozen signals could not judge the moved gate over");
        }

        Console.WriteLine(
            scoring.StoppedBecause is null
                ? $"{Name}: {scoring.Outcome.ToStorageText()}, {scoring.RowsWritten} row(s) written"
                : $"{Name}: {scoring.Outcome.ToStorageText()}, {scoring.StoppedBecause}");

        return scoring.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>Every night every live version can be scored on and has not been.</summary>
    public VariantScoring Score(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "variant_score", "score_run");

        DateTimeOffset computedAt = _clock.UtcNow;
        string zone = _options.SessionZone;

        IReadOnlyList<StoredVariant> live = VariantReader.LiveOn(connection, asOf, zone);
        bool baseline = live.Any(v => v.IsBaseline);

        IReadOnlyList<StoredVariant> scoreable =
            [.. live.Where(v => v.Family == VariantFamily.Selection && v.Moved is not null)];

        string? stopped =
            !baseline && live.Count > 0 ? NoBaseline
            : scoreable.Count == 0 ? NoVersions
            : null;

        var tally = new Tally();

        if (stopped is null)
        {
            foreach (StoredVariant variant in scoreable)
            {
                ScoreOne(connection, variant, asOf, computedAt, zone, tally);
            }
        }

        // Partial rather than failed on both empty states. A night with nothing to score is a night
        // the lab still records setups on, and a failed run would take the slots after it with it.
        RunOutcome outcome = stopped is null ? RunOutcome.Clean : RunOutcome.Partial;
        WriteRun(connection, asOf, computedAt, live.Count, stopped is null ? scoreable.Count : 0, tally, outcome, stopped);

        RunSummary summary = run.Complete(outcome);

        return new VariantScoring(
            asOf, live.Count, stopped is null ? scoreable.Count : 0,
            tally.NightsScored, tally.NightsWaiting, tally.Longs, tally.Shorts, tally.Unscoreable,
            summary.RowsWritten, outcome, stopped);
    }

    /// <summary>
    /// One version, over every night it was live on that this run can settle.
    ///
    /// <b>The nights are the version's own, not the lab's.</b> A version registered on Tuesday was
    /// not running on Monday, so Monday is not a night it has a selection on and scoring it would
    /// invent one (see: A reader's signature does not establish point-in-time; the query does).
    /// </summary>
    private void ScoreOne(
        SqliteConnection connection,
        StoredVariant variant,
        DateOnly asOf,
        DateTimeOffset computedAt,
        string zone,
        Tally tally)
    {
        MovedThreshold moved = variant.Moved!;
        SelectionRule baseline = SelectionRule.For(moved.Direction);
        SelectionRule rule = baseline.With(moved.ThresholdName, moved.To);

        // The session the version was registered in rather than the UTC date of the stamp. An
        // evening registration lands after midnight UTC, so the calendar date of the instant is
        // the session after the one the operator was standing in, and the version would miss its
        // own first night.
        DateOnly from = _clock.SessionDate(variant.CreatedAt, zone);

        foreach (DateOnly night in UnscoredNights(connection, variant.VariantId, moved.Direction, from, asOf, zone))
        {
            IReadOnlyList<StoredSetup> setups =
                [.. SetupReader.Read(connection, night).Where(s => s.Direction == moved.Direction)];

            if (setups.Count == 0)
            {
                continue;
            }

            if (!HorizonClosed(connection, setups, asOf, zone))
            {
                tally.NightsWaiting++;
                continue;
            }

            IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> signals =
                FrozenSignals(connection, night, zone);
            IReadOnlyDictionary<string, decimal> outcomes = Outcomes(connection, night, asOf, zone);

            var baselineSet = new List<string>();
            var variantSet = new List<string>();
            int unscoreable = 0;
            int baselineOutside = 0;
            int variantOutside = 0;

            foreach (StoredSetup setup in setups)
            {
                bool outsideCap = setup.CappedOut != false;

                if (setup.PassedAll)
                {
                    baselineSet.Add(setup.SetupId);

                    if (outsideCap)
                    {
                        baselineOutside++;
                    }
                }

                bool? selected = SelectsUnder(setup, rule, baseline, moved.Gate, signals);

                if (selected is null)
                {
                    unscoreable++;
                    continue;
                }

                if (selected.Value)
                {
                    variantSet.Add(setup.SetupId);

                    if (outsideCap)
                    {
                        variantOutside++;
                    }
                }
            }

            decimal[] baselineReturns = [.. baselineSet.Where(outcomes.ContainsKey).Select(id => outcomes[id])];
            decimal[] variantReturns = [.. variantSet.Where(outcomes.ContainsKey).Select(id => outcomes[id])];

            string? withheld =
                baselineSet.Count == 0 && variantSet.Count == 0 ? NeitherSideSelected
                : baselineSet.Count == 0 || variantSet.Count == 0 ? OneSideEmpty
                : baselineReturns.Length == 0 || variantReturns.Length == 0 ? NoOutcomes
                : null;

            decimal? baselineMean = withheld is null ? baselineReturns.Average() : null;
            decimal? variantMean = withheld is null ? variantReturns.Average() : null;

            Insert(
                connection, variant, night, moved.Direction, setups.Count,
                baselineSet, variantSet, baselineMean, variantMean,
                baselineOutside, variantOutside, unscoreable, withheld, computedAt);

            tally.NightsScored++;
            tally.Unscoreable += unscoreable;

            if (moved.Direction == SetupDirection.Long)
            {
                tally.Longs++;
            }
            else
            {
                tally.Shorts++;
            }
        }
    }

    /// <summary>
    /// Whether one version's rule selects one setup, or null where the record cannot say.
    ///
    /// <b>Every gate but the moved one is read back rather than re-judged.</b> The version differs
    /// from the baseline by exactly one threshold, so every other gate's verdict is the one the
    /// night recorded and re-deriving it would be a second implementation of the rule.
    ///
    /// <b>The moved gate is judged through the one implementation, and the baseline's own verdict is
    /// judged the same way as a check on the rebuild.</b> Where the replay under the baseline's own
    /// rule disagrees with what the night recorded, the evidence this rebuilt is not the evidence
    /// the night judged, and the setup is unscoreable rather than counted on a guess. That is the
    /// per-row guard against a rebuild that is quietly wrong, and it costs one more evaluation.
    /// </summary>
    private static bool? SelectsUnder(
        StoredSetup setup,
        SelectionRule rule,
        SelectionRule baseline,
        string gate,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> signals)
    {
        IReadOnlyList<CheckResult> recorded = Recorded(setup);

        if (recorded.Count == 0 || recorded.All(r => r.Name != gate))
        {
            return null;
        }

        if (!signals.TryGetValue(setup.SetupId, out IReadOnlyDictionary<string, decimal>? row))
        {
            return null;
        }

        CheckResult? asBaseline = SelectionReplay.Judge(baseline, gate, row);
        CheckResult? asVersion = SelectionReplay.Judge(rule, gate, row);

        if (asBaseline is null || asVersion is null)
        {
            return null;
        }

        if (asBaseline.Passed != recorded.Single(r => r.Name == gate).Passed)
        {
            return null;
        }

        bool everythingElse = recorded
            .Where(r => r.Name != gate && !SetupChecks.RecordedNotRequired.Contains(r.Name))
            .All(r => r.Passed);

        return everythingElse && asVersion.Passed;
    }

    private static IReadOnlyList<CheckResult> Recorded(StoredSetup setup)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CheckResult>>(setup.CheckResults, CheckResultsJson) ?? [];
        }
        catch (JsonException)
        {
            // A row whose verdicts cannot be read is one this cannot judge, and it is counted as
            // unscoreable rather than throwing the night away.
            return [];
        }
    }

    private static readonly JsonSerializerOptions CheckResultsJson =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The nights this version was live on, of its own side, that carry no score row yet.
    ///
    /// Bounded above by the session being run and below by the session the version was registered
    /// in, so a replay of an evening scores the nights that evening could have scored.
    /// </summary>
    private static IReadOnlyList<DateOnly> UnscoredNights(
        SqliteConnection connection, string variantId, string direction, DateOnly from, DateOnly asOf, string zone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT s.as_of
              FROM setup s
             WHERE s.direction = @direction
               AND s.as_of >= @from
               AND s.as_of <= @as_of
               AND NOT EXISTS (SELECT 1 FROM variant_score v
                                WHERE v.variant_id = @variant_id
                                  AND v.session_date = s.as_of
                                  AND v.direction = @direction
                                  AND v.computed_at <= @computed_before)
             ORDER BY s.as_of
            """;

        command.Parameters.AddWithValue("@variant_id", variantId);
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        // The already-scored test is bounded too. A replay of an evening must see the scores that
        // evening had: unbounded, a night scored last week by a later run would read as settled on
        // an evening that had not yet settled it, and the replay would produce a different set of
        // nights from the run it is replaying.
        command.Parameters.AddWithValue("@computed_before", StoreText.EndOfSession(asOf, zone));

        var nights = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            nights.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return nights;
    }

    /// <summary>
    /// Whether every name flagged on a night has run its scoring horizon.
    ///
    /// Counted from the store's own bars rather than from a calendar, on the ruling 4.5 took: a
    /// session is a date the store holds bars for. The night's own session is in the count, so
    /// eleven is ten having passed. A night still inside its horizon waits, which is the ordinary
    /// state of every recent night and is not a fault.
    /// see: A session is a date the store holds minutes for, and no calendar is authored here
    /// </summary>
    private static bool HorizonClosed(
        SqliteConnection connection, IReadOnlyList<StoredSetup> setups, DateOnly asOf, string zone)
    {
        foreach (StoredSetup setup in setups)
        {
            int sessions = DailyBarReader.SessionsBetween(
                connection, setup.Ticker, setup.AsOf, asOf, asOf, zone);

            if (sessions <= MeasurementParameters.ScoringHorizonSessions)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every setup of one night, with the signals a replay can read, by setup.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> FrozenSignals(
        SqliteConnection connection, DateOnly night, string zone)
    {
        var rows = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);

        foreach (StoredSetupSignal signal in SetupSignalReader.Read(connection, night, zone))
        {
            if (!SelectionReplay.DirectSignals.Contains(signal.SignalName))
            {
                continue;
            }

            if (!decimal.TryParse(
                    signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                continue;
            }

            if (!rows.TryGetValue(signal.SetupId, out Dictionary<string, decimal>? row))
            {
                row = new Dictionary<string, decimal>(StringComparer.Ordinal);
                rows[signal.SetupId] = row;
            }

            row[signal.SignalName] = value;
        }

        return rows.ToDictionary(
            p => p.Key,
            p => (IReadOnlyDictionary<string, decimal>)p.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The scoring-horizon forward return of every setup of one night, by setup.
    ///
    /// Bounded on `filled_at`, which is what says the lab could have had the figure by the session
    /// this run is for. A return filled tomorrow is invisible to a replay of tonight.
    /// </summary>
    private static IReadOnlyDictionary<string, decimal> Outcomes(
        SqliteConnection connection, DateOnly night, DateOnly asOf, string zone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.subject_id, f.return_signed
              FROM forward_return f
              JOIN setup s ON s.setup_id = f.subject_id
             WHERE f.subject_kind = 'setup'
               AND f.horizon_days = @horizon
               AND f.filled_at <= @filled_before
               AND s.as_of = @night
            """;

        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@filled_before", StoreText.EndOfSession(asOf, zone));
        command.Parameters.AddWithValue("@night", StoreText.DateToStorageText(night));

        var outcomes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            outcomes[reader.GetString(0)] = StoreText.StorageTextToRatio(reader.GetString(1));
        }

        return outcomes;
    }

    private static void Insert(
        SqliteConnection connection,
        StoredVariant variant,
        DateOnly night,
        string direction,
        int flagged,
        IReadOnlyCollection<string> baselineSet,
        IReadOnlyCollection<string> variantSet,
        decimal? baselineMean,
        decimal? variantMean,
        int baselineOutside,
        int variantOutside,
        int unscoreable,
        string? withheld,
        DateTimeOffset computedAt)
    {
        int both = baselineSet.Intersect(variantSet, StringComparer.Ordinal).Count();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO variant_score (
                variant_id, session_date, direction, generation, family, horizon_days,
                flagged, baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
                baseline_mean_return, variant_mean_return, mean_difference,
                baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at)
            VALUES (
                @variant_id, @session_date, @direction, @generation, @family, @horizon,
                @flagged, @baseline_selected, @variant_selected, @both, @variant_only, @baseline_only,
                @baseline_mean, @variant_mean, @difference,
                @baseline_outside, @variant_outside, @unscoreable, @withheld, @computed_at);
            """;

        command.Parameters.AddWithValue("@variant_id", variant.VariantId);
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(night));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@generation", variant.Generation);
        command.Parameters.AddWithValue("@family", variant.Family);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@flagged", flagged);
        command.Parameters.AddWithValue("@baseline_selected", baselineSet.Count);
        command.Parameters.AddWithValue("@variant_selected", variantSet.Count);
        command.Parameters.AddWithValue("@both", both);
        command.Parameters.AddWithValue("@variant_only", variantSet.Count - both);
        command.Parameters.AddWithValue("@baseline_only", baselineSet.Count - both);
        command.Parameters.AddWithValue(
            "@baseline_mean",
            baselineMean is decimal b ? StoreText.RatioToStorageText(b) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@variant_mean",
            variantMean is decimal v ? StoreText.RatioToStorageText(v) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@difference",
            baselineMean is decimal bm && variantMean is decimal vm
                ? StoreText.RatioToStorageText(vm - bm)
                : DBNull.Value);
        command.Parameters.AddWithValue("@baseline_outside", baselineOutside);
        command.Parameters.AddWithValue("@variant_outside", variantOutside);
        command.Parameters.AddWithValue("@unscoreable", unscoreable);
        command.Parameters.AddWithValue("@withheld", (object?)withheld ?? DBNull.Value);
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The night's run row.
    ///
    /// <b>The conflict clause is what makes a rerun inside one instant a no-op rather than a
    /// crash</b>, on the same terms `loss_run` takes: a second run at the same recorded instant did
    /// the same work over the same store, so its row is the row that is already there. It is not a
    /// licence to overwrite: the clause does nothing rather than updating, so the first run's
    /// figures stand and a later instant writes a row of its own beside them.
    /// </summary>
    private static void WriteRun(
        SqliteConnection connection,
        DateOnly asOf,
        DateTimeOffset observedAt,
        int live,
        int scored,
        Tally tally,
        RunOutcome outcome,
        string? stopped)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO score_run (
                session_date, observed_at, versions_live, versions_scored,
                nights_scored, nights_waiting, longs, shorts, unscoreable, outcome, stopped_because)
            VALUES (
                @session_date, @observed_at, @live, @scored,
                @nights_scored, @nights_waiting, @longs, @shorts, @unscoreable, @outcome, @stopped)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@live", live);
        command.Parameters.AddWithValue("@scored", scored);
        command.Parameters.AddWithValue("@nights_scored", tally.NightsScored);
        command.Parameters.AddWithValue("@nights_waiting", tally.NightsWaiting);
        command.Parameters.AddWithValue("@longs", tally.Longs);
        command.Parameters.AddWithValue("@shorts", tally.Shorts);
        command.Parameters.AddWithValue("@unscoreable", tally.Unscoreable);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped", (object?)stopped ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    private sealed class Tally
    {
        public int NightsScored { get; set; }

        public int NightsWaiting { get; set; }

        public int Longs { get; set; }

        public int Shorts { get; set; }

        public int Unscoreable { get; set; }
    }
}

/// <summary>What one run of the scorer did, as the stage reports it.</summary>
public sealed record VariantScoring(
    DateOnly AsOf,
    int VersionsLive,
    int VersionsScored,
    int NightsScored,
    int NightsWaiting,
    int Longs,
    int Shorts,
    int Unscoreable,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause);
