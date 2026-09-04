using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The eight holdout windows, what a register holding nothing reports, and the store refusing a
/// re-spend.
///
/// <b>Every case here is authored, and the reason is a fact about the calendar rather than a
/// convenience.</b> The register is created empty and a window becomes available the day its quarter
/// completes; the lab's first night was 2026-08-27, so the first window is the fourth quarter of
/// 2026 and it matures on 2027-01-01. **No window can exist at this checkpoint and none is asked
/// for.** The population below is a store seeded with a first session and a clock stood at a chosen
/// date, so a window exists only where a test puts the date past its maturity
/// (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it)
/// (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight).
/// </summary>
public sealed class HoldoutRegistryTests : IDisposable
{
    private const string Zone = "America/New_York";

    /// <summary>The lab's first recorded night, which is what the whole schedule is computed from.</summary>
    private static readonly DateOnly FirstSession = new(2026, 8, 27);

    /// <summary>The first window's maturity, derived by hand: 2026-Q4 completes on 2026-12-31.</summary>
    private static readonly DateOnly FirstMaturity = new(2027, 1, 1);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;

    public HoldoutRegistryTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private IOptions<PullbackStrategyLabOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(
            new PullbackStrategyLabOptions { DataRoot = _root.Path });

    private HoldoutRegistry Registry(DateOnly standingOn)
    {
        var clock = new FixedClock(
            SessionBoundaries.At(standingOn, new TimeOnly(21, 45), SessionBoundaries.UsEquities));

        return new HoldoutRegistry(_connections, new RunLogger(clock, Options()), clock, Options());
    }

    // ---- the schedule ------------------------------------------------------------------------

    /// <summary>
    /// Eight windows, one calendar quarter each, oldest first, and the first is the first quarter
    /// the lab could have collected in full.
    ///
    /// <b>Derived by hand and asserted as dates rather than as a count.</b> The lab's first night is
    /// inside the third quarter of 2026, which therefore holds sessions nobody recorded, so the first
    /// window is 2026-Q4 and the eighth is 2028-Q3. A count of eight would pass over a schedule that
    /// started in the wrong quarter.
    /// </summary>
    [Fact]
    public void The_eight_windows_are_the_eight_quarters_after_the_first_session()
    {
        IReadOnlyList<HoldoutWindow> schedule = HoldoutWindows.Schedule(FirstSession);

        Assert.Equal(HoldoutWindows.Capacity, schedule.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], schedule.Select(w => w.Ordinal));

        Assert.Equal("2026-Q4", schedule[0].WindowId);
        Assert.Equal(new DateOnly(2026, 10, 1), schedule[0].Start);
        Assert.Equal(new DateOnly(2026, 12, 31), schedule[0].End);
        Assert.Equal(FirstMaturity, schedule[0].MaturesOn);

        Assert.Equal("2028-Q3", schedule[7].WindowId);
        Assert.Equal(new DateOnly(2028, 7, 1), schedule[7].Start);
        Assert.Equal(new DateOnly(2028, 9, 30), schedule[7].End);
        Assert.Equal(new DateOnly(2028, 10, 1), schedule[7].MaturesOn);

