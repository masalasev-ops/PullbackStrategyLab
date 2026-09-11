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
/// A signal the library gains, computed across every setup already stored.
///
/// <b>Three properties, and the middle one is the only one that could go silently wrong.</b> The
/// stage has to reach old setups, it has to compute each at its own session rather than at the run's,
/// and it has to leave alone every value a night already froze. The first two fail loudly if the
/// walk is empty or the write is refused; the third would produce a store full of plausible numbers
/// computed against today's bars, and nothing about it would look wrong afterwards.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public sealed class SignalBackfillerTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 28, 22, 0, 0, TimeSpan.Zero));

    /// <summary>The night the setups are flagged on, two sessions before the backfill's own date.</summary>
    private static readonly DateOnly Flagged = new(2026, 8, 26);

    /// <summary>The backfill's as-of, which is later, because the vectorizer owns its own session.</summary>
    private static readonly DateOnly Today = new(2026, 8, 28);

    public SignalBackfillerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> LabOptions() =>
        Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private SignalBackfiller Stage() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    private SignalVectorizer Vectorizer() =>
        new(_connections, new RunLogger(_clock, LabOptions()), _clock, LabOptions());

    // ---- the deliverable ---------------------------------------------------------------------

    /// <summary>
    /// The two names 6.1 added, which these counts were written against. The sourced candidates 7.4
    /// adds to the fill list need a longer history than these seeded rows carry, so naming the pair
    /// keeps each count a statement about two signals rather than about how much history a test seeds.
    /// </summary>
    private static readonly string[] SixOne = ["ema_gap_21_50_over_avg", "ceiling_distance_ranges"];

    /// <summary>
    /// A signal the library has gained is computed across the whole stored setup history in one
    /// run, which is the checkpoint's deliverable stated as a test.
    ///
    /// The night is vectorized without the two names first, standing in for every setup recorded
    /// before the library gained them, and the backfill then reaches all of them.
    /// </summary>
    [Fact]
    public void A_signal_the_library_gains_is_computed_across_every_stored_setup_in_one_run()
    {
        Seed("AAA", Flagged);
        Seed("BBB", Flagged);
        FreezeEverythingExceptTheNewSignals();

        Assert.Empty(ValuesOf("ema_gap_21_50_over_avg"));

        SignalBackfillResult result = Stage().Backfill(Today, SixOne);

        Assert.Equal(1, result.Sessions);
        Assert.Equal(2, result.Setups);
        Assert.Equal(4, result.Written);
        Assert.Equal(0, result.AlreadyFrozen);
        Assert.Equal(RunOutcome.Clean, result.Outcome);

        Assert.Equal(2, ValuesOf("ema_gap_21_50_over_avg").Count);
        Assert.Equal(2, ValuesOf("ceiling_distance_ranges").Count);
    }

    /// <summary>
    /// A second run writes nothing, which is what the store's own key buys and what makes the stage
    /// safe to run again when the library grows once more.
    /// </summary>
    [Fact]
    public void A_second_run_over_the_same_history_writes_nothing()
    {
        Seed("AAA", Flagged);
        FreezeEverythingExceptTheNewSignals();

        SignalBackfillResult first = Stage().Backfill(Today, SixOne);
        SignalBackfillResult second = Stage().Backfill(Today, SixOne);

        Assert.Equal(2, first.Written);
        Assert.Equal(0, second.Written);
        Assert.Equal(SixOne.Length, second.AlreadyFrozen);
        Assert.Equal(0, second.RowsWritten);
    }

    // ---- the property that could go silently wrong -------------------------------------------

    /// <summary>
    /// Each setup is computed at its own session, not at the run's, proved by moving the bars
    /// between the two dates and requiring the backfilled value to ignore what arrived.
    ///
    /// <b>This is the one failure a passing run would hide.</b> A backfill run with today's date as
    /// the as-of produces a value no night could have held, on a row nothing ever revises, and the
    /// store would then be full of plausible numbers that describe a decision nobody made. The
    /// numbers differ here by construction: the two sessions after the setup move the price far
    /// enough that a distance computed over them cannot be mistaken for one computed without them.
    /// </summary>
    [Fact]
    public void A_backfilled_value_is_computed_at_the_setups_own_session_and_not_at_the_runs()
    {
        Seed("AAA", Flagged);
        FreezeEverythingExceptTheNewSignals();

        // What the night itself would have frozen, taken from a vectorizer run over a second store
        // seeded identically and stopped at the setup's own session.
        string atTheNight = TheNightsOwnValue();

        // Two more sessions, well away from the run of closes behind them.
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            Bar(connection, "AAA", Flagged.AddDays(1), 400m);
            Bar(connection, "AAA", Flagged.AddDays(2), 500m);
        }

        Stage().Backfill(Today, ["ceiling_distance_ranges"]);

        Assert.Equal(atTheNight, ValuesOf("ceiling_distance_ranges").Single().Value);
    }

    /// <summary>
    /// A value a night already froze is left exactly as the night wrote it, even where the stage is
    /// asked for that signal by name.
    ///
    /// The store's key refuses the second write, and the stage's own guard means it is not even
    /// attempted; both are here because a guard removed while the key stays would still pass and a
    /// key relaxed while the guard stays would still pass.
    /// </summary>
    [Fact]
    public void A_value_the_night_froze_is_not_revised()
    {
        Seed("AAA", Flagged);
        Vectorizer().Vectorize(Flagged);

        string frozen = ValuesOf("ceiling_distance_ranges").Single().Value;

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            Bar(connection, "AAA", Flagged.AddDays(1), 400m);
        }

        SignalBackfillResult result = Stage().Backfill(Today, ["ceiling_distance_ranges"]);

        Assert.Equal(0, result.Written);
        Assert.Equal(frozen, ValuesOf("ceiling_distance_ranges").Single().Value);
    }

    // ---- the two writers, disjoint by date ---------------------------------------------------

    /// <summary>
    /// The session the run is for belongs to the vectorizer, so a backfill never touches it.
    ///
    /// The two stages are declared disjoint by date and by signal, and the key holds only the
    /// second half: without this the two would race for exactly the names the backfill was asked
    /// to fill on the one night the vectorizer had not yet run.
    /// </summary>
    [Fact]
    public void The_runs_own_session_belongs_to_the_vectorizer_and_is_not_walked()
    {
        Seed("AAA", Flagged);

        SignalBackfillResult result = Stage().Backfill(Flagged, SignalBackfiller.Fills);

        Assert.Equal(0, result.Sessions);
        Assert.Equal(0, result.Setups);
        Assert.Equal(0, result.Written);
        Assert.Empty(ValuesOf("ceiling_distance_ranges"));
    }

    // ---- the two reads -----------------------------------------------------------------------

    /// <summary>
    /// The stamped read and the replay read differ on exactly the backfilled rows, and neither is
    /// the other's relaxation.
    ///
    /// <b>This is the property that makes a backfill useful without making it a lie.</b> A read
    /// answering "what did the night's decision rest on" must not return a value computed months
    /// later, and a read answering "what may the lab now compute about that night" must, because a
    /// backfilled value is a function of the night's own inputs and only the arithmetic is late.
    /// Asserted as a difference of two sets rather than as two counts, so a read that started
    /// returning nothing would fail rather than agreeing with the other by being empty.
    /// </summary>
    [Fact]
    public void The_stamped_read_and_the_replay_read_differ_on_exactly_the_backfilled_rows()
    {
        Seed("AAA", Flagged);
        FreezeEverythingExceptTheNewSignals();
        Stage().Backfill(Today, SixOne);

        using SqliteConnection connection = _connections.OpenReadOnly();

        string[] asTheNightSawIt =
        [
            .. SetupSignalReader.Read(connection, Flagged, SessionBoundaries.UsEquities)
                .Select(s => s.SignalName).Order(StringComparer.Ordinal),
        ];
        string[] asAReplayReadsIt =
        [
            .. SetupSignalReader.ReadIncludingBackfilled(connection, Flagged)
                .Select(s => s.SignalName).Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(asTheNightSawIt);
        Assert.Equal(
            SixOne.Order(StringComparer.Ordinal),
            asAReplayReadsIt.Except(asTheNightSawIt, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(asTheNightSawIt.Except(asAReplayReadsIt, StringComparer.Ordinal));
    }

    // ---- the roster --------------------------------------------------------------------------

    /// <summary>
    /// Every name the stage fills is one the vectorizer freezes, so a signal is never present on old
    /// setups and absent on new ones.
    ///
    /// A backfilled signal the nightly stage does not write would read as a signal that stopped
    /// being computed, which is indistinguishable from a stage that broke.
    /// </summary>
    [Fact]
    public void Every_signal_the_backfiller_fills_is_one_the_vectorizer_freezes()
    {
        Assert.NotEmpty(SignalBackfiller.Fills);
        Assert.All(SignalBackfiller.Fills, name => Assert.Contains(name, SignalVectorizer.Frozen, StringComparer.Ordinal));
    }

    /// <summary>
    /// A name nothing computes is refused rather than run as an empty pass, because a run that
    /// filled nothing and a run asked for a signal nobody computes read the same in a log.
    /// </summary>
    [Fact]
    public void A_signal_nothing_computes_is_refused_with_a_distinct_exit_code()
    {
        Seed("AAA", Flagged);

        Assert.Equal(2, Stage().Run(["2026-08-28", "not_a_signal"]));
        Assert.Equal(0, Stage().Run(["2026-08-28"]));
    }

    /// <summary>
    /// A candidate the library declares and the vectorizer does not freeze is refused the same way,
    /// from 7.4: declaring a signal does not make it fillable, and only the frozen set is. The name is
    /// a real candidate, being one nothing computes, so the refusal is about the freeze rather than
    /// about a name the library has never heard of.
    /// </summary>
    [Fact]
    public void A_declared_candidate_the_vectorizer_does_not_freeze_is_refused()
    {
        Assert.Contains(PullbackStrategyLab.Core.Research.SignalLibrary.Candidates, s => s.Name == "volume_dryup");
        Assert.DoesNotContain("volume_dryup", SignalVectorizer.Frozen);

        Assert.Equal(2, Stage().Run(["2026-08-28", "volume_dryup"]));
    }

    // ---- seeding -----------------------------------------------------------------------------

    /// <summary>
    /// The night's frozen row as it stands before the library grows: every signal the vectorizer
    /// writes except the two this checkpoint added.
    ///
    /// Written through the vectorizer's own <c>Values</c> rather than by hand, so the row is the row
    /// a night would have produced and the backfill is filling a real gap rather than an authored
    /// one.
    /// </summary>
    private void FreezeEverythingExceptTheNewSignals()
    {
        using SqliteConnection connection = _connections.OpenWrite();

        foreach (StoredSetup setup in SetupReader.Read(connection, Flagged))
        {
            IReadOnlyDictionary<string, string> values =
                SignalVectorizer.Values(connection, setup, setup.AsOf, SessionBoundaries.UsEquities);

            foreach (string name in SignalVectorizer.Frozen)
            {
                if (SignalBackfiller.Fills.Contains(name, StringComparer.Ordinal)
                    || !values.TryGetValue(name, out string? value))
                {
                    continue;
                }

                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
                    VALUES (@setup_id, @signal_name, @value, '2026-08-26T22:00:00.000Z')
                    """;
                insert.Parameters.AddWithValue("@setup_id", setup.SetupId);
                insert.Parameters.AddWithValue("@signal_name", name);
                insert.Parameters.AddWithValue("@value", value);
                insert.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// What the night itself would have frozen for the ceiling distance, from a second store seeded
    /// identically and vectorized at the setup's own session.
    ///
    /// A second store rather than a value read back from this one, because the point is to have a
    /// figure produced with no later bars in existence at all: comparing against a number this store
    /// produced would be comparing the backfill against itself.
    /// </summary>
    private string TheNightsOwnValue()
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = root.Path });

        Seed("AAA", Flagged, connections);
        new SignalVectorizer(connections, new RunLogger(_clock, options), _clock, options).Vectorize(Flagged);

        // Through the wider read, because this harness runs its vectorizer on the backfill's clock
        // rather than the night's, so the stamped read would hide the very value being compared.
        // Nothing is backfilled in this store, so the two reads return the same set.
        using SqliteConnection connection = connections.OpenReadOnly();
        return SetupSignalReader.ReadIncludingBackfilled(connection, Flagged)
            .Single(s => s.SignalName == "ceiling_distance_ranges")
            .Value;
    }

    /// <summary>
    /// The values on the flagged session, read the way a replay reads them.
    ///
    /// Through <c>ReadIncludingBackfilled</c> and not the stamped read, because the stamped read
    /// answers what the night's decision rested on and a backfilled value is by construction not
    /// that. The distinction is asserted directly in
    /// <see cref="The_stamped_read_and_the_replay_read_differ_on_exactly_the_backfilled_rows"/>.
    /// </summary>
    private IReadOnlyList<StoredSetupSignal> ValuesOf(string signal)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        return
        [
            .. SetupSignalReader.ReadIncludingBackfilled(connection, Flagged)
                .Where(s => s.SignalName == signal),
        ];
    }

    private void Seed(string ticker, DateOnly asOf) => Seed(ticker, asOf, _connections);

    /// <summary>
    /// A security, a run of bars long enough for the averages, one indicator row and one setup.
    ///
    /// The closes drift rather than sitting flat, because a flat run gives two averages that are
    /// equal and a gap of nought, and a ratio over a mean of nought is absent rather than a number.
    /// </summary>
    private static void Seed(string ticker, DateOnly asOf, StoreConnectionFactory connections)
    {
        using SqliteConnection connection = connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
                """;
            security.Parameters.AddWithValue("@ticker", ticker);
            security.ExecuteNonQuery();
        }

        const int Bars = 200;
        DateOnly date = asOf.AddDays(-Bars);

        for (int i = 0; i < Bars; i++)
        {
            date = date.AddDays(1);
            Bar(connection, ticker, i == Bars - 1 ? asOf : date, 100m + i);
        }

        using (SqliteCommand indicators = connection.CreateCommand())
        {
            indicators.CommandText = """
                INSERT INTO indicator_daily
                    (ticker, as_of, ema_9, ema_21, ema_50, atr_14, adr_20, range_avg_20,
                     dollar_volume_median_20, computed_at)
                VALUES (@ticker, @as_of, '297.0000', '295.0000', '290.0000', '4.0000', '0.0400',
                        '4.0000', '60000000.0000', '2026-08-26T20:30:00.000Z')
                """;
            indicators.Parameters.AddWithValue("@ticker", ticker);
            indicators.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            indicators.ExecuteNonQuery();
        }

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES (@setup_id, @as_of, @ticker, 'long', '{}', 0, '302.00', '298.00', '0.3300')
            """;
        setup.Parameters.AddWithValue("@setup_id", $"{ticker}-long");
        setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        setup.Parameters.AddWithValue("@ticker", ticker);
        setup.ExecuteNonQuery();
    }

    private static void Bar(SqliteConnection connection, string ticker, DateOnly barDate, decimal close)
    {
        using SqliteCommand bar = connection.CreateCommand();
        bar.CommandText = """
            INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
            VALUES (@ticker, @bar_date, @open, @high, @low, @close, @close, 2000000, @observed_at)
            """;
        bar.Parameters.AddWithValue("@ticker", ticker);
        bar.Parameters.AddWithValue("@bar_date", StoreText.DateToStorageText(barDate));
        bar.Parameters.AddWithValue("@open", StoreText.PriceToStorageText(close - 1m));
        bar.Parameters.AddWithValue("@high", StoreText.PriceToStorageText(close + 2m));
        bar.Parameters.AddWithValue("@low", StoreText.PriceToStorageText(close - 2m));
        bar.Parameters.AddWithValue("@close", StoreText.PriceToStorageText(close));

        // Stamped on the bar's own session, so a read bounded at an earlier as-of cannot see it.
        bar.Parameters.AddWithValue(
            "@observed_at", $"{StoreText.DateToStorageText(barDate)}T20:00:00.000Z");
        bar.ExecuteNonQuery();
    }
}
