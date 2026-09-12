using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The forecast pairs a night with the evening its list is actually bought on.
///
/// <b>The defect this closes, found at the 7.12 sign-off and repaired at 7.14.</b>
/// <see cref="IntradayFetcher"/> runs at 20:30 and buys the minutes of the session that has just
/// closed, for the names flagged on the evening before it. So night N's list is bought on the evening
/// of the session after N, and the forecast read the quota day holding N's own evening, which is the
/// day the night before's list spends in. Every figure it counted was right; the day it set them
/// against was one evening early.
///
/// <b>Why no figure over the golden fixture could show it.</b> Every replay stage runs at 22:00 UTC
/// on the as-of, which is the quota day before the fetch's either way, so the spend term is a
/// structural nought there and both readings agree on nought. The store below is built so the two
/// candidate days hold different spends, which is the only way to tell which one is read.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
/// </summary>
public sealed class ForecastPairingTests : IDisposable
{
    /// <summary>A Tuesday, with the Wednesday after it recorded as a session.</summary>
    private static readonly DateOnly Night = new(2026, 8, 25);

    private static readonly DateOnly NextSession = new(2026, 8, 26);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    public ForecastPairingTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Two quota days hold different spends, and the night's headroom is the one its fetch falls in.
    ///
    /// The night's own evening at 20:30 Eastern falls in the UTC day after the night, and the next
    /// session's evening one further on. A stage spends 400 in the first and 2,000 in the second, so
    /// the two readings differ by 1,600 calls and only one of them is the day the list is bought in.
    /// </summary>
    [Fact]
    public void A_nights_headroom_is_the_day_its_own_list_is_bought_in()
    {
        Session(Night);
        Session(NextSession);

        // The day holding the night's own evening, which is what the forecast read until 7.14.
        Spent("universe-build", new DateOnly(2026, 8, 26), 400);

        // The day the fetch of this night's list spends in, being the next session's evening.
        Spent("universe-build", new DateOnly(2026, 8, 27), 2000);

        ForecastNight forecast = Forecast();

        Assert.Equal(NextSession, forecast.BoughtOn);
        Assert.True(forecast.BoughtOnIsRecorded);
        Assert.Equal(new DateOnly(2026, 8, 27), forecast.QuotaDay);

        Assert.Equal(2000, forecast.SpentElsewhere);
        Assert.Equal(3000, forecast.Headroom);
    }

    /// <summary>
    /// The fetch's own spend is not counted against it, on either day.
    ///
    /// The question the headroom answers is what the fetch would have had left once every other stage
    /// of its day had spent, so a night the fetch already ran would otherwise be charged twice.
    /// </summary>
    [Fact]
    public void The_fetchs_own_spend_is_not_counted_against_its_headroom()
    {
        Session(Night);
        Session(NextSession);

        Spent(IntradayFetcher.Name, new DateOnly(2026, 8, 27), 1500);
        Spent("universe-build", new DateOnly(2026, 8, 27), 500);

        Assert.Equal(500, Forecast().SpentElsewhere);
    }

    /// <summary>
    /// Where the store records no session after the night, the next calendar day stands in and the
    /// report says so of that night.
    ///
    /// This is the ordinary state of the last night of a range, which is why RUNBOOK tells the
    /// operator to end the range one session past the last night they want read. Said on the row
    /// rather than left to be inferred, because a Friday's real buying evening is the Monday and a
    /// calendar day is not a market calendar.
    /// </summary>
    [Fact]
    public void A_night_with_no_session_after_it_says_its_evening_was_assumed()
    {
        Session(Night);

        ForecastNight forecast = Forecast();

        Assert.Equal(Night.AddDays(1), forecast.BoughtOn);
        Assert.False(forecast.BoughtOnIsRecorded);
    }

    private ForecastNight Forecast()
    {
        var options = new PullbackStrategyLabOptions { DataRoot = _root.Path };

        GenerationOneForecastReport report = new GenerationOneForecast(
            _connections,
            new RunLogger(_clock, Options.Create(options)),
            _clock,
            Options.Create(options),
            new PullbackStrategyLabPaths(_root.Path)).Forecast(Night, Night);

        return report.Nights.Single();
    }

    /// <summary>A session the store records, which is what the pairing reads forward for.</summary>
    private void Session(DateOnly asOf)
    {
        Execute("""
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('AAA', 'AAA', 'US', 'Common Stock', '2020-01-02')
            ON CONFLICT (ticker) DO NOTHING
            """);

        Execute(
            "INSERT INTO universe_snapshot (as_of, ticker) VALUES (@as_of, 'AAA')",
            ("@as_of", StoreText.DateToStorageText(asOf)));
    }

    /// <summary>One stage's spend inside a named vendor quota day, at noon UTC so the day is unambiguous.</summary>
    private void Spent(string stage, DateOnly quotaDay, int calls)
    {
        Execute("""
            INSERT INTO run_log
                (run_id, stage, started_at, ended_at, outcome, rows_written, calls_used, counts_against_ceiling)
            VALUES (@run_id, @stage, @at, @at, 'clean', 0, @calls, 1)
            """,
            ("@run_id", Guid.NewGuid().ToString("n")),
            ("@stage", stage),
            ("@at", StoreText.TimestampToStorageText(
                new DateTimeOffset(quotaDay.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero))),
            ("@calls", calls));
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
