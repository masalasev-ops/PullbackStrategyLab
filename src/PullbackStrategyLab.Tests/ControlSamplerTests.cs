using System.Globalization;
using System.Text.Json;
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
/// Both control sets drawn within the night, and the trend ladder as the only thing separating them.
///
/// <b>The tight set reached across sessions for one day and this file is what held it.</b> The
/// market mood is a property of the session, so within one night every candidate carries the
/// subject's own and an equality clause on it excludes nobody. The ruling of 2026-08-30 read that
/// invariance as a dimension that was never checked and made the mood vary by letting the tight set
/// draw from other sessions sharing the label. The ruling of 2026-08-31 reads the same invariance
/// as a perfect control and reverses the reach.
/// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
///
/// <b>What the reach cost is the cancellation the pairing exists to produce, and it was measured.</b>
/// A paired difference removes the market factor common to a night by construction, which is the
/// only reason a night is worth more than one observation. A control from another session does not
/// share the subject's night, so its side of the difference carries a different market move: over
/// identical rows and nights the tight comparison came back worth about a seventh of the loose one.
///
/// <b>The store below is seeded so a reach would be visible.</b> The earlier same-mood session holds
/// names nearer the subject on both distances than anything on the night, so a draw that could leave
/// the night would take them first. Nothing here passes because there was nowhere else to go.
/// </summary>
public sealed class ControlSamplerTests : IDisposable
{
    private static readonly DateOnly Tonight = new(2026, 8, 27);
    private static readonly DateOnly SameMoodEarlier = new(2026, 8, 25);
    private static readonly DateOnly After = new(2026, 8, 28);

