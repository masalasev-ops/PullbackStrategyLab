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
/// Two setups that looked the same on every recorded signal and ended somewhere else.
///
/// <b>The window is what this checkpoint is really about, and it is the half a passing run would
/// hide.</b> The metric standardises each signal over the trailing 250 setups, the store holds a
/// fraction of that and will for months, and a run that reported only "no twins" would read the
/// same on a window of four as on a full one. So the assertions here are as much about what a run
/// says it looked at as about what it found.
/// see: The twin-pair threshold is reviewed at the first full window rather than at a phase
///
/// <b>Every population is authored, on the same terms 6.2's were.</b> No setup's ten-day horizon
/// has closed, so there is no captured population to find a pair in and the boundaries are exercised
/// by rows built to sit either side of them (see: Gate boundaries are exercised by authored cases
/// and the captured fixture is not asked to do it).
/// </summary>
public sealed class TwinPairFinderTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Flagged = new(2026, 8, 20);
    private static readonly DateOnly Today = new(2026, 9, 6);

    /// <summary>
    /// The two signals the authored space is built from. Both are active in the library, because the
    /// finder takes its axes from the active set and a name outside it would be ignored.
    /// </summary>
    private const string First = "retrace_depth";

    private const string Second = "pullback_bars";

    public TwinPairFinderTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private TwinPairFinder Stage() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the deliverable ----------------------------------------------------------------------

    /// <summary>
    /// Two setups close in the signal space whose outcomes ended far apart are found as a pair, and
    /// the row carries what the two figures were computed over.
    /// </summary>
    [Fact]
    public void Two_setups_that_look_alike_and_ended_far_apart_are_found_as_a_pair()
    {
        // Four setups. The first two sit almost on top of each other and ended 30 points apart; the
        // other two are far away in the space and hold the distribution open, so the z-scores of the
        // near pair do not collapse to nought.
        Seed("long", 0, outcome: 0.20, first: 1.00, second: 4.0);
        Seed("long", 1, outcome: -0.10, first: 1.01, second: 4.0);
        Seed("long", 2, outcome: 0.02, first: 3.00, second: 9.0);
        Seed("long", 3, outcome: 0.03, first: 5.00, second: 14.0);

        TwinPairResult result = Stage().Find(Today);

        TwinPairSide longs = result.Sides.Single(s => s.Direction == "long");

        Assert.Equal(4, longs.WindowSetups);
        Assert.Equal(6, longs.CandidatePairs);
        Assert.Equal(2, longs.SignalsCompared);
        Assert.Equal(1, longs.PairsFound);
        Assert.Null(longs.EmptyBecause);

        StoredTwinPair pair = Pairs("long").Single();

        Assert.Equal($"{Flagged:yyyy-MM-dd}-long-00", pair.LeftSetupId);
        Assert.Equal($"{Flagged:yyyy-MM-dd}-long-01", pair.RightSetupId);
        Assert.True(pair.Distance < TwinPairs.MaximumDistance);
        Assert.True(pair.GapPoints > TwinPairs.MinimumOutcomeGapPoints);

        // What the two figures above were computed over, carried on the pair's own row. A pair at
        // 0.42 over four setups is not the same measurement as the same two at 0.42 over 250.
        Assert.Equal(2, pair.SignalsCompared);
        Assert.Equal(4, pair.WindowSetups);
    }

    // ---- the failure a passing run would hide --------------------------------------------------

    /// <summary>
    /// A run that finds no pair still reports the window it looked in, and says which shape of
    /// nothing it was.
    ///
    /// <b>This is the checkpoint's own done condition and the thing a bare pair table could not
    /// hold.</b> Three populations, all reporting nought twins: one too thin to form a pair at all,
    /// one with a real window whose pairs the thresholds refused, and one with no numeric signal
    /// common to every row. A store keyed only on pairs would write nothing in all three and the
    /// three would be indistinguishable.
    /// </summary>
    [Fact]
    public void A_run_that_finds_nothing_reports_the_window_it_looked_in_and_which_absence_it_was()
    {
        Seed("long", 0, outcome: 0.20, first: 1.00, second: 4.0);

        TwinPairSide thin = Stage().Find(Today).Sides.Single(s => s.Direction == "long");

        Assert.Equal(1, thin.WindowSetups);
        Assert.Equal(0, thin.CandidatePairs);
        Assert.Equal(0, thin.PairsFound);
        Assert.Equal(TwinPairFinder.WindowTooThin, thin.EmptyBecause);

        // A real window whose pairs were all refused: four setups spread out, outcomes all close
        // together, so nothing clears the gap.
        using (TemporaryDirectory second = new())
        {
            var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(second.Path));
            new MigrationRunner(connections).Apply();

            var options = Options.Create(new PullbackStrategyLabOptions { DataRoot = second.Path });
            var seeder = new Seeder(connections, _clock);

            for (int i = 0; i < 4; i++)
            {
                seeder.Seed("long", i, outcome: 0.01 * i, first: i, second: i * 2.0);
            }

            TwinPairSide refused =
                new TwinPairFinder(connections, new RunLogger(_clock, options), _clock, options)
                    .Find(Today).Sides.Single(s => s.Direction == "long");

            Assert.Equal(4, refused.WindowSetups);
            Assert.Equal(6, refused.CandidatePairs);
            Assert.Equal(0, refused.PairsFound);

            // A finding rather than a gap, and the sentence says so by naming both figures the
            // thresholds were asked over.
            Assert.NotNull(refused.EmptyBecause);
            Assert.Contains("no pair among 6 candidate(s) over a window of 4", refused.EmptyBecause);
            Assert.NotEqual(TwinPairFinder.WindowTooThin, refused.EmptyBecause);
            Assert.NotEqual(TwinPairFinder.NoCommonSignals, refused.EmptyBecause);
        }
    }

    /// <summary>
    /// A window whose rows share no numeric signal forms no space, which is a third absence and the
    /// only one of the three that is a gap in the evidence rather than a reading.
    /// </summary>
    [Fact]
    public void A_window_with_no_signal_common_to_every_row_says_there_were_no_axes()
    {
        Seed("long", 0, outcome: 0.20, first: 1.00, second: null);
        Seed("long", 1, outcome: -0.10, first: null, second: 4.0);

        TwinPairSide side = Stage().Find(Today).Sides.Single(s => s.Direction == "long");

        Assert.Equal(2, side.WindowSetups);
        Assert.Equal(0, side.SignalsCompared);
        Assert.Equal(0, side.PairsFound);
        Assert.Equal(TwinPairFinder.NoCommonSignals, side.EmptyBecause);
    }

    /// <summary>
    /// An empty store reports a window of nought on both sides rather than writing nothing, which is
    /// the state the live lab is in today and will be in for months.
    /// </summary>
    [Fact]
    public void An_empty_store_reports_a_window_of_nought_on_both_sides()
    {
        TwinPairResult result = Stage().Find(Today);

        Assert.Equal(2, result.Sides.Count);

        foreach (TwinPairSide side in result.Sides)
        {
            Assert.Equal(0, side.WindowSetups);
            Assert.Equal(0, side.PairsFound);
            Assert.Equal(TwinPairFinder.WindowTooThin, side.EmptyBecause);
        }

        // Both run rows exist, which is what makes the window figure readable at all.
        Assert.Equal(2, RunRows());
    }

    /// <summary>
    /// The outcome gap binds at the value it is pinned to, exercised from both sides of it.
    ///
    /// One pair just over the threshold and one just under, over the same space, so the assertion
    /// fails if the comparison is moved in either direction. Without the near-miss the test would
    /// pass over any threshold at all below the gap the qualifying pair happened to have.
    /// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
    /// </summary>
    [Fact]
    public void The_outcome_gap_binds_at_the_value_it_is_pinned_to_and_not_either_side_of_it()
    {
        IReadOnlyList<string> ids = ["a", "b", "c"];
        IReadOnlyList<IReadOnlyList<double>> rows = [[1.00], [1.00], [5.00]];

        // The gap is in percentage points of a fraction, so a hundredth of a point either way.
        double over = (TwinPairs.MinimumOutcomeGapPoints + 0.01) / 100;
        double under = (TwinPairs.MinimumOutcomeGapPoints - 0.01) / 100;

        Assert.Single(TwinPairs.Find(ids, rows, [0, over, 0.5]));
        Assert.Empty(TwinPairs.Find(ids, rows, [0, under, 0.5]));
    }

    /// <summary>
    /// The distance binds at the value it is pinned to, exercised from both sides of it.
    ///
    /// The two figures are asserted before the verdicts are, because a generator that drifted would
    /// put both pairs on one side of the threshold and the test would still pass.
    /// </summary>
    [Fact]
    public void The_distance_binds_at_the_value_it_is_pinned_to_and_not_either_side_of_it()
    {
        IReadOnlyList<string> ids = ["a", "b", "c", "d"];
        IReadOnlyList<double> outcomes = [0, 0.5, 0.1, 0.9];

        // One axis, four points. Standardising divides by the population's own deviation, so the
        // separation the pair ends up with is computed here rather than assumed.
        // The separations are raw and the threshold is in standard deviations, so what matters is
        // each one against the spread of its own four points rather than against 0.5 directly. Both
        // are asserted below before either verdict is.
        IReadOnlyList<IReadOnlyList<double>> near = [[0.00], [0.40], [4.00], [8.00]];
        IReadOnlyList<IReadOnlyList<double>> far = [[0.00], [2.00], [4.00], [8.00]];

        double nearDistance = PairDistance(near);
        double farDistance = PairDistance(far);

        Assert.True(nearDistance < TwinPairs.MaximumDistance,
            $"the near pair sits at {nearDistance}, which is not below the threshold, so this test no "
            + "longer exercises the boundary from underneath.");
        Assert.True(farDistance > TwinPairs.MaximumDistance,
            $"the far pair sits at {farDistance}, which is not above the threshold.");

        Assert.Contains(TwinPairs.Find(ids, near, outcomes), p => p.Left == "a" && p.Right == "b");
        Assert.DoesNotContain(TwinPairs.Find(ids, far, outcomes), p => p.Left == "a" && p.Right == "b");
    }

    /// <summary>The standardised distance between the first two rows, which is what the threshold is compared against.</summary>
    private static double PairDistance(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        IReadOnlyList<IReadOnlyList<double>> z = SignalSpace.ZScore(rows);
        return SignalSpace.Distance(z[0], z[1]);
    }

    // ---- the two sides ------------------------------------------------------------------------

    /// <summary>
    /// The two sides are searched over their own populations and a pair never spans them.
    ///
    /// The outcome is signed by direction, so a long that rose twenty points and a short that fell
    /// twenty points both read +0.20. Seeded so the two nearest points in the whole store are one of
    /// each: a pooled implementation would find that pair, and a per-side one finds none.
    /// </summary>
    [Fact]
    public void A_pair_never_spans_the_two_sides()
    {
        Seed("long", 0, outcome: 0.20, first: 1.00, second: 4.0);
        Seed("short", 1, outcome: -0.10, first: 1.01, second: 4.0);
        Seed("long", 2, outcome: 0.02, first: 3.00, second: 9.0);
        Seed("short", 3, outcome: 0.03, first: 3.01, second: 9.0);

        TwinPairResult result = Stage().Find(Today);

        Assert.Equal(2, result.Sides.Single(s => s.Direction == "long").WindowSetups);
        Assert.Equal(2, result.Sides.Single(s => s.Direction == "short").WindowSetups);

        // The nearest two points in the store are the long at 1.00 and the short at 1.01, 30 points
        // apart. Neither side finds them, because neither side holds both.
        Assert.Empty(Pairs("long"));
        Assert.Empty(Pairs("short"));
    }

    // ---- the window ---------------------------------------------------------------------------

    /// <summary>
    /// The window is the trailing setups and never the whole store, and the row reports the count it
    /// actually held.
    ///
    /// Stated with a window smaller than the shipped 250 would need to exercise, by asserting the
    /// figure the run reports against the population seeded: a run over fewer setups than the metric
    /// wants reports what it held, which is the condition the threshold review waits on.
    /// </summary>
    [Fact]
    public void The_run_reports_the_window_it_held_against_the_window_the_metric_wants()
    {
        for (int i = 0; i < 5; i++)
        {
            Seed("long", i, outcome: 0.01 * i, first: i, second: i * 2.0);
        }

        Stage().Find(Today);

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT window_setups, window_wanted FROM twin_run WHERE direction = 'long'";

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(TwinPairs.WindowSetups, reader.GetInt32(1));
        Assert.True(reader.GetInt32(0) < reader.GetInt32(1),
            "the seeded window is not below the one the metric wants, so this test no longer exercises "
            + "the short-window reading it was written for.");
    }

    /// <summary>
    /// A rerun of a date writes a new generation beside the old rather than rewriting it, and the
    /// reader takes the latest.
    /// </summary>
    [Fact]
    public void A_rerun_writes_a_new_generation_and_the_reader_takes_the_latest()
    {
        Seed("long", 0, outcome: 0.20, first: 1.00, second: 4.0);
        Seed("long", 1, outcome: -0.10, first: 1.01, second: 4.0);
        Seed("long", 2, outcome: 0.02, first: 3.00, second: 9.0);
        Seed("long", 3, outcome: 0.03, first: 5.00, second: 14.0);

        Stage().Find(Today);

        // A later instant, so the second run is a second generation rather than the same one.
        _clock.Advance(TimeSpan.FromHours(1));
        Stage().Find(Today);

        Assert.Equal(2, RowCount("SELECT COUNT(DISTINCT observed_at) FROM twin_pair"));
        Assert.Equal(4, RunRows());

        // The reader takes one generation, so the panel shows one pair rather than two copies of it.
        Assert.Single(Pairs("long"));
    }

    // ---- seeding ------------------------------------------------------------------------------

    private void Seed(string direction, int index, double outcome, double? first, double? second) =>
        new Seeder(_connections, _clock).Seed(direction, index, outcome, first, second);

    private IReadOnlyList<StoredTwinPair> Pairs(string direction)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return TwinPairReader.Read(connection, Today, "America/New_York")
            .Where(r => r.Direction == direction)
            .SelectMany(r => r.Pairs)
            .ToList();
    }

    private int RunRows() => RowCount("SELECT COUNT(*) FROM twin_run");

    private int RowCount(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One authored setup with a closed outcome and up to two frozen signal values.</summary>
    private sealed class Seeder(StoreConnectionFactory connections, FixedClock clock)
    {
        public void Seed(string direction, int index, double outcome, double? first, double? second)
        {
            string setupId = $"{Flagged:yyyy-MM-dd}-{direction}-{index:00}";
            string ticker = $"T{index:00}";

            Execute("""
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
                ON CONFLICT (ticker) DO NOTHING
                """, ("@ticker", ticker));

            Execute("""
                INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
                VALUES (@setup_id, @as_of, @ticker, @direction, '{}', 1)
                """,
                ("@setup_id", setupId),
                ("@as_of", StoreText.DateToStorageText(Flagged)),
                ("@ticker", ticker),
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
                ("@filled_at", StoreText.TimestampToStorageText(clock.UtcNow.AddDays(-1))));

            Freeze(setupId, First, first);
            Freeze(setupId, Second, second);
        }

        private void Freeze(string setupId, string name, double? value)
        {
            if (value is not double held)
            {
                return;
            }

            Execute("""
                INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
                VALUES (@setup_id, @signal_name, @value, @computed_at)
                """,
                ("@setup_id", setupId),
                ("@signal_name", name),
                ("@value", StoreText.StatisticToStorageText(held)),
                ("@computed_at", StoreText.TimestampToStorageText(clock.UtcNow.AddDays(-1))));
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = connections.OpenWrite();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;

            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }
    }
}
