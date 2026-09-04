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
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// What a night's difference between a version and the baseline comes to.
///
/// <b>Every case here is authored and the reason is the same one the register's tests give.</b> No
/// version other than the baseline has ever been registered, the funnel has passed a median of
/// nought candidates a night, and no trade has ever fired, so there is no captured population a
/// score could be run over and there will not be one until a proposal is admitted. The rows below
/// sit either side of the properties under test.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class VariantScoringTests : IDisposable
{
    private const string Zone = "America/New_York";

    /// <summary>The night the setups are flagged on.</summary>
    private static readonly DateOnly Night = new(2026, 8, 3);

    /// <summary>The evening the scorer runs on, far enough past the night for the horizon to have closed.</summary>
    private static readonly DateOnly Evening = new(2026, 9, 3);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Evening, new TimeOnly(21, 40), SessionBoundaries.UsEquities));

    public VariantScoringTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private VariantScorer Scorer() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    private VariantAdmitter Admitter() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    // ---- what one night's difference comes to --------------------------------------------

    /// <summary>
    /// A version that loosens one threshold picks up a name the baseline refused on that threshold
    /// and nothing else, and the row says which rows each mean was taken over.
    /// </summary>
    [Fact]
    public void A_looser_threshold_selects_the_name_the_baseline_refused_on_it()
    {
        SeedBaselineAndNight();

        // Passes everything. Both rules take it.
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);

        // Fails on the retrace alone, at 45%. The baseline's ceiling is 40% and this version's is 50%.
        SeedSetup("BBB", retrace: 0.45m, dipShapePassed: false, passedAll: false, forwardReturn: 0.06m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);

        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(RunOutcome.Clean, scoring.Outcome);
        Assert.Equal(1, scoring.NightsScored);
        Assert.Equal(1, scoring.Longs);
        Assert.Equal(0, scoring.Shorts);

        Row row = ReadRow("V1", Night, SetupDirection.Long);

        Assert.Equal(2, row.Flagged);
        Assert.Equal(1, row.BaselineSelected);
        Assert.Equal(2, row.VariantSelected);
        Assert.Equal(1, row.BothSelected);
        Assert.Equal(1, row.VariantOnly);
        Assert.Equal(0, row.BaselineOnly);
        Assert.Equal(0, row.Unscoreable);
        Assert.Null(row.WithheldBecause);
    }

    /// <summary>
    /// The difference is the difference of two means, over two populations the row states, and it is
    /// not a mean of per-name differences: the two sets are not the same set.
    /// </summary>
    [Fact]
    public void The_difference_is_two_means_over_two_stated_populations()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);
        SeedSetup("BBB", retrace: 0.45m, dipShapePassed: false, passedAll: false, forwardReturn: 0.06m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        Scorer().Score(Evening);

        Row row = ReadRow("V1", Night, SetupDirection.Long);

        // The baseline took AAA alone at 2%; the version took AAA and BBB, so 4%.
        Assert.Equal(0.02m, row.BaselineMean);
        Assert.Equal(0.04m, row.VariantMean);
        Assert.Equal(0.02m, row.Difference);
        Assert.Equal(row.VariantMean - row.BaselineMean, row.Difference);
    }

    /// <summary>
    /// A version is one side's, so a long version writes a long row and no short one. The two are
    /// never added and there is no row here that could add them.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public void A_long_version_writes_no_short_row()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);
        SeedSetup("SSS", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.09m,
            direction: SetupDirection.Short);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(1, scoring.Longs);
        Assert.Equal(0, scoring.Shorts);
        Row only = Assert.Single(Rows("V1"));
        Assert.Equal(SetupDirection.Long, only.Direction);
    }

    /// <summary>
    /// A night whose scoring horizon has not closed waits, and waiting is reported as its own count
    /// rather than as a night scored over whatever had arrived.
    /// </summary>
    [Fact]
    public void A_night_inside_its_horizon_waits_and_is_counted_as_waiting()
    {
        SeedBaselineAndNight(sessionsAfterTheNight: 4);
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(0, scoring.NightsScored);
        Assert.Equal(1, scoring.NightsWaiting);
        Assert.Empty(Rows("V1"));
    }

    /// <summary>A night already scored is not scored again, so a rerun writes nothing.</summary>
    [Fact]
    public void A_rerun_writes_nothing()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);

        Assert.Equal(1, Scorer().Score(Evening).NightsScored);

        VariantScoring again = Scorer().Score(Evening);
        Assert.Equal(0, again.NightsScored);
        Assert.Equal(0, again.RowsWritten);
        Assert.Single(Rows("V1"));
    }

    /// <summary>
    /// A selection the night's cap did not reach is refused a fill, and the count is on both sides:
    /// the baseline's own selections past the cap are refused on identical terms.
    /// see: The spread capture stays at the capped sixty, and a version selecting outside it is scored as refused
    /// </summary>
    [Fact]
    public void A_selection_the_cap_did_not_reach_is_counted_on_both_sides()
    {
        SeedBaselineAndNight();

        // Inside the cap, and taken by both rules.
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m,
            cappedOut: false);

        // Passed every gate and was truncated by rank, so the baseline selected it and cannot fill it.
        SeedSetup("CCC", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.03m,
            cappedOut: true);

        // The version alone selects it, and it was never ranked at all, so it is outside the cap too.
        SeedSetup("BBB", retrace: 0.45m, dipShapePassed: false, passedAll: false, forwardReturn: 0.06m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        Scorer().Score(Evening);

        Row row = ReadRow("V1", Night, SetupDirection.Long);

        Assert.Equal(2, row.BaselineSelected);
        Assert.Equal(1, row.BaselineOutsideCap);
        Assert.Equal(3, row.VariantSelected);
        Assert.Equal(2, row.VariantOutsideCap);
    }

    /// <summary>
    /// A setup whose frozen row does not carry the signals the moved gate reads is unscoreable,
    /// counted apart, and never folded into either selection.
    /// </summary>
    [Fact]
    public void A_setup_with_no_frozen_signals_is_unscoreable_rather_than_unselected()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);
        SeedSetup("DDD", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.05m,
            freezeSignals: false);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        VariantScoring scoring = Scorer().Score(Evening);

        Row row = ReadRow("V1", Night, SetupDirection.Long);

        Assert.Equal(1, row.Unscoreable);
        Assert.Equal(1, scoring.Unscoreable);

        // The baseline still selected it, because the baseline's verdict is the one the night
        // recorded and is not replayed. Only the version's side of the comparison is missing.
        Assert.Equal(2, row.BaselineSelected);
        Assert.Equal(1, row.VariantSelected);
    }

    /// <summary>
    /// A rebuild that disagrees with the night's own verdict is refused rather than believed, which
    /// is the per-row guard on the evidence this replay reconstructs.
    /// </summary>
    [Fact]
    public void A_rebuild_disagreeing_with_the_recorded_verdict_is_unscoreable()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);

        // The row says dip-shape passed and the frozen retrace says it cannot have. One of the two
        // is wrong and nothing here can say which, so the setup is not scored either way.
        SeedSetup("EEE", retrace: 0.90m, dipShapePassed: true, passedAll: true, forwardReturn: 0.05m);

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        Scorer().Score(Evening);

        Row row = ReadRow("V1", Night, SetupDirection.Long);
        Assert.Equal(1, row.Unscoreable);
        Assert.Equal(1, row.VariantSelected);
    }

    /// <summary>
    /// A night neither rule selected on is a row carrying its reason rather than an absence, so the
    /// night is settled and never scored again.
    /// </summary>
    [Fact]
    public void A_night_neither_rule_selected_is_a_row_with_its_reason()
    {
        SeedBaselineAndNight();

        // Fails a gate the version does not move, so neither rule takes it.
        SeedSetup("FFF", retrace: 0.20m, dipShapePassed: true, passedAll: false, forwardReturn: 0.01m,
            failedGate: "contraction");

        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m);
        Scorer().Score(Evening);

        Row row = ReadRow("V1", Night, SetupDirection.Long);

        Assert.Equal(0, row.BaselineSelected);
        Assert.Equal(0, row.VariantSelected);
        Assert.Null(row.Difference);
        Assert.Equal(VariantScorer.NeitherSideSelected, row.WithheldBecause);
    }

    /// <summary>
    /// A version registered after a night was not running on it, so that night is not one it has a
    /// selection on and scoring it would invent one.
    /// </summary>
    [Fact]
    public void A_version_registered_after_a_night_is_not_scored_on_it()
    {
        SeedBaselineAndNight();
        SeedSetup("AAA", retrace: 0.20m, dipShapePassed: true, passedAll: true, forwardReturn: 0.02m);

        // Registered on the evening the scorer runs, which is a month after the night.
        Register("V1", SetupDirection.Long, SelectionRule.MaximumRetrace, 0.50m, registeredOn: Evening);

        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(1, scoring.VersionsScored);
        Assert.Equal(0, scoring.NightsScored);
        Assert.Equal(0, scoring.NightsWaiting);
        Assert.Empty(Rows("V1"));
    }

    // ---- what the run says when there is nothing to score --------------------------------

    /// <summary>
    /// A register holding only the baseline scores nothing, and the run says which of the two empty
    /// states it was rather than reporting a nought that fits both.
    /// </summary>
    [Fact]
    public void A_register_holding_only_the_baseline_is_partial_and_names_the_state()
    {
        SeedBaselineAndNight();

        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(RunOutcome.Partial, scoring.Outcome);
        Assert.Equal(1, scoring.VersionsLive);
        Assert.Equal(0, scoring.VersionsScored);
        Assert.Equal(VariantScorer.NoVersions, scoring.StoppedBecause);
    }

    /// <summary>An empty register is partial for its own reason, not for the one above.</summary>
    [Fact]
    public void An_empty_register_is_partial_for_a_different_reason()
    {
        VariantScoring scoring = Scorer().Score(Evening);

        Assert.Equal(RunOutcome.Partial, scoring.Outcome);
        Assert.Equal(0, scoring.VersionsLive);
        Assert.Equal(VariantScorer.NoVersions, scoring.StoppedBecause);
    }

    /// <summary>The run row is written whatever the outcome, so a night that scored nothing is on the record.</summary>
    [Fact]
    public void A_run_that_scored_nothing_still_writes_its_row()
    {
        SeedBaselineAndNight();
        Scorer().Score(Evening);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT versions_live, versions_scored, outcome, stopped_because FROM score_run";

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal("partial", reader.GetString(2));
        Assert.Equal(VariantScorer.NoVersions, reader.GetString(3));
    }

    // ---- seeding -------------------------------------------------------------------------

    private void SeedBaselineAndNight(int sessionsAfterTheNight = 20)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        TestVersions.SeedBaseline(connection, new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        _sessionsAfterTheNight = sessionsAfterTheNight;
    }

    private int _sessionsAfterTheNight = 20;

    /// <summary>
    /// Registers a version, stamped by a clock the caller chooses.
    ///
    /// The default is a month before the night, because a version is only live on nights after the
    /// session it was registered in, and a version registered tonight has no night to be scored on.
    /// </summary>
    private void Register(
        string variantId, string direction, string threshold, decimal to, DateOnly? registeredOn = null)
    {
        SelectionRule baseline = SelectionRule.For(direction);
        AdmissionVerdict verdict = SelectionReplay.AssertAdmissible(baseline.With(threshold, to), baseline);
        Assert.True(verdict.IsAdmitted, verdict.Reason);

        var clock = new FixedClock(SessionBoundaries.At(
            registeredOn ?? new DateOnly(2026, 7, 1), new TimeOnly(18, 28), SessionBoundaries.UsEquities));

        var admitter = new VariantAdmitter(
            _connections, new RunLogger(clock, Options()), clock, Options());

        admitter.Admit(
            variantId, VariantFamily.Selection, verdict.Reason, "a two-point gain in ten-day forward return",
            dryRun: false,
            moved: new MovedThreshold(direction, verdict.Gate!, verdict.Threshold!, verdict.From!.Value, verdict.To!.Value));
    }

    /// <summary>
    /// One flagged setup, with the check verdicts the night recorded, the signals it froze, its
    /// outcome, and enough bars after its own session for the horizon to be closed or not.
    /// </summary>
    private void SeedSetup(
        string ticker,
        decimal retrace,
        bool dipShapePassed,
        bool passedAll,
        decimal forwardReturn,
        string direction = SetupDirection.Long,
        bool cappedOut = false,
        bool freezeSignals = true,
        string? failedGate = null)
    {
        string setupId = $"{Night:yyyy-MM-dd}-{ticker}-{direction}";
        string shapeGate = direction == SetupDirection.Long ? "dip-shape" : "bounce-shape";

        var results = new List<CheckResult>();

        foreach (string gate in direction == SetupDirection.Long ? SetupChecks.Long : SetupChecks.Short)
        {
            bool passed = gate == shapeGate ? dipShapePassed : gate != failedGate;
            results.Add(new CheckResult(gate, passed, gate == shapeGate ? retrace : 1m));
        }

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @ticker, 'NASDAQ', 'Common Stock', '2020-01-02');
            """, ("@ticker", ticker));

        Execute(connection, transaction, """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               rank, capped_out)
            VALUES (@id, @as_of, @ticker, @direction, @results, @passed_all, @rank, @capped_out);
            """,
            ("@id", setupId), ("@as_of", StoreText.DateToStorageText(Night)),
            ("@ticker", ticker), ("@direction", direction),
            ("@results", JsonSerializer.Serialize(results, Web)),
            ("@passed_all", passedAll ? 1 : 0),
            ("@rank", passedAll ? 1 : (object)DBNull.Value),
            ("@capped_out", passedAll ? (cappedOut ? 1 : 0) : (object)DBNull.Value));

        if (freezeSignals)
        {
            foreach ((string name, decimal value) in new[]
            {
                ("close_adjusted", 100m),
                ("dollar_volume_median_20", 100_000_000m),
                ("market_cap", 5_000_000_000m),
                ("listing_age_sessions", 500m),
                ("adr_20", 0.08m),
                ("days_since_thrust", 3m),
                ("pullback_bars", 3m),
                ("retrace_depth", retrace),
                ("closes_beyond_floor", 0m),
                ("range_today_over_avg", 0.7m),
                ("trigger_distance_ranges", 0.5m),
                ("stop_distance_ranges", 0.3m),
                ("cluster_count", 0m),
            })
            {
                Execute(connection, transaction, """
                    INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
                    VALUES (@id, @name, @value, @computed_at);
                    """,
                    ("@id", setupId), ("@name", name),
                    ("@value", value.ToString(CultureInfo.InvariantCulture)),
                    ("@computed_at", StoreText.EndOfSession(Night, Zone)));
            }
        }

        Execute(connection, transaction, """
            INSERT INTO forward_return (subject_id, subject_kind, horizon_days, intended_date,
                                        actual_date, return_signed, mfe_atr, mae_atr, filled_at)
            VALUES (@id, 'setup', @horizon, @date, @date, @return, '1.0', '1.0', @filled_at);
            """,
            ("@id", setupId), ("@horizon", MeasurementParameters.ScoringHorizonSessions),
            ("@date", StoreText.DateToStorageText(Night.AddDays(14))),
            ("@return", StoreText.RatioToStorageText(forwardReturn)),
            ("@filled_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));

        // The horizon is counted from the store's own bars, so how many sessions this name has after
        // its own is what decides whether the night can be scored at all.
        for (int i = 0; i <= _sessionsAfterTheNight; i++)
        {
            DateOnly bar = Night.AddDays(i);

            Execute(connection, transaction, """
                INSERT OR IGNORE INTO daily_bar
                    (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@ticker, @date, '100', '101', '99', '100.00', '100.00', 1000000, @observed_at);
                """,
                ("@ticker", ticker), ("@date", StoreText.DateToStorageText(bar)),
                ("@observed_at", StoreText.EndOfSession(bar, Zone)));
        }

        transaction.Commit();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    // ---- reading back --------------------------------------------------------------------

    private sealed record Row(
        string VariantId,
        DateOnly SessionDate,
        string Direction,
        int Flagged,
        int BaselineSelected,
        int VariantSelected,
        int BothSelected,
        int VariantOnly,
        int BaselineOnly,
        decimal? BaselineMean,
        decimal? VariantMean,
        decimal? Difference,
        int BaselineOutsideCap,
        int VariantOutsideCap,
        int Unscoreable,
        string? WithheldBecause);

    private Row ReadRow(string variantId, DateOnly night, string direction) =>
        Rows(variantId).Single(r => r.SessionDate == night && r.Direction == direction);

    private IReadOnlyList<Row> Rows(string variantId)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT variant_id, session_date, direction, flagged,
                   baseline_selected, variant_selected, both_selected, variant_only, baseline_only,
                   baseline_mean_return, variant_mean_return, mean_difference,
                   baseline_outside_cap, variant_outside_cap, unscoreable, withheld_because
              FROM variant_score
             WHERE variant_id = @variant_id
             ORDER BY session_date, direction
            """;

        command.Parameters.AddWithValue("@variant_id", variantId);

        var rows = new List<Row>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : StoreText.StorageTextToRatio(reader.GetString(9)),
                reader.IsDBNull(10) ? null : StoreText.StorageTextToRatio(reader.GetString(10)),
                reader.IsDBNull(11) ? null : StoreText.StorageTextToRatio(reader.GetString(11)),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return rows;
    }
}
