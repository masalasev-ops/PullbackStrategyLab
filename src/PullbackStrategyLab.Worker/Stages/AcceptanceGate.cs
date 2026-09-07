using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Settles a matured version against the pre-registration written when it was created.
///
/// <b>It writes `status` and `resolved_at` and has no path to any other column of `variant`.</b> That
/// is the row where a future change could quietly let a result rewrite its own target, so the
/// statement is one UPDATE naming two columns, this type holds no value that could reach a third,
/// and the store's own key refuses a second insert. Twenty worthless candidates give a 64% chance
/// that one of them looks impressive by luck, and a target that can move after the result is not a
/// target (see: Targets and minimum samples are written at creation and are immutable).
///
/// <b>Everything it weighed goes to `acceptance_reading` rather than beside the status.</b> The
/// version row may hold nothing new, so a reading is a row of its own, keyed on the instant it was
/// taken, written on every run and not only on the run that settles. A version that never
/// accumulates its sample is the case the failure table names, and the sequence of readings is what
/// says so on the ledger.
///
/// <b>The baseline is passed over and the run counts it.</b> It is the arm every other version is
/// differenced against, so there is no baseline-minus-baseline series to take an interval over, and
/// V0's own pre-registration says it in terms: it is not itself accepted or rejected, and it closes
/// only if the baseline is edited, which starts a new generation. Counted rather than filtered
/// silently, because a run that read nothing and a run that found nothing registered are different
/// nights (see: An approved proposal creates a new version from zero, and a running version is never
/// edited).
///
/// <b>Nothing here decides on the calendar.</b> A version short of its minimum stays open with the
/// shortfall stated, on the night it is read and on every night after it.
/// see: Acceptance measures expectancy, never win rate
/// </summary>
public sealed class AcceptanceGate
{
    public const string Name = "settle-variants";

    /// <summary>Recorded where the register holds no version this gate could settle.</summary>
    public const string NothingToSettle =
        "the register holds no open selection version, so there is nothing to settle. That is the state "
        + "after the freeze and before the first proposal is accepted into a version, and it is not an error";

    /// <summary>Recorded where versions are registered and the generation in force has no baseline.</summary>
    public const string NoBaseline =
        "versions are registered and none of them is this generation's baseline, so the differences a "
        + "settlement rests on were measured against nothing";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public AcceptanceGate(
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

        AcceptanceSettlement settlement = Settle(asOf);

        Console.WriteLine(
            $"{Name}: session {settlement.AsOf:yyyy-MM-dd}, {settlement.VersionsLive} version(s) live, "
            + $"{settlement.VersionsRead} read, {settlement.BaselinesPassed} baseline(s) passed over");
        Console.WriteLine(
            $"{Name}: {settlement.Accepted} accepted, {settlement.Rejected} rejected, "
            + $"{settlement.LeftOpen} left open");

        foreach (AcceptanceTest.Reading reading in settlement.Readings)
        {
            Console.WriteLine(
                $"{Name}: {reading.EffectiveObservations} of {reading.MinimumSample} "
                + $"{reading.MinimumSampleUnit}, {reading.Verdict}: "
                + (reading.SettledBecause ?? reading.WithheldBecause));
        }

        Console.WriteLine(
            settlement.StoppedBecause is null
                ? $"{Name}: {settlement.Outcome.ToStorageText()}, {settlement.RowsWritten} row(s) written"
                : $"{Name}: {settlement.Outcome.ToStorageText()}, {settlement.StoppedBecause}");

        return settlement.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>Reads every open selection version and settles the ones that have matured.</summary>
    public AcceptanceSettlement Settle(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "variant", "acceptance_reading", "acceptance_run");

        DateTimeOffset observedAt = _clock.UtcNow;
        string zone = _options.SessionZone;

        IReadOnlyList<StoredVariant> live = VariantReader.LiveOn(connection, asOf, zone);
        int baselines = live.Count(v => v.IsBaseline);

        IReadOnlyList<StoredVariant> settleable =
            [.. live.Where(v =>
                !v.IsBaseline
                && v.Family == VariantFamily.Selection
                && v.Moved is not null
                && v.Status == VariantStatus.Open)];

        string? stopped =
            baselines == 0 && live.Count > 0 ? NoBaseline
            : settleable.Count == 0 ? NothingToSettle
            : null;

        var readings = new List<AcceptanceTest.Reading>();

        if (stopped is null)
        {
            IReadOnlyList<StoredVariantScore> scores =
                VariantScoreReader.ScoredBy(connection, asOf, zone);

            foreach (StoredVariant variant in settleable)
            {
                readings.Add(ReadOne(connection, variant, scores, asOf, observedAt, zone));
            }
        }

        int accepted = readings.Count(r => r.Verdict == VariantStatus.Accepted);
        int rejected = readings.Count(r => r.Verdict == VariantStatus.Rejected);
        int open = readings.Count(r => r.Verdict == VariantStatus.Open);

        // Partial rather than failed where there is nothing to settle. A night with no open version
        // is the ordinary night of this lab's whole life so far, and a failed run would take the
        // slots after it with it.
        RunOutcome outcome = stopped is null ? RunOutcome.Clean : RunOutcome.Partial;

        WriteRun(
            connection, asOf, observedAt, live.Count, readings.Count, baselines,
            readings.Count(r => r.Matured), accepted, rejected, open, outcome, stopped);

        RunSummary summary = run.Complete(outcome);

        return new AcceptanceSettlement(
            asOf, live.Count, readings.Count, baselines, readings.Count(r => r.Matured),
            accepted, rejected, open, readings, summary.RowsWritten, outcome, stopped);
    }

