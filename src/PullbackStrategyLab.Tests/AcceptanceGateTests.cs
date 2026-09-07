using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What settles a version, and what may not.
///
/// <b>Every case here is authored and there is no other footing.</b> No version has matured, none
/// can: V0 was frozen on 2026-09-03 against 1,802 effective paired setup observations and a
/// version's effective count rises at most one a night, so the captured store cannot exercise a
/// settlement in this decade. What the fixture can exercise is the case the failure table names,
/// and the rest is written as series either side of each boundary.
/// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
///
/// <b>The minimum is lowered on the authored versions and never in the shipped constant.</b> A
/// series long enough to clear 1,802 would be 1,802 nights of authored rows, so the pre-registration
/// on these rows carries a small figure and the version is settled against its own. That is what a
/// pre-registration is: the figure written into the row rather than a constant the gate reads
/// (see: Targets and minimum samples are written at creation and are immutable).
/// </summary>
public sealed class AcceptanceGateTests : IDisposable
{
    private static readonly DateOnly Evening = new(2026, 9, 4);
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 22, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(Now);
    private readonly IOptions<PullbackStrategyLabOptions> _options;

    public AcceptanceGateTests()
    {
        _options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        using SqliteConnection seed = _connections.OpenWrite();
        TestVersions.SeedBaseline(seed, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A version short of its pre-registered sample stays open, and the reading says by how much.
    ///
    /// <b>This is the failure table's own row and it is the state the lab is actually in.</b> A
    /// version whose sample never accumulates stays open, the ledger shows its age, and there is no
    /// timeout that quietly accepts or rejects it, because a timeout is a decision made by the
    /// calendar rather than by evidence.
    /// </summary>
    [Fact]
    public void A_version_short_of_its_sample_stays_open_and_the_reading_says_by_how_much()
    {
        SeedVersion("V1", minimumSample: 40);
        SeedSeries("V1", nights: 6, difference: 0.02m);

        AcceptanceSettlement settlement = Gate().Settle(Evening);

        Assert.Equal(1, settlement.VersionsRead);
        Assert.Equal(0, settlement.VersionsMatured);
        Assert.Equal(1, settlement.LeftOpen);
        Assert.Equal(0, settlement.Accepted + settlement.Rejected);

        StoredVariant stored = Version("V1");
        Assert.Equal(VariantStatus.Open, stored.Status);
        Assert.Null(stored.ResolvedAt);

        StoredAcceptanceReading reading = Reading("V1");
        Assert.Equal(VariantStatus.Open, reading.Verdict);
        Assert.False(reading.Matured);
        Assert.Null(reading.SettledBecause);
        Assert.Contains("of 40", reading.WithheldBecause!, StringComparison.Ordinal);

        // The age, which is the clause the failure table asks the ledger to show. The version was
        // registered on 2026-08-25 and this evening is 2026-09-04.
        Assert.Equal(10, reading.AgeDays);
    }

    /// <summary>
    /// A matured version whose interval clears nought is accepted, and the gate writes two columns.
    ///
    /// <b>The other columns are read back afterwards and asserted unchanged.</b> That the type has
    /// no path to a target is a claim about the code; that the row still carries the target it was
    /// registered with is a claim about what happened, and only the second survives a refactor.
    /// see: Targets and minimum samples are written at creation and are immutable
    /// </summary>
    [Fact]
    public void A_matured_version_whose_interval_clears_nought_is_accepted_and_two_columns_move()
    {
        SeedVersion("V1", minimumSample: 20);
        StoredVariant before = Version("V1");

        SeedSeries("V1", nights: 40, difference: 0.05m, wobble: 0.002m);

        AcceptanceSettlement settlement = Gate().Settle(Evening);

        Assert.Equal(1, settlement.Accepted);
        Assert.Equal(1, settlement.VersionsMatured);

        StoredVariant after = Version("V1");
        Assert.Equal(VariantStatus.Accepted, after.Status);
        Assert.Equal(Now, after.ResolvedAt);

        Assert.Equal(before.Target, after.Target);
        Assert.Equal(before.MinimumSample, after.MinimumSample);
        Assert.Equal(before.MinimumSampleUnit, after.MinimumSampleUnit);
        Assert.Equal(before.Definition, after.Definition);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.Moved!.To, after.Moved!.To);

        StoredAcceptanceReading reading = Reading("V1");
        Assert.True(reading.Matured);
        Assert.NotNull(reading.MeanDifference);
        Assert.NotNull(reading.IntervalLow);
        Assert.Contains("clears nought", reading.SettledBecause!, StringComparison.Ordinal);
    }

    /// <summary>A matured version whose interval does not clear nought is rejected, and stays in the register.</summary>
    [Fact]
    public void A_matured_version_whose_interval_does_not_clear_is_rejected()
    {
        SeedVersion("V1", minimumSample: 20);
        SeedSeries("V1", nights: 40, difference: 0m, wobble: 0.01m);

        Gate().Settle(Evening);

        StoredVariant after = Version("V1");
        Assert.Equal(VariantStatus.Rejected, after.Status);
        Assert.NotNull(after.ResolvedAt);

        Assert.Contains(
            "does not clear nought", Reading("V1").SettledBecause!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version that raised the win rate while lowering expectancy is rejected under that name.
    ///
    /// <b>The name is the point rather than the verdict.</b> Such a version would be rejected by the
    /// bound anyway; what would be lost is which lever it pulled, and widening a stop is the change
    /// that pulls this one. The rule was written before any result existed on purpose.
    /// see: Acceptance measures expectancy, never win rate
    /// </summary>
    [Fact]
    public void A_version_that_bought_win_rate_with_expectancy_is_rejected_under_that_name()
    {
        SeedVersion("V1", minimumSample: 20);

        // More of the version's selections end ahead, and the mean difference is negative: the
        // shape of widening a stop, where more trades win and each one is worth far less.
        SeedSeries(
            "V1", nights: 40, difference: -0.02m, wobble: 0.002m,
            baselineWins: 1, variantWins: 4);

        Gate().Settle(Evening);

        StoredAcceptanceReading reading = Reading("V1");
        Assert.Equal(VariantStatus.Rejected, reading.Verdict);
        Assert.Equal(AcceptanceTest.WinRateForExpectancy, reading.SettledBecause);

        // Both rates on the row, over their own denominators. One denominator would put one rule's
        // wins over the other's population.
        Assert.NotNull(reading.BaselineWinRate);
        Assert.NotNull(reading.VariantWinRate);
        Assert.NotEqual(reading.BaselineWinRate, reading.VariantWinRate);
    }

    /// <summary>
    /// A night the two rules selected identically is counted and kept out of the series.
    ///
    /// <b>The permanent proof of the population guard, and the fault it removes is arithmetic.</b>
    /// Such a night carries a difference of exactly nought by construction, so counting it would
    /// advance the version toward its sample on nights it was never exercised on and pull the mean
    /// toward nought at the same time. Every count would have been right and the verdict wrong.
    /// </summary>
    [Fact]
    public void A_night_the_two_rules_agreed_on_is_counted_and_kept_out_of_the_series()
    {
        SeedVersion("V1", minimumSample: 20);

        SeedSeries("V1", nights: 40, difference: 0.05m, wobble: 0.002m);
        SeedSeries("V1", nights: 30, difference: 0m, from: new DateOnly(2026, 6, 1), identical: true);

        Gate().Settle(Evening);

        StoredAcceptanceReading reading = Reading("V1");

        Assert.Equal(70, reading.NightsScored);
        Assert.Equal(30, reading.NightsIdentical);
        Assert.Equal(40, reading.NightsInSeries);

        // The count the version is settled at is over the forty, not the seventy, and the mean is
        // the forty's rather than a figure the thirty pulled down.
        Assert.True(reading.EffectiveObservations <= 40);
        Assert.Equal(VariantStatus.Accepted, reading.Verdict);
    }

    /// <summary>
    /// The baseline is passed over, and the run counts it rather than filtering it silently.
    ///
    /// It is the arm every other version is differenced against, so there is no series to take an
    /// interval over. V0's own pre-registration says so: it is not itself accepted or rejected.
    /// </summary>
    [Fact]
    public void The_baseline_is_passed_over_and_the_run_counts_it()
    {
        SeedVersion("V1", minimumSample: 40);

        AcceptanceSettlement settlement = Gate().Settle(Evening);

        Assert.Equal(2, settlement.VersionsLive);
        Assert.Equal(1, settlement.VersionsRead);
        Assert.Equal(1, settlement.BaselinesPassed);

        Assert.Equal(VariantStatus.Open, Version(TestVersions.Baseline).Status);
        Assert.DoesNotContain(
            Readings(),
            r => string.Equals(r.VariantId, TestVersions.Baseline, StringComparison.Ordinal));
    }

    /// <summary>
    /// A register holding nothing but the baseline reports partial and says why.
    ///
    /// That is the state after the freeze and before the first proposal is accepted into a version,
    /// which is every night this lab has run, and a failed run would take the slots after it.
    /// </summary>
    [Fact]
    public void A_register_holding_only_the_baseline_is_partial_and_says_why()
    {
        AcceptanceSettlement settlement = Gate().Settle(Evening);

        Assert.Equal(RunOutcome.Partial, settlement.Outcome);
        Assert.Equal(AcceptanceGate.NothingToSettle, settlement.StoppedBecause);
        Assert.Empty(Readings());
    }

    /// <summary>
    /// A second run of an evening does not re-settle what the first settled.
    ///
    /// The gate reads open versions, so a settled one is out of its population; and the statement is
    /// guarded on the version still being open besides, so a second write could not move a
    /// resolution date that a reading already points at.
    /// </summary>
    [Fact]
    public void A_second_run_does_not_move_a_resolution_the_first_wrote()
    {
        SeedVersion("V1", minimumSample: 20);
        SeedSeries("V1", nights: 40, difference: 0.05m, wobble: 0.002m);

        Gate().Settle(Evening);
        DateTimeOffset? first = Version("V1").ResolvedAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        AcceptanceSettlement second = Gate().Settle(Evening);

        // The version is settled, so it is no longer among the open versions this gate reads.
        Assert.Equal(0, second.VersionsRead);
        Assert.Equal(first, Version("V1").ResolvedAt);
        Assert.Single(Readings());
    }

    /// <summary>
    /// A reading taken after the as-of is invisible to a read standing at it.
    ///
    /// <b>The half of point-in-time a signature cannot establish.</b> A ledger opened on an old date
    /// must show where a version stood then, and an unbounded read would show it settled on an
    /// evening it was still open.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    [Fact]
    public void A_reading_taken_after_the_as_of_is_invisible_to_a_read_standing_at_it()
    {
        SeedVersion("V1", minimumSample: 40);
        Gate().Settle(Evening);

        Assert.Single(Readings());
        Assert.Empty(Readings(Evening.AddDays(-1)));
    }

    /// <summary>
    /// The gate's only statement against the register names two columns.
    ///
    /// <b>A source scan, and it is declared in `fixtures/source-scans.json` with the behavioural
    /// test that carries the claim.</b> The scan says there is no second statement to declare, which
    /// is what makes the writer-ownership row true; what says the columns did not move is the
    /// acceptance test above, which reads them back after a settlement.
    /// see: Targets and minimum samples are written at creation and are immutable
    /// </summary>
    [Fact]
    public void The_gate_carries_one_update_against_the_register_and_it_names_two_columns()
    {
        string source = RepositoryLayout.Read(Path.Combine(
            RepositoryLayout.Source, "PullbackStrategyLab.Worker", "Stages", "AcceptanceGate.cs"));

        MatchCollection updates = Regex.Matches(
            source, @"UPDATE\s+variant\b", RegexOptions.IgnoreCase);

        Assert.Single(updates);

        MatchCollection setClauses = Regex.Matches(
            source, @"SET\s+status\s*=\s*@status,\s*resolved_at\s*=\s*@resolved_at",
            RegexOptions.IgnoreCase);

        Assert.Single(setClauses);

        // And no statement anywhere in the file mentions a column the pre-registration owns, which
        // is the half a two-column SET cannot say on its own.
        foreach (string column in new[] { "target", "minimum_sample", "created_at" })
        {
            Assert.DoesNotContain(
                $"SET {column}", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The store refuses a settled reading that never matured, which is the calendar rule as a CHECK.
    ///
    /// The gate cannot write one, so this asserts the store rather than the stage: a later stage
    /// that decided a version had waited long enough would be refused by the row rather than by
    /// somebody remembering the rule.
    /// </summary>
    [Fact]
    public void The_store_refuses_a_settlement_that_never_matured()
    {
        SeedVersion("V1", minimumSample: 40);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO acceptance_reading (
                variant_id, observed_at, session_date, direction, generation, age_days,
                nights_scored, nights_with_a_figure, nights_identical, nights_in_series, disagreements,
                effective_observations, minimum_sample, minimum_sample_unit, matured,
                mean_difference, interval_low, interval_high, baseline_win_rate, variant_win_rate,
                verdict, settled_because, withheld_because, population)
            VALUES (
                'V1', '2026-09-04T23:00:00.000Z', '2026-09-04', 'long', 0, 10,
                6, 6, 0, 6, 12,
                6, 40, 'effective_paired_setup_observations', 0,
                NULL, NULL, NULL, NULL, NULL,
                'accepted', 'it has been open long enough', NULL, 'the long side');
            """;

        SqliteException refused = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
    }

    private AcceptanceGate Gate() =>
        new(_connections, new RunLogger(_clock, _options), _clock, _options);

    private StoredVariant Version(string variantId)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return VariantReader
            .RegisteredBy(connection, Evening, SessionBoundaries.UsEquities)
            .Single(v => string.Equals(v.VariantId, variantId, StringComparison.Ordinal));
    }

    private StoredAcceptanceReading Reading(string variantId) =>
        Readings().Single(r => string.Equals(r.VariantId, variantId, StringComparison.Ordinal));

    private IReadOnlyList<StoredAcceptanceReading> Readings(DateOnly? asOf = null)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return AcceptanceReadingReader.LatestBy(
            connection, asOf ?? Evening, SessionBoundaries.UsEquities);
    }

    /// <summary>
    /// One selection version with its own pre-registered minimum.
    ///
    /// Inserted rather than admitted, because the minimum has to be small enough for an authored
    /// series to reach and the admitter derives it from the family. The target is the derived one,
    /// so the row is the row the admitter would have written but for the figure.
    /// </summary>
    private void SeedVersion(string variantId, int minimumSample)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES (
                @variant_id, 0, 'selection', 'loosens the retrace ceiling', @target,
                @minimum_sample, @unit, 'open', NULL, '2026-08-25T22:00:00.000Z',
                'long', 'dip-shape', 'maximum-retrace', '0.40', '0.50');
            """;

        command.Parameters.AddWithValue("@variant_id", variantId);
        command.Parameters.AddWithValue(
            "@target",
            AcceptanceTest.Describe(minimumSample, MinimumSampleUnit.EffectivePairedSetupObservations));
        command.Parameters.AddWithValue("@minimum_sample", minimumSample);
        command.Parameters.AddWithValue("@unit", MinimumSampleUnit.EffectivePairedSetupObservations);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A run of scored nights for one version.
    ///
    /// <paramref name="identical"/> writes nights on which the two rules selected the same names,
    /// which carry a difference of nought by construction and are the population guard's subject.
    /// <paramref name="wobble"/> spreads the difference around its mean, because a series whose
    /// blocks all carry one mean has no standard error to studentise by and the interval is withheld
    /// rather than shown. It is spread by a fixed pattern with a period coprime to the block length,
    /// so the blocks differ from each other; alternating it strictly would give every block of ten
    /// the same mean and withhold the interval on a series that visibly varies.
    /// </summary>
    private void SeedSeries(
        string variantId,
        int nights,
        decimal difference,
        decimal wobble = 0m,
        DateOnly? from = null,
        bool identical = false,
        int baselineWins = 2,
        int variantWins = 2)
    {
        DateOnly session = from ?? new DateOnly(2026, 1, 1);

        using SqliteConnection connection = _connections.OpenWrite();

        for (int night = 0; night < nights; night++)
        {
            decimal value = difference + (wobble * (((night * 7) % 11) - 5));

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO variant_score (
                    variant_id, session_date, direction, generation, family, horizon_days,
                    flagged, baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
                    baseline_mean_return, variant_mean_return, mean_difference,
                    baseline_scored, variant_scored, baseline_wins, variant_wins,
                    baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at)
                VALUES (
                    @variant_id, @session_date, 'long', 0, 'selection', 10,
                    12, 5, @variant_selected, @both, @variant_only, @baseline_only,
                    '0.0100', @variant_mean, @difference,
                    5, 5, @baseline_wins, @variant_wins,
                    0, 0, 0, NULL, '2026-09-04T21:00:00.000Z');
                """;

            command.Parameters.AddWithValue("@variant_id", variantId);
            command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
            command.Parameters.AddWithValue("@variant_selected", 5);
            command.Parameters.AddWithValue("@both", identical ? 5 : 3);
            command.Parameters.AddWithValue("@variant_only", identical ? 0 : 2);
            command.Parameters.AddWithValue("@baseline_only", identical ? 0 : 2);
            command.Parameters.AddWithValue(
                "@variant_mean", StoreText.RatioToStorageText(0.01m + value));
            command.Parameters.AddWithValue("@difference", StoreText.RatioToStorageText(value));
            command.Parameters.AddWithValue("@baseline_wins", baselineWins);
            command.Parameters.AddWithValue("@variant_wins", variantWins);

            command.ExecuteNonQuery();
            session = session.AddDays(1);
        }
    }
}