    private const string Observed = "2026-01-01T00:00:00.000Z";

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 22, 26, 0, TimeSpan.Zero));

    public ControlSamplerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Every tight control is drawn from the subject's own session, and `control_as_of` says so on
    /// every row.
    ///
    /// <b>The invariant the column now carries, asserted rather than inferred.</b> `control_as_of`
    /// arrived with migration 035 to record the session a control's outcome is measured from, which
    /// stopped being the setup's when the reach landed and is the setup's again. The column stays,
    /// because a fact worth stating is better stated than derived from a join, and because it is
    /// what a returning reach would need. A column that is always equal to something else is one
    /// nobody checks, so this checks it.
    /// </summary>
    [Fact]
    public void A_tight_control_is_drawn_from_the_subjects_own_session()
    {
        SeedNight();

        Sampler().Draw(Tonight);

        IReadOnlyList<StoredControl> tight = Controls("tight");

        Assert.Equal(MeasurementParameters.ControlsPerSet, tight.Count);
        Assert.All(tight, c => Assert.Equal(Tonight, c.AsOf));

        // The names the earlier session holds are nearer the subject than any of tonight's, so this
        // is a set that a reach would have filled differently rather than one it could not reach.
        Assert.DoesNotContain(tight, c => c.Ticker.StartsWith("EARLY", StringComparison.Ordinal));
    }

    /// <summary>The loose set is unchanged and stays within the night, as it always has.</summary>
    [Fact]
    public void A_loose_control_is_drawn_from_the_subjects_own_session()
    {
        SeedNight();

        Sampler().Draw(Tonight);

        IReadOnlyList<StoredControl> loose = Controls("loose");

        Assert.NotEmpty(loose);
        Assert.All(loose, c => Assert.Equal(Tonight, c.AsOf));
    }

    /// <summary>
    /// The trend ladder is what makes the tight set tighter, and it is the only thing that does.
    ///
    /// Tonight holds two names on the subject's grade and six on another, and the six are nearer on
    /// both distances. The loose set takes five and the tight set takes the two, which is the whole
    /// of what the tight set now asks: is the pattern worth anything beyond owning stocks in
    /// uptrends. A tight set equal to the loose one would be the failure this asserts against, and it
    /// is a failure that has shipped here before, when the ladder grade read null on every candidate.
    /// </summary>
    [Fact]
    public void The_trend_ladder_is_what_separates_the_tight_set_from_the_loose_one()
    {
        Mood(Tonight, MarketMood.RiskOn);
        Name("SUBJ", Tonight, 100_000_000m, 2.0m, TierClassifier.Rising);
        Name("RISE0", Tonight, 150_000_000m, 3.0m, TierClassifier.Rising);
        Name("RISE1", Tonight, 160_000_000m, 3.1m, TierClassifier.Rising);

        for (int i = 0; i < 6; i++)
        {
            Name($"FALL{i}", Tonight, 101_000_000m + (i * 100_000m), 2.01m, TierClassifier.Falling);
        }

        Flag("SUBJ", Tonight, "long");

        Sampler().Draw(Tonight);

        Assert.Equal(MeasurementParameters.ControlsPerSet, Controls("loose").Count);
        Assert.Equal(2, Controls("tight").Count);
        Assert.All(Controls("tight"), c => Assert.StartsWith("RISE", c.Ticker, StringComparison.Ordinal));
    }

    /// <summary>
    /// A name flagged on the night is not a control for it, on either set.
    ///
    /// A control that was itself a setup is not a control, and admitting one narrows every
    /// comparison toward zero without changing a number a reader could see. The question used to
    /// have to be asked of the session being drawn from rather than of tonight, because the pool
    /// spanned sessions; within the night there is only one session it could be asked about and the
    /// two readings coincide.
    /// </summary>
    [Fact]
    public void A_name_flagged_on_the_night_is_not_a_control_for_it()
    {
        SeedNight();
        Flag("TONIGHT0", Tonight, "long");

        Sampler().Draw(Tonight);

        Assert.DoesNotContain(Controls("tight"), c => c.Ticker == "TONIGHT0");
        Assert.DoesNotContain(Controls("loose"), c => c.Ticker == "TONIGHT0");
    }

    /// <summary>
    /// An unlabelled night draws its tight set like any other, where under the superseded ruling it
    /// drew none.
    ///
    /// <b>The behaviour that changed direction, asserted in its new direction.</b> No session could
    /// be said to share a mood that was never recorded, so a missing label emptied the tight pool
    /// and a night whose regime stage failed lost its tight comparison. Within the night the label
    /// is not what does the controlling: every candidate sat through the same session whether or not
    /// anybody wrote the label down, so the mood is held fixed either way and the comparison stands.
    /// </summary>
    [Fact]
    public void A_night_with_no_mood_label_still_draws_its_tight_set()
    {
        SeedNight();
        Execute("DELETE FROM regime_daily WHERE as_of = @d", ("@d", Session(Tonight)));

        Sampler().Draw(Tonight);

        Assert.Equal(MeasurementParameters.ControlsPerSet, Controls("tight").Count);
        Assert.Equal(MeasurementParameters.ControlsPerSet, Controls("loose").Count);
        Assert.All(Controls("tight"), c => Assert.Equal(Tonight, c.AsOf));
    }

    /// <summary>
    /// The match quality records the mood as matched on the tight set and nought sessions apart on
    /// both, and the distance is computed rather than written as a constant.
    ///
    /// <b>Nought is the answer and not the assumption.</b> `sessionsApart` is the field a reader
    /// measures a reach by, so it is derived from the two dates on every row. A field that reported
    /// its own premise would read the same whether the draw stayed within the night or not, which is
    /// the shape of assertion this corpus keeps finding: right for as long as nothing moves.
    /// </summary>
    [Fact]
    public void The_match_quality_records_the_mood_matched_and_nought_sessions_apart()
    {
        SeedNight();

        Sampler().Draw(Tonight);

        foreach (StoredControl control in Controls("tight"))
        {
            Assert.Equal("same", control.MatchQuality["marketMood"]);
            Assert.Equal("same", control.MatchQuality["ladderGrade"]);
            Assert.Equal("0", control.MatchQuality["sessionsApart"]);
        }

        foreach (StoredControl control in Controls("loose"))
        {
            Assert.Equal("not matched", control.MatchQuality["marketMood"]);
            Assert.Equal("0", control.MatchQuality["sessionsApart"]);
        }
    }

    /// <summary>Five per set is five names, which is what five is for.</summary>
    [Fact]
    public void A_set_of_five_is_five_different_names()
    {
        SeedNight();

        Sampler().Draw(Tonight);

        foreach (string set in new[] { "loose", "tight" })
        {
            IReadOnlyList<StoredControl> drawn = Controls(set);

            Assert.NotEmpty(drawn);
            Assert.Equal(drawn.Count, drawn.Select(c => c.Ticker).Distinct(StringComparer.Ordinal).Count());
        }
    }

    // ---- the store the tests above run against -------------------------------------------------

    /// <summary>
    /// One night carrying enough of its own, with nearer names on an earlier session and on a later
    /// one so a draw that left the night would be visible.
    ///
    /// Every name carries the subject's ladder grade, so the ladder excludes nothing here and the
    /// session is the only thing that could separate the pool. The test that gives the ladder work
    /// to do seeds its own store.
    /// </summary>
    private void SeedNight()
    {
        Mood(SameMoodEarlier, MarketMood.RiskOn);
        Mood(Tonight, MarketMood.RiskOn);
        Mood(After, MarketMood.RiskOn);

        Name("SUBJ", Tonight, 100_000_000m, 2.0m, TierClassifier.Rising);

        for (int i = 0; i < 6; i++)
        {
            Name($"TONIGHT{i}", Tonight, 140_000_000m + (i * 10_000_000m), 2.8m, TierClassifier.Rising);
        }

        // Nearer the subject than anything on the night, on a session carrying the same mood label.
        // These are exactly the rows the superseded ruling drew and this one must not.
        for (int i = 0; i < 6; i++)
        {
            Name($"EARLY{i}", SameMoodEarlier, 100_100_000m + (i * 100_000m), 2.01m, TierClassifier.Rising);
        }

        // Nearest of anything and after the as-of. Point in time kept these out before the reach was
        // reversed and the night keeps them out now; both are worth asserting against.
        for (int i = 0; i < 6; i++)
        {
            Name($"LATER{i}", After, 100_000_000m, 2.0m, TierClassifier.Rising);
        }

        Flag("SUBJ", Tonight, "long");
    }

    private void Mood(DateOnly session, string label) =>
        Execute("""
            INSERT INTO regime_daily (as_of, index_score, breadth_score, label,
                                      long_ladder_count, short_ladder_count, indexes_above)
            VALUES (@d, 0, 0, @l, 0, 0, 0)
            ON CONFLICT (as_of) DO NOTHING
            """,
            ("@d", Session(session)), ("@l", label));

    /// <summary>One name's figures on one session, above the liquidity floor and on one ladder grade.</summary>
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

    private void Flag(string ticker, DateOnly session, string direction) =>
        Execute("""
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @d, @t, @dir, '[]', 1, '100.0', '97.0', '0.5')
            """,
            ("@id", $"{Session(session)}-{ticker}-{direction}"),
            ("@d", Session(session)), ("@t", ticker), ("@dir", direction));

    private IReadOnlyList<StoredControl> Controls(string set)
    {
        var controls = new List<StoredControl>();

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT control_ticker, control_as_of, match_quality FROM control_setup "
            + "WHERE control_set = @set ORDER BY rank";
        command.Parameters.AddWithValue("@set", set);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            controls.Add(new StoredControl(
                reader.GetString(0),
                StoreText.StorageTextToDate(reader.GetString(1)),
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2))
                    ?? throw new InvalidOperationException("match_quality did not parse.")));
        }

        return controls;
    }

    private sealed record StoredControl(
        string Ticker, DateOnly AsOf, IReadOnlyDictionary<string, string> MatchQuality);

    private ControlSampler Sampler()
    {
        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new ControlSampler(_connections, new RunLogger(_clock, options), _clock, options);
    }

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
