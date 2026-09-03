using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A scoreboard rebuild that writes nothing does not report success.
///
/// <b>The shape this closes.</b> The panel insert is <c>ON CONFLICT DO NOTHING</c> and there is no
/// update path, so building a date that already carries panels writes none of them. Until 3.9(e)
/// that run reported clean with the same panel count as a real build, and the only difference a
/// reader could have seen was `rows_written`, which is uninformative on several stages already. An
/// operator rebuilding a past date would have read a clean run and gone to look at panels that had
/// not moved.
///
/// It was found while writing a point-in-time test rather than by inspection, which is why the
/// record says so: the earlier reading was that eight unbounded scoreboard reads were "latent until
/// a rebuild", and the rebuild they were latent behind could not happen.
///
/// <b>Failing rather than refusing up front.</b> A refusal would have to know whether the date
/// already has panels before doing the work, which is a second query that can disagree with the
/// insert. Counting what the insert actually skipped cannot disagree with it, and it keeps a first
/// build for a date working while making a genuine rebuild loud.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public sealed class ScoreboardRebuildTests : IDisposable
{
    private static readonly DateOnly AsOf = new(2026, 8, 27);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 27, 22, 50, 0, TimeSpan.Zero));

    public ScoreboardRebuildTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// The first build writes its panels and the second writes none of them, and only the first
    /// reports clean.
    ///
    /// Both builds in one test, because a run that failed on an empty store would satisfy the second
    /// assertion without the property holding at all.
    /// </summary>
    [Fact]
    public void A_second_build_for_the_same_date_writes_nothing_and_does_not_report_clean()
    {
        ScoreboardResult first = Builder().Build(AsOf);

        Assert.True(first.Attempted > 0, "the build produced no panels at all, so nothing here is being tested.");
        Assert.Equal(0, first.Skipped);
        Assert.Equal("clean", first.Outcome.ToStorageText());

        ScoreboardResult second = Builder().Build(AsOf);

        // Every panel skipped, which is what an in-place rebuild does, and the run says so rather
        // than reporting the same panel count as the build that wrote them.
        Assert.Equal(first.Attempted, second.Attempted);
        Assert.Equal(second.Attempted, second.Skipped);
        Assert.Equal("failed", second.Outcome.ToStorageText());
    }

    /// <summary>
    /// The command exits non-zero and names the skipped count, which is what an operator sees.
    ///
    /// The stage's return value is what the slot script keys on, and the message is what the night's
    /// log carries, so both are asserted rather than only the result object.
    /// </summary>
    [Fact]
    public void The_command_exits_non_zero_on_a_rebuild_that_wrote_nothing()
    {
        Assert.Equal(0, Builder().Run([AsOf.ToString("yyyy-MM-dd")]));

        StringWriter errors = new();
        TextWriter previous = Console.Error;
        Console.SetError(errors);

        try
        {
            Assert.Equal(1, Builder().Run([AsOf.ToString("yyyy-MM-dd")]));
        }
        finally
        {
            Console.SetError(previous);
        }

        string said = errors.ToString();

        Assert.Contains("panel(s) were skipped", said, StringComparison.Ordinal);
        Assert.Contains("nothing was rebuilt", said, StringComparison.Ordinal);

        // And it names the supported route, because a failure that does not say what to do next
        // sends the operator to the source.
        Assert.Contains(ScoreboardBuilder.RebuildFlag, said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The supported route: a rebuild writes a new generation of the date's panels beside the one it
    /// carries, and a reader takes the latest generation it may see.
    ///
    /// <b>The shape 2026-08-28 left behind, authored.</b> A night's scoreboard ran before an input
    /// existed, so band 0 says nought setups on file; the input then arrives and the night is
    /// rebuilt. The first generation stays, unchanged, and a read bounded on the night's own end of
    /// day still returns it, because that is what the night showed. A read bounded after the
    /// rebuild returns the second. Neither generation is deleted and no declared writer gains a
    /// delete.
    /// see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
    /// </summary>
    [Fact]
    public void A_rebuild_writes_a_new_generation_and_a_reader_takes_the_latest_it_may_see()
    {
        ScoreboardResult first = Builder().Build(AsOf);
        Assert.Equal("failed", Builder().Build(AsOf).Outcome.ToStorageText());

        // The input that arrived after the night's scoreboard ran.
        SetupOnFile("AAPL");

        // The rebuild, the next morning, after the night's own end of day.
        DateTimeOffset nextMorning = new(2026, 8, 28, 10, 0, 0, TimeSpan.Zero);
        ScoreboardResult rebuilt = Builder(nextMorning).Build(AsOf, rebuild: true);

        Assert.True(rebuilt.Rebuilt);
        Assert.Equal(first.Attempted, rebuilt.Attempted);
        Assert.Equal(0, rebuilt.Skipped);
        Assert.Equal(first.Attempted, rebuilt.Superseded);
        Assert.Equal("clean", rebuilt.Outcome.ToStorageText());
        Assert.Equal(nextMorning, rebuilt.ComputedAt);

        // Two generations, both on file.
        Assert.Equal(first.Attempted * 2, Panels());

        // Read as of the night itself, the night's own generation, as it stood.
        Assert.Equal("0", SetupsOnFile(asOf: AsOf));

        // Read as of the day the rebuild ran, the rebuilt generation, and exactly one row per panel.
        Assert.Equal("1", SetupsOnFile(asOf: AsOf.AddDays(1)));
        ScoreboardResponse afterwards = LabScoreboard.Read(_connections, AsOf.AddDays(1), SessionBoundaries.UsEquities);
        Assert.Equal(first.Attempted, afterwards.Health.Count + afterwards.Long.Count + afterwards.Short.Count);
    }

    /// <summary>
    /// An ordinary build after a rebuild still writes nothing and fails: any generation on file
    /// makes the date one that carries panels, so an accidental rerun cannot open a third.
    /// </summary>
    [Fact]
    public void An_ordinary_build_after_a_rebuild_still_writes_nothing()
    {
        ScoreboardResult first = Builder().Build(AsOf);
        Builder(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero)).Build(AsOf, rebuild: true);

        ScoreboardResult again = Builder(new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero)).Build(AsOf);

        Assert.Equal(first.Attempted, again.Skipped);
        Assert.Equal("failed", again.Outcome.ToStorageText());
        Assert.Equal(first.Attempted * 2, Panels());
    }

    /// <summary>
    /// A build for a date the store has never seen is clean even though another date has panels, so
    /// the failure is about this date rather than about the table being non-empty.
    /// </summary>
    [Fact]
    public void A_build_for_a_new_date_is_clean_while_another_date_already_has_panels()
    {
        Builder().Build(AsOf);

        ScoreboardResult next = Builder().Build(AsOf.AddDays(1));

        Assert.Equal(0, next.Skipped);
        Assert.Equal("clean", next.Outcome.ToStorageText());
    }

    /// <summary>
    /// An account-wide panel is unique per date, which the primary key alone does not make true.
    ///
    /// <b>This is the defect the no-op was hiding half of.</b> `scoreboard` declares
    /// <c>PRIMARY KEY (as_of, panel, direction)</c> and every band 0 panel has a null direction,
    /// because it is account-wide. SQLite treats nulls as distinct in a unique index, so that key
    /// never constrained those rows: a second build inserted a second copy of all five, a third
    /// build a third, and the page would have been handed one row per copy. The six panels carrying
    /// a direction were skipped correctly throughout, which is exactly why the whole thing read as a
    /// silent no-op rather than as a duplication.
    ///
    /// Stated as counts rather than as "no duplicates", because a query returning nothing is
    /// self-validating and this one has a number it should return.
    /// </summary>
    [Fact]
    public void An_account_wide_panel_is_written_once_however_many_times_the_date_is_built()
    {
        ScoreboardResult first = Builder().Build(AsOf);

        int accountWide = AccountWide();
        Assert.True(accountWide >= 3,
            $"only {accountWide} account-wide panel(s) were written, so the null-direction case this asserts "
            + "is barely present and a passing run would mean little.");

        Builder().Build(AsOf);
        Builder().Build(AsOf);

        Assert.Equal(accountWide, AccountWide());
        Assert.Equal(first.Attempted, Panels());

        // And every row is distinct on the key the schema claims, counting a null direction as one
        // value rather than as many, within a generation: a rebuild adds a generation and never a
        // second copy inside one.
        Builder(new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero)).Build(AsOf, rebuild: true);
        Assert.Equal(accountWide * 2, AccountWide());

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM (SELECT as_of, panel, COALESCE(direction, ''), computed_at FROM scoreboard "
            + "GROUP BY 1, 2, 3, 4 HAVING COUNT(*) > 1)";

        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>One setup on file for the date, which moves band 0's count from nought to one.</summary>
    private void SetupOnFile(string ticker)
    {
        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText =
                "INSERT INTO security (ticker, name, exchange, type, first_seen) "
                + "VALUES (@t, @t, 'NASDAQ', 'Common Stock', @d) ON CONFLICT (ticker) DO NOTHING;";
            security.Parameters.AddWithValue("@t", ticker);
            security.Parameters.AddWithValue("@d", StoreText.DateToStorageText(AsOf.AddDays(-40)));
            security.ExecuteNonQuery();
        }

        using SqliteCommand setup = connection.CreateCommand();
        setup.CommandText = """
            INSERT INTO setup
                (setup_id, as_of, ticker, direction, check_results, passed_all, capped_out,
                 trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @as_of, @ticker, 'long', '[]', 1, 0, '100', '95', '0.30')
            """;
        setup.Parameters.AddWithValue("@id", $"{AsOf:yyyy-MM-dd}-{ticker}-long");
        setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        setup.Parameters.AddWithValue("@ticker", ticker);
        setup.ExecuteNonQuery();
    }

    /// <summary>Band 0's setups-on-file figure, read through the page's own reader as of a date.</summary>
    private string SetupsOnFile(DateOnly asOf) =>
        LabScoreboard.Read(_connections, asOf, SessionBoundaries.UsEquities).Health
            .Single(p => string.Equals(p.Name, "band0.setupsOnFile", StringComparison.Ordinal))
            .Figure;

    /// <summary>How many panels of the date carry no direction, which is band 0.</summary>
    private int AccountWide()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scoreboard WHERE as_of = @as_of AND direction IS NULL";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private int Panels()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scoreboard WHERE as_of = @as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private ScoreboardBuilder Builder() => Builder(_clock.UtcNow);

    /// <summary>A builder whose clock reads <paramref name="at"/>, which is the instant a generation is keyed on.</summary>
    private ScoreboardBuilder Builder(DateTimeOffset at)
    {
        var options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });
        var clock = new FixedClock(at);
        return new ScoreboardBuilder(_connections, new RunLogger(clock, options), clock, options);
    }
}
