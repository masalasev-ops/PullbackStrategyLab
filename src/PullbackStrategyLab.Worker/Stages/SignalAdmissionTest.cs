using System.Globalization;
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
/// Seeds the signal library into the store and rules on every candidate in it.
///
/// <b>Two jobs in one stage because they are one question asked twice.</b> The seed answers "what
/// does the specification declare", and the judgement answers "what has the evidence said about the
/// part of it nothing computes yet". Splitting them would put the library's only writer in one
/// component and the verdicts that change it in another, and `signal_definition` declares one
/// writer.
///
/// <b>The specification is SCHEMA.md's Signals section and this stage never invents a row.</b>
/// Every row it writes comes from <see cref="SignalLibrary.Declared"/>, which is the runnable copy
/// of that section, and `signal-library` reconciles the two in both directions. A signal reaching
/// the store that the section does not declare would be a library nobody wrote down.
/// see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
///
/// <b>It judges per side and never over both.</b> The tightness ratio is a mean over pairs of
/// setups whose outcomes sit near each other, and a long outcome and a short outcome are signed in
/// opposite senses, so a pooled population would measure the gap between the two books and report
/// it as discrimination. The two verdicts are taken separately, stored in columns named for their
/// side, and never added.
/// see: Long and short are never pooled into one figure
///
/// <b>Today every candidate is undecided and the row says which shape of undecided it is.</b> No
/// setup's ten-day horizon has closed, and no candidate has a value on any setup because nothing
/// computes one. Those are two different absences and the record keeps them apart: a library that
/// wrote "undecided" over both would be indistinguishable in six months from a library whose
/// candidates were all measured and all failed.
/// see: Admission compares outcome-similar pairs against all pairs, and a population too small for the comparison is undecided rather than rejected
/// </summary>
public sealed class SignalAdmissionTest
{
    public const string Name = "admit-signals";

    /// <summary>
    /// Why a candidate could not be judged at all: nothing computes it, so no setup carries a value.
    ///
    /// Distinct from the population floor on purpose. A candidate with no producer has not been
    /// measured over a thin population; it has not been measured. What closes it is SignalBackfiller
    /// computing the declared formula across the stored history, which is a build task rather than
    /// an accumulation.
    /// </summary>
    public const string NothingComputesIt =
        "no setup carries a value for it, because nothing computes it yet";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public SignalAdmissionTest(
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

    /// <summary><c>admit-signals [as-of]</c>.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        AdmissionResult result = Admit(asOf);

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, {result.Declared} signal(s) declared, "
            + $"{result.Seeded} written, {result.Unchanged} unchanged");
        Console.WriteLine(
            $"{Name}: long population {result.LongPopulation} setup(s) with a closed outcome, "
            + $"short population {result.ShortPopulation}, never added");
        Console.WriteLine(
            $"{Name}: {result.Candidates} candidate(s), {result.Admitted} admitted, "
            + $"{result.Rejected} rejected at the correlation limit, {result.Undecided} undecided");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One pass: seed the library, then rule on every candidate in it.</summary>
    public AdmissionResult Admit(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "signal_definition");

        DateTimeOffset observedAt = run.StartedAt;

        Population longs = PopulationFor(connection, SetupDirection.Long, asOf);
        Population shorts = PopulationFor(connection, SetupDirection.Short, asOf);