    /// <summary>
    /// One version: the reading, the row that records it, and the settlement where it settles.
    ///
    /// <b>The series is the version's own side and its own nights.</b> A version registered on
    /// Tuesday has no selection on Monday, and the scorer already writes only the nights the version
    /// was live on, so the rows this filters are the rows that exist rather than a window this
    /// reimposes.
    /// </summary>
    private AcceptanceTest.Reading ReadOne(
        SqliteConnection connection,
        StoredVariant variant,
        IReadOnlyList<StoredVariantScore> scores,
        DateOnly asOf,
        DateTimeOffset observedAt,
        string zone)
    {
        MovedThreshold moved = variant.Moved!;

        IReadOnlyList<AcceptanceTest.Night> nights =
            [.. scores
                .Where(s =>
                    string.Equals(s.VariantId, variant.VariantId, StringComparison.Ordinal)
                    && string.Equals(s.Direction, moved.Direction, StringComparison.Ordinal))
                .OrderBy(s => s.SessionDate)
                .Select(Night)];

        AcceptanceTest.Reading reading = AcceptanceTest.Of(
            moved.Direction, variant.MinimumSample, variant.MinimumSampleUnit, nights);

        // Age in whole days from the session the version was registered in, which is the figure the
        // failure table asks the ledger to show. The session rather than the UTC calendar date of
        // the stamp, on the same terms the scorer takes a version's first night.
        int age = Math.Max(0, asOf.DayNumber - _clock.SessionDate(variant.CreatedAt, zone).DayNumber);

        InsertReading(connection, variant, moved.Direction, asOf, observedAt, age, reading);

        if (reading.Verdict != VariantStatus.Open)
        {
            Resolve(connection, variant.VariantId, reading.Verdict, observedAt);
        }

        return reading;
    }

    /// <summary>
    /// One score row as the settling rule reads it.
    ///
    /// A row with no figure carries no denominator either, and it reaches the rule as a night with
    /// nought scored on both sides rather than being dropped: the reading counts the nights the
    /// scorer wrote, and a night dropped here would be a night missing from that count.
    /// </summary>
    private static AcceptanceTest.Night Night(StoredVariantScore score) => new(
        score.SessionDate,
        score.MeanDifference is null ? 0m : StoreText.StorageTextToRatio(score.MeanDifference),
        score.BaselineOnly,
        score.VariantOnly,
        score.BaselineScored ?? 0,
        score.VariantScored ?? 0,
        score.BaselineWins ?? 0,
        score.VariantWins ?? 0);

