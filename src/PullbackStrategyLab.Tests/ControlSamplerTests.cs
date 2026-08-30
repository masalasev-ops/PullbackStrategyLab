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
/// The tight control set reaching across sessions, and the loose set staying within the night.
///
/// <b>The dimension was declared and never implemented, and it could not be.</b> The tight set is
/// declared to match on the trend ladder <b>and</b> the market mood. The mood is a property of the
/// session, so within one night every candidate carries the same one and matching on it excludes
/// nothing; the draw left it out rather than performing a comparison true by construction, on the
/// grounds that a dimension which always matches reads in the record as a dimension that was
/// checked. The choice was to make it real or to drop it, and the operator ruled on 2026-08-30 that
/// it is kept: the tight set draws from any session sharing the mood, the loose set stays within
/// the night.
/// see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
///
/// <b>What the ruling costs is asserted here as well as stated.</b> A setup and its tight controls
/// may now come from different sessions, so the market factor common to one night no longer cancels
/// between them. That is why the session a control was drawn from is recorded on its row and why
/// its outcome is measured from that session: a tight control drawn from an earlier night whose
/// ten-day return was measured from the setup's night would be a real return of a real stock over
/// the wrong window, which is not a shape anything downstream could see.
/// </summary>
public sealed class ControlSamplerTests : IDisposable
{
    private static readonly DateOnly Tonight = new(2026, 8, 27);
    private static readonly DateOnly SameMoodEarlier = new(2026, 8, 25);
    private static readonly DateOnly OtherMoodEarlier = new(2026, 8, 26);
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
    /// The whole ruling in one run: the tight set reaches an earlier session sharing the mood, the
    /// loose set does not leave the night, and every drawn row says which session it came from.
    ///
    /// <b>The pool is built so the answer cannot come out right by accident.</b> Tonight carries one
    /// unflagged name, so a tight set confined to the night could draw at most one control. The
    /// earlier same-mood session carries four more. Five is therefore reachable only by reaching,
    /// and one is what the old draw would have produced.
    /// </summary>
    [Fact]
    public void The_tight_set_draws_from_an_earlier_session_sharing_the_mood()
    {
        SeedRuling();

        Sampler().Draw(Tonight);

        IReadOnlyList<StoredControl> tight = Controls("tight");
        IReadOnlyList<StoredControl> loose = Controls("loose");

        Assert.Equal(MeasurementParameters.ControlsPerSet, tight.Count);
        Assert.Contains(tight, c => c.AsOf == SameMoodEarlier);

        // The loose set stays within the night, on every row without exception. It matches on
        // liquidity and daily range, both properties of the name rather than of the session, so it
        // has nothing to gain from reaching and would pay the same cost for it.
        Assert.NotEmpty(loose);
        Assert.All(loose, c => Assert.Equal(Tonight, c.AsOf));
    }

    /// <summary>
    /// A session carrying a different mood contributes nothing to the tight set, end to end.
    ///
    /// The other-mood session is seeded with names nearer the subject on liquidity and daily range
    /// than any same-mood name, so a draw ignoring the mood would take them first: distance alone
    /// prefers exactly the rows the dimension exists to exclude.
    ///
    /// <b>This is not the test that guards the dimension, and saying so is the point.</b> The mood
    /// is excluded twice, once when `MoodPool` selects sessions and once when `ControlMatching`
    /// compares candidates, and this route goes through both. Removing either one on its own leaves
    /// this test green, which was measured rather than assumed: the clause in `ControlMatching` was
    /// deleted and all seven tests in this class passed. The guard is
    /// `ControlMatchingTests.A_tight_draw_excludes_a_candidate_from_a_session_carrying_a_different_mood`,
    /// which hands a mixed pool straight to the matcher. What this test holds is that the two halves
    /// are wired together, which no unit test of either half can say.
    /// </summary>
    [Fact]
    public void A_session_carrying_a_different_mood_contributes_no_tight_control()
    {
        SeedRuling();

        Sampler().Draw(Tonight);

        Assert.DoesNotContain(Controls("tight"), c => c.AsOf == OtherMoodEarlier);
    }