        int seeded = 0;
        int unchanged = 0;
        int admitted = 0;
        int rejected = 0;
        int undecided = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (DeclaredSignal signal in SignalLibrary.Declared)
            {
                SignalVerdict? onLong = null;
                SignalVerdict? onShort = null;

                if (signal.Status == SignalStatus.Candidate)
                {
                    onLong = Verdict(signal.Name, longs);
                    onShort = Verdict(signal.Name, shorts);

                    if (onLong.Outcome == SignalVerdict.AdmittedOutcome
                        || onShort.Outcome == SignalVerdict.AdmittedOutcome)
                    {
                        admitted++;
                    }
                    else if (onLong.Outcome == SignalVerdict.RejectedOutcome
                             && onShort.Outcome == SignalVerdict.RejectedOutcome)
                    {
                        rejected++;
                    }
                    else if (onLong.Outcome == SignalVerdict.UndecidedOutcome
                             && onShort.Outcome == SignalVerdict.UndecidedOutcome)
                    {
                        undecided++;
                    }
                }

                if (Write(connection, transaction, signal, onLong, onShort, observedAt))
                {
                    seeded++;
                }
                else
                {
                    unchanged++;
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new AdmissionResult(
            asOf,
            SignalLibrary.Declared.Count,
            seeded,
            unchanged,
            longs.Outcomes.Count,
            shorts.Outcomes.Count,
            SignalLibrary.Candidates.Count,
            admitted,
            rejected,
            undecided,
            summary.RowsWritten,
            RunOutcome.Clean);
    }

    /// <summary>
    /// The verdict for one candidate over one side's population, with the two absences kept apart.
    ///
    /// A candidate nothing computes carries no values at all, which is a different state from a
    /// candidate measured over a population too thin to say anything, and only the second one gets
    /// better by waiting.
    /// </summary>
    private static SignalVerdict Verdict(string candidate, Population population)
    {
        if (!population.Values.TryGetValue(candidate, out IReadOnlyList<double>? values))
        {
            return SignalVerdict.Undecided(candidate, NothingComputesIt);
        }

        return SignalAdmission.Judge(candidate, values, population.Active, population.Outcomes);
    }

    /// <summary>
    /// One side's judgeable population: the setups of that direction whose scoring-horizon outcome
    /// the lab could have had by <paramref name="asOf"/>, and the numeric signal values they carry.
    ///
    /// <b>A signal is in the space only where every row of the population carries it as a number.</b>
    /// The library holds words as well as numbers, and `regime_label`, `industry`, `thrust_scan` and
    /// `ladder_grade` are categories rather than quantities: a distance taken over them would be a
    /// distance over whatever their text happened to parse as. A signal missing on some rows is
    /// excluded for the same reason, because filling a gap with nought would put every row that has
    /// no value at the middle of the distribution and call that a measurement.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    private Population PopulationFor(SqliteConnection connection, string direction, DateOnly asOf)
    {
        IReadOnlyDictionary<string, decimal> outcomes = Outcomes(connection, direction, asOf);

        if (outcomes.Count == 0)
        {
            return new Population(
                [],
                new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal),
                new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal));
        }

        // Read through the backfill-aware reader, which is what a research read of stored history
        // uses: a value computed later from the night's own inputs is admissible evidence about
        // that night, and bounding on the instant the arithmetic ran would make every backfilled
        // signal invisible to the library that asked for it.
        // see: A backfilled signal is read by a replay and not by a surface, because point-in-time is a property of the inputs
        var byName = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        foreach (DateOnly session in SetupReader.Sessions(connection, direction, DateOnly.MinValue, asOf))
        {
            foreach (StoredSetupSignal signal in SetupSignalReader.ReadIncludingBackfilled(connection, session))
            {
                if (!outcomes.ContainsKey(signal.SetupId)
                    || !double.TryParse(signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    continue;
                }

                if (!byName.TryGetValue(signal.SignalName, out Dictionary<string, double>? column))
                {
                    column = new Dictionary<string, double>(StringComparer.Ordinal);
                    byName[signal.SignalName] = column;
                }

                column[signal.SetupId] = value;
            }
        }

        // A fixed row order, so every column is over the same rows in the same order and the space
        // is the same space from one signal to the next.
        IReadOnlyList<string> rows = [.. outcomes.Keys.OrderBy(k => k, StringComparer.Ordinal)];

        var values = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);

        foreach ((string name, Dictionary<string, double> column) in byName)
        {
            if (rows.Any(r => !column.ContainsKey(r)))
            {
                continue;
            }

            values[name] = [.. rows.Select(r => column[r])];
        }

