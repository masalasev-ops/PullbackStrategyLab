using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two averages, and the anchor that makes the second one mean something.
///
/// <b>Every figure here is over an authored population and that is stated once.</b> The store holds
/// no minute bars on the night this was written and the golden fixture holds one market day, so
/// nothing captured can exercise an average over a run of minutes. The bars below are authored to
/// sit either side of the properties under test.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class VwapEngineTests : IDisposable
{
    /// <summary>The session whose minutes are priced, and the evening before it, which flagged.</summary>
    private static readonly DateOnly Session = new(2026, 8, 25);

    private static readonly DateOnly PriorSession = new(2026, 8, 24);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(
        SessionBoundaries.At(Session, new TimeOnly(21, 0), SessionBoundaries.UsEquities));

    public VwapEngineTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private VwapEngine Engine()
    {
        IOptions<PullbackStrategyLabOptions> options = Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new VwapEngine(_connections, new RunLogger(_clock, options), _clock, options);
    }

    // ---- the anchor ----------------------------------------------------------------------

    [Fact]
    public void The_anchored_average_differs_from_the_session_average_where_the_anchor_is_not_the_open()
    {
        // The property the whole clause rests on. Anchored at a high that traded mid-session, the
        // level is an average of the part of the day after that high, and it is a different number
        // from the average of the whole day. If the two agreed, the anchor would be decoration and
        // `reached-ceiling` would have gained a third clause that says what its first two already do.
        Short("AAPL", peakAt: new TimeOnly(11, 0));

        VwapRunResult result = Engine().Compute(Session);

        Assert.Equal(1, result.AnchorsAsked);
        Assert.Equal(1, result.AnchorsPriced);

        decimal anchored = Anchored("AAPL")!.Value!.Value;
        decimal wholeSession = WholeSessionAverage("AAPL", PriorSession);

        Assert.NotEqual(wholeSession, anchored);

        // And it is the average of the minutes from the peak forward rather than some other subset,
        // asserted against the arithmetic rather than against a remembered figure.
        Assert.Equal(AverageFrom("AAPL", PriorSession, new TimeOnly(11, 0)), anchored);
    }

    [Fact]
    public void The_two_agree_where_the_anchor_is_the_first_minute_of_the_session()
    {
        // The other half, and it is what makes the first assertion mean something: a difference that
        // appeared whatever the anchor was would be evidence of a bug rather than of an anchor. With
        // the high on the opening minute the anchored run is the whole session and the two figures
        // are one figure.
        Short("MSFT", peakAt: SessionBoundaries.RegularSessionOpen);

        Engine().Compute(Session);

        Assert.Equal(WholeSessionAverage("MSFT", PriorSession), Anchored("MSFT")!.Value!.Value);
    }

    [Fact]
    public void The_anchor_minute_is_recorded_so_the_level_can_be_rebuilt_from_the_store()
    {
        // The anchor was a phrase until 4.4. What makes it a rule is that a reader who does not know
        // which component wrote the row can find the minute it started from.
        Short("NVDA", peakAt: new TimeOnly(13, 30));

        Engine().Compute(Session);

        StoredAnchoredVwap row = Anchored("NVDA")!;

        Assert.Equal(PriorSession, row.AnchorSession);
        Assert.Equal(
            SessionBoundaries.At(PriorSession, new TimeOnly(13, 30), SessionBoundaries.UsEquities),
            row.AnchorAt);
        Assert.Equal(VwapEngine.SwingHigh, row.AnchorKind);

        // The last session included, which is the half a reader would otherwise have to date from
        // `observed_at`. It is the session priced, not the evening that flagged.
        Assert.Equal(Session, row.ThroughSession);
        Assert.Equal(PriorSession, row.SetupAsOf);
        Assert.True(row.Bars > 0);
        Assert.True(row.Volume > 0);
    }

    [Fact]
    public void An_anchor_the_store_cannot_reach_is_a_row_with_a_reason_rather_than_no_row()
    {
        // The ordinary state, and it stays ordinary for a long time: the fetch buys one session a
        // night per flagged name and a swing sits three to twenty-seven sessions back. A night that
        // could anchor nothing has to stay distinguishable from a night nobody ran, which is only
        // true because the engine writes a row either way.
        Short("TSLA", peakAt: new TimeOnly(11, 0), storeMinutes: false);

        VwapRunResult result = Engine().Compute(Session);

        Assert.Equal(1, result.AnchorsAsked);
        Assert.Equal(0, result.AnchorsPriced);

        StoredAnchoredVwap row = Anchored("TSLA")!;

        Assert.Null(row.Value);
        Assert.Equal(VwapEngine.AnchorNotStored, row.AbsentBecause);

        // And it is still a clean run. Nothing was asked of the vendor and nothing failed, so a
        // partial here would report every night as partial until the store had years of minutes.
        Assert.Equal(RunOutcome.Clean, result.Outcome);
    }

    [Fact]
    public void A_setup_with_no_thrust_records_that_it_has_no_swing_to_anchor_at()
    {
        // A different absence from the one above and it is not the store's fault: with no thrust
        // there is no move, so there is no swing the move ran from and no anchor to reach for.
        Short("INTC", peakAt: new TimeOnly(11, 0), thrust: false);

        VwapRunResult result = Engine().Compute(Session);

        Assert.Equal(1, result.AnchorsAsked);
        Assert.Equal(0, result.AnchorsPriced);
        Assert.Equal(VwapEngine.NoAnchor, Anchored("INTC")!.AbsentBecause);
    }

    // ---- the session average -------------------------------------------------------------

    [Fact]
    public void The_session_average_is_written_onto_every_minute_as_it_stood_at_that_minute()
    {
        // A series and not a figure. A resolver standing at 10:47 asks what the average was at
        // 10:47, and one closing number per session would answer with a value the session had not
        // reached yet at every minute but the last.
        Short("AAPL", peakAt: new TimeOnly(11, 0), alsoOn: Session);

        VwapRunResult result = Engine().Compute(Session);

        Assert.Equal(1, result.SessionsPriced);

        IReadOnlyList<StoredIntradayBar> priced = Bars("AAPL", Session);
        Assert.All(priced, bar => Assert.NotNull(bar.VwapSession));

        // The first minute's average is its own typical price, and the last is the whole session's.
        Assert.Equal(
            VolumeWeightedAverage.TypicalPrice(priced[0].High, priced[0].Low, priced[0].Close),
            priced[0].VwapSession);
        Assert.Equal(WholeSessionAverage("AAPL", Session), priced[^1].VwapSession);
        Assert.NotEqual(priced[0].VwapSession, priced[^1].VwapSession);
    }

    [Fact]
    public void Extended_hours_minutes_are_left_without_a_session_average()
    {
        // The store holds them deliberately and 59% of a captured day sits outside the regular
        // session, so an average taken over everything would describe a different day from the one
        // every other figure in the lab is about. Null rather than a number over a population
        // nobody named.
        Short("AAPL", peakAt: new TimeOnly(11, 0), alsoOn: Session);
        Minute("AAPL", Session, new TimeOnly(8, 15), 100m, 100m, 100m, 1_000, "extended");

        Engine().Compute(Session);

        StoredIntradayBar early = Bars("AAPL", Session, regularOnly: false)
            .Single(b => b.SessionWindow == "extended");

        Assert.Null(early.VwapSession);
    }

    // ---- the night's record --------------------------------------------------------------

    [Fact]
    public void A_session_no_prior_evening_flagged_is_recorded_as_a_night_that_priced_nothing()
    {
        // The first-night state, and the same shape the fetch and the spread capture record. A
        // session with no row is a session nobody ran.
        VwapRunResult result = Engine().Compute(Session);

        Assert.Null(result.SetupAsOf);
        Assert.Equal(VwapEngine.NoPriorSession, result.StoppedBecause);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        using SqliteConnection connection = _connections.OpenReadOnly();
        StoredVwapRun? run = AnchoredVwapReader.LatestRun(connection, Session, Session);

        Assert.NotNull(run);
        Assert.Equal(0, run.AnchorsAsked);
        Assert.Equal(VwapEngine.NoPriorSession, run.StoppedBecause);
    }

    [Fact]
    public void A_long_setup_is_asked_for_no_anchor()
    {
        // `reached-ceiling` is a short check and the long list has no counterpart, so a long row has
        // no anchor to price. Asserted rather than assumed, because the engine reads the evening's
        // setups and the population it takes from them is the one thing a later edit could widen
        // without anything noticing.
        Short("AAPL", peakAt: new TimeOnly(11, 0));
        Setup("MSFT", "long", thrust: true);

        VwapRunResult result = Engine().Compute(Session);

        Assert.Equal(2, result.Names);
        Assert.Equal(1, result.AnchorsAsked);
        Assert.Null(Anchored("MSFT"));
    }

    // ---- the store -----------------------------------------------------------------------

    private StoredAnchoredVwap? Anchored(string ticker)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return AnchoredVwapReader.Latest(connection, ticker, PriorSession, Session);
    }

    private IReadOnlyList<StoredIntradayBar> Bars(string ticker, DateOnly session, bool regularOnly = true)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return IntradayBarReader.Read(connection, ticker, session, Session, regularOnly);
    }

    private decimal WholeSessionAverage(string ticker, DateOnly session) =>
        VolumeWeightedAverage.Of(
            Bars(ticker, session).Select(b =>
                new VolumeWeightedAverage.Minute(b.OpenedAt, b.High, b.Low, b.Close, b.Volume)))!.Value;

    private decimal AverageFrom(string ticker, DateOnly session, TimeOnly from) =>
        VolumeWeightedAverage.From(
            Bars(ticker, session).Select(b =>
                new VolumeWeightedAverage.Minute(b.OpenedAt, b.High, b.Low, b.Close, b.Volume)),
            SessionBoundaries.At(session, from, SessionBoundaries.UsEquities))!.Value;

    /// <summary>
    /// A short setup on the prior evening, its daily history, and the minutes behind its anchor.
    ///
    /// <paramref name="peakAt"/> is where the anchor session's high trades, which is the one thing
    /// these cases vary. A one-session thrust scan puts the swing in the flagged session itself, so
    /// the anchor is that session's high and the minute it traded in is authored here.
    /// </summary>
    private void Short(
        string ticker,
        TimeOnly peakAt,
        bool storeMinutes = true,
        bool thrust = true,
        DateOnly? alsoOn = null)
    {
        Setup(ticker, "short", thrust);

        if (storeMinutes)
        {
            Session_(ticker, PriorSession, peakAt);
        }

        if (alsoOn is DateOnly extra)
        {
            Session_(ticker, extra, peakAt);
        }
    }

    /// <summary>
    /// A session of minutes, rising to a single high at <paramref name="peakAt"/> and easing after.
    ///
    /// Six minutes rather than three hundred and ninety, because the property under test is which
    /// minutes the average runs over and not how many. Volumes differ per minute so a volume-weighted
    /// average and an unweighted one cannot come out the same by accident.
    /// </summary>
    private void Session_(string ticker, DateOnly session, TimeOnly peakAt)
    {
        TimeOnly[] minutes =
        [
            SessionBoundaries.RegularSessionOpen,
            new TimeOnly(11, 0),
            new TimeOnly(13, 30),
            new TimeOnly(15, 0),
        ];

        for (int i = 0; i < minutes.Length; i++)
        {
            bool peak = minutes[i] == peakAt;
            decimal mid = 100m + i;
            decimal high = peak ? 140m : mid + 0.5m;

            Minute(ticker, session, minutes[i], high, mid - 0.5m, mid, 1_000 + (i * 700), "regular");
        }
    }

    private void Minute(
        string ticker,
        DateOnly session,
        TimeOnly local,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        string window)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO intraday_bar
                (ticker, bar_ts, session_date, interval_code, session_window, price_basis,
                 open, high, low, close, volume, vwap_session, observed_at)
            VALUES (@ticker, @bar_ts, @session_date, '1m', @window, 'raw',
                    @open, @high, @low, @close, @volume, NULL, @observed_at);
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue(
            "@bar_ts",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, local, SessionBoundaries.UsEquities)));
        command.Parameters.AddWithValue("@session_date", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@window", window);
        command.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(close));
        command.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(high));
        command.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(low));
        command.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));
        command.Parameters.AddWithValue("@volume", volume);
        command.Parameters.AddWithValue(
            "@observed_at",
            StoreText.TimestampToStorageText(
                SessionBoundaries.At(session, new TimeOnly(20, 30), SessionBoundaries.UsEquities)));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A setup on the prior evening, with the daily history the anchor is resolved from.
    ///
    /// The daily bars fall through the window and bottom on the flagged session, so a one-session
    /// `decliner` puts both the swing and the thrust extreme in that session: the move ran from its
    /// high to its low. That is the shape the anchor is defined against and the shortest one that
    /// exercises it.
    /// </summary>
    private void Setup(string ticker, string direction, bool thrust)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(PriorSession.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        for (int back = 9; back >= 0; back--)
        {
            DateOnly date = PriorSession.AddDays(-back);
            decimal close = 120m - ((9 - back) * 2m);

            using SqliteCommand bar = connection.CreateCommand();
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES (@t, @d, @o, @h, @l, @c, @c, 1000000, @obs);
                """;
            bar.Parameters.AddWithValue("@t", ticker);
            bar.Parameters.AddWithValue("@d", StoreText.DateToStorageText(date));
            bar.Parameters.AddWithValue("@o", StoreText.PriceToStorageText(close + 1m));
            bar.Parameters.AddWithValue("@h", StoreText.PriceToStorageText(close + 2m));
            bar.Parameters.AddWithValue("@l", StoreText.PriceToStorageText(close - 1m));
            bar.Parameters.AddWithValue("@c", StoreText.PriceToStorageText(close));
            bar.Parameters.AddWithValue(
                "@obs",
                StoreText.TimestampToStorageText(
                    SessionBoundaries.At(date, new TimeOnly(18, 0), SessionBoundaries.UsEquities)));
            bar.ExecuteNonQuery();
        }

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                 thrust_scan, thrust_session)
            VALUES (@id, @as_of, @ticker, @direction, '[]', 1, 0, @scan, @session);
            """;
        setup.Parameters.AddWithValue("@id", $"{PriorSession:yyyy-MM-dd}-{ticker}-{direction}");
        setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(PriorSession));
        setup.Parameters.AddWithValue("@ticker", ticker);
        setup.Parameters.AddWithValue("@direction", direction);
        setup.Parameters.AddWithValue("@scan", thrust ? "decliner" : DBNull.Value);
        setup.Parameters.AddWithValue(
            "@session", thrust ? StoreText.DateToStorageText(PriorSession) : DBNull.Value);
        setup.ExecuteNonQuery();
    }
}
