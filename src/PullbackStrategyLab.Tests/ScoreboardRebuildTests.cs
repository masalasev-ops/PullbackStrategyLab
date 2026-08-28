using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
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
        Assert.Contains("restore the snapshot", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The supported route still works: with that date's panels gone, a rebuild writes them.
    ///
    /// This is what restoring the snapshot taken before the night and re-running reduces to, from
    /// the stage's point of view. Without this half the change would be indistinguishable from the
    /// stage having lost the ability to build a date twice for any reason.
    /// </summary>
    [Fact]
    public void A_rebuild_after_the_date_is_cleared_writes_its_panels_again()
    {
        ScoreboardResult first = Builder().Build(AsOf);
        Assert.Equal("failed", Builder().Build(AsOf).Outcome.ToStorageText());

        using (SqliteConnection connection = _connections.OpenWrite())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM scoreboard WHERE as_of = @as_of";
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            command.ExecuteNonQuery();
        }

        ScoreboardResult again = Builder().Build(AsOf);

        Assert.Equal(first.Attempted, again.Attempted);
        Assert.Equal(0, again.Skipped);
        Assert.Equal("clean", again.Outcome.ToStorageText());
        Assert.Equal(first.Attempted, Panels());
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
        // value rather than as many.
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM (SELECT as_of, panel, COALESCE(direction, '') FROM scoreboard "
            + "GROUP BY 1, 2, 3 HAVING COUNT(*) > 1)";

        Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

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

    private ScoreboardBuilder Builder()
    {
        var options = Options.Create(new PullbackStrategyLabOptions { DataRoot = _root.Path });
        return new ScoreboardBuilder(_connections, new RunLogger(_clock, options), _clock, options);
    }
}