        // Non-overlapping and contiguous, which is what makes eight of them two years of evidence
        // rather than eight arbitrary spans.
        foreach ((HoldoutWindow before, HoldoutWindow after) in schedule.Zip(schedule.Skip(1)))
        {
            Assert.Equal(before.End.AddDays(1), after.Start);
        }
    }

    /// <summary>
    /// A first session landing exactly on a quarter boundary makes that quarter the first window,
    /// and any other date pushes to the next.
    ///
    /// <b>The boundary is the only date on which the two readings agree</b>, so it is the one case
    /// worth asserting either side of: a rule written as "the quarter after the first session" would
    /// throw away a quarter the lab did collect in full.
    /// </summary>
    [Fact]
    public void A_first_session_on_a_quarter_boundary_makes_that_quarter_the_first_window()
    {
        Assert.Equal(
            new DateOnly(2026, 10, 1),
            HoldoutWindows.FirstQuarterFullyForwardOf(new DateOnly(2026, 10, 1)));

        Assert.Equal(
            new DateOnly(2027, 1, 1),
            HoldoutWindows.FirstQuarterFullyForwardOf(new DateOnly(2026, 10, 2)));

        Assert.Equal(
            new DateOnly(2026, 10, 1),
            HoldoutWindows.FirstQuarterFullyForwardOf(new DateOnly(2026, 9, 30)));
    }

    /// <summary>A window is not available on the last day of its own quarter.</summary>
    [Fact]
    public void A_window_matures_the_day_after_its_quarter_and_not_on_its_last_day()
    {
        Assert.Empty(HoldoutWindows.MaturedBy(FirstSession, new DateOnly(2026, 12, 31)));
        Assert.Single(HoldoutWindows.MaturedBy(FirstSession, FirstMaturity));
    }

    // ---- what a register holding nothing reports ----------------------------------------------

    /// <summary>
    /// A store with no session at all reports that no quarter has begun, which is a different fact
    /// from a quarter having begun and not completed.
    /// </summary>
    [Fact]
    public void A_register_with_no_session_says_no_quarter_has_begun()
    {
        HoldoutRegisterState state = Registry(FirstMaturity).Mature(FirstMaturity);

        Assert.Null(state.FirstSession);
        Assert.Equal(0, state.Matured);
        Assert.Equal(0, state.Recorded);
        Assert.Equal(0, state.Available);
        Assert.Equal(HoldoutRegistry.NoSessionRecorded, state.EmptyBecause);
        Assert.False(state.IsExhausted);
        Assert.Equal(RunOutcome.Clean, state.Outcome);
    }

    /// <summary>
    /// The state this checkpoint actually ships in: sessions recorded, no quarter completed, and the
    /// register empty and correct.
    ///
    /// <b>This is the one that will read identically to a defect for three months</b>, which is why
    /// the reason is stored rather than inferred from the count. The run is clean, nothing is
    /// missing, and the register says why it holds nothing.
    /// </summary>
    [Fact]
    public void A_register_before_the_first_quarter_completes_is_empty_and_correct()
    {
        SeedSession(FirstSession);

        HoldoutRegisterState state = Registry(FirstSession).Mature(FirstSession);

        Assert.Equal(FirstSession, state.FirstSession);
        Assert.Equal(0, state.Matured);
        Assert.Equal(0, state.Recorded);
        Assert.Equal(0, state.Available);
        Assert.Equal(HoldoutRegistry.NoQuarterMaturedYet, state.EmptyBecause);
        Assert.Empty(state.Missing);
        Assert.False(state.IsExhausted);
        Assert.Equal(RunOutcome.Clean, state.Outcome);
    }

    /// <summary>
    /// A register that should hold a window and does not is partial and names it, which is what
    /// tells the empty-and-correct state above from a failure to record one.
    ///
    /// <b>This is the assertion the corpus's own rule asks for.</b> Both states hold nothing; only
    /// one of them is a fault, and for the first months of this lab's life they are the same
    /// screenful of noughts. The registry computes what should exist from the store's own earliest
    /// session and compares, so the difference is measured rather than remembered.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    [Fact]
    public void A_register_nothing_recorded_into_is_partial_and_names_what_is_missing()
    {
        SeedSession(FirstSession);

        // Read on a date past the first maturity with nothing having recorded a window, which is
        // what a registry that never ran leaves behind. The read is what sees it: the run cures the
        // defect in the act of looking for it.
        HoldoutRegisterState state = Registry(FirstMaturity).Read(FirstMaturity);

        Assert.Equal(1, state.Matured);
        Assert.Equal(0, state.Recorded);
        Assert.Equal(0, state.Available);
        Assert.Equal(["2026-Q4"], state.Missing);
        Assert.Equal(RunOutcome.Partial, state.Outcome);

        // The whole point, and it is the assertion the corpus's rule asks for: the two empty states
        // are distinguishable. Same nought available, different reason and different outcome.
        Assert.Equal(HoldoutRegistry.NotRecorded, state.EmptyBecause);
        Assert.NotEqual(HoldoutRegistry.NoQuarterMaturedYet, state.EmptyBecause);

        HoldoutRegisterState ordinary = Registry(FirstSession).Read(FirstSession);

        Assert.Equal(0, ordinary.Available);
        Assert.Equal(HoldoutRegistry.NoQuarterMaturedYet, ordinary.EmptyBecause);
        Assert.Equal(RunOutcome.Clean, ordinary.Outcome);
        Assert.Empty(ordinary.Missing);
    }

    /// <summary>A run records every window that has matured, and a rerun records none.</summary>
    [Fact]
    public void Maturing_records_the_windows_that_have_completed_and_a_rerun_writes_nothing()
    {
        SeedSession(FirstSession);

        HoldoutRegisterState first = Registry(FirstMaturity).Mature(FirstMaturity);

        Assert.Equal(1, first.Matured);
        Assert.Equal(1, first.Recorded);
        Assert.Equal(1, first.Written);
        Assert.Equal(1, first.Available);
        Assert.Null(first.EmptyBecause);
        Assert.Equal("2026-Q4", first.Register[0].Window.WindowId);

        HoldoutRegisterState again = Registry(FirstMaturity).Mature(FirstMaturity);

        Assert.Equal(1, again.Recorded);
        Assert.Equal(0, again.Written);
        Assert.Equal(RunOutcome.Clean, again.Outcome);
    }

    /// <summary>
    /// The register never holds more than eight, however far past the last quarter the clock stands.
    /// </summary>
    [Fact]
    public void The_register_stops_at_eight_however_late_it_is_read()
    {
        SeedSession(FirstSession);

        HoldoutRegisterState state = Registry(new DateOnly(2031, 1, 1)).Mature(new DateOnly(2031, 1, 1));

        Assert.Equal(HoldoutWindows.Capacity, state.Matured);
        Assert.Equal(HoldoutWindows.Capacity, state.Recorded);
        Assert.Equal("2028-Q3", state.Register[^1].Window.WindowId);
    }

    // ---- spending, and the refusal the store makes ---------------------------------------------

    /// <summary>A spend records the decision and the outcome, and the window stops being available.</summary>
    [Fact]
    public void A_spend_records_what_it_was_spent_on_and_what_came_of_it()
    {
        SeedSession(FirstSession);
        Registry(FirstMaturity).Mature(FirstMaturity);

        HoldoutSpendResult result = Registry(FirstMaturity)
            .SpendOldest("pack v1 against v2", "v2 admitted", FirstMaturity);

        Assert.True(result.Spent);
        Assert.Equal("2026-Q4", result.WindowId);

        HoldoutRegisterState after = Registry(FirstMaturity).Mature(FirstMaturity);

        Assert.Equal(1, after.Spent);
        Assert.Equal(0, after.Available);
        Assert.Equal("pack v1 against v2", after.Register[0].Spend!.SpentOn);
        Assert.Equal("v2 admitted", after.Register[0].Spend!.Outcome);
    }

    /// <summary>
    /// <b>The re-spend is refused by the store's own key, not by the stage.</b>
    ///
    /// The registry's own path answers with a sentence, which is what a caller wants. This test goes
    /// underneath it and writes the second spend straight to the store, so what refuses is SQLite's
    /// primary key on `holdout_spend` and nothing else. That is the whole reason the spend is a row
    /// rather than a nullable column: strip every check in the stage and the rule still holds, where
    /// a rule living in an `UPDATE` statement's `WHERE` clause is a rule the next statement can be
    /// written without.
    /// see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight
    /// </summary>
    [Fact]
    public void A_second_spend_of_one_window_is_refused_by_the_store_itself()
    {
        SeedSession(FirstSession);
        Registry(FirstMaturity).Mature(FirstMaturity);
        Registry(FirstMaturity).SpendOldest("the first decision", "admitted", FirstMaturity);

        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand second = connection.CreateCommand();
        second.CommandText = """
            INSERT INTO holdout_spend (window_id, spent_on, outcome, spent_at)
            VALUES ('2026-Q4', 'a second decision', 'admitted', '2027-02-01T21:45:00.000Z')
            """;

        SqliteException refused = Assert.Throws<SqliteException>(() => second.ExecuteNonQuery());
        Assert.Contains("UNIQUE constraint failed", refused.Message, StringComparison.Ordinal);
        Assert.Contains("holdout_spend.window_id", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>And the stage's own path refuses the same thing with a reason rather than a throw.</summary>
    [Fact]
    public void The_stage_refuses_a_re_spend_with_the_reason()
    {
        SeedSession(FirstSession);
        Registry(FirstMaturity).Mature(FirstMaturity);
        Registry(FirstMaturity).SpendOldest("the first decision", "admitted", FirstMaturity);

        HoldoutSpendResult again = Registry(FirstMaturity)
            .Spend("2026-Q4", "a second decision", "admitted", FirstMaturity);

        Assert.False(again.Spent);
        Assert.Equal(HoldoutRegistry.AlreadySpent, again.RefusedBecause);
    }

    /// <summary>Windows are spent oldest first, so a decision cannot choose the quarter that suits it.</summary>
    [Fact]
    public void Windows_are_spent_oldest_first()
    {
        SeedSession(FirstSession);
        DateOnly late = new(2027, 7, 1);
        Registry(late).Mature(late);

        Assert.Equal("2026-Q4", Registry(late).SpendOldest("first", "done", late).WindowId);
        Assert.Equal("2027-Q1", Registry(late).SpendOldest("second", "done", late).WindowId);
        Assert.Equal("2027-Q2", Registry(late).SpendOldest("third", "done", late).WindowId);
    }

    /// <summary>
    /// Exhaustion is a designed dead end and reads as one: nought available, a reason that names it,
    /// and a spend that refuses rather than throwing.
    ///
    /// <b>Told apart from having nothing yet by the reason and not by the count</b>, both being
    /// nought available. One is permanent and one lasts until the next quarter closes.
    /// see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight
    /// </summary>
    [Fact]
    public void An_exhausted_register_says_so_and_is_not_the_same_state_as_an_empty_one()
    {
        SeedSession(FirstSession);
        DateOnly late = new(2031, 1, 1);
        Registry(late).Mature(late);

        foreach (int i in Enumerable.Range(1, HoldoutWindows.Capacity))
        {
            Assert.True(Registry(late).SpendOldest($"decision {i}", "done", late).Spent);
        }

        HoldoutRegisterState state = Registry(late).Mature(late);

        Assert.Equal(HoldoutWindows.Capacity, state.Spent);
        Assert.Equal(0, state.Available);
        Assert.Equal(HoldoutRegistry.EveryMaturedWindowSpent, state.EmptyBecause);
        Assert.True(state.IsExhausted);

        // A designed dead end and not a failure: the run is clean and the refusal is a sentence.
        Assert.Equal(RunOutcome.Clean, state.Outcome);

        HoldoutSpendResult refused = Registry(late).SpendOldest("a ninth decision", "done", late);
        Assert.False(refused.Spent);
        Assert.Equal(HoldoutRegistry.NothingToSpend, refused.RefusedBecause);
    }

    /// <summary>A window that has not matured cannot be spent, because the register does not hold it.</summary>
    [Fact]
    public void A_window_that_has_not_matured_cannot_be_spent()
    {
        SeedSession(FirstSession);
        Registry(FirstSession).Mature(FirstSession);

        HoldoutSpendResult result = Registry(FirstSession).Spend("2026-Q4", "too early", "n/a", FirstSession);

        Assert.False(result.Spent);
        Assert.Equal(HoldoutRegistry.NoSuchWindow, result.RefusedBecause);
    }

    /// <summary>
    /// A spend recorded after the as-of is invisible to a read standing at it, so a replay of an
    /// evening reports the budget that evening had.
    /// </summary>
    [Fact]
    public void A_spend_after_the_as_of_is_invisible_to_a_read_standing_at_it()
    {
        SeedSession(FirstSession);

        // Recorded on the day it matures, so the window itself is visible from then on. What moves
        // between the two reads below is only the spend.
        Registry(FirstMaturity).Mature(FirstMaturity);

        DateOnly later = new(2027, 7, 1);
        Registry(later).SpendOldest("a later decision", "done", later);

        using SqliteConnection connection = _connections.OpenReadOnly();

        StoredHoldoutWindow atMaturity =
            Assert.Single(HoldoutWindowReader.Read(connection, FirstMaturity, Zone));
        Assert.True(atMaturity.IsAvailable);

        StoredHoldoutWindow afterwards = HoldoutWindowReader.Read(connection, later, Zone)[0];
        Assert.False(afterwards.IsAvailable);
    }

    /// <summary>
    /// A window recorded after the as-of is invisible to a read standing at it, even though the
    /// calendar says it had matured by then.
    ///
    /// <b>This is the bound that makes the missing-window read mean anything.</b> The register fills
    /// up as the registry runs, so a registry that runs late records a window with a stamp later
    /// than the evenings it was already mature on. A read that ignored the stamp would report a
    /// budget nobody held, and would report nought missing on every past evening however late the
    /// recording happened.
    /// </summary>
    [Fact]
    public void A_window_recorded_after_the_as_of_is_invisible_to_a_read_standing_at_it()
    {
        SeedSession(FirstSession);

        DateOnly late = new(2027, 7, 1);
        Registry(late).Mature(late);

        using SqliteConnection connection = _connections.OpenReadOnly();

        // Three windows had matured by the late date and the register holds all three, but on the
        // first window's own maturity the register held none of them.
        Assert.Equal(3, HoldoutWindowReader.Read(connection, late, Zone).Count);
        Assert.Empty(HoldoutWindowReader.Read(connection, FirstMaturity, Zone));

        // And the read standing there says so rather than saying nothing has matured.
        HoldoutRegisterState state = Registry(FirstMaturity).Read(FirstMaturity);

        Assert.Equal(1, state.Matured);
        Assert.Equal(0, state.Recorded);
        Assert.Equal(HoldoutRegistry.NotRecorded, state.EmptyBecause);
    }

    // ---- seeding -----------------------------------------------------------------------------

    /// <summary>
    /// One setup on one session, which is the whole of what the schedule needs from the store.
    ///
    /// The window arithmetic reads the earliest session rather than an authored go-live date, so
    /// what a test seeds is evidence and not a configuration value.
    /// </summary>
    private void SeedSession(DateOnly session)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
            VALUES ('AAA', 'AAA', 'NASDAQ', 'Common Stock', '2020-01-02');

            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all)
            VALUES (@id, @as_of, 'AAA', 'long', '[]', 0);
            """;

        command.Parameters.AddWithValue("@id", $"{session:yyyy-MM-dd}-AAA-long");
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(session));
        command.ExecuteNonQuery();
    }

}
