using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The arithmetic a threshold is read against, swept rather than sampled.
///
/// The run itself is asserted over the golden fixture in <c>PhaseReplay</c>, where the figures it
/// produces become expectations. What is here is the statistics: a median read from an even number
/// of nights, a quantile convention that several libraries disagree about on small samples, and the
/// scaling that turns a count over thirty names into a count over two thousand.
/// see: Phase 2 thresholds are calibrated once against nightly counts, before phase 3
/// </summary>
public sealed class CalibrationTests
{
    [Fact]
    public void A_run_over_history_leaves_the_evidence_store_exactly_as_it_found_it()
    {
        // The property the whole checkpoint turns on, and the one that is unrecoverable if it
        // fails: a reconstructed setup in the evidence store is indistinguishable from a flagged
        // one the day after it is written, and every measurement phase 3 makes is over that store.
        // see: The evidence store holds only setups flagged forward, never setups reconstructed from history
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        string[] before = Rows(replay, SetupReader.SetupTable);
        string[] calibrated = Rows(replay, SetupReader.CalibrationTable);

        Assert.NotEmpty(calibrated);

        DateOnly from = Sessions(replay)[IndicatorEngine.WarmupSessions - 1];
        replay.CalibrateLong(from, replay.AsOf);
        replay.CalibrateShort(from, replay.AsOf);

        Assert.Equal(before, Rows(replay, SetupReader.SetupTable));

        // And the second half of the same property: a rerun of the range writes nothing new either.
        // The insert collides on the setup's own key and does nothing, so a calibration interrupted
        // halfway and started again counts each night once rather than twice.
        Assert.Equal(calibrated, Rows(replay, SetupReader.CalibrationTable));
    }

    /// <summary>
    /// The read side of the same property, which nothing asserted until it was asked about.
    ///
    /// <b>The write side is above: a calibration run leaves the evidence store as it found it.</b>
    /// That says reconstructed rows never enter `setup`. It does not say the scoreboard cannot go and
    /// read them where they do live, and a band 1 panel computed over 49,450 survivorship-biased rows
    /// would look exactly like a band 1 panel that had finally accumulated a sample.
    ///
    /// So it is asked behaviourally rather than by reading the SQL: fill the calibration table, then
    /// build the panels, and require every count to be a count of the evidence store. A builder that
    /// joined the wrong table would move `setupsOnFile` by four orders of magnitude.
    ///
    /// <b>Written because a reader of a withheld panel needs to know this is not why.</b> Three things
    /// could stop band 1 answering, and two of them are settled by waiting. This one is settled by
    /// construction, which is worth nothing at all unless something checks it.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    [Fact]
    public void The_scoreboard_counts_the_evidence_store_and_never_the_calibration_table()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        DateOnly from = Sessions(replay)[IndicatorEngine.WarmupSessions - 1];
        replay.CalibrateLong(from, replay.AsOf);
        replay.CalibrateShort(from, replay.AsOf);

        int flagged = Scalar(replay, $"SELECT COUNT(*) FROM {SetupReader.SetupTable}");
        int reconstructed = Scalar(replay, $"SELECT COUNT(*) FROM {SetupReader.CalibrationTable}");

        // Without a populated calibration table this test asserts nothing at all, so it says so
        // rather than passing quietly over an empty one.
        Assert.True(
            reconstructed > flagged,
            $"the calibration table holds {reconstructed} row(s) against the evidence store's {flagged}, "
            + "so this test cannot tell the two populations apart");

        // The panels as they stood are keyed on the day and will not be overwritten, so the question
        // is asked of a build that runs with the calibration table already full.
        Execute(replay, "DELETE FROM scoreboard");
        replay.BuildScoreboard();

        Assert.Equal(flagged, Scalar(replay,
            "SELECT n_rows FROM scoreboard WHERE panel = 'band0.setupsOnFile'"));

        int band1Rows = Scalar(replay,
            "SELECT COALESCE(SUM(n_rows), 0) FROM scoreboard WHERE panel LIKE 'band1.%'");