    /// <summary>
    /// The settlement, which is two columns and cannot be more.
    ///
    /// <b>The statement names `status` and `resolved_at` and nothing else, and it is the only UPDATE
    /// this file contains.</b> `writer-ownership` declares the gate as the updater of those two, and
    /// what makes that true is that there is no other statement here to declare.
    ///
    /// <b>Guarded on the version still being open.</b> A version settled by an earlier run of this
    /// evening is settled, and a second write would move a resolution date that a reading already
    /// points at.
    /// </summary>
    private static void Resolve(
        SqliteConnection connection, string variantId, string verdict, DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE variant
               SET status = @status,
                   resolved_at = @resolved_at
             WHERE variant_id = @variant_id
               AND status = 'open';
            """;

        command.Parameters.AddWithValue("@status", verdict);
        command.Parameters.AddWithValue("@resolved_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@variant_id", variantId);

        command.ExecuteNonQuery();
    }

    private static void InsertReading(
        SqliteConnection connection,
        StoredVariant variant,
        string direction,
        DateOnly asOf,
        DateTimeOffset observedAt,
        int ageDays,
        AcceptanceTest.Reading reading)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO acceptance_reading (
                variant_id, observed_at, session_date, direction, generation, age_days,
                nights_scored, nights_with_a_figure, nights_identical, nights_in_series, disagreements,
                effective_observations, minimum_sample, minimum_sample_unit, matured,
                mean_difference, interval_low, interval_high, baseline_win_rate, variant_win_rate,
                verdict, settled_because, withheld_because, population)
            VALUES (
                @variant_id, @observed_at, @session_date, @direction, @generation, @age_days,
                @nights_scored, @nights_with_a_figure, @nights_identical, @nights_in_series, @disagreements,
                @effective, @minimum_sample, @minimum_sample_unit, @matured,
                @mean, @low, @high, @baseline_win_rate, @variant_win_rate,
                @verdict, @settled_because, @withheld_because, @population);
            """;

        command.Parameters.AddWithValue("@variant_id", variant.VariantId);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@generation", variant.Generation);
        command.Parameters.AddWithValue("@age_days", ageDays);
        command.Parameters.AddWithValue("@nights_scored", reading.NightsScored);
        command.Parameters.AddWithValue("@nights_with_a_figure", reading.NightsCarryingADifference);
        command.Parameters.AddWithValue("@nights_identical", reading.NightsIdentical);
        command.Parameters.AddWithValue("@nights_in_series", reading.NightsInSeries);
        command.Parameters.AddWithValue("@disagreements", reading.Disagreements);
        command.Parameters.AddWithValue("@effective", reading.EffectiveObservations);
        command.Parameters.AddWithValue("@minimum_sample", reading.MinimumSample);
        command.Parameters.AddWithValue("@minimum_sample_unit", reading.MinimumSampleUnit);
        command.Parameters.AddWithValue("@matured", reading.Matured ? 1 : 0);
        command.Parameters.AddWithValue("@mean", Ratio(reading.Mean));
        command.Parameters.AddWithValue("@low", Ratio(reading.Low));
        command.Parameters.AddWithValue("@high", Ratio(reading.High));
        command.Parameters.AddWithValue("@baseline_win_rate", Ratio(reading.BaselineWinRate));
        command.Parameters.AddWithValue("@variant_win_rate", Ratio(reading.VariantWinRate));
        command.Parameters.AddWithValue("@verdict", reading.Verdict);
        command.Parameters.AddWithValue("@settled_because", (object?)reading.SettledBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@withheld_because", (object?)reading.WithheldBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@population", reading.Population);

        command.ExecuteNonQuery();
    }

    private static object Ratio(decimal? value) =>
        value is decimal d ? StoreText.RatioToStorageText(d) : DBNull.Value;

    private static void WriteRun(
        SqliteConnection connection,
        DateOnly asOf,
        DateTimeOffset observedAt,
        int live,
        int read,
        int baselines,
        int matured,
        int accepted,
        int rejected,
        int open,
        RunOutcome outcome,
        string? stopped)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO acceptance_run (
                session_date, observed_at, versions_live, versions_read, baselines_passed,
                versions_matured, accepted, rejected, left_open, outcome, stopped_because)
            VALUES (
                @session_date, @observed_at, @live, @read, @baselines,
                @matured, @accepted, @rejected, @open, @outcome, @stopped)
            ON CONFLICT (session_date, observed_at) DO NOTHING;
            """;

        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
        command.Parameters.AddWithValue("@live", live);
        command.Parameters.AddWithValue("@read", read);
        command.Parameters.AddWithValue("@baselines", baselines);
        command.Parameters.AddWithValue("@matured", matured);
        command.Parameters.AddWithValue("@accepted", accepted);
        command.Parameters.AddWithValue("@rejected", rejected);
        command.Parameters.AddWithValue("@open", open);
        command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
        command.Parameters.AddWithValue("@stopped", (object?)stopped ?? DBNull.Value);

        command.ExecuteNonQuery();
    }
}

/// <summary>What one run of the gate did, as the stage reports it.</summary>
public sealed record AcceptanceSettlement(
    DateOnly AsOf,
    int VersionsLive,
    int VersionsRead,
    int BaselinesPassed,
    int VersionsMatured,
    int Accepted,
    int Rejected,
    int LeftOpen,
    IReadOnlyList<AcceptanceTest.Reading> Readings,
    int RowsWritten,
    RunOutcome Outcome,
    string? StoppedBecause);
