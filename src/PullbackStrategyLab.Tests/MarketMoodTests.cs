using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// One mood scoring, reached from the nightly path and from a reconstructed one.
///
/// <b>The property that needed a test is not the arithmetic.</b> The scoring is pure and is tested
/// as arithmetic in <see cref="RegimeLabelerTests"/>, and the nightly stage's stored output is held
/// to seven `DERIVED` fixture expectations over a real market day, which is what says the extraction
/// to Core changed nothing. What neither of those reaches is the second caller: `CalibrationFigures`
/// computes a session's mood from the ladder counts it holds at `Rank` time, and a reconstructed
/// session is the only thing that exercises it.
///
/// <b>That is the eighth failure shape and it is why this file exists.</b> A clause written, correct
/// and proved, whose rows go down a different branch. Extracting the scoring so both paths share it
/// and then testing only the path that already worked would leave the reconstructed one asserted by
/// nothing at all, and a scope floor counts what a check looked at rather than which population it
/// looked at.
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
/// </summary>
public sealed class MarketMoodTests : IDisposable
{
    private static readonly DateOnly Session = new(2024, 3, 14);
    private static readonly string[] Trackers = ["SPY", "QQQ", "IWM"];

    /// <summary>Every bar in this store was observed long after its own session, as a backfill leaves them.</summary>
    private static readonly DateTimeOffset BackfilledAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public MarketMoodTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void A_reconstructed_session_scores_the_mood_the_nightly_stage_would_have_scored()
    {
        // <b>Seeded as a forward night saw them, and that is the only store on which the two paths
        // can be compared at all.</b> The nightly reader bounds `observed_at` on the session's own
        // end of day, so on a backfilled store it reads nothing and would "disagree" for a reason
        // that has nothing to do with the scoring. The test below is the one that holds that.
        //
        // Here every tracker bar was observed on its own session, both readers see the same window,
        // and any disagreement left is the arithmetic.
        SeedTrackers(rising: true, observedOnTheSession: true);
        SeedLadder(rising: 12, falling: 4);

        using SqliteConnection connection = _connections.OpenWrite();

        // The nightly path, reading the store, bounded on the session's own end of day.
        (int longLadder, int shortLadder) = (12, 4);
        MoodScore nightly = MarketMood.Of(
            NightlyTrackers(connection), Session, RegimeLabeler.HistorySessions, longLadder, shortLadder);

        // The reconstructed path, bounded on the run's own instant, which is the bound a calibration
        // walk passes and the four-argument reader cannot take.
        MoodScore reach = MarketMood.Of(
            ReconstructedTrackers(connection), Session, RegimeLabeler.HistorySessions, longLadder, shortLadder);

        Assert.Equal(nightly.Label, reach.Label);
        Assert.Equal(nightly.IndexScore, reach.IndexScore);
        Assert.Equal(nightly.BreadthScore, reach.BreadthScore);
        // Not merely equal, but equal at a value that is not the default. Two paths that both read
        // nothing agree on `mixed` and would pass an equality assertion having measured no tracker
        // at all, which is the shape of agreement this corpus keeps finding.
        Assert.Equal(MarketMood.RiskOn, reach.Label);
        Assert.Equal(3, reach.IndexesMeasured);
        Assert.Equal(3, nightly.IndexesMeasured);
    }

    [Fact]
    public void The_tracker_read_bounded_on_the_session_sees_nothing_of_a_backfilled_history()
    {
        // The defect the fifth argument exists for, asserted rather than described. Every index bar
        // in this store was observed on 2026-08-30 and the session is in 2024, so a read bounded on
        // the session's own end of day returns nothing at all: not a stale answer, no answer. Left
        // alone, a reconstructed session scores every tracker unmeasured, the index score falls to
        // 0 by the rule that says "none of nothing was above" is not "none of three was above", and
        // the mood is mixed on every session of history whatever the market did.
        SeedTrackers(rising: true, observedOnTheSession: false);

        using SqliteConnection connection = _connections.OpenWrite();

        Assert.Empty(IndexBarReader.Read(connection, "SPY", Session, RegimeLabeler.HistorySessions, SessionBoundaries.UsEquities));
        Assert.NotEmpty(IndexBarReader.Read(
            connection, "SPY", Session, RegimeLabeler.HistorySessions, BackfilledAt, SessionBoundaries.UsEquities));

        MoodScore unbounded = MarketMood.Of(
            NightlyTrackers(connection), Session, RegimeLabeler.HistorySessions, 12, 4);

        Assert.Equal(0, unbounded.IndexesMeasured);
        Assert.Equal(0, unbounded.IndexScore);
        Assert.Equal(MarketMood.Mixed, unbounded.Label);
    }