    /// <summary>
    /// Nothing is drawn from a session after the setup's, which is the point-in-time rule applied to
    /// the dimension the ruling opened up.
    ///
    /// The pool reaches backwards only. A control drawn from a later session would be an outcome
    /// the lab could not have had on the night it is being compared for, and the whole reason the
    /// pool is bounded at the as-of rather than merely filtered by mood.
    /// </summary>
    [Fact]
    public void No_tight_control_comes_from_a_session_after_the_setups_own()
    {
        SeedRuling();

        Sampler().Draw(Tonight);

        Assert.All(Controls("tight"), c => Assert.True(c.AsOf <= Tonight,
            $"a control was drawn from {c.AsOf}, which is after the setup's own session {Tonight}."));
    }

    /// <summary>
    /// A name flagged on its own session is not a control for that session, and the question is
    /// asked of the session drawn from rather than of tonight.
    ///
    /// <b>Both directions of the same error.</b> Asking tonight's question of a pool spanning two
    /// years would drop names that were ordinary on the session being drawn from, and admit names
    /// that were flagged on it. The second is the one that matters: a control that was itself a
    /// setup narrows every comparison toward zero without changing a number a reader could see.
    /// </summary>
    [Fact]
    public void A_name_flagged_on_the_session_drawn_from_is_not_a_control_for_it()
    {
        SeedRuling();

        // Flagged on the earlier same-mood session and on no other. It stays eligible tonight,
        // where it was not flagged, and must not be drawn from the session where it was.
        Flag("EARLY0", SameMoodEarlier, "long");

        Sampler().Draw(Tonight);

        Assert.DoesNotContain(
            Controls("tight"),
            c => string.Equals(c.Ticker, "EARLY0", StringComparison.Ordinal) && c.AsOf == SameMoodEarlier);
    }

