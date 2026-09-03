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
/// The win-rate ceiling as the stage computes it, which had no test of any kind until 3.11.
///
/// Band 2 turns on this figure and so does the project's own question at 3.6: if the gap between
/// the bound and what was achieved is near zero, selection has no room and the loop should point at
/// execution instead. The arithmetic behind it was exercised only through the fixture replay, which
/// holds three setups and no closed horizon, so nothing anywhere asserted what the stage does with
/// a population.
///
/// The conversion is the trap and it is the reason these are written as cases rather than as one
/// end-to-end run. The excursion is in ATR and the give-up distance is in daily ranges, so a
/// comparison between them is only meaningful once both are prices, and a version of this that
/// compared the two multiples directly would look entirely reasonable and be wrong by the ratio of
/// two volatility measures.
/// see: The ceiling is computed from the path, not from the terminal return
/// </summary>
public sealed class CeilingCalculatorTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 28);
    private static readonly DateOnly Session = new(2026, 8, 10);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 28, 22, 0, 0, TimeSpan.Zero));

    public CeilingCalculatorTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private CeilingCalculator Stage()
    {
        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new CeilingCalculator(_connections, new RunLogger(_clock, options), _clock, options);
    }

    /// <summary>
    /// One subject, with every figure the bound reads.
    ///
    /// The prices are chosen so the conversion is checkable by hand: the close is 100 and the daily
    /// range is 2% of it, so a give-up distance of one range is 2.00 in price, and the ATR is 1.00
    /// so an excursion of n ATR is n in price. A subject survives when its adverse excursion in
    /// price is under its give-up in price.
    /// </summary>
    private void Seed(
        string ticker,
        string direction,
        decimal returnSigned,
        decimal? maeAtr,
        decimal? stopDistanceRanges,
        DateOnly? session = null)
    {
        DateOnly on = session ?? Session;
        string setupId = $"{on:yyyy-MM-dd}-{ticker}-{direction}";

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, """
            INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @ticker, 'NASDAQ', 'Common Stock', '2020-01-02');
            """, ("@ticker", ticker));

        Execute(connection, transaction, """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @as_of, @ticker, @direction, '[]', 1, '101.00', '99.00', @give_up);
            """,
            ("@id", setupId), ("@as_of", StoreText.DateToStorageText(on)),
            ("@ticker", ticker), ("@direction", direction),
            ("@give_up", stopDistanceRanges is decimal g
                ? StoreText.RatioToStorageText(g)
                : (object)DBNull.Value));

        // A null adverse excursion is the row 050 admits: no excursions at all, with the reason
        // beside them, which is what a subject with no range on its own session is written as.
        Execute(connection, transaction, """
            INSERT INTO forward_return (subject_id, subject_kind, horizon_days, intended_date,
                                        actual_date, return_signed, mfe_atr, mae_atr,
                                        excursions_absent_because, filled_at)
            VALUES (@id, 'setup', @horizon, @date, @date, @return, @mfe, @mae, @absent, @filled_at);
            """,
            ("@id", setupId), ("@horizon", MeasurementParameters.ScoringHorizonSessions),
            ("@date", StoreText.DateToStorageText(on.AddDays(14))),
            ("@return", StoreText.PriceToStorageText(returnSigned)),
            ("@mfe", maeAtr is null ? DBNull.Value : "1.0"),
            ("@mae", maeAtr is decimal m ? StoreText.PriceToStorageText(m) : DBNull.Value),
            ("@absent", maeAtr is null ? ForwardReturnFiller.ExcursionsUndefined : DBNull.Value),
            ("@filled_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));

        Execute(connection, transaction, """
            INSERT INTO indicator_daily (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14,
                                         adr_20, dollar_volume_median_20, range_avg_20)
            VALUES (@ticker, @as_of, @computed_at, '100', '100', '100', '1.00', '0.02', '1000000', '2.00');
            """,
            ("@ticker", ticker), ("@as_of", StoreText.DateToStorageText(on)),
            ("@computed_at", StoreText.EndOfSession(on, SessionBoundaryZone)));

        Execute(connection, transaction, """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @as_of, '100', '101', '99', '100.00', '100.00', 1000000, @observed_at);
            """,
            ("@ticker", ticker), ("@as_of", StoreText.DateToStorageText(on)),
            ("@observed_at", StoreText.EndOfSession(on, SessionBoundaryZone)));

        transaction.Commit();
    }

    private const string SessionBoundaryZone = "America/New_York";

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

    [Fact]
    public void The_bound_counts_a_subject_whose_adverse_excursion_stayed_inside_its_give_up()
    {
        // Give-up 1.00 range = 2.00 in price. Excursion 1.5 ATR = 1.50 in price. Inside, so the
        // subject is one a perfect exit could have kept, and it made money, so achieved counts it.
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.5m, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        (string Direction, int Subjects, decimal Bound, decimal Achieved) bound =
            Assert.Single(result.Bounds);

        Assert.Equal("long", bound.Direction);
        Assert.Equal(1, bound.Subjects);
        Assert.Equal(1m, bound.Bound);
    }

    [Fact]
    public void A_subject_stopped_out_before_its_horizon_is_not_available_to_a_perfect_exit()
    {
        // Give-up 1.00 range = 2.00 in price. Excursion 3.0 ATR = 3.00 in price, so the stop was
        // hit and no exit rule could have kept this one.
        Seed("BBB", "long", returnSigned: 0.08m, maeAtr: -3.0m, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        Assert.Equal(0m, Assert.Single(result.Bounds).Bound);
    }

    [Fact]
    public void A_subject_that_never_traded_against_its_entry_is_nought_adverse_rather_than_its_own_size()
    {
        // The sign trap, which was live until 3.5 was reopened. A positive excursion means the path
        // never went against the subject at all. Read through an absolute value it becomes 3.0 ATR
        // adverse, the subject is judged stopped out, and it is dropped from the bound: exactly the
        // rows a perfect forecaster selects, removed from the figure that measures selection.
        Seed("CCC", "long", returnSigned: 0.17m, maeAtr: 3.0m, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        Assert.Equal(1m, Assert.Single(result.Bounds).Bound);
    }

    [Fact]
    public void The_two_directions_are_bounded_separately_and_never_pooled()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);
        Seed("BBB", "short", returnSigned: -0.02m, maeAtr: -4.0m, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        Assert.Equal(2, result.Bounds.Count);

        // One survivor on the long side and none on the short. A pooled bound would report 0.5 for
        // both and the borrow assumption on one side would be carried into the other.
        Assert.Equal(1m, result.Bounds.Single(b => b.Direction == "long").Bound);
        Assert.Equal(0m, result.Bounds.Single(b => b.Direction == "short").Bound);
    }

    [Fact]
    public void A_side_with_nothing_closed_writes_no_row_rather_than_a_row_of_noughts()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        // A ceiling of nought reads on a scoreboard as "selection has no room". What it would mean
        // here is "nobody has measured anything yet", and those are different sentences.
        Assert.DoesNotContain(result.Bounds, b => b.Direction == "short");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM ceiling_bound"));
    }

    /// <summary>
    /// A recomputation of a week that already carries its bound writes nothing and does not report
    /// clean, and the first bound stands.
    ///
    /// <b>The form 3.9(e) wrote for the scoreboard, applied here at 5.8.</b> The insert did nothing
    /// on conflict under a comment saying a recomputed week replaces its own row, and reported a
    /// clean run either way. Both computes in one test, because a run that failed on an empty store
    /// would satisfy the second assertion without the property holding at all.
    /// </summary>
    [Fact]
    public void A_recomputation_of_a_week_with_a_bound_writes_nothing_and_does_not_report_clean()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);

        CeilingResult first = Stage().Compute(AsOf);

        Assert.Equal(1, first.Attempted);
        Assert.Equal(0, first.Skipped);
        Assert.Equal("clean", first.Outcome.ToStorageText());

        // A second subject arrives; the recomputation would bound over two and is refused.
        Seed("BBB", "long", returnSigned: -0.05m, maeAtr: -2.0m, stopDistanceRanges: 1.00m);

        CeilingResult again = Stage().Compute(AsOf);

        Assert.Equal(1, again.Attempted);
        Assert.Equal(1, again.Skipped);
        Assert.Equal("failed", again.Outcome.ToStorageText());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM ceiling_bound"));
        Assert.Equal(1, Count("SELECT subjects FROM ceiling_bound"));
    }

    /// <summary>The command exits non-zero on a recomputation that wrote nothing, and says what stands.</summary>
    [Fact]
    public void The_command_exits_non_zero_on_a_recomputation_that_wrote_nothing()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);
        Assert.Equal(0, Stage().Run([AsOf.ToString("yyyy-MM-dd")]));

        StringWriter errors = new();
        TextWriter previous = Console.Error;
        Console.SetError(errors);

        try
        {
            Assert.Equal(1, Stage().Run([AsOf.ToString("yyyy-MM-dd")]));
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains("nothing was recomputed", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("The first bound written for a week stands", errors.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A subject whose excursions could not be measured is out of the population, on the same terms
    /// as one with no give-up distance, rather than read as nought adverse and counted as having
    /// survived. Until 050 the store held nought for that row and this bound would have counted it.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void A_subject_with_no_excursions_is_not_in_the_population()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);
        Seed("BBB", "long", returnSigned: 0.05m, maeAtr: null, stopDistanceRanges: 1.00m);

        CeilingResult result = Stage().Compute(AsOf);

        (string Direction, int Subjects, decimal Bound, decimal Achieved) bound =
            Assert.Single(result.Bounds);

        Assert.Equal(1, bound.Subjects);
        Assert.Equal(1m, bound.Bound);
    }

    [Fact]
    public void A_setup_with_no_give_up_distance_is_not_in_the_population()
    {
        // The column is nullable from 031 and a setup whose geometry the detector could not compute
        // has no stop, so there is no trade for a ceiling to be a ceiling of. Counting it as stopped
        // out would push the bound down for a row that was never a candidate.
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);
        Seed("BBB", "long", returnSigned: 0.05m, maeAtr: -1.0m, stopDistanceRanges: null);

        CeilingResult result = Stage().Compute(AsOf);

        (string Direction, int Subjects, decimal Bound, decimal Achieved) bound =
            Assert.Single(result.Bounds);

        Assert.Equal(1, bound.Subjects);
        Assert.Equal(1m, bound.Bound);
    }

    [Fact]
    public void A_forward_return_filled_after_the_run_instant_is_not_read()
    {
        Seed("AAA", "long", returnSigned: 0.08m, maeAtr: -1.0m, stopDistanceRanges: 1.00m);

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE forward_return SET filled_at = @later;";
            command.Parameters.AddWithValue(
                "@later", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(1)));
            command.ExecuteNonQuery();
        }

        // filled_at is what makes this read point-in-time honest. A row filled after the run's own
        // instant is one the lab could not have had, and a bound that saw it would be a statement
        // about a night that had not happened.
        Assert.Empty(Stage().Compute(AsOf).Bounds);
    }

    private long Count(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
