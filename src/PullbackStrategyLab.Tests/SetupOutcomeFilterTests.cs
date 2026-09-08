using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The gallery's outcome filter, over a night this file authors, through the read surface itself.
///
/// <b>Behavioural rather than a scan, because a scan could not have caught what this is for.</b> The
/// predicate is three lines and finding them in the source would say the filter exists. What is
/// worth asserting is which rows come back, and specifically that the answer runs the detector's own
/// definition of passing rather than a second one: `cluster` is recorded and never required, so a
/// setup failing only it is a candidate, and a filter counting failed rows would report it as one
/// gate short. Over the 367 rows the live store held on 2026-09-08 those two readings differ by
/// eight, so the difference is a population and not a hypothetical.
///
/// <b>The night is authored and the reason is the same one phase 6 gives throughout.</b> No setup
/// has ever passed every gating check on a real night, so a fixture over captured rows could not
/// exercise the passed-everything branch at all: it would assert an empty answer and pass whether
/// or not the filter worked. The authored night carries one of each shape on purpose.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class SetupOutcomeFilterTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 8, 24);

    private readonly TemporaryDirectory _root = new();
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 24, 22, 0, 0, TimeSpan.Zero));

    public SetupOutcomeFilterTests()
    {
        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();
    }

    public void Dispose() => _root.Dispose();

    private SetupsResponse Read(string? failed = null, string? outcome = null) =>
        new LabSetups(_connections).Read(Session, _clock.UtcNow, "America/New_York", failed, outcome);

    /// <summary>
    /// One long setup with the verdicts it is given, and `passed_all` derived rather than passed in.
    ///
    /// Derived on purpose: the column is what the detector writes and the filter reads it back, so a
    /// test that set the column by hand could seed a row whose column and whose checks disagree, and
    /// would then be asserting something no detector can produce.
    /// </summary>
    private void Seed(string ticker, params (string Name, bool Passed)[] checks)
    {
        CheckResult[] results = [.. checks.Select(c => new CheckResult(c.Name, c.Passed, null))];
        string setupId = $"{Session:yyyy-MM-dd}-{ticker}-long";

        using SqliteConnection connection = _connections.OpenWrite();

        using (SqliteCommand security = connection.CreateCommand())
        {
            security.CommandText = """
                INSERT OR IGNORE INTO security (ticker, name, exchange, type, first_seen)
                VALUES (@ticker, @ticker, 'NASDAQ', 'Common Stock', '2020-01-02');
                """;
            security.Parameters.AddWithValue("@ticker", ticker);
            security.ExecuteNonQuery();
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                               trigger_price, stop_price, stop_distance_ranges)
            VALUES (@id, @as_of, @ticker, 'long', @checks, @passed_all, '101.00', '99.00', '0.40');
            """;
        command.Parameters.AddWithValue("@id", setupId);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(Session));
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@checks", JsonSerializer.Serialize(results));
        command.Parameters.AddWithValue("@passed_all", SetupChecks.PassedAll(results) ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The four shapes the filter has to tell apart, seeded once and read many ways.
    ///
    /// CLEAN passes everything. ONLYCLUSTER fails the recorded and never required check alone, which
    /// makes it a candidate and is the row the two definitions disagree about. ONEGATE fails one
    /// gate. GATEPLUS fails one gate and `cluster`, which is still one gate from a candidate and is
    /// the row a count of failed rows would misplace. TWOGATES fails two.
    /// </summary>
    private void SeedTheNight()
    {
        Seed("CLEAN", ("tradable", true), ("dip-shape", true), ("exit-tight", true), ("cluster", true));
        Seed("ONLYCLUSTER", ("tradable", true), ("dip-shape", true), ("exit-tight", true), ("cluster", false));
        Seed("ONEGATE", ("tradable", true), ("dip-shape", true), ("exit-tight", false), ("cluster", true));
        Seed("GATEPLUS", ("tradable", true), ("dip-shape", true), ("exit-tight", false), ("cluster", false));
        Seed("TWOGATES", ("tradable", true), ("dip-shape", false), ("exit-tight", false), ("cluster", true));
    }

    private static string[] Tickers(SetupsResponse response) =>
        [.. response.Long.Concat(response.Short).Select(s => s.Ticker).Order(StringComparer.Ordinal)];

    [Fact]
    public void Passed_everything_returns_the_candidates_and_counts_cluster_as_not_gating()
    {
        SeedTheNight();

        SetupsResponse response = Read(outcome: SetupOutcomes.PassedEverything);

        // ONLYCLUSTER is the assertion. A filter counting failed checks would leave it out, and the
        // detector calls it a candidate, so leaving it out would be the gallery disagreeing with the
        // store about which rows the lab would have traded.
        Assert.Equal(["CLEAN", "ONLYCLUSTER"], Tickers(response));

        // And it agrees with the column the detector wrote, over every row of the night rather than
        // over the two it returned, so the two definitions are reconciled in both directions.
        Assert.All(
            Read().Long.Where(s => Tickers(response).Contains(s.Ticker)),
            s => Assert.True(s.PassedAll));
    }

    [Fact]
    public void Failed_only_one_counts_gating_checks_rather_than_failed_rows()
    {
        SeedTheNight();

        SetupsResponse response = Read(outcome: SetupOutcomes.FailedOnlyOne);

        // GATEPLUS fails two checks and one gate. It belongs here, and a count of failed rows would
        // put it with TWOGATES, which is the eight-row difference measured over the live store.
        Assert.Equal(["GATEPLUS", "ONEGATE"], Tickers(response));
    }

    [Fact]
    public void The_two_filters_compose_rather_than_replacing_each_other()
    {
        SeedTheNight();

        // Of the names exit-tight rejected, which were otherwise clean. Three rows fail exit-tight
        // and two of them are one gate short, so an outcome filter that replaced the check filter
        // would answer with GATEPLUS and ONEGATE whatever check was named.
        Assert.Equal(
            ["GATEPLUS", "ONEGATE"],
            Tickers(Read(failed: "exit-tight", outcome: SetupOutcomes.FailedOnlyOne)));

        Assert.Equal(["GATEPLUS", "ONEGATE", "TWOGATES"], Tickers(Read(failed: "exit-tight")));

        // And the composition can empty the night, which is the case the page has to explain rather
        // than render as a night that flagged nothing.
        Assert.Empty(Tickers(Read(failed: "exit-tight", outcome: SetupOutcomes.PassedEverything)));
    }

    [Fact]
    public void An_outcome_nobody_recognises_matches_nothing_rather_than_everything()
    {
        SeedTheNight();

        // The failure that could not be seen. A filter falling through to "no filter" renders as a
        // night in which every name qualified, and on the passed-everything option that is the
        // difference between "nothing passed" and "everything did".
        Assert.Empty(Tickers(Read(outcome: "whatever")));
        Assert.Equal(5, Read().Flagged);
    }

    [Fact]
    public void The_response_says_what_it_was_asked_so_an_empty_night_can_give_its_reason()
    {
        SeedTheNight();

        SetupsResponse response = Read(failed: "exit-tight", outcome: SetupOutcomes.PassedEverything);

        Assert.Equal("exit-tight", response.FailedCheck);
        Assert.Equal(SetupOutcomes.PassedEverything, response.Outcome);

        // Flagged is the night, not the filtered set, so the page can say how many it is hiding.
        Assert.Equal(5, response.Flagged);
    }

    [Fact]
    public void A_night_with_no_filter_is_unchanged_by_any_of_this()
    {
        SeedTheNight();

        SetupsResponse response = Read();

        Assert.Null(response.FailedCheck);
        Assert.Null(response.Outcome);
        Assert.Equal(5, response.Flagged);
        Assert.Equal(["CLEAN", "GATEPLUS", "ONEGATE", "ONLYCLUSTER", "TWOGATES"], Tickers(response));
    }

    /// <summary>
    /// The rule itself, at the boundary, without a store.
    ///
    /// Kept beside the surface tests rather than in the detection folder because the boundary that
    /// matters here is the one the filter reads: nought gating failures is a candidate and one is a
    /// near miss, and `cluster` moves neither.
    /// </summary>
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, false)]
    public void The_two_outcomes_partition_on_the_gating_count(int gates, bool passed, bool nearMiss)
    {
        CheckResult[] results =
        [
            new("cluster", false, null),
            .. Enumerable.Range(0, gates).Select(i => new CheckResult($"gate-{i}", false, null)),
        ];

        Assert.Equal(gates, SetupChecks.GatingFailures(results));
        Assert.Equal(passed, SetupOutcomes.Matches(SetupOutcomes.PassedEverything, results));
        Assert.Equal(nearMiss, SetupOutcomes.Matches(SetupOutcomes.FailedOnlyOne, results));

        // PassedAll is the same rule and is asserted to stay the same rule, because it was written
        // out separately until 6.12 and the refactor that joined them is exactly the kind that is
        // correct on the day and drifts afterwards.
        Assert.Equal(passed, SetupChecks.PassedAll(results));
    }
}