    /// <summary>
    /// A name qualifying on several sessions is drawn once, so a set of five is five names.
    ///
    /// Five per set exists so a comparison does not inherit one name's idiosyncratic move. A tight
    /// set that took the same name from five adjacent sessions would inherit it while looking like
    /// five, and the pool holds every qualifying name once per session, so nothing but this stops it.
    /// </summary>
    [Fact]
    public void One_row_per_name_however_many_sessions_it_qualifies_on()
    {
        SeedRuling();

        Sampler().Draw(Tonight);

        IReadOnlyList<StoredControl> tight = Controls("tight");

        Assert.NotEmpty(tight);
        Assert.Contains(tight, c => string.Equals(c.Ticker, "REPEAT", StringComparison.Ordinal));
        Assert.Equal(tight.Count, tight.Select(c => c.Ticker).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The mood is recorded as a matched dimension on the tight set and as unmatched on the loose
    /// one, and the session distance the ruling paid for is recorded per row.
    ///
    /// <b>The distance is the price, so it is a value rather than an argument.</b> The decision
    /// accepts a comparison across time in exchange for a matched dimension. How far across is a
    /// measurement, and recording it per row is what lets a later session measure the cost instead
    /// of re-arguing the trade.
    /// </summary>
    [Fact]
    public void The_match_quality_records_the_mood_and_how_far_the_draw_reached()
    {
        SeedRuling();

        Sampler().Draw(Tonight);

        foreach (StoredControl control in Controls("tight"))
        {
            Assert.Equal("same", control.MatchQuality["marketMood"]);
            Assert.Equal(
                Math.Abs(control.AsOf.DayNumber - Tonight.DayNumber).ToString(CultureInfo.InvariantCulture),
                control.MatchQuality["sessionsApart"]);
        }

        foreach (StoredControl control in Controls("loose"))
        {
            Assert.Equal("not matched", control.MatchQuality["marketMood"]);
            Assert.Equal("0", control.MatchQuality["sessionsApart"]);
        }
    }

    /// <summary>
    /// An unlabelled night draws no tight controls rather than drawing from everywhere.
    ///
    /// No session can be said to share a mood that was never recorded, and matching on an unknown is
    /// the comparison true by construction this whole change exists to remove. The loose set is
    /// unaffected, because it never matched on the mood.
    /// </summary>
    [Fact]
    public void A_night_with_no_mood_label_draws_no_tight_controls()
    {
        SeedRuling();
        Execute("DELETE FROM regime_daily WHERE as_of = @d", ("@d", Session(Tonight)));

        Sampler().Draw(Tonight);

        Assert.Empty(Controls("tight"));
        Assert.NotEmpty(Controls("loose"));
    }

    // ---- the store the tests above run against -------------------------------------------------

    /// <summary>
    /// Three sessions and one flagged setup, arranged so the ruling is the only thing that can
    /// produce the result.
    ///
    /// Tonight is risk-on and carries one eligible unflagged name. The earlier session two days back
    /// is also risk-on and carries four. The session in between is risk-off and carries names that
    /// are <i>nearer</i> the subject than any risk-on name, so a draw that ignored the mood would
    /// take them. A later session is risk-on as well, to hold the point-in-time bound.
    ///
    /// Every name carries the same ladder grade as the subject, so the ladder dimension excludes
    /// nothing here and the mood is the only thing separating the pool.
    /// </summary>
    private void SeedRuling()
    {
        Mood(SameMoodEarlier, MarketMood.RiskOn);
        Mood(OtherMoodEarlier, MarketMood.RiskOff);
        Mood(Tonight, MarketMood.RiskOn);
        Mood(After, MarketMood.RiskOn);

        // The subject, and the one unflagged name its own night holds.
        Name("SUBJ", Tonight, turnover: 100_000_000m, range: 2.0m);
        Name("TONIGHT0", Tonight, turnover: 140_000_000m, range: 2.8m);

        // Four on the earlier same-mood session, all further from the subject than the risk-off
        // names below, so nothing here is drawn for being closest.
        for (int i = 0; i < 4; i++)
        {
            Name($"EARLY{i}", SameMoodEarlier, turnover: 150_000_000m + (i * 10_000_000m), range: 3.0m);
        }

        // Nearest of all, and on the wrong mood. A draw ignoring the mood takes these first.
        for (int i = 0; i < 6; i++)
        {
            Name($"WRONG{i}", OtherMoodEarlier, turnover: 101_000_000m + (i * 100_000m), range: 2.01m);
        }

        // After the as-of, and nearest of anything. Only the bound keeps these out.
        for (int i = 0; i < 6; i++)
        {
            Name($"LATER{i}", After, turnover: 100_000_000m, range: 2.0m);
        }

        // The same name on several qualifying sessions, so the one-row-per-name property has a
        // subject rather than holding vacuously.
        Name("REPEAT", SameMoodEarlier, turnover: 160_000_000m, range: 3.1m);
        Name("REPEAT", Tonight, turnover: 160_000_000m, range: 3.1m);

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
    private void Name(string ticker, DateOnly session, decimal turnover, decimal range)
    {
        Execute(
            "INSERT INTO security VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01', "
            + "NULL, NULL, NULL, NULL) ON CONFLICT (ticker) DO NOTHING",
            ("@t", ticker));

        Execute("""
            INSERT INTO indicator_daily
                (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20,
                 dollar_volume_median_20, range_avg_20, ladder_grade)
            VALUES (@t, @d, @obs, '1', '1', '1', '2.0', @adr, @dv, '2.0', 'rising')
            ON CONFLICT DO NOTHING
            """,
            ("@t", ticker), ("@d", Session(session)), ("@obs", Observed),
            ("@adr", range.ToString(CultureInfo.InvariantCulture)),
            ("@dv", turnover.ToString(CultureInfo.InvariantCulture)));
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
