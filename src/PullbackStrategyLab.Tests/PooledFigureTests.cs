using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Web.Shell;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two surfaces that added the two sides together, over a store where the sides differ.
///
/// <b>Why the populations here are lopsided.</b> Both defects were invisible over the golden fixture
/// and over every test that came before them, because the two sides carried the same figures or
/// nought: a pooled sum of two equal halves and a per-side figure look alike, and a pooled sum of two
/// noughts is nought. So each test below gives the long side a different count from the short side
/// and asserts the figure a pooled reading would have produced is nowhere on the surface.
///
/// <b>The pack's population section, from 6.4 to 7.13.</b> Three of its lines were one figure over
/// both books, under a doc comment in the same method saying the two sides are two lines and are
/// never added into one. The pack is the one surface written to be reasoned from rather than read,
/// which is what made it the worst place in the lab for the shape to sit.
///
/// <b>Band 3's twin outcome spread, from 6.3 to 7.13.</b> The reader returns one reading per side
/// carrying its own window, and the panel flattened the two, summed the windows, and said "on both
/// sides" in the population string. Both windows are nought over the golden fixture, so no wrong
/// number was ever shown and no expectation could have moved.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed class PooledFigureTests : IDisposable
{
    private static readonly DateOnly FirstSession = new(2026, 8, 20);
    private static readonly DateOnly SecondSession = new(2026, 8, 21);
    private static readonly DateOnly Today = new(2026, 9, 6);

    private const string First = "retrace_depth";
    private const string Second = "pullback_bars";

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 6, 22, 0, 0, TimeSpan.Zero));

    public PooledFigureTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Three long setups over two sessions and one short setup over one, and the section states six
    /// figures and no total.
    ///
    /// The pooled section would have read "setups: 4", "setups per session: 2.000000" and "passed
    /// every gate: 2", and all three strings are asserted absent: a rate is the line that hides the
    /// pooling best, because 4 over 2 sessions is a plausible-looking number that belongs to neither
    /// book.
    /// </summary>
    [Fact]
    public void The_packs_population_section_states_each_side_and_no_figure_over_both()
    {
        Flag("long", 0, FirstSession, passedAll: true);
        Flag("long", 1, FirstSession, passedAll: true);
        Flag("long", 2, SecondSession, passedAll: false);
        Flag("short", 3, FirstSession, passedAll: false);

        RenderedSection population = Section("Population");

        Assert.Contains("long setups: 3 over 2 session(s)", population.Lines);
        Assert.Contains("short setups: 1 over 1 session(s)", population.Lines);

        // Each rate over its own side's sessions. The long side ran on two and the short on one, so
        // a rate taken over the sessions either side was detected on would understate the short book
        // by half while looking exactly as authoritative.
        Assert.Contains("long setups per session: 1.500000", population.Lines);
        Assert.Contains("short setups per session: 1.000000", population.Lines);

        Assert.Contains("long passed every gate: 2", population.Lines);
        Assert.Contains("short passed every gate: 0", population.Lines);

        // The reach of the record is one fact about the record rather than a figure about setups, so
        // it stays, and it says outright which question it answers.
        Assert.Contains("sessions with a setup on either side: 2", population.Lines);

        Assert.DoesNotContain(population.Lines, l => l.StartsWith("setups:", StringComparison.Ordinal));
        Assert.DoesNotContain(population.Lines, l => l.Contains("4", StringComparison.Ordinal));
        Assert.DoesNotContain(population.Lines, l => l.StartsWith("setups per session:", StringComparison.Ordinal));
        Assert.DoesNotContain(population.Lines, l => l.StartsWith("passed every gate:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A side whose book the store has never held reads none rather than nought per session.
    ///
    /// Nought over nought is not a smaller rate, and this is the one arithmetic the pooled line could
    /// never produce: it divided by the sessions either side was seen on, so a side with no row at
    /// all was silently given the other side's denominator.
    /// </summary>
    [Fact]
    public void A_side_with_no_setup_has_no_rate_rather_than_a_rate_of_nought()
    {
        Flag("long", 0, FirstSession, passedAll: true);

        RenderedSection population = Section("Population");

        Assert.Contains("short setups: 0 over 0 session(s)", population.Lines);
        Assert.Contains("short setups per session: none", population.Lines);
        Assert.Contains("long setups per session: 1.000000", population.Lines);
    }

    /// <summary>
    /// Four long setups and two short ones, and band 3 carries two twin panels, each over its own
    /// side's window.
    ///
    /// <b>The two sides end on different branches of the same panel, which is what the pooled row
    /// could not hold.</b> The long side finds a pair and shows a mean; the short side finds none and
    /// is withheld against the two setups its window held. Pooled, the pair was averaged across both
    /// books and the short side's window was added to the long side's, so one row said a thing that
    /// was true of neither: a reader watching the window climb towards the 250 the metric wants would
    /// have been reading the two books' shortfalls added together.
    /// </summary>
    [Fact]
    public void Band_threes_twin_panel_is_one_per_side_and_each_carries_its_own_window()
    {
        Flag("long", 0, FirstSession, passedAll: true, outcome: 0.20, first: 1.00, second: 4.0);
        Flag("long", 1, FirstSession, passedAll: true, outcome: -0.10, first: 1.01, second: 4.0);
        Flag("long", 2, FirstSession, passedAll: true, outcome: 0.02, first: 3.00, second: 9.0);
        Flag("long", 3, FirstSession, passedAll: true, outcome: 0.04, first: 5.00, second: 14.0);
        Flag("short", 4, FirstSession, passedAll: true, outcome: 0.05, first: 1.00, second: 4.0);
        Flag("short", 5, FirstSession, passedAll: true, outcome: 0.06, first: 3.00, second: 9.0);

        new TwinPairFinder(_connections, Logger(), _clock, Options()).Run([Today.ToString("yyyy-MM-dd")]);
        new ScoreboardBuilder(_connections, Logger(), _clock, Options()).Build(Today);

        // The long side found its pair, so its panel carries a figure over the pairs it found; the
        // short side found none, so its panel is withheld and carries the window it looked in. Two
        // different branches of the same panel on one night, which is the state the pooled version
        // could not represent at all: it had one row to say both things in.
        Assert.Equal("twin pairs found on the long side", Population("long"));
        Assert.Equal(1, Window("long"));

        Assert.Equal("the setups the trailing window held on the short side", Population("short"));
        Assert.Equal(2, Window("short"));
        Assert.Equal("withheld", Figure("short"));

        // The panel is per side and nothing account-wide is written under that name, which is the
        // half a population string alone would not have shown: a pooled row with a better sentence
        // on it is still one row over two books.
        Assert.Equal(0, Rows(
            "SELECT COUNT(*) FROM scoreboard WHERE panel = 'band3.twinOutcomeSpread' AND direction IS NULL"));
    }

    /// <summary>
    /// A per-side band 3 panel renders under band 3 and not a second time in its own side's block.
    ///
    /// <b>The repair could have dropped both panels off the page and left every count right.</b> The
    /// band reads its panels by name and a panel carrying a side arrives in that side's list on the
    /// wire, so a band that went on reading the account-wide list alone would have shown neither
    /// twin panel at all: a correct answer discarded by a surface, which is the shape the corpus
    /// keeps finding. The other half is the one this asserts from the other direction, being that
    /// gathering them back must not render them twice.
    /// </summary>
    [Fact]
    public void A_panel_of_band_three_with_a_side_renders_under_its_band_and_not_in_the_side_block()
    {
        PanelView spread = new(
            "band3.twinOutcomeSpread", "long", "withheld", null, null, 4, null,
            "the setups the trailing window held on the long side", null,
            "no twin pair has been found");

        PanelView band1 = new(
            "band1.vsTight", "long", "withheld", null, null, 0, null, "every flagged setup", 262, null);

        ScoreboardView view = new("2026-09-06", null, [], [band1, spread], []);

        Assert.Equal(["band3.twinOutcomeSpread"], view.Band3.Select(p => p.Name));
        Assert.Equal(["band1.vsTight"], view.LongSide.Select(p => p.Name));

        // And the side is in the title, because two panels of one measure inside one band are
        // otherwise the same words twice.
        Assert.Equal("Mean twin outcome spread, long", view.Band3.Single().Title);
    }

    // ---- seeding ------------------------------------------------------------------------------

    private RenderedSection Section(string name)
    {
        PackResult pack = new ContextPacker(_connections, Logger(), _clock, Options()).Build(Today);
        return pack.Pack.Sections.Single(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }

    private int Window(string direction) => Rows(
        "SELECT n_rows FROM scoreboard WHERE panel = 'band3.twinOutcomeSpread' "
        + $"AND direction = '{direction}'");

    private string Figure(string direction) => Read("figure", direction);

    private string Population(string direction) => Read("population", direction);

    private string Read(string column, string direction)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {column} FROM scoreboard WHERE panel = 'band3.twinOutcomeSpread' "
            + $"AND direction = '{direction}'";
        return command.ExecuteScalar() as string ?? "absent";
    }

    private int Rows(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private RunLogger Logger() => new(_clock, Options());

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

    /// <summary>One authored setup, with a closed outcome and frozen signals where the test wants them.</summary>
    private void Flag(
        string direction,
        int index,
        DateOnly session,
        bool passedAll,
        double? outcome = null,
        double? first = null,
        double? second = null)
    {
        string setupId = $"{session:yyyy-MM-dd}-{direction}-{index:00}";
        string ticker = $"T{index:00}";

        Execute("""
            INSERT INTO security (ticker, name, exchange, type, first_seen)
            VALUES (@ticker, @ticker, 'US', 'Common Stock', '2020-01-02')
            ON CONFLICT (ticker) DO NOTHING
            """, ("@ticker", ticker));

        Execute("""
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@setup_id, @as_of, @ticker, @direction, '{}', @passed_all)
            """,
            ("@setup_id", setupId),
            ("@as_of", StoreText.DateToStorageText(session)),
            ("@ticker", ticker),
            ("@direction", direction),
            ("@passed_all", passedAll ? 1 : 0));

        if (outcome is double closed)
        {
            Execute("""
                INSERT INTO forward_return (subject_id, subject_kind, horizon_days, intended_date,
                                            actual_date, return_signed, mfe_atr, mae_atr, filled_at)
                VALUES (@setup_id, 'setup', @horizon, @date, @date, @return, '1.0', '1.0', @filled_at)
                """,
                ("@setup_id", setupId),
                ("@horizon", MeasurementParameters.ScoringHorizonSessions),
                ("@date", StoreText.DateToStorageText(session.AddDays(14))),
                ("@return", StoreText.StatisticToStorageText(closed)),
                ("@filled_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));
        }

        Freeze(setupId, First, first);
        Freeze(setupId, Second, second);
    }

    private void Freeze(string setupId, string name, double? value)
    {
        if (value is not double held)
        {
            return;
        }

        Execute("""
            INSERT INTO setup_signal (setup_id, signal_name, value, computed_at)
            VALUES (@setup_id, @signal_name, @value, @computed_at)
            """,
            ("@setup_id", setupId),
            ("@signal_name", name),
            ("@value", StoreText.StatisticToStorageText(held)),
            ("@computed_at", StoreText.TimestampToStorageText(_clock.UtcNow.AddDays(-1))));
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
