using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What the research ledger reads: the register of rule versions, each version's difference series,
/// and the holdout budget.
///
/// <b>Every case here is authored, and the population is named on every one.</b> No version exists
/// in the live store, no night has ever been scored, and no holdout window can exist before
/// 2027-01-01, so nothing here is a measurement of the lab. The population is a store seeded with
/// the rows a case is about and read from a date the case chooses
/// (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it).
/// </summary>
public sealed class LabResearchTests : IDisposable
{
    private const string Zone = "America/New_York";

    /// <summary>The lab's first recorded night, which is what the holdout schedule is computed from.</summary>
    private static readonly DateOnly FirstSession = new(2026, 8, 27);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public LabResearchTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    // ---- the register ------------------------------------------------------------------------

    /// <summary>
    /// A ledger over a store with no version says so in its own words rather than rendering a table
    /// of nothing.
    ///
    /// Population: an empty store, migrated and holding no row at all.
    /// </summary>
    [Fact]
    public void A_ledger_over_an_empty_register_says_no_version_is_registered()
    {
        ResearchResponse ledger = LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone);

        Assert.Empty(ledger.Versions);
        Assert.Equal(LabResearch.NoVersionRegistered, ledger.Absent);
        Assert.Null(ledger.Generation);
        Assert.Null(ledger.LastScoreRun);
    }

    /// <summary>
    /// The register comes back with the pre-registration on every row, and the unit beside the
    /// figure.
    ///
    /// <b>The unit is the point of the assertion.</b> 1802 effective observations and 200 paired
    /// trades are not comparable, and one integer column with no unit would make them look it.
    ///
    /// Population: two authored versions, a baseline and one selection version, registered on
    /// 2026-09-01 and read as of 2026-09-04.
    /// </summary>
    [Fact]
    public void The_register_carries_every_versions_target_and_its_minimum_sample_with_its_unit()
    {
        SeedBaseline();
        SeedSelectionVersion("F1a", "long");

        ResearchResponse ledger = LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone);

        Assert.Equal(2, ledger.Versions.Count);
        Assert.Equal(0, ledger.Generation);

        VersionResponse baseline = ledger.Versions.Single(v => v.IsBaseline);
        Assert.Equal(1802, baseline.MinimumSample);
        Assert.Equal("effective_paired_setup_observations", baseline.MinimumSampleUnit);
        Assert.Null(baseline.Moved);
        Assert.True(baseline.Live);

        VersionResponse version = ledger.Versions.Single(v => !v.IsBaseline);
        Assert.Equal("long", version.Direction);
        Assert.Equal("dip-shape", version.Gate);
        Assert.Equal("maximum-retrace", version.ThresholdName);
        Assert.Equal("0.40", version.ThresholdFrom);
        Assert.Equal("0.50", version.ThresholdTo);
        Assert.Contains("maximum-retrace", version.Moved!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version registered after the date read is invisible to a ledger standing at it.
    ///
    /// <b>The bound is on `created_at` and this is what makes a ledger opened on an old date a
    /// reading of what the lab knew then.</b> Without it, a register read for a past evening would
    /// show a version that evening had never heard of.
    ///
    /// Population: one version registered on 2026-09-04, read as of 2026-09-03 and again as of
    /// 2026-09-04.
    /// </summary>
    [Fact]
    public void A_version_registered_after_the_as_of_is_invisible_to_a_ledger_standing_at_it()
    {
        SeedBaseline(createdAt: "2026-09-04T22:00:00.000Z");

        Assert.Empty(LabResearch.Read(_connections, new DateOnly(2026, 9, 3), Zone).Versions);
        Assert.Single(LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone).Versions);
    }

    // ---- the difference series ---------------------------------------------------------------

    /// <summary>
    /// A version scored on both sides comes back as two blocks and there is no field holding a
    /// figure over the pair.
    ///
    /// <b>The store forbids a two-sided version today and the shape does not depend on that.</b> A
    /// threshold belongs to one side's gate list, so a selection version carries one direction and
    /// the store holds that as a CHECK. What is asserted here is that the ledger's own shape would
    /// keep the two apart if one ever did, which is what the pooling rule asks of a surface.
    ///
    /// Population: one authored version with two authored score rows, one a side, on the same night.
    /// </summary>
    [Fact]
    public void A_version_touching_both_sides_reports_two_blocks_and_no_figure_over_the_pair()
    {
        SeedBaseline();
        SeedSelectionVersion("F1a", "long");
        SeedScore("F1a", new DateOnly(2026, 8, 27), "long", "0.0210");
        SeedScore("F1a", new DateOnly(2026, 8, 27), "short", "-0.0180");

        ResearchResponse ledger = LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone);
        VersionResponse version = ledger.Versions.Single(v => !v.IsBaseline);

        Assert.Equal(2, version.Sides.Count);
        Assert.Equal(["long", "short"], version.Sides.Select(s => s.Direction));
        Assert.Equal("0.0210", version.Sides.Single(s => s.Direction == "long").Nights[0].MeanDifference);
        Assert.Equal("-0.0180", version.Sides.Single(s => s.Direction == "short").Nights[0].MeanDifference);

        // The whole of the assertion: no property on any of these types answers over both sides.
        // A mean of the two would be 0.0015 and would read as a version that changed almost nothing,
        // which is exactly the reading the pooling rule exists to refuse.
        Assert.DoesNotContain(
            typeof(VersionResponse).GetProperties(),
            p => p.Name.Contains("Mean", StringComparison.Ordinal)
                || p.Name.Contains("Difference", StringComparison.Ordinal));
    }

    /// <summary>
    /// A night still inside its scoring horizon is counted and carries no figure, and the two
    /// counts are separate.
    ///
    /// A page showing only the night count would say a version had been measured over nights it is
    /// still waiting on.
    ///
    /// Population: one authored version with two authored score rows on one side, one carrying a
    /// difference and one withheld.
    /// </summary>
    [Fact]
    public void A_withheld_night_is_counted_and_carries_no_figure()
    {
        SeedBaseline();
        SeedSelectionVersion("F1a", "long");
        SeedScore("F1a", new DateOnly(2026, 8, 27), "long", "0.0210");
        SeedScore("F1a", new DateOnly(2026, 8, 28), "long", null, "the scoring horizon has not closed");

        SideResponse side = LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone)
            .Versions.Single(v => !v.IsBaseline).Sides.Single();

        Assert.Equal(2, side.NightsScored);
        Assert.Equal(1, side.NightsCarryingADifference);
        Assert.Equal("the scoring horizon has not closed", side.Nights[1].WithheldBecause);
        Assert.Null(side.Nights[1].MeanDifference);
    }

    /// <summary>
    /// A night scored after the date read is invisible to a ledger standing at it.
    ///
    /// Population: one score row computed on 2026-09-04, read as of 2026-09-03 and again as of the
    /// 4th.
    /// </summary>
    [Fact]
    public void A_night_scored_after_the_as_of_is_invisible_to_a_ledger_standing_at_it()
    {
        SeedBaseline();
        SeedSelectionVersion("F1a", "long");
        SeedScore("F1a", new DateOnly(2026, 8, 27), "long", "0.0210", computedAt: "2026-09-04T21:40:00.000Z");

        Assert.Empty(LabResearch.Read(_connections, new DateOnly(2026, 9, 3), Zone)
            .Versions.Single(v => !v.IsBaseline).Sides);
        Assert.Single(LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone)
            .Versions.Single(v => !v.IsBaseline).Sides);
    }

    // ---- the holdout budget ------------------------------------------------------------------

    /// <summary>
    /// The ledger's holdout register reports the reason it is empty rather than a count of nothing.
    ///
    /// <b>This is the read the 5.4 entry said the ledger would make.</b> The registry's own
    /// comparison lives in the Data assembly for exactly this, so the page and the stage cannot
    /// disagree about why a register holds nothing.
    ///
    /// Population: a store holding one session on 2026-08-27 and no window, read as of 2026-09-04,
    /// which is before the first quarter completes.
    /// </summary>
    [Fact]
    public void The_ledger_says_why_the_holdout_register_holds_nothing()
    {
        SeedSession(FirstSession);

        HoldoutResponse holdout = LabResearch.Read(_connections, new DateOnly(2026, 9, 4), Zone).Holdout;

        Assert.Equal(HoldoutWindows.Capacity, holdout.Capacity);
        Assert.Equal(0, holdout.Matured);
        Assert.Equal(0, holdout.Recorded);
        Assert.Equal(0, holdout.Available);
        Assert.Equal("2026-08-27", holdout.FirstSession);
        Assert.Equal(HoldoutRegister.NoQuarterMaturedYet, holdout.EmptyBecause);
        Assert.False(holdout.Exhausted);
        Assert.Empty(holdout.Missing);
    }

    /// <summary>
    /// A register short of a window the calendar says it should hold reports that instead of the
    /// ordinary reason, and the two sentences are different.
    ///
    /// <b>For three months these two states hold the same noughts on every other figure.</b> The
    /// ledger is the surface an operator reads on the morning the registry did not run, so the
    /// distinction has to survive as far as this response and not only as far as the store.
    ///
    /// Population: a store holding one session on 2026-08-27 and no window, read as of 2027-01-05,
    /// by which date 2026-Q4 has matured and nothing has recorded it.
    /// </summary>
    [Fact]
    public void A_register_missing_a_matured_window_says_so_rather_than_saying_none_has_matured()
    {
        SeedSession(FirstSession);

        HoldoutResponse holdout = LabResearch.Read(_connections, new DateOnly(2027, 1, 5), Zone).Holdout;

        Assert.Equal(1, holdout.Matured);
        Assert.Equal(0, holdout.Recorded);
        Assert.Equal(["2026-Q4"], holdout.Missing);
        Assert.Equal(HoldoutRegister.NotRecorded, holdout.EmptyBecause);
        Assert.NotEqual(HoldoutRegister.NoQuarterMaturedYet, holdout.EmptyBecause);
    }

    /// <summary>
    /// A spent window comes back with what it was spent on and what came of it, which is the whole
    /// of what a budget record is for.
    ///
    /// Population: one authored window, matured and recorded, with an authored spend on it, read as
    /// of 2027-01-05.
    /// </summary>
    [Fact]
    public void A_spent_window_carries_what_it_was_spent_on_and_the_outcome()
    {
        SeedSession(FirstSession);
        SeedWindow("2026-Q4", 1, "2026-10-01", "2026-12-31", "2027-01-01");
        SeedSpend("2026-Q4", "pack v1 against v2", "v2 proposed better and the window agreed");

        HoldoutResponse holdout = LabResearch.Read(_connections, new DateOnly(2027, 1, 5), Zone).Holdout;

        WindowResponse window = Assert.Single(holdout.Windows);
        Assert.Equal("pack v1 against v2", window.SpentOn);
        Assert.Equal("v2 proposed better and the window agreed", window.Outcome);
        Assert.Equal(1, holdout.Spent);
        Assert.Equal(0, holdout.Available);

        // The designed dead end, told apart from a register with nothing in it yet by the reason
        // rather than by the count. Both hold nought available.
        Assert.Equal(HoldoutRegister.EveryMaturedWindowSpent, holdout.EmptyBecause);
        Assert.True(holdout.Exhausted);
    }

    // ---- seeds -------------------------------------------------------------------------------

    private void SeedSession(DateOnly session)
    {
        Execute("""
            INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2020-01-02');

            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@id, @as_of, 'AAA', 'long', '[]', 0);
            """,
            ("@id", $"{session:yyyy-MM-dd}-AAA-long"),
            ("@as_of", StoreText.DateToStorageText(session)));
    }

    private void SeedBaseline(string createdAt = "2026-09-01T22:00:00.000Z") =>
        Execute("""
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES ('V0', 0, 'baseline', 'the rule as it stands', 'the reference every version is differenced against',
                    1802, 'effective_paired_setup_observations', 'open', NULL, @created_at,
                    NULL, NULL, NULL, NULL, NULL);
            """,
            ("@created_at", createdAt));

    private void SeedSelectionVersion(string id, string direction) =>
        Execute("""
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES (@id, 0, 'selection', 'loosens the retrace ceiling', 'two points of forward return',
                    1802, 'effective_paired_setup_observations', 'open', NULL, '2026-09-01T22:00:00.000Z',
                    @direction, 'dip-shape', 'maximum-retrace', '0.40', '0.50');
            """,
            ("@id", id),
            ("@direction", direction));

    private void SeedScore(
        string variantId,
        DateOnly session,
        string direction,
        string? difference,
        string? withheldBecause = null,
        string computedAt = "2026-09-01T22:00:00.000Z") =>
        Execute("""
            INSERT INTO variant_score (
                variant_id, session_date, direction, generation, family, horizon_days,
                flagged, baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
                baseline_mean_return, variant_mean_return, mean_difference,
                baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because, computed_at)
            VALUES (@variant_id, @session_date, @direction, 0, 'selection', 10,
                    11, 4, 5, 4, 1, 0,
                    @baseline, @variant, @difference,
                    0, 0, 0, @withheld, @computed_at);
            """,
            ("@variant_id", variantId),
            ("@session_date", StoreText.DateToStorageText(session)),
            ("@direction", direction),
            ("@baseline", difference is null ? (object)DBNull.Value : "0.0100"),
            ("@variant", difference is null ? (object)DBNull.Value : "0.0310"),
            ("@difference", (object?)difference ?? DBNull.Value),
            ("@withheld", (object?)withheldBecause ?? DBNull.Value),
            ("@computed_at", computedAt));

    private void SeedWindow(string id, int ordinal, string start, string end, string matures) =>
        Execute("""
            INSERT INTO holdout_window (window_id, ordinal, quarter_start, quarter_end, matures_on, recorded_at)
            VALUES (@id, @ordinal, @start, @end, @matures, '2027-01-01T22:00:00.000Z');
            """,
            ("@id", id), ("@ordinal", ordinal), ("@start", start), ("@end", end), ("@matures", matures));

    private void SeedSpend(string id, string spentOn, string outcome) =>
        Execute("""
            INSERT INTO holdout_spend (window_id, spent_on, outcome, spent_at)
            VALUES (@id, @spent_on, @outcome, '2027-01-02T15:00:00.000Z');
            """,
            ("@id", id), ("@spent_on", spentOn), ("@outcome", outcome));

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
}
