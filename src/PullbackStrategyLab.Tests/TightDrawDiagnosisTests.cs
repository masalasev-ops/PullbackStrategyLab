using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The funnel that says why a tight set came up short, held against the draw it describes.
///
/// <b>The property under test is agreement with the draw, not the arithmetic of the counts.</b> A
/// counting pass beside a filter is free to drift from the filter, and this corpus has shipped that
/// defect four times in the shape of an assertion whose subject moved while the assertion went on
/// saying what it always said. So every test here draws through `ControlSampler` and compares the
/// diagnosis against the rows that draw wrote, rather than against a number computed here.
///
/// <b>And the pool is arranged so the two could disagree.</b> A store where every subject draws its
/// full five would let a prediction of five agree with a draw of five while measuring nothing, so
/// the tests below starve the pool on the dimension that can starve it and show that the other one
/// cannot.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
/// </summary>
public sealed class TightDrawDiagnosisTests : IDisposable
{
    private static readonly DateOnly Tonight = new(2026, 8, 27);
    private static readonly DateOnly SameMoodEarlier = new(2026, 8, 25);
    private static readonly DateOnly OtherMoodEarlier = new(2026, 8, 26);

    private const string Observed = "2026-01-01T00:00:00.000Z";
    private const string Zone = "America/New_York";

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 22, 26, 0, TimeSpan.Zero));

    public TightDrawDiagnosisTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A full set: the funnel predicts five and the draw writes five.
    ///
    /// The weak half of the pair on its own, and it is here because the two starved cases below
    /// would pass against a diagnosis that always answered nought.
    /// </summary>
    [Fact]
    public void The_funnel_predicts_the_draw_where_the_pool_is_wide_enough()
    {
        Mood(Tonight, MarketMood.RiskOn);
        Name("SUBJ", Tonight, 100_000_000m, 2.0m, TierClassifier.Rising);

        for (int i = 0; i < 6; i++)
        {
            Name($"NIGHT{i}", Tonight, 110_000_000m + (i * 1_000_000m), 2.1m, TierClassifier.Rising);
        }

        Flag("SUBJ", Tonight);

        TightDrawDiagnosis.Entry entry = RunAndDiagnose();

        Assert.Equal(MeasurementParameters.ControlsPerSet, entry.Predicted);
        Assert.Equal(MeasurementParameters.ControlsPerSet, Drawn("tight"));
    }

    /// <summary>
    /// The ladder grade starves the set, and the funnel names it as the only clause that can.
    ///
    /// Every candidate shares the subject's mood, because they sat through the same session, and
    /// only two share its ladder grade, so the draw gets two. The leave-one-out carries the finding
    /// in both directions: dropping the ladder reaches five, and dropping the mood changes nothing
    /// at all.
    /// </summary>
    [Fact]
    public void The_funnel_names_the_ladder_where_the_ladder_is_what_eliminated()
    {
        Mood(Tonight, MarketMood.RiskOn);
        Name("SUBJ", Tonight, 100_000_000m, 2.0m, TierClassifier.Rising);

        Name("RISE0", Tonight, 110_000_000m, 2.1m, TierClassifier.Rising);
        Name("RISE1", Tonight, 120_000_000m, 2.2m, TierClassifier.Rising);

        for (int i = 0; i < 6; i++)
        {
            Name($"FALL{i}", Tonight, 101_000_000m + (i * 100_000m), 2.01m, TierClassifier.Falling);
        }

        Flag("SUBJ", Tonight);

        TightDrawDiagnosis.Entry entry = RunAndDiagnose();

        Assert.Equal(2, Drawn("tight"));
        Assert.Equal(2, entry.Predicted);
        Assert.Equal(2, entry.DistinctNames);

        // Eight names shared the mood and two shared the ladder, so the ladder is the clause that
        // did the eliminating and the leave-one-out says so.
        Assert.Equal(8, entry.PoolAfterMood);
        Assert.Equal(2, entry.PoolAfterLadder);
        Assert.True(entry.WithoutLadder >= MeasurementParameters.ControlsPerSet);
        Assert.Equal(entry.DistinctNames, entry.WithoutMood);
    }

    /// <summary>
    /// The mood eliminates nobody, on a night whose other sessions carry a different label and hold
    /// the nearest names of all.
    ///
    /// <b>The decision's central claim, asserted as a count.</b> Within the night the mood is a
    /// constant, so the clause holds on every candidate and the pool after it is the pool before it.
    /// The store is seeded with six nearer names on a risk-off session so that the previous ruling's
    /// question is still on the table: those rows are excluded, and what excludes them is that they
    /// are not on the subject's night rather than that their label differs.
    /// </summary>
    [Fact]
    public void The_mood_eliminates_nobody_within_the_night()
    {
        Mood(OtherMoodEarlier, MarketMood.RiskOff);
        Mood(Tonight, MarketMood.RiskOn);
        Name("SUBJ", Tonight, 100_000_000m, 2.0m, TierClassifier.Rising);

        for (int i = 0; i < 6; i++)
        {
            Name($"NIGHT{i}", Tonight, 110_000_000m + (i * 1_000_000m), 2.1m, TierClassifier.Rising);
        }

        // Nearer the subject than anything on its own night, and on a session carrying another mood.
        for (int i = 0; i < 6; i++)
        {
            Name($"WRONG{i}", OtherMoodEarlier, 101_000_000m + (i * 100_000m), 2.01m, TierClassifier.Rising);
        }

        Flag("SUBJ", Tonight);

        TightDrawDiagnosis.Entry entry = RunAndDiagnose();

        Assert.Equal(MeasurementParameters.ControlsPerSet, Drawn("tight"));
        Assert.Equal(entry.PoolOnTheNight, entry.PoolAfterMood);
        Assert.Equal(entry.DistinctNames, entry.WithoutMood);
        Assert.Equal(6, entry.PoolOnTheNight);
    }

    /// <summary>
    /// Turnover and daily range eliminate nobody, which is the claim the funnel makes in prose and
    /// is asserted here in rows.
    ///
    /// Every candidate is orders of magnitude away from the subject on both distances and the draw
    /// still takes five. They order the survivors; they do not decide who survives. Stated as a test
    /// because a funnel reporting a pool size "after turnover" would be reporting a stage that does
    /// not exist, and a reader would take the unchanged number as a dimension that happened not to
    /// bind on this data.
    /// </summary>
    [Fact]
    public void Turnover_and_daily_range_exclude_nobody_from_a_tight_set()
    {
        Mood(Tonight, MarketMood.RiskOn);
        Name("SUBJ", Tonight, 60_000_000m, 0.5m, TierClassifier.Rising);

        for (int i = 0; i < 5; i++)
        {
            Name($"FAR{i}", Tonight, 4_000_000_000m + (i * 1_000_000_000m), 40m, TierClassifier.Rising);
        }

        Flag("SUBJ", Tonight);

        TightDrawDiagnosis.Entry entry = RunAndDiagnose();

        Assert.Equal(MeasurementParameters.ControlsPerSet, Drawn("tight"));
        Assert.Equal(MeasurementParameters.ControlsPerSet, entry.Predicted);
    }

    // ---- the run -------------------------------------------------------------------------------

    /// <summary>
    /// Observes the subject's own night, draws through `ControlSampler`, and returns the one
    /// subject's funnel.
    ///
    /// The earlier sessions are observed too, so that a diagnosis counting anything but the night's
    /// own pool would report a number the draw cannot produce and the prediction would disagree with
    /// the rows written.
    /// </summary>
    private TightDrawDiagnosis.Entry RunAndDiagnose()
    {
        var diagnosis = new TightDrawDiagnosis();

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            var source = new StoredFigures(connection);

            foreach (DateOnly session in new[] { SameMoodEarlier, OtherMoodEarlier, Tonight })
            {
                diagnosis.Observe(connection, source, session, SubjectTables.Evidence, Zone);
            }
        }

        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path, SessionZone = Zone });

        new ControlSampler(_connections, new RunLogger(_clock, options), _clock, options).Draw(Tonight);

        return Assert.Single(diagnosis.Entries, e => e.AsOf == Tonight);
    }

    private int Drawn(string set)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM control_setup WHERE control_set = @set";
        command.Parameters.AddWithValue("@set", set);
        return (int)(long)(command.ExecuteScalar() ?? 0L);
    }

    // ---- the store -----------------------------------------------------------------------------

    private void Mood(DateOnly session, string label) =>
        Execute("""
            INSERT INTO regime_daily (as_of, index_score, breadth_score, label,
                                      long_ladder_count, short_ladder_count, indexes_above)
            VALUES (@d, 0, 0, @l, 0, 0, 0)
            ON CONFLICT (as_of) DO NOTHING
            """,
            ("@d", Session(session)), ("@l", label));

    private void Name(string ticker, DateOnly session, decimal turnover, decimal range, string ladder)
    {
        Execute(
            "INSERT INTO security VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01', "
            + "NULL, NULL, NULL, NULL) ON CONFLICT (ticker) DO NOTHING",
            ("@t", ticker));

        Execute("""
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                 dollar_volume_median_20, range_avg_20, ladder_grade)
            VALUES (@t, @d, @obs, '1', '1', '1', '2.0', @adr, @dv, '2.0', @grade)
            ON CONFLICT DO NOTHING
            """,
            ("@t", ticker), ("@d", Session(session)), ("@obs", Observed),
            ("@adr", range.ToString(CultureInfo.InvariantCulture)),
            ("@dv", turnover.ToString(CultureInfo.InvariantCulture)),
            ("@grade", ladder));
    }

    private void Flag(string ticker, DateOnly session) =>
        Execute("""
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @d, @t, 'long', '[]', 1, '100.0', '97.0', '0.5')
            """,
            ("@id", $"{Session(session)}-{ticker}-long"),
            ("@d", Session(session)), ("@t", ticker));

    private static string Session(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
