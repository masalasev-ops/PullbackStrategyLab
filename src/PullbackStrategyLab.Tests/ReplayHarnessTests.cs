using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// Screening a selection version over the stored history, and the acceptance test that says the
/// harness and the live detector are one implementation.
///
/// <b>The population is authored and the reason is the corpus's own.</b> The captured fixture holds
/// one market day which flagged one row a side and passed neither, so a set comparison over it is
/// empty against empty and would be self-validating. Exact reproduction of a <i>non-empty</i>
/// selection needs a population where the baseline takes some rows and refuses others, and that is
/// what is built here
/// (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it).
/// </summary>
public sealed class ReplayHarnessTests : IDisposable
{
    private const string Zone = "America/New_York";

    private static readonly DateOnly FirstNight = new(2026, 8, 3);
    private static readonly DateOnly SecondNight = new(2026, 8, 4);
    private static readonly DateOnly Evening = new(2026, 9, 3);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Evening, new TimeOnly(21, 40), SessionBoundaries.UsEquities));

    public ReplayHarnessTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private ReplayHarness Harness() =>
        new(_connections, new RunLogger(_clock, Options()), _clock, Options());

    // ---- the acceptance test ---------------------------------------------------------------

    /// <summary>
    /// The baseline's own rule, replayed over the rows the nights recorded, selects exactly the set
    /// the store says was selected.
    ///
    /// <b>This is the checkpoint's whole claim and the population is what makes it mean anything.</b>
    /// Four rows over two nights, two of which the baseline took and two of which it refused, so a
    /// harness that selected everything and one that selected nothing both fail. If this ever goes
    /// red the harness and the detector have stopped being one implementation, and every replay
    /// result the lab has produced since is worthless rather than merely suspect.
    /// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
    /// </summary>
    [Fact]
    public void The_baseline_replayed_reproduces_the_stored_selections_exactly()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedRefusedOnRetrace("BBB", FirstNight, retrace: 0.45m);
        SeedSelected("CCC", SecondNight, retrace: 0.31m);
        SeedRefusedOnRetrace("DDD", SecondNight, retrace: 0.90m);

        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, Evening);

        // Non-empty and not everything, which is what stops either degenerate harness passing.
        Assert.Equal(4, run.RowsExamined);
        Assert.Equal(2, run.BaselineSelected);
        Assert.Equal(2, run.CandidateSelected);
        Assert.Equal(2, run.BothSelected);
        Assert.Equal(0, run.CandidateOnly);
        Assert.Equal(0, run.BaselineOnly);

        Assert.Empty(run.Disagreements);
        Assert.Equal(0, run.Unjudgeable);
        Assert.True(run.SelectionsReproduced);
        Assert.True(run.Reproduced);
    }

    /// <summary>
    /// A row whose recorded verdict the frozen signals do not reproduce voids the screen, and the
    /// disagreement names the row and the gate.
    ///
    /// <b>This is the permanent proof that the test above can fail.</b> A green acceptance run over
    /// a harness that never disagrees with anything says nothing at all, and this corpus has shipped
    /// that shape four times: an assertion whose subject went away and which kept saying what it
    /// always said. The row here records `dip-shape` as passed while its retrace is past the
    /// baseline's ceiling, so the rebuild reaches the opposite verdict.
    /// </summary>
    [Fact]
    public void A_row_the_signals_do_not_reproduce_voids_the_screen_and_names_the_gate()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);

        // Recorded as having cleared the dip-shape gate at a retrace the baseline refuses.
        SeedSetup("EEE", FirstNight, retrace: 0.90m, shapePassed: true, passedAll: true);

        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, Evening);

        ReplayDisagreement disagreement = Assert.Single(run.Disagreements);
        Assert.Equal($"{FirstNight:yyyy-MM-dd}-EEE-long", disagreement.SetupId);
        Assert.Equal("dip-shape", disagreement.Gate);
        Assert.True(disagreement.Recorded);
        Assert.False(disagreement.Rebuilt);

        Assert.False(run.Reproduced);

        // And the row is neither selected nor rejected on a guess: a rebuild that cannot stand
        // behind the record produces no answer at all.
        Assert.Equal(1, run.Unjudgeable);
        Assert.Equal(1, run.CandidateSelected);
    }

    // ---- screening a candidate --------------------------------------------------------------

    /// <summary>
    /// A version that loosens one threshold selects the baseline's set plus the names the baseline
    /// refused on that threshold alone, and nothing else moves.
    /// </summary>
    [Fact]
    public void A_looser_threshold_adds_the_names_the_baseline_refused_on_it()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedRefusedOnRetrace("BBB", FirstNight, retrace: 0.45m);
        SeedRefusedOnRetrace("DDD", FirstNight, retrace: 0.90m);

        ReplayScreening run = Harness().Screen(
            SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.50m), Evening);

        Assert.Null(run.Refused);
        Assert.Equal(1, run.BaselineSelected);
        Assert.Equal(2, run.CandidateSelected);
        Assert.Equal(1, run.BothSelected);
        Assert.Equal(1, run.CandidateOnly);
        Assert.Equal(0, run.BaselineOnly);

        // The screen is not a reproduction and does not claim to be: the candidate's set differs
        // from the baseline's on purpose, which is what makes it a screen.
        Assert.False(run.SelectionsReproduced);
        Assert.Empty(run.Disagreements);
    }

    /// <summary>A tighter threshold drops names the baseline took, and the drop is counted apart.</summary>
    [Fact]
    public void A_tighter_threshold_drops_the_names_it_now_refuses()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedSelected("CCC", FirstNight, retrace: 0.31m);

        ReplayScreening run = Harness().Screen(
            SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.25m), Evening);

        Assert.Equal(2, run.BaselineSelected);
        Assert.Equal(1, run.CandidateSelected);
        Assert.Equal(1, run.BaselineOnly);
        Assert.Equal(0, run.CandidateOnly);
    }

    /// <summary>
    /// A candidate the register would not take is refused before anything is read, and the reason
    /// says which rule refused it rather than reporting a screen over nothing.
    /// </summary>
    [Fact]
    public void A_candidate_the_register_would_refuse_is_not_screened()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);

        ReplayScreening run = Harness().Screen(
            SelectionRule.Long
                .With(SelectionRule.MaximumRetrace, 0.50m)
                .With(SelectionRule.TriggerReachRanges, 2.0m),
            Evening);

        Assert.NotNull(run.Refused);
        Assert.Contains(ReplayHarness.NotAdmissible, run.Refused, StringComparison.Ordinal);
        Assert.Contains(RuleAdmission.MoreThanOneMoved, run.Refused, StringComparison.Ordinal);

        // Nothing was read, so no figure here can be mistaken for a result.
        Assert.Equal(0, run.RowsExamined);
        Assert.Equal(0, run.SessionsRead);
        Assert.False(run.SelectionsReproduced);
    }

    // ---- what the record cannot answer ------------------------------------------------------

    /// <summary>
    /// A row missing a signal a judgeable gate reads is unjudgeable rather than refused, because the
    /// two are different facts: one is about the name and one is about the record.
    /// </summary>
    [Fact]
    public void A_row_missing_a_signal_is_unjudgeable_rather_than_refused()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedSelected("BBB", FirstNight, retrace: 0.20m, omitSignal: "retrace_depth");

        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, Evening);

        Assert.Equal(1, run.Unjudgeable);
        Assert.Equal(1, run.CandidateSelected);
        Assert.Equal(2, run.BaselineSelected);
        Assert.False(run.Reproduced);
    }

    /// <summary>
    /// A gate the night recorded with no value made no comparison, so its verdict is read back and
    /// the row still reproduces.
    ///
    /// <b>Rebuilding it would be a false alarm on every such row.</b> The night refused the gate for
    /// want of a quantity; a threshold cannot move a quantity that was never measured, so the
    /// night's verdict stands under every version of the rule. The count is reported so a population
    /// of them is visible rather than silent.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void A_gate_the_night_could_not_measure_is_read_back_and_not_rebuilt()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedSetup("FFF", FirstNight, retrace: 0.20m, shapePassed: true, passedAll: false,
            unmeasured: "trigger-near");

        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, Evening);

        Assert.Equal(1, run.UnmeasuredGateVerdicts);

        // The signals carry a trigger distance the gate would have cleared, and the read-back is
        // what stops that being reported as a disagreement. Counted, because a row whose verdicts
        // and whose signals describe two different things is worth seeing.
        Assert.Equal(1, run.FrozenYetUnmeasured);

        Assert.Empty(run.Disagreements);
        Assert.Equal(0, run.Unjudgeable);

        // The night's own refusal decides the row, so the replay does not select it either.
        Assert.Equal(1, run.BaselineSelected);
        Assert.Equal(1, run.CandidateSelected);
        Assert.True(run.Reproduced);
    }

    // ---- the two sides, and the shape of the walk -------------------------------------------

    /// <summary>
    /// A screen reads one side and never the other, because a version is one side's and a figure
    /// over both would be the pooling the rule forbids.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    [Fact]
    public void A_screen_reads_one_side_and_never_the_other()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedSelected("SSS", FirstNight, retrace: 0.20m, direction: SetupDirection.Short);

        ReplayScreening longs = Harness().Reproduce(SetupDirection.Long, Evening);
        ReplayScreening shorts = Harness().Reproduce(SetupDirection.Short, Evening);

        Assert.Equal(1, longs.RowsExamined);
        Assert.Equal(1, shorts.RowsExamined);
        Assert.Equal(SetupDirection.Long, longs.Direction);
        Assert.Equal(SetupDirection.Short, shorts.Direction);

        // The gate counts differ because the two sides lose different gates for different reasons,
        // and nothing here adds them.
        Assert.Equal(9, longs.GatesJudged);
        Assert.Equal(7, shorts.GatesJudged);
    }

    /// <summary>
    /// The walk is one read of the setups and one of the signals per session, which is what "in
    /// seconds" rests on, and the elapsed time is reported rather than asserted tightly.
    ///
    /// <b>The property is the shape and not the clock.</b> A wall-clock bound tight enough to be
    /// interesting is a bound that fails on a loaded runner, and a bound loose enough to be stable
    /// asserts almost nothing; what actually decides whether a screen takes seconds or minutes is
    /// whether its cost is a function of sessions or of rows. So the session count is asserted and
    /// the clock is given a ceiling generous enough that only a change of shape could reach it.
    /// </summary>
    [Fact]
    public void The_walk_reads_each_session_once_however_many_rows_it_holds()
    {
        foreach (int i in Enumerable.Range(0, 30))
        {
            SeedSelected($"N{i:00}", FirstNight, retrace: 0.20m);
        }

        SeedSelected("ZZZ", SecondNight, retrace: 0.20m);

        var stopwatch = Stopwatch.StartNew();
        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, Evening);
        stopwatch.Stop();

        Assert.Equal(2, run.SessionsRead);
        Assert.Equal(31, run.RowsExamined);
        Assert.True(run.Reproduced);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"the screen took {stopwatch.Elapsed.TotalSeconds:0.00}s over {run.RowsExamined} row(s) "
            + $"in {run.SessionsRead} session(s), which is long enough to mean the walk stopped being "
            + "one read per session");
    }

    /// <summary>
    /// A night after the as-of is invisible to a screen standing at it, on the same terms as every
    /// other read in this project.
    /// </summary>
    [Fact]
    public void A_night_after_the_as_of_is_not_screened()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);
        SeedSelected("BBB", SecondNight, retrace: 0.20m);

        ReplayScreening run = Harness().Reproduce(SetupDirection.Long, FirstNight);

        Assert.Equal(1, run.SessionsRead);
        Assert.Equal(1, run.RowsExamined);
    }

    /// <summary>
    /// The harness writes its run entry and nothing else, which is what makes "a screen never admits
    /// a proposal" a property of the store rather than a sentence in a comment.
    /// see: Replay screens proposals and the forward paired test admits them
    /// </summary>
    [Fact]
    public void The_harness_writes_its_run_entry_and_nothing_else()
    {
        SeedSelected("AAA", FirstNight, retrace: 0.20m);

        IReadOnlyDictionary<string, int> before = RowCounts();
        Harness().Screen(SelectionRule.Long.With(SelectionRule.MaximumRetrace, 0.50m), Evening);
        IReadOnlyDictionary<string, int> after = RowCounts();

        foreach ((string table, int count) in before)
        {
            if (table == "run_log")
            {
                Assert.Equal(count + 1, after[table]);
                continue;
            }

            Assert.Equal(count, after[table]);
        }
    }

    // ---- what cannot be screened at all ------------------------------------------------------

    /// <summary>
    /// The reconstructed history cannot be screened, and the store is what refuses it: a signal
    /// cannot be frozen against a calibration setup because `setup_signal` keys into `setup`.
    ///
    /// <b>This is the market-cap clause obligation raised at 3.3, answered by something larger than
    /// it.</b> That row worried that a short rule replayed over calibration rows would be screened
    /// against a funnel missing the market-capitalisation clause, and scoped a shares-outstanding
    /// purchase to close it. The purchase would not close it. A replay reads the quantity a gate
    /// compared, not the clause list it ran under, and no calibration row has one on either side,
    /// so both sides are unscreenable and no purchase of history changes that.
    ///
    /// <b>Asserted against the store rather than described</b>, because a comment saying a foreign
    /// key exists is the kind of statement that survives the key being dropped.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    [Fact]
    public void A_signal_cannot_be_frozen_against_a_reconstructed_setup()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO calibration_setup (setup_id, as_of, ticker, direction, check_results,
                                               passed_all, trigger_price, stop_price,
                                               stop_distance_ranges)
                VALUES ('cal-1', '2024-04-01', 'AAA', 'long', '[]', 0, '10.00', '9.00', '0.30');
                """;
            seed.ExecuteNonQuery();
        }

        using SqliteCommand freeze = connection.CreateCommand();
        freeze.CommandText = """
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES ('cal-1', 'retrace_depth', '0.20', '2024-04-01T20:00:00.000Z');
            """;

        SqliteException refused = Assert.Throws<SqliteException>(() => freeze.ExecuteNonQuery());
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And the calibration table holds no signal of its own to read instead, which is the other
        // half of why nothing can be replayed over it.
        Assert.DoesNotContain(
            "calibration_setup_signal",
            TableNames(connection),
            StringComparer.Ordinal);
    }

    // ---- seeding -----------------------------------------------------------------------------

    private void SeedSelected(
        string ticker,
        DateOnly night,
        decimal retrace,
        string direction = SetupDirection.Long,
        string? omitSignal = null) =>
        SeedSetup(ticker, night, retrace, shapePassed: true, passedAll: true,
            direction: direction, omitSignal: omitSignal);

    private void SeedRefusedOnRetrace(string ticker, DateOnly night, decimal retrace) =>
        SeedSetup(ticker, night, retrace, shapePassed: false, passedAll: false);

    /// <summary>
    /// One stored row: its verdicts, and the signals the night froze beside them.
    ///
    /// The verdicts are written rather than evaluated, which is the point: this is the record a
    /// replay is held against, and a seed that produced it through the rules under test would make
    /// every assertion here circular.
    /// </summary>
    private void SeedSetup(
        string ticker,
        DateOnly night,
        decimal retrace,
        bool shapePassed,
        bool passedAll,
        string direction = SetupDirection.Long,
        string? omitSignal = null,
        string? unmeasured = null)
    {
        string setupId = $"{night:yyyy-MM-dd}-{ticker}-{direction}";
        string shapeGate = direction == SetupDirection.Long ? "dip-shape" : "bounce-shape";

        var results = new List<CheckResult>();

        foreach (string gate in direction == SetupDirection.Long ? SetupChecks.Long : SetupChecks.Short)
        {
            if (gate == unmeasured)
            {
                results.Add(CheckResult.Unknown(gate, "no trigger or no daily range for the session"));
                continue;
            }

            bool passed = gate == shapeGate ? shapePassed : true;
            results.Add(new CheckResult(gate, passed, gate == shapeGate ? retrace : 1m));
        }

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @ticker, 'NASDAQ', 'Common Stock', '2020-01-02');
            """, ("@ticker", ticker));

        Execute(connection, transaction, """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@id, @as_of, @ticker, @direction, @results, @passed_all);
            """,
            ("@id", setupId), ("@as_of", StoreText.DateToStorageText(night)),
            ("@ticker", ticker), ("@direction", direction),
            ("@results", JsonSerializer.Serialize(results, Web)),
            ("@passed_all", passedAll ? 1 : 0));

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

            // At the gate's own threshold, so the recorded pass and the rebuilt one agree. Seeded
            // from the rule rather than as a literal: `cluster` gates nothing, so a seed that let
            // the two disagree would leave every row here unjudgeable and every assertion below
            // passing for the wrong reason, which is how the first run of these tests went.
            ("cluster_count", SelectionRule.For(direction).Value(SelectionRule.ClusterThreshold)),
        })
        {
            if (name == omitSignal)
            {
                continue;
            }

            Execute(connection, transaction, """
                INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
                VALUES (@id, @name, @value, @computed_at);
                """,
                ("@id", setupId), ("@name", name),
                ("@value", value.ToString(CultureInfo.InvariantCulture)),
                ("@computed_at", StoreText.EndOfSession(night, Zone)));
        }

        transaction.Commit();
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
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

    /// <summary>Every table's row count, which is how "writes nothing" is asserted rather than said.</summary>
    private IReadOnlyDictionary<string, int> RowCounts()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string table in TableNames(connection))
        {
            using SqliteCommand command = connection.CreateCommand();
            SqliteIdentifier.Validate(table);
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            counts[table] = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    private static IReadOnlyList<string> TableNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