        Assert.True(
            band1Rows <= flagged * 2,
            $"band 1 counted {band1Rows} row(s) across its four panels, which is more than the "
            + $"{flagged} setup(s) on file can supply to two control sets");
    }

    private static int Scalar(PhaseReplay replay, string sql)
    {
        using SqliteConnection connection = replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(PhaseReplay replay, string sql)
    {
        using SqliteConnection connection = replay.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Every_short_row_a_calibration_run_writes_says_the_cap_was_exempt()
    {
        // The exemption is only defensible if it is on the record. Two clauses of the same gate
        // decide different things and a count produced by nine clauses reads exactly like a count
        // produced by ten, so the row carries which one it was.
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        using SqliteConnection connection = replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT check_results FROM calibration_setup WHERE direction = @direction";
        command.Parameters.AddWithValue("@direction", SetupDirection.Short);

        int rows = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows++;
            Assert.Contains(ShortPullbackRules.ClausesRunWithoutTheCap, reader.GetString(0), StringComparison.Ordinal);
        }

        Assert.True(rows > 0, "the calibration run wrote no short row, so nothing here was asserted.");
    }

    [Fact]
    public void A_forward_night_still_fails_a_name_with_no_resolved_capitalisation()
    {
        // The exemption's other side, and the one that would be silent. A default that leaked into
        // the nightly detector would turn an unknown into a pass on the one check standing in for
        // information the feed does not carry at all.
        var evidence = new ShortPullbackRules.ShortEvidence
        {
            Close = 40m,
            MedianDollarVolume = 90_000_000m,
            SessionsListed = 400,
            MarketCap = null,
        };

        CheckResult verdict = Assert.Single(
            ShortPullbackRules.Evaluate(evidence), r => r.Name == "tradable-shortable");

        Assert.False(verdict.Passed);
        Assert.Contains("no resolved market capitalisation", verdict.Note ?? string.Empty, StringComparison.Ordinal);
    }

    private static string[] Rows(PhaseReplay replay, string table)
    {
        using SqliteConnection connection = replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();

        // Two statements rather than one with the table interpolated, for the reason the detectors
        // write two inserts: a table name that only exists at runtime is invisible to the checks
        // that read the shipped source.
        command.CommandText = string.Equals(table, SetupReader.CalibrationTable, StringComparison.Ordinal)
            ? "SELECT setup_id, check_results, passed_all FROM calibration_setup ORDER BY setup_id"
            : "SELECT setup_id, check_results, passed_all FROM setup ORDER BY setup_id";

        var rows = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetInt32(2)}");
        }

        return [.. rows];
    }

    private static DateOnly[] Sessions(PhaseReplay replay)
    {
        using SqliteConnection connection = replay.OpenStore();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT bar_date FROM daily_bar ORDER BY bar_date";

        var dates = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            dates.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return [.. dates];
    }

    [Fact]
    public void A_distribution_over_no_nights_is_refused_rather_than_reported_as_zero()
    {
        // The degenerate case a threshold could be set against without anything saying so. Nought
        // candidates a night and no nights at all are different facts, and only one of them means
        // the gates are too tight.
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => NightlyCounts.Of([]));
        Assert.Contains("says nothing", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_median_of_an_even_number_of_nights_is_the_half_between_the_middle_two()
    {
        NightlyCounts.Distribution d = NightlyCounts.Of([4, 1, 3, 2]);

        Assert.Equal(4, d.Nights);
        Assert.Equal(1, d.Lowest);
        Assert.Equal(4, d.Highest);
        Assert.Equal(2.5, d.Median);
        Assert.Equal(1.75, d.LowerQuartile);
        Assert.Equal(3.25, d.UpperQuartile);
        Assert.Equal(10, d.Total);
        Assert.Equal(0, d.EmptyNights);
    }

    [Fact]
    public void A_night_of_no_candidates_is_counted_rather_than_dropped()
    {
        // The figure that says whether a median of nought is a quiet population or an empty one.
        NightlyCounts.Distribution d = NightlyCounts.Of([0, 0, 0, 7]);

        Assert.Equal(3, d.EmptyNights);
        Assert.Equal(0, d.Median);
        Assert.Equal(7, d.Highest);
    }

    [Fact]
    public void One_night_is_its_own_every_quartile()
    {
        NightlyCounts.Distribution d = NightlyCounts.Of([6]);

        Assert.Equal(6, d.Median);
        Assert.Equal(6, d.LowerQuartile);
        Assert.Equal(6, d.UpperQuartile);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.5, 3)]
    [InlineData(0.75, 4)]
    [InlineData(1.0, 5)]
    public void The_quantile_convention_is_linear_interpolation_and_is_stated_rather_than_assumed(
        double q, double expected)
    {
        // Five points, evenly spaced, so every convention that exists agrees here. What this pins
        // is that the ends are the extremes rather than an extrapolation past them, which is where
        // the conventions differ and where a distribution of a hundred nights would drift.
        Assert.Equal(expected, NightlyCounts.Quantile([1, 2, 3, 4, 5], q));
    }

    [Fact]
    public void The_band_includes_both_ends()
    {
        Assert.True(NightlyCounts.InsideTheBand(NightlyCounts.BandLow));
        Assert.True(NightlyCounts.InsideTheBand(NightlyCounts.BandHigh));
        Assert.False(NightlyCounts.InsideTheBand(NightlyCounts.BandLow - 0.01));
        Assert.False(NightlyCounts.InsideTheBand(NightlyCounts.BandHigh + 0.01));
    }

    [Fact]
    public void A_rate_per_name_scaled_back_to_its_own_universe_is_the_count_it_came_from()
    {
        // The property that makes the scaling readable rather than a fudge: it is one
        // multiplication and one division, and over its own universe it is the identity.
        double rate = NightlyCounts.RatePerName(12, 30);

        Assert.Equal(12, NightlyCounts.ScaledTo(rate, 30), 10);
        Assert.Equal(0.4, rate, 10);
        Assert.Equal(828, NightlyCounts.ScaledTo(rate, 2070), 10);
    }

    [Fact]
    public void A_rate_over_no_names_is_refused_rather_than_infinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NightlyCounts.RatePerName(3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NightlyCounts.ScaledTo(0.1, 0));
    }

    [Fact]
    public void The_rank_decile_is_taken_over_the_direction_own_allocation()
    {
        // NightlyCap ranks each side separately and says so: "Ranked within a direction and never
        // across". The decile denominator was the pooled sixty, so a short rank of 20, which is the
        // last one the cap takes on that side, landed in decile 4 and band2.decile5 through
        // decile10 did not exist on the short side at all.
        //
        // Both sides now span the full ten, so a decile label means the same fraction of its own
        // ordering on either side and the two curves can be read against each other.
        Assert.Equal(1, ScoreboardBuilder.Decile(1, "long"));
        Assert.Equal(10, ScoreboardBuilder.Decile(NightlyCap.LongAllocation, "long"));

        Assert.Equal(1, ScoreboardBuilder.Decile(1, "short"));
        Assert.Equal(10, ScoreboardBuilder.Decile(NightlyCap.ShortAllocation, "short"));

        // The defect as a case: under the pooled denominator this was 4.
        Assert.Equal(10, ScoreboardBuilder.Decile(20, "short"));

        // A rank past the cap is a candidate the cap truncated. It stays inside the scale rather
        // than opening an eleventh decile, which is what the clamp is for.
        Assert.Equal(10, ScoreboardBuilder.Decile(NightlyCap.LongAllocation + 25, "long"));

        // And the two sides are not the same function, which is the whole point.
        Assert.NotEqual(ScoreboardBuilder.Decile(20, "long"), ScoreboardBuilder.Decile(20, "short"));
    }

}