        var active = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);

        foreach (DeclaredSignal signal in SignalLibrary.Active)
        {
            if (values.TryGetValue(signal.Name, out IReadOnlyList<double>? column))
            {
                active[signal.Name] = column;
            }
        }

        return new Population(
            [.. rows.Select(r => (double)outcomes[r])],
            active,
            values);
    }

    /// <summary>
    /// The scoring-horizon outcome of one direction's setups, bounded on `filled_at`.
    ///
    /// Bounded because a return filled tomorrow is not evidence this run could have had, and the
    /// whole point of judging a signal on stored rows is that the judgement is one a replay can
    /// reproduce.
    /// </summary>
    private IReadOnlyDictionary<string, decimal> Outcomes(
        SqliteConnection connection, string direction, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.subject_id, f.return_signed
              FROM forward_return f
              JOIN setup s ON s.setup_id = f.subject_id
             WHERE f.subject_kind = 'setup'
               AND f.horizon_days = @horizon
               AND f.filled_at <= @filled_before
               AND s.direction = @direction
               AND s.as_of <= @as_of
            """;

        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@filled_before", StoreText.EndOfSession(asOf, _options.SessionZone));
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var outcomes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            outcomes[reader.GetString(0)] = StoreText.StorageTextToRatio(reader.GetString(1));
        }

        return outcomes;
    }

    /// <summary>
    /// One library row, written where anything about it has changed and left alone where nothing
    /// has.
    ///
    /// <b>The unchanged case is why the write is conditional rather than unconditional.</b>
    /// `observed_at` is what a read of the library bounds on, so rewriting every row on every run
    /// would move the stamp on thirty-five rows nightly and lose the date a verdict was actually
    /// taken. The return value is whether the store changed, which is what the run counts.
    /// </summary>
    private static bool Write(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeclaredSignal signal,
        SignalVerdict? onLong,
        SignalVerdict? onShort,
        DateTimeOffset observedAt)
    {
        string status = StatusOf(signal, onLong, onShort);
        Stored? existing = Read(connection, transaction, signal.Name);

        var row = new Stored(
            signal.Formula,
            signal.SourceColumns,
            status,
            signal.IsNullControl,
            onLong?.Outcome,
            onLong?.Because,
            Statistic(onLong?.TightnessBefore),
            Statistic(onLong?.TightnessAfter),
            Statistic(onLong?.Correlation),
            onLong?.CorrelatedWith,
            onShort?.Outcome,
            onShort?.Because,
            Statistic(onShort?.TightnessBefore),
            Statistic(onShort?.TightnessAfter),
            Statistic(onShort?.Correlation),
            onShort?.CorrelatedWith);

        if (existing == row)
        {
            return false;
        }

        string? decidedAt = onLong is null && onShort is null
            ? null
            : StoreText.TimestampToStorageText(observedAt);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_definition (
                signal_name, formula, source_columns, status, is_null_control, decided_at,
                long_outcome, long_because, long_tightness_before, long_tightness_after,
                long_correlation, long_correlated_with,
                short_outcome, short_because, short_tightness_before, short_tightness_after,
                short_correlation, short_correlated_with,
                observed_at)
            VALUES (
                @signal_name, @formula, @source_columns, @status, @is_null_control, @decided_at,
                @long_outcome, @long_because, @long_tightness_before, @long_tightness_after,
                @long_correlation, @long_correlated_with,
                @short_outcome, @short_because, @short_tightness_before, @short_tightness_after,
                @short_correlation, @short_correlated_with,
                @observed_at)
            ON CONFLICT (signal_name) DO UPDATE SET
                formula = excluded.formula,
                source_columns = excluded.source_columns,
                status = excluded.status,
                is_null_control = excluded.is_null_control,
                decided_at = excluded.decided_at,
                long_outcome = excluded.long_outcome,
                long_because = excluded.long_because,
                long_tightness_before = excluded.long_tightness_before,
                long_tightness_after = excluded.long_tightness_after,
                long_correlation = excluded.long_correlation,
                long_correlated_with = excluded.long_correlated_with,
                short_outcome = excluded.short_outcome,
                short_because = excluded.short_because,
                short_tightness_before = excluded.short_tightness_before,
                short_tightness_after = excluded.short_tightness_after,
                short_correlation = excluded.short_correlation,
                short_correlated_with = excluded.short_correlated_with,
                observed_at = excluded.observed_at
            """;

        command.Parameters.AddWithValue("@signal_name", signal.Name);
        command.Parameters.AddWithValue("@formula", row.Formula);
        command.Parameters.AddWithValue("@source_columns", row.SourceColumns);
        command.Parameters.AddWithValue("@status", row.Status);
        command.Parameters.AddWithValue("@is_null_control", row.IsNullControl ? 1 : 0);
        command.Parameters.AddWithValue("@decided_at", (object?)decidedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_outcome", (object?)row.LongOutcome ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_because", (object?)row.LongBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_tightness_before", (object?)row.LongBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_tightness_after", (object?)row.LongAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_correlation", (object?)row.LongCorrelation ?? DBNull.Value);
        command.Parameters.AddWithValue("@long_correlated_with", (object?)row.LongAgainst ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_outcome", (object?)row.ShortOutcome ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_because", (object?)row.ShortBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_tightness_before", (object?)row.ShortBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_tightness_after", (object?)row.ShortAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_correlation", (object?)row.ShortCorrelation ?? DBNull.Value);
        command.Parameters.AddWithValue("@short_correlated_with", (object?)row.ShortAgainst ?? DBNull.Value);
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        command.ExecuteNonQuery();
        return true;
    }

    /// <summary>
    /// What the library row's status becomes: the specification's, unless both sides refused the
    /// candidate at the correlation limit, or either side admitted it.
    ///
    /// Either-side admission and both-side rejection are asymmetric on purpose. A signal that
    /// discriminates among shorts is evidence, and the library is one library, so it is computed on
    /// every setup and the per-side outcomes say where it earned its place. A signal is a
    /// restatement of something already present only if it is one on both sides.
    /// </summary>
    private static string StatusOf(DeclaredSignal signal, SignalVerdict? onLong, SignalVerdict? onShort)
    {
        if (onLong is null || onShort is null)
        {
            return signal.Status;
        }

        if (onLong.Outcome == SignalVerdict.AdmittedOutcome
            || onShort.Outcome == SignalVerdict.AdmittedOutcome)
        {
            return SignalStatus.Active;
        }

        return onLong.Outcome == SignalVerdict.RejectedOutcome
               && onShort.Outcome == SignalVerdict.RejectedOutcome
            ? SignalStatus.RejectedCorrelation
            : signal.Status;
    }

    private static string? Statistic(double? value) =>
        value is double d ? StoreText.StatisticToStorageText(d) : null;

    /// <summary>The stored row as it stands, so a run that changes nothing writes nothing.</summary>
    private static Stored? Read(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT formula, source_columns, status, is_null_control,
                   long_outcome, long_because, long_tightness_before, long_tightness_after,
                   long_correlation, long_correlated_with,
                   short_outcome, short_because, short_tightness_before, short_tightness_after,
                   short_correlation, short_correlated_with
              FROM signal_definition
             WHERE signal_name = @signal_name
            """;

        command.Parameters.AddWithValue("@signal_name", name);

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return new Stored(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3) == 1,
            Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9),
            Text(reader, 10), Text(reader, 11), Text(reader, 12), Text(reader, 13), Text(reader, 14), Text(reader, 15));
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>One library row without its stamps, which is what "nothing changed" compares.</summary>
    private sealed record Stored(
        string Formula,
        string SourceColumns,
        string Status,
        bool IsNullControl,
        string? LongOutcome,
        string? LongBecause,
        string? LongBefore,
        string? LongAfter,
        string? LongCorrelation,
        string? LongAgainst,
        string? ShortOutcome,
        string? ShortBecause,
        string? ShortBefore,
        string? ShortAfter,
        string? ShortCorrelation,
        string? ShortAgainst);

    /// <summary>One side's judgeable rows: the outcomes, the admitted columns, and every column read.</summary>
    private sealed record Population(
        IReadOnlyList<double> Outcomes,
        IReadOnlyDictionary<string, IReadOnlyList<double>> Active,
        IReadOnlyDictionary<string, IReadOnlyList<double>> Values);
}

/// <summary>
/// What one admission run did.
///
/// The two populations are stated apart and never added, on the same terms every other per-side
/// figure in this lab is (see: Long and short are never pooled into one figure). <c>Admitted</c>,
/// <c>Rejected</c> and <c>Undecided</c> count candidates whose two sides agreed on that outcome, so
/// they do not sum to <c>Candidates</c> wherever a side disagreed with the other, which is a fact
/// about the evidence rather than an arithmetic slip.
/// </summary>
public sealed record AdmissionResult(
    DateOnly AsOf,
    int Declared,
    int Seeded,
    int Unchanged,
    int LongPopulation,
    int ShortPopulation,
    int Candidates,
    int Admitted,
    int Rejected,
    int Undecided,
    int RowsWritten,
    RunOutcome Outcome);
