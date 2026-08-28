using System.Globalization;
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
/// The fill writes an outcome for both kinds of subject, which is the half that was missing.
///
/// <b>What went wrong is worth stating, because every instrument in the corpus was green while it
/// was true.</b> The stage bound `subject_kind` to the literal `setup` and its subject query read
/// only the `setup` table, while `ScoreboardBuilder.Series` joins outcomes on
/// `subject_kind = 'control'`. So the control-mean subquery matched nothing, band 1's difference
/// series was empty on every night for every direction and every set, the panel was withheld with an
/// effective count pinned at nought, and 3.6 fires on that count. The decision point the phase
/// exists to reach could never arrive.
///
/// <b>Nothing could have caught it.</b> The golden fixture holds one night, so no horizon closes and
/// `forward.written` is legitimately nought. The interval cases hand authored nightly means straight
/// to `PairedInterval`, so they never touch the query that was empty. And the one sentence in the
/// corpus asserting control returns are recorded sat in prose rather than in a table, so
/// `architecture-conformance` never enumerated it as a claim. Three guards, and the subject fell
/// between all three.
///
/// These are the property, not the checkpoint's verification, which is the fixture diff. Each one
/// fails if the control path is removed.
/// see: Matched control populations are drawn nightly, loose and tight
/// </summary>
public sealed class ForwardReturnFillerTests : IDisposable
{
    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    /// <summary>Late enough that every horizon below has closed against the bars seeded here.</summary>
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 3, 27, 22, 0, 0, TimeSpan.Zero));

    private static readonly DateOnly Flagged = new(2026, 3, 2);

    private static readonly DateOnly FillOn = new(2026, 3, 27);

    public ForwardReturnFillerTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    // ---- the property ------------------------------------------------------------------------

    /// <summary>
    /// A control draw produces `forward_return` rows of kind `control`.
    ///
    /// <b>The assertion the whole of phase 3 was missing.</b> It fails outright if the control
    /// subject query is removed, if the insert goes back to binding a literal, or if the control
    /// rows are written under the setup's kind.
    /// </summary>
    [Fact]
    public void A_control_draw_produces_forward_returns_of_kind_control()
    {
        Seed("HOOD", "long", ["COIN", "SOFI"]);

        FillResult filled = Stage().Fill(FillOn);

        Assert.Equal(2, filled.ControlSubjects);
        Assert.Equal(2 * ForwardOutcome.Horizons.Count, filled.ControlsWritten);
        Assert.Equal(1, filled.Subjects);
        Assert.Equal(ForwardOutcome.Horizons.Count, filled.Written);

        Assert.Equal(
            2 * ForwardOutcome.Horizons.Count,
            Count("SELECT COUNT(*) FROM forward_return WHERE subject_kind = 'control'"));

        // Named by the control's own surrogate, which is the column `forward_return` was given a
        // single subject key for.
        Assert.Equal(
            ForwardOutcome.Horizons.Count,
            Count(
                "SELECT COUNT(*) FROM forward_return f JOIN control_setup c "
                + "ON c.control_id = f.subject_id WHERE f.subject_kind = 'control' "
                + "AND c.control_ticker = 'COIN'"));
    }

    /// <summary>
    /// A control's outcome is signed by the setup's direction, never by one of its own.
    ///
    /// <b>The silent half of the same defect.</b> Band 1 subtracts the control mean from the setup's
    /// return, so a control signed the market's way against a setup signed the direction's way makes
    /// that subtraction a sum on the short side. Every number would be right and the comparison
    /// would be of two unlike quantities, which nothing downstream could see.
    ///
    /// The control here rises over the horizon while the setup is short, so an unsigned reading is
    /// positive and the signed one is negative. The two cannot be confused.
    /// </summary>
    [Fact]
    public void A_control_is_signed_by_the_setups_direction_rather_than_its_own()
    {
        Seed("INTC", "short", ["AMD"]);

        Stage().Fill(FillOn);

        decimal control = Ratio(
            "SELECT f.return_signed FROM forward_return f JOIN control_setup c "
            + "ON c.control_id = f.subject_id WHERE f.subject_kind = 'control' AND f.horizon_days = 10");

        // Every seeded name rises, so a control read the market's way would be positive here.
        Assert.True(
            control < 0m,
            $"the control's ten-session return came back {control}, which is the market's sign rather "
            + "than the short setup's. The paired difference would then be a sum.");
    }

    /// <summary>
    /// The excursions are stated in the control's own range, not the setup's.
    ///
    /// A control matched on liquidity and daily range is still a different stock, and expressing its
    /// path in the setup's ATR would state one name's move in another name's volatility. The two
    /// ATRs here differ by a factor of ten, so borrowing the wrong one is not a rounding.
    /// </summary>
    [Fact]
    public void A_control_excursion_is_stated_in_its_own_range()
    {
        Seed("HOOD", "long", ["COIN"], setupAtr: 20m, controlAtr: 2m);

        Stage().Fill(FillOn);

        decimal control = Ratio(
            "SELECT f.mfe_atr FROM forward_return f JOIN control_setup c "
            + "ON c.control_id = f.subject_id WHERE f.subject_kind = 'control' AND f.horizon_days = 10");
        decimal setup = Ratio(
            "SELECT mfe_atr FROM forward_return WHERE subject_kind = 'setup' AND horizon_days = 10");

        // Same seeded path, ten times the range on the setup, so the setup's excursion in ATR is a
        // tenth of the control's. Reading the setup's ATR for the control would make them equal.
        Assert.True(
            control > setup * 5m,
            $"the control's excursion came back at {control} against the setup's {setup}. They are "
            + "too close to have been stated in different ranges, so the control took the setup's.");
    }

    /// <summary>
    /// A second pass writes nothing, for controls as much as for setups.
    ///
    /// The immutability the store's own key carries, held for the kind that was added later. A fill
    /// that revised a control outcome would move a band 1 figure under a reader with nothing saying
    /// so.
    /// </summary>
    [Fact]
    public void A_second_fill_writes_nothing_for_either_kind()
    {
        Seed("HOOD", "long", ["COIN", "SOFI"]);

        FillResult first = Stage().Fill(FillOn);
        FillResult second = Stage().Fill(FillOn);

        Assert.True(first.ControlsWritten > 0, "the first fill wrote no control outcome, so the second proves nothing");
        Assert.Equal(0, second.Written);
        Assert.Equal(0, second.ControlsWritten);
        Assert.Equal(0, second.RowsWritten);
    }

    /// <summary>
    /// A horizon that has not closed is not written, for a control as for a setup.
    ///
    /// The fill runs the day after the flag, so only the one-session horizon has anything to say.
    /// Held because a control filled early would carry a partial window as though it were the
    /// horizon, and a ten-session return measured over one session is a different quantity pooled
    /// with the right ones.
    /// </summary>
    [Fact]
    public void An_unclosed_horizon_is_left_for_a_later_night_for_both_kinds()
    {
        Seed("HOOD", "long", ["COIN"]);

        FillResult filled = Stage().Fill(Flagged.AddDays(1));

        Assert.Equal(1, filled.Written);
        Assert.Equal(1, filled.ControlsWritten);
        Assert.Equal(ForwardOutcome.Horizons.Count - 1, filled.NotYetElapsed);
        Assert.Equal(ForwardOutcome.Horizons.Count - 1, filled.ControlHorizonsNotYetElapsed);
    }

    // ---- the store ---------------------------------------------------------------------------

    /// <summary>
    /// One flagged setup, its controls, and a rising bar series for every name involved.
    ///
    /// Every name rises, which is what lets the sign test above tell a short setup's control from an
    /// unsigned one. The bars run well past the ten-session horizon so a fill on
    /// <see cref="FillOn"/> closes every horizon.
    /// </summary>
    private void Seed(
        string ticker,
        string direction,
        IReadOnlyList<string> controls,
        decimal setupAtr = 2m,
        decimal controlAtr = 2m)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        const string Observed = "2026-01-01T00:00:00.000Z";
        string setupId = $"{Flagged:yyyy-MM-dd}-{ticker}-{direction}";

        foreach (string name in controls.Prepend(ticker))
        {
            Execute(connection,
                "INSERT INTO security VALUES (@t, @t, 'NASDAQ', 'Common Stock', '2020-01-01', NULL, NULL, NULL, NULL)",
                ("@t", name));

            decimal price = 100m;
            DateOnly day = Flagged;

            for (int i = 0; i < 20; i++)
            {
                while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    day = day.AddDays(1);
                }

                decimal close = price + i;

                Execute(connection,
                    """
                    INSERT INTO daily_bar VALUES (@t, @d, @o, @h, @l, @c, @c, 1000000, @obs)
                    """,
                    ("@t", name),
                    ("@d", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    ("@o", Text(close)),
                    ("@h", Text(close + 1m)),
                    ("@l", Text(close - 0.5m)),
                    ("@c", Text(close)),
                    ("@obs", Observed));

                day = day.AddDays(1);
            }

            Execute(connection,
                """
                INSERT INTO indicator_daily
                VALUES (@t, @d, @obs, '1', '1', '1', @atr, '2.0', '50000000', '2.0', 'rising')
                """,
                ("@t", name),
                ("@d", Flagged.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("@obs", Observed),
                ("@atr", Text(string.Equals(name, ticker, StringComparison.Ordinal) ? setupAtr : controlAtr)));
        }

        Execute(connection,
            """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @d, @t, @dir, '[]', 1, '100.0', '97.0', '0.5')
            """,
            ("@id", setupId),
            ("@d", Flagged.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("@t", ticker),
            ("@dir", direction));

        int rank = 1;

        foreach (string name in controls)
        {
            Execute(connection,
                """
                INSERT INTO control_setup
                VALUES (@cid, @sid, @ct, 'loose', '{}', @rank, @obs)
                """,
                ("@cid", $"{setupId}-loose-{name}"),
                ("@sid", setupId),
                ("@ct", name),
                ("@rank", rank++),
                ("@obs", Observed));
        }
    }

    private ForwardReturnFiller Stage()
    {
        IOptions<PullbackStrategyLabOptions> options =
            Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });

        return new ForwardReturnFiller(_connections, new RunLogger(_clock, options), _clock, options);
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Execute(
        SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private int Count(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private decimal Ratio(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = command.ExecuteScalar();

        Assert.NotNull(value);

        return StoreText.StorageTextToRatio((string)value);
    }
}
