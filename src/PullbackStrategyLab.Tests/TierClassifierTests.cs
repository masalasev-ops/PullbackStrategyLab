using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The ladder grade: three grades that partition every name, written as a later observation.
///
/// The partition is the property worth holding. A name graded both ways, or neither, would be a
/// hole in something every later stage reads, and "mixed" is the grade that makes it total rather
/// than a bucket for names the stage failed on.
/// </summary>
public sealed class TierClassifierTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 26, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly AsOf = new(2026, 8, 26);

    public TierClassifierTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private TierClassifier Stage() => new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the partition ----------------------------------------------------------------------

    [Theory]
    [InlineData(110, 100, 90, 80, TierClassifier.Rising)]
    [InlineData(70, 80, 90, 100, TierClassifier.Falling)]
    [InlineData(110, 100, 90, 95, TierClassifier.Mixed)]
    [InlineData(95, 100, 90, 80, TierClassifier.Mixed)]
    [InlineData(100, 100, 100, 100, TierClassifier.Mixed)]
    public void Every_arrangement_of_the_four_numbers_gets_exactly_one_grade(
        int close, int shortAverage, int medium, int longAverage, string expected) =>
        Assert.Equal(expected, TierClassifier.Grade(close, Figures(shortAverage, medium, longAverage)));

    [Fact]
    public void The_three_grades_are_exhaustive_and_exclusive()
    {
        // Swept rather than sampled. Every ordering of four values drawn from a small set has to
        // produce one of the three, and the two directed grades must never both apply.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int graded = 0;

        for (int close = 1; close <= 4; close++)
        {
            for (int s = 1; s <= 4; s++)
            {
                for (int m = 1; m <= 4; m++)
                {
                    for (int l = 1; l <= 4; l++)
                    {
                        string grade = TierClassifier.Grade(close, Figures(s, m, l));
                        Assert.Contains(grade, new[] { TierClassifier.Rising, TierClassifier.Mixed, TierClassifier.Falling });
                        seen.Add(grade);
                        graded++;
                    }
                }
            }
        }

        // Stated in advance rather than left self-validating: 4^4 arrangements, and all three
        // grades have to appear or the sweep proved only that the method returns something.
        Assert.Equal(256, graded);
        Assert.Equal(3, seen.Count);
    }

    // ---- the later observation ----------------------------------------------------------------

    [Fact]
    public void The_grade_is_written_as_a_later_observation_and_the_engines_row_survives()
    {
        Seed("RISER", close: 110m, s: 100m, m: 90m, l: 80m);

        TierResult result = Stage().Classify(AsOf);

        Assert.Equal(1, result.Graded);
        Assert.Equal(0, result.Collided);
        Assert.Equal(1, result.Rising);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Equal(2, Rows(connection, "RISER"));

        // The latest observation carries the grade, and the earlier one is untouched.
        StoredIndicators latest = IndicatorDailyReader.Latest(connection, "RISER", AsOf)!;
        Assert.Equal(TierClassifier.Rising, latest.LadderGrade);
        Assert.Equal(100m, latest.EmaShort);
    }

    [Fact]
    public void A_grade_written_at_the_instant_the_engine_wrote_would_collide_and_does_not()
    {
        // The defect this stage shipped with for one run. The key is (ticker, as_of, computed_at)
        // and the insert says DO NOTHING, so writing at the engine's own instant is silent: the
        // stage counted thirty grades and wrote no rows. A fixed clock made it certain; a real
        // clock made it merely unlikely, which is worse.
        Seed("RISER", close: 110m, s: 100m, m: 90m, l: 80m, computedAt: _clock.UtcNow);

        TierResult result = Stage().Classify(AsOf);

        Assert.Equal(1, result.Graded);
        Assert.Equal(0, result.Collided);

        using SqliteConnection connection = _connections.OpenReadOnly();
        Assert.Equal(TierClassifier.Rising, IndicatorDailyReader.Latest(connection, "RISER", AsOf)!.LadderGrade);
    }

    [Fact]
    public void A_second_run_grades_nothing_twice()
    {
        Seed("RISER", close: 110m, s: 100m, m: 90m, l: 80m);

        Stage().Classify(AsOf);
        TierResult second = Stage().Classify(AsOf);

        Assert.Equal(0, second.Graded);
        Assert.Equal(1, second.AlreadyGraded);
    }

    [Fact]
    public void A_name_with_no_figures_for_this_session_is_left_ungraded()
    {
        // The engine refuses for a name short of its warm-up or carrying an open rebuild demand,
        // and a grade taken against an older session's averages would be a statement about the
        // wrong night.
        SeedSecurityAndMember("BLANK");

        TierResult result = Stage().Classify(AsOf);

        Assert.Equal(1, result.NoIndicators);
        Assert.Equal(0, result.Graded);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static IIndicatorFigures Figures(decimal s, decimal m, decimal l) =>
        new StoredIndicators("X", AsOf, DateTimeOffset.UnixEpoch, s, m, l, 1m, 0.05m, 1m, 1m, null);

    private static int Rows(SqliteConnection connection, string ticker)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM indicator_daily WHERE ticker = @t";
        command.Parameters.AddWithValue("@t", ticker);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void SeedSecurityAndMember(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@t, @t, 'US', 'Common Stock', '2020-01-02')
                """;
            security.Parameters.AddWithValue("@t", ticker);
            security.ExecuteNonQuery();
        }

        using SqliteCommand snapshot = connection.CreateCommand();
        snapshot.CommandText = "INSERT INTO universe_snapshot (as_of, ticker) VALUES (@d, @t)";
        snapshot.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        snapshot.Parameters.AddWithValue("@t", ticker);
        snapshot.ExecuteNonQuery();
    }

    private void Seed(string ticker, decimal close, decimal s, decimal m, decimal l, DateTimeOffset? computedAt = null)
    {
        SeedSecurityAndMember(ticker);

        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand bar = connection.CreateCommand())
        {
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@t, @d, @c, @c, @c, @c, @c, 1000000, '2026-08-26T20:00:00.000Z')
                """;
            bar.Parameters.AddWithValue("@t", ticker);
            bar.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
            bar.Parameters.AddWithValue("@c", StoreText.PriceToStorageText(close));
            bar.ExecuteNonQuery();
        }

        using SqliteCommand row = connection.CreateCommand();
        row.CommandText = """
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20)
            VALUES (@t, @d, @at, @s, @m, @l, '1', '0.05', '1', '1')
            """;
        row.Parameters.AddWithValue("@t", ticker);
        row.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf));
        row.Parameters.AddWithValue("@at", StoreText.TimestampToStorageText(computedAt ?? _clock.UtcNow.AddHours(-1)));
        row.Parameters.AddWithValue("@s", StoreText.PriceToStorageText(s));
        row.Parameters.AddWithValue("@m", StoreText.PriceToStorageText(m));
        row.Parameters.AddWithValue("@l", StoreText.PriceToStorageText(l));
        row.ExecuteNonQuery();
    }
}
