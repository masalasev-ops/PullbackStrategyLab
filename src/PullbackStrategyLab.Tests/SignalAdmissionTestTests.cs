using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A signal earns its place in the library, or is refused, on rows already stored.
///
/// <b>Every population here is authored, and the reason is a fact about the calendar rather than a
/// convenience.</b> A neighbourhood is tightened or not against outcomes; the first forward night
/// was 2026-08-27 and with Labor Day inside the window the tenth session after it falls on
/// 2026-09-11, so on the day this was written no setup's ten-day horizon had closed and the live
/// store held nought closed outcomes. An authored population is what lets the judgement be
/// exercised at all, and it is built to sit either side of each boundary rather than to be
/// realistic (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it).
///
/// <b>The one failure a passing run would hide is the dimension count.</b> Euclidean distance grows
/// with the width of the space whatever the extra column carries, so a comparison taken on raw
/// distance refuses every candidate and looks exactly like a working test doing it. That is asserted
/// directly rather than left to be inferred from a candidate happening to pass.
/// see: Admission compares outcome-similar pairs against all pairs, and a population too small for the comparison is undecided rather than rejected
/// </summary>
public sealed class SignalAdmissionTestTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    /// <summary>The night the authored setups are flagged on.</summary>
    private static readonly DateOnly Flagged = new(2026, 8, 20);

    /// <summary>The run's own date, after every outcome the population carries was filled.</summary>
    private static readonly DateOnly Today = new(2026, 9, 6);

    /// <summary>
    /// The candidate every authored population carries, standing in for a signal nothing computes
    /// yet. It is a declared candidate rather than an invented name, because the stage only rules
    /// on what the library declares and a name outside it would be ruled on by nothing.
    /// </summary>
    private const string Candidate = "volume_dryup";

    /// <summary>A second declared candidate, for the tests that need two.</summary>
    private const string OtherCandidate = "volume_thrust";

    /// <summary>
    /// The admitted signal the authored space is built out of. One is enough: the correlation is
    /// measured against each admitted signal rather than against an average of them, so a library of
    /// one is the sharpest place to see the limit bind.
    /// </summary>
    private const string Admitted = "retrace_depth";

    public SignalAdmissionTestTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private SignalAdmissionTest Stage() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the deliverable ----------------------------------------------------------------------

    /// <summary>
    /// A candidate that pulls outcome-similar setups closer together is admitted, and the row says
    /// what it was admitted on.
    ///
    /// The population is built so the admitted signal alone says nothing about the outcome and the
    /// candidate tracks it, which is the shape a genuinely discriminating signal has.
    /// </summary>
    [Fact]
    public void A_candidate_that_tightens_outcome_similar_neighbourhoods_is_admitted()
    {
        SeedDiscriminating("long", 24);

        AdmissionResult result = Stage().Admit(Today);

        Assert.Equal(24, result.LongPopulation);
        Assert.Equal(0, result.ShortPopulation);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        StoredRow row = Row(Candidate);

        Assert.Equal(SignalStatus.Active, row.Status);
        Assert.Equal(SignalVerdict.AdmittedOutcome, row.LongOutcome);
        Assert.NotNull(row.LongTightnessBefore);
        Assert.NotNull(row.LongTightnessAfter);

        // The figures are the comparison rather than a score, so the one with the candidate has to
        // be the lower of the two. A row carrying two numbers that do not stand in that relation
        // would be an admission whose own basis contradicts it.
        Assert.True(
            double.Parse(row.LongTightnessAfter!, CultureInfo.InvariantCulture)
            < double.Parse(row.LongTightnessBefore!, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A candidate that restates a signal already present is refused, and the row carries the
    /// correlation it was measured at and which admitted signal it was measured against.
    ///
    /// <b>This is the failure-behaviour row asserted as a behaviour.</b> The document says a
    /// rejection is recorded with both figures rather than discarded, because a library that keeps
    /// only its acceptances cannot say how many times it refused nor what it refused against. The
    /// store's own constraint holds the same property, and `architecture-conformance` reads that;
    /// this is the half that proves a run produces the row at all.
    /// </summary>
    [Fact]
    public void A_candidate_that_restates_an_admitted_signal_is_rejected_with_what_it_was_measured_against()
    {
        SeedRestating("long", 24);

        Stage().Admit(Today);

        StoredRow row = Row(Candidate);

        Assert.Equal(SignalVerdict.RejectedOutcome, row.LongOutcome);
        Assert.Equal(Admitted, row.LongCorrelatedWith);
        Assert.NotNull(row.LongCorrelation);
        Assert.True(double.Parse(row.LongCorrelation!, CultureInfo.InvariantCulture) > SignalAdmission.CorrelationLimit);
        Assert.Contains("above the limit", row.LongBecause);
    }

    /// <summary>
    /// The limit binds where it is stated and not a little either side of it, which is the half a
    /// test over one lopsided population cannot see.
    ///
    /// Two populations, one built to correlate just under 0.70 and one just over, and the verdicts
    /// differ. Without the near-miss the assertion would pass over any limit at all below the
    /// correlation the duplicate happened to reach.
    /// </summary>
    [Fact]
    public void The_correlation_limit_binds_at_the_value_it_is_pinned_to_and_not_either_side_of_it()
    {
        IReadOnlyList<double> admitted = [.. Enumerable.Range(0, 24).Select(i => (double)i)];

        // Two candidates built from the admitted column plus noise, tuned so one sits under the
        // limit and one over it. The figures are asserted rather than assumed, because a generator
        // that drifted would leave both on one side and the test would still pass.
        IReadOnlyList<double> under = [.. admitted.Select((v, i) => v + (i % 2 == 0 ? 14.0 : -14.0))];
        IReadOnlyList<double> over = [.. admitted.Select((v, i) => v + (i % 2 == 0 ? 3.0 : -3.0))];

        double? underCorrelation = SignalAdmission.Correlation(under, admitted);
        double? overCorrelation = SignalAdmission.Correlation(over, admitted);

        Assert.NotNull(underCorrelation);
        Assert.NotNull(overCorrelation);
        Assert.True(underCorrelation < SignalAdmission.CorrelationLimit,
            $"the near-miss column correlates {underCorrelation}, which is not below the limit, so this test "
            + "no longer exercises the boundary from underneath.");
        Assert.True(overCorrelation > SignalAdmission.CorrelationLimit,
            $"the duplicate column correlates {overCorrelation}, which is not above the limit.");

        var active = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal) { [Admitted] = admitted };
        IReadOnlyList<double> outcomes = [.. Enumerable.Range(0, 24).Select(i => i % 3 * 0.01)];

        Assert.NotEqual(
            SignalVerdict.RejectedOutcome,
            SignalAdmission.Judge(Candidate, under, active, outcomes).Outcome);
        Assert.Equal(
            SignalVerdict.RejectedOutcome,
            SignalAdmission.Judge(Candidate, over, active, outcomes).Outcome);
    }

    // ---- the failure a passing run would hide -------------------------------------------------

    /// <summary>
    /// Raw distance grows whether the added column carries anything or not, and the ratio separates
    /// the two cases. That separation is the whole reason the criterion is a ratio.
    ///
    /// <b>The failure a passing run would hide.</b> Euclidean distance in a wider space is larger
    /// whatever the extra dimension holds, so a criterion taken on distance alone refuses a
    /// discriminating candidate for exactly the reason it refuses a noise one, and both refusals
    /// read as the test working. Here the same two candidates are put through both measures: raw
    /// distance grows on both, and the ratio falls on one and rises on the other.
    ///
    /// <b>It is not that the ratio ignores the width.</b> An independent column adds the same
    /// variance to a near pair as to a far one, so it lifts the near pair proportionally more and
    /// the ratio moves toward one: a column carrying nothing genuinely makes the space worse at
    /// separating outcomes, and the criterion is right to say so. What the ratio removes is the part
    /// of the growth that is arithmetic rather than evidence.
    /// </summary>
    [Fact]
    public void Raw_distance_grows_on_a_noise_column_and_on_a_useful_one_and_only_the_ratio_tells_them_apart()
    {
        var random = new Random(20260906);
        IReadOnlyList<double> outcomes = [.. Enumerable.Range(0, 30).Select(i => i % 5 * 0.02)];

        // A base space that says something about the outcome but not everything, so both directions
        // are available to the candidate. A base that predicted the outcome exactly would leave the
        // ratio at the floor with nowhere to fall, which would make the admission half of this test
        // unfalsifiable.
        List<IReadOnlyList<double>> baseSpace =
        [
            .. Enumerable.Range(0, 30).Select(i => (IReadOnlyList<double>)new[] { i % 5 * 1.0 + (i / 5 * 0.9) }),
        ];

        List<IReadOnlyList<double>> withNoise =
            [.. baseSpace.Select(r => (IReadOnlyList<double>)new[] { r[0], random.NextDouble() * 10 })];

        List<IReadOnlyList<double>> withUseful =
            [.. baseSpace.Select((r, i) => (IReadOnlyList<double>)new[] { r[0], outcomes[i] * 100 })];

        double baseline = MeanDistance(baseSpace);

        // Both grow, which is the point. Stated in advance so a generator that stopped adding a
        // column would fail here rather than leaving two equal numbers and a green.
        Assert.True(MeanDistance(withNoise) > baseline,
            $"the noise column left the mean distance at {MeanDistance(withNoise)} against {baseline}.");
        Assert.True(MeanDistance(withUseful) > baseline,
            $"the useful column left the mean distance at {MeanDistance(withUseful)} against {baseline}.");

        double? before = SignalAdmission.NeighbourhoodTightness(baseSpace, outcomes);
        double? noise = SignalAdmission.NeighbourhoodTightness(withNoise, outcomes);
        double? useful = SignalAdmission.NeighbourhoodTightness(withUseful, outcomes);

        Assert.NotNull(before);
        Assert.NotNull(noise);
        Assert.NotNull(useful);

        Assert.True(useful < before,
            $"the ratio went from {before} to {useful} on a column that tracks the outcome exactly, so the "
            + "criterion would refuse a perfectly discriminating signal.");
        Assert.True(noise > before,
            $"the ratio went from {before} to {noise} on a column of noise, so the criterion would admit a "
            + "signal carrying nothing.");
    }

    // ---- the two shapes of undecided ----------------------------------------------------------

    /// <summary>
    /// A candidate nothing computes is recorded undecided saying so, rather than rejected, and that
    /// is the state every candidate in the live library is in today.
    /// </summary>
    [Fact]
    public void A_candidate_nothing_computes_is_undecided_and_the_row_says_which_absence_it_is()
    {
        SeedDiscriminating("long", 24);

        Stage().Admit(Today);

        StoredRow row = Row(OtherCandidate);

        Assert.Equal(SignalVerdict.UndecidedOutcome, row.LongOutcome);
        Assert.Equal(SignalAdmissionTest.NothingComputesIt, row.LongBecause);
        Assert.Equal(SignalStatus.Candidate, row.Status);
    }

    /// <summary>
    /// A population under the floor is undecided naming the count, which is a different absence from
    /// the one above and only this one gets better by waiting.
    /// </summary>
    [Fact]
    public void A_population_under_the_floor_is_undecided_naming_the_count_rather_than_rejected()
    {
        SeedDiscriminating("long", SignalAdmission.MinimumPopulation - 1);

        Stage().Admit(Today);

        StoredRow row = Row(Candidate);

        Assert.Equal(SignalVerdict.UndecidedOutcome, row.LongOutcome);
        Assert.Contains($"{SignalAdmission.MinimumPopulation - 1} setup(s)", row.LongBecause);
        Assert.NotEqual(SignalAdmissionTest.NothingComputesIt, row.LongBecause);
    }

    /// <summary>
    /// An empty store leaves every candidate undecided and every specification row written, which is
    /// what a first run against the live store does today and is the state the record has to be able
    /// to tell apart from a library that measured everything and refused it.
    /// </summary>
    [Fact]
    public void A_store_with_no_closed_outcome_seeds_the_library_and_decides_nothing()
    {
        AdmissionResult result = Stage().Admit(Today);

        Assert.Equal(SignalLibrary.Declared.Count, result.Declared);
        Assert.Equal(SignalLibrary.Declared.Count, result.Seeded);
        Assert.Equal(0, result.LongPopulation);
        Assert.Equal(0, result.ShortPopulation);
        Assert.Equal(SignalLibrary.Candidates.Count, result.Undecided);
        Assert.Equal(0, result.Admitted);
        Assert.Equal(0, result.Rejected);

        foreach (DeclaredSignal candidate in SignalLibrary.Candidates)
        {
            StoredRow row = Row(candidate.Name);
            Assert.Equal(SignalVerdict.UndecidedOutcome, row.LongOutcome);
            Assert.Equal(SignalVerdict.UndecidedOutcome, row.ShortOutcome);
            Assert.Equal(SignalStatus.Candidate, row.Status);
        }
    }

    /// <summary>
    /// A candidate the vectorizer freezes, from 7.4, is undecided for want of rows rather than for
    /// want of a producer, and the row says which. Saying "nothing computes it" of a signal frozen
    /// every night would send a reader to build a producer that exists.
    /// </summary>
    [Fact]
    public void A_frozen_candidate_is_undecided_for_want_of_rows_rather_than_of_a_producer()
    {
        Stage().Admit(Today);

        Assert.Equal(SignalAdmissionTest.NotYetOnEverySetup, Row("entry_ceiling").LongBecause);
        Assert.Equal(SignalAdmissionTest.NothingComputesIt, Row("volume_dryup").LongBecause);
    }

    // ---- the two sides ------------------------------------------------------------------------

    /// <summary>
    /// The two sides are judged over their own populations and the row carries both, which is the
    /// pooling rule at the point it would otherwise be broken.
    ///
    /// The long side is built so the candidate discriminates and the short side so it restates an
    /// admitted signal, and the two verdicts come back different on one row. A pooled implementation
    /// would produce one verdict and there would be nowhere for the second to go.
    /// </summary>
    [Fact]
    public void The_two_sides_are_judged_over_their_own_populations_and_the_row_carries_both()
    {
        SeedDiscriminating("long", 24);
        SeedRestating("short", 24);

        AdmissionResult result = Stage().Admit(Today);

        Assert.Equal(24, result.LongPopulation);
        Assert.Equal(24, result.ShortPopulation);

        StoredRow row = Row(Candidate);

        Assert.Equal(SignalVerdict.AdmittedOutcome, row.LongOutcome);
        Assert.Equal(SignalVerdict.RejectedOutcome, row.ShortOutcome);

        // Admitted on one side is admitted, because the library is one library. The short side's
        // refusal stays legible on the row rather than being lost to the status.
        Assert.Equal(SignalStatus.Active, row.Status);
        Assert.Equal(Admitted, row.ShortCorrelatedWith);
    }

    /// <summary>
    /// A signal both sides refuse is the one that reaches the rejected status, which is the other
    /// half of the asymmetry above.
    /// </summary>
    [Fact]
    public void A_signal_both_sides_refuse_is_the_one_the_status_records_as_rejected()
    {
        SeedRestating("long", 24);
        SeedRestating("short", 24);

        Stage().Admit(Today);

        StoredRow row = Row(Candidate);

        Assert.Equal(SignalStatus.RejectedCorrelation, row.Status);
        Assert.Equal(SignalVerdict.RejectedOutcome, row.LongOutcome);
        Assert.Equal(SignalVerdict.RejectedOutcome, row.ShortOutcome);
    }

    // ---- the seed -----------------------------------------------------------------------------

    /// <summary>
    /// A second run over an unchanged store writes nothing, which is what keeps `observed_at` the
    /// date the row last changed rather than the date the stage last ran.
    /// </summary>
    [Fact]
    public void A_second_run_over_an_unchanged_store_writes_nothing()
    {
        Stage().Admit(Today);

        AdmissionResult second = Stage().Admit(Today);

        Assert.Equal(0, second.Seeded);
        Assert.Equal(SignalLibrary.Declared.Count, second.Unchanged);
    }

    /// <summary>
    /// The planted null control is seeded carrying its flag and nothing else is, which is the one
    /// row 6.4 will look for.
    /// see: One meaningless signal is planted in the conditional tables
    /// </summary>
    [Fact]
    public void Exactly_one_seeded_row_carries_the_null_control_flag()
    {
        Stage().Admit(Today);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT signal_name FROM signal_definition WHERE is_null_control = 1";

        var flagged = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            flagged.Add(reader.GetString(0));
        }

        Assert.Equal([SignalLibrary.NullControl], flagged);
    }

    // ---- seeding ------------------------------------------------------------------------------

    private static double MeanDistance(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        var distances = new List<double>();

        for (int i = 0; i < rows.Count; i++)
        {
            for (int j = i + 1; j < rows.Count; j++)
            {
                double sum = 0;
                for (int c = 0; c < rows[i].Count; c++)
                {
                    double d = rows[i][c] - rows[j][c];
                    sum += d * d;
                }

                distances.Add(Math.Sqrt(sum));
            }
        }

        return distances.Average();
    }

    /// <summary>
    /// A population where the admitted signal says nothing about the outcome and the candidate
    /// tracks it, so outcome-similar setups sit closer once the candidate is part of the space.
    /// </summary>
    private void SeedDiscriminating(string direction, int rows)
    {
        for (int i = 0; i < rows; i++)
        {
            double outcome = (i % 3 - 1) * 0.05;
            Seed(direction, i, outcome, admitted: (i * 7 % 11) - 5, candidate: outcome * 100);
        }
    }

    /// <summary>A population where the candidate is the admitted signal with a little noise on it.</summary>
    private void SeedRestating(string direction, int rows)
    {
        for (int i = 0; i < rows; i++)
        {
            double admitted = (i * 7 % 11) - 5;
            Seed(direction, i, (i % 3 - 1) * 0.05, admitted, candidate: admitted + (i % 2 == 0 ? 0.4 : -0.4));
        }
    }

    /// <summary>One authored setup with a closed outcome and two frozen signal values.</summary>
    private void Seed(string direction, int index, double outcome, double admitted, double candidate)
    {
        string setupId = $"{Flagged:yyyy-MM-dd}-{direction}-{index:00}";

        Execute("""
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
            ON CONFLICT (ticker) DO NOTHING
            """,
            ("@ticker", $"T{index:00}"));

        Execute("""
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@setup_id, @as_of, @ticker, @direction, '{}', 1)
            """,
            ("@setup_id", setupId),
            ("@as_of", StoreText.DateToStorageText(Flagged)),
            ("@ticker", $"T{index:00}"),
            ("@direction", direction));

        Execute("""
            INSERT INTO forward_return (subject_id, subject_kind, horizon_days, intended_date,
                                        actual_date, return_signed, mfe_atr, mae_atr, filled_at)
            VALUES (@setup_id, 'setup', @horizon, @date, @date, @return, '1.0', '1.0', @filled_at)
            """,
            ("@setup_id", setupId),
            ("@horizon", MeasurementParameters.ScoringHorizonSessions),
            ("@date", StoreText.DateToStorageText(Flagged.AddDays(14))),
            ("@return", StoreText.StatisticToStorageText(outcome)),
            ("@filled_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));

        Freeze(setupId, Admitted, admitted);
        Freeze(setupId, Candidate, candidate);
    }

    private void Freeze(string setupId, string name, double value) =>
        Execute("""
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES (@setup_id, @signal_name, @value, @computed_at)
            """,
            ("@setup_id", setupId),
            ("@signal_name", name),
            ("@value", StoreText.StatisticToStorageText(value)),
            ("@computed_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private StoredRow Row(string name)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, long_outcome, long_because, long_tightness_before, long_tightness_after,
                   long_correlation, long_correlated_with,
                   short_outcome, short_because, short_correlation, short_correlated_with
              FROM signal_definition
             WHERE signal_name = @signal_name
            """;

        command.Parameters.AddWithValue("@signal_name", name);

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"signal_definition holds no row for {name}.");

        return new StoredRow(
            reader.GetString(0),
            Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6),
            Text(reader, 7), Text(reader, 8), Text(reader, 9), Text(reader, 10));
    }

    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record StoredRow(
        string Status,
        string? LongOutcome,
        string? LongBecause,
        string? LongTightnessBefore,
        string? LongTightnessAfter,
        string? LongCorrelation,
        string? LongCorrelatedWith,
        string? ShortOutcome,
        string? ShortBecause,
        string? ShortCorrelation,
        string? ShortCorrelatedWith);
}