    private IReadOnlyList<MarketMood.Tracker> NightlyTrackers(SqliteConnection connection) =>
        [.. Trackers.Select(symbol =>
        {
            IReadOnlyList<StoredDailyBar> bars =
                IndexBarReader.Read(connection, symbol, Session, RegimeLabeler.HistorySessions, SessionBoundaries.UsEquities);
            return new MarketMood.Tracker(
                [.. bars.Select(b => b.AdjustedClose)], bars.Count == 0 ? default : bars[^1].BarDate);
        })];

    private IReadOnlyList<MarketMood.Tracker> ReconstructedTrackers(SqliteConnection connection) =>
        [.. Trackers.Select(symbol =>
        {
            IReadOnlyList<StoredDailyBar> bars = IndexBarReader.Read(
                connection, symbol, Session, RegimeLabeler.HistorySessions, BackfilledAt, SessionBoundaries.UsEquities);
            return new MarketMood.Tracker(
                [.. bars.Select(b => b.AdjustedClose)], bars.Count == 0 ? default : bars[^1].BarDate);
        })];

    /// <summary>A full warm-up of tracker bars ending on the session, climbing so every close is above its average.</summary>
    private void SeedTrackers(bool rising, bool observedOnTheSession)
    {
        DateOnly date = Session.AddDays(-(RegimeLabeler.HistorySessions * 2));
        var dates = new List<DateOnly>();

        while (dates.Count < RegimeLabeler.HistorySessions - 1)
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                dates.Add(date);
            }

            date = date.AddDays(1);
        }

        dates.Add(Session);

        for (int i = 0; i < dates.Count; i++)
        {
            decimal close = rising ? 100m + i : 100m - i;

            foreach (string symbol in Trackers)
            {
                Execute("""
                    INSERT INTO index_bar
                        (symbol, bar_date, open, high, low, close, adj_close, volume, observed_at)
                    VALUES (@s, @d, @c, @c, @c, @c, @c, 1000000, @o)
                    ON CONFLICT (symbol, bar_date, observed_at) DO NOTHING
                    """,
                    ("@s", symbol),
                    ("@d", StoreText.DateToStorageText(dates[i])),
                    ("@c", StoreText.PriceToStorageText(close)),
                    ("@o", StoreText.TimestampToStorageText(
                        observedOnTheSession
                            ? new DateTimeOffset(dates[i].Year, dates[i].Month, dates[i].Day, 21, 0, 0, TimeSpan.Zero)
                            : BackfilledAt)));
            }
        }
    }

    /// <summary>Ladder grades on the session, which is what the nightly breadth count reads.</summary>
    private void SeedLadder(int rising, int falling)
    {
        for (int i = 0; i < rising + falling; i++)
        {
            string ticker = $"N{i:D3}";
            string grade = i < rising ? TierClassifier.Rising : TierClassifier.Falling;

            Execute(
                "INSERT INTO security VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01', "
                + "NULL, NULL, NULL, NULL) ON CONFLICT (ticker) DO NOTHING",
                ("@t", ticker));

            Execute("""
                INSERT INTO indicator_daily
                    (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                     dollar_volume_median_20, range_avg_20, ladder_grade)
                VALUES (@t, @d, @c, '1.0000', '1.0000', '1.0000', '1.0000', '1.0000',
                        '50000000.0000', '1.0000', @g)
                ON CONFLICT (ticker, as_of, computed_at) DO NOTHING
                """,
                ("@t", ticker),
                ("@d", StoreText.DateToStorageText(Session)),
                ("@c", StoreText.TimestampToStorageText(BackfilledAt)),
                ("@g", grade));
        }
    }

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
