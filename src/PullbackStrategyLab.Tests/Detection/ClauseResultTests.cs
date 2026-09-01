using System.Text.Json;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Detection;

/// <summary>
/// A multi-clause gate records a verdict and a number per clause, and both reach the store.
///
/// <b>This is the 2.9 obligation, raised by the gallery review and due at 4.1.</b> `CheckResult`
/// carried one value, so `tradable-shortable` tested four things and recorded one number: a failing
/// verdict told a reader nothing about whether it was turnover, price, capitalisation or listing
/// age. The screen could already say which clause the number came from and could not say which
/// clause the gate fell over, which is the question a person asks in front of a greyed row.
///
/// <b>The store is what is asserted here rather than the in-memory result.</b> A record gaining a
/// property proves nothing about the evidence: the detector serialises what it evaluated, the API
/// deserialises it back, and either end could drop the field without any test of the type noticing.
/// So a detector is run, a row is read out of the store, and the clause values are read off it.
/// see: Failed checks are recorded rather than discarded
/// </summary>
public sealed class ClauseResultTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_stored_setup_carries_a_value_per_clause_for_every_multi_clause_gate()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        IReadOnlyList<CheckResult> longChecks = StoredChecks(replay, SetupDirection.Long);
        IReadOnlyList<CheckResult> shortChecks = StoredChecks(replay, SetupDirection.Short);

        CheckResult tradable = longChecks.Single(c => c.Name == "tradable");
        CheckResult shortable = shortChecks.Single(c => c.Name == "tradable-shortable");

        // Two and four, and the difference between them is the point: the reading beside these two
        // said "of four clauses" for both until 4.1, and the long gate tests turnover and price.
        Assert.Equal(
            ["liquidity", "price"],
            tradable.Clauses!.Select(c => c.Name).ToArray());
        Assert.Equal(
            ["liquidity", "price", "market capitalisation", "listing age"],
            shortable.Clauses!.Select(c => c.Name).ToArray());

        // A number per clause, not one number for the gate. This is the half that makes it useful
        // rather than decorative: a threshold experiment moving the price floor needs the price
        // clause's distribution over the rows that failed it, which one recorded value cannot give.
        Assert.All(tradable.Clauses!, clause => Assert.NotNull(clause.Value));
        Assert.NotEqual(
            tradable.Clauses![0].Value,
            tradable.Clauses![1].Value);
    }

    [Fact]
    public void A_gate_that_fails_says_which_of_its_clauses_did()
    {
        // Authored rather than drawn from the fixture, because the fixture's captured day may hold
        // no name that fails exactly one clause of this gate, and a test that asserts a property
        // only where the market happened to supply it is a test that stops asserting it.
        CheckResult failed = ShortPullbackRules
            .Evaluate(new ShortPullbackRules.ShortEvidence
            {
                Close = 100m,
                MedianDollarVolume = 9_000_000_000m,
                MarketCap = 1m,
                SessionsListed = 500,
            })
            .Single(c => c.Name == "tradable-shortable");

        Assert.False(failed.Passed);
        Assert.Equal("market capitalisation", Assert.Single(failed.FailedClauses).Name);

        // And the three that held are still recorded, on the rule that failed checks are kept: a
        // row saying only what broke cannot answer how close the others were.
        Assert.Equal(3, failed.Clauses!.Count(c => c.Passed));
    }

    [Fact]
    public void A_single_clause_gate_records_no_clause_list_rather_than_an_empty_one()
    {
        CheckResult movesEnough = LongPullbackRules
            .Evaluate(new LongPullbackRules.LongEvidence { AverageDailyRange = 0.08m })
            .Single(c => c.Name == "moves-enough");

        // Null, not empty. "This gate has no clauses" and "this gate is its own clause" are
        // different statements, and an empty list says the first about every check in the corpus.
        Assert.Null(movesEnough.Clauses);
        Assert.Empty(movesEnough.FailedClauses);
    }

    [Fact]
    public void The_exempt_capitalisation_clause_passes_with_no_value_rather_than_with_a_number()
    {
        CheckResult exempt = ShortPullbackRules
            .Evaluate(new ShortPullbackRules.ShortEvidence
            {
                Close = 100m,
                MedianDollarVolume = 9_000_000_000m,
                MarketCapExempt = true,
                SessionsListed = 500,
            })
            .Single(c => c.Name == "tradable-shortable");

        ClauseResult cap = exempt.Clauses!.Single(c => c.Name == "market capitalisation");

        // It passed because it was exempt and not because a figure cleared a floor, so it carries
        // no figure. A calibration row and a nightly row are told apart by the value being absent
        // rather than by the note beside them, which is a thing a query can select on.
        Assert.True(cap.Passed);
        Assert.Null(cap.Value);
    }

    private static IReadOnlyList<CheckResult> StoredChecks(PhaseReplay replay, string direction)
    {
        using SqliteConnection read = replay.OpenWrite();
        using SqliteCommand command = read.CreateCommand();
        command.CommandText = """
            SELECT check_results
              FROM setup
             WHERE direction = @direction
             ORDER BY setup_id
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@direction", direction);

        string? json = command.ExecuteScalar() as string;
        Assert.False(
            string.IsNullOrWhiteSpace(json),
            $"The replay stored no {direction} setup, so there is no row to read clauses off.");

        return JsonSerializer.Deserialize<CheckResult[]>(json!, Json) ?? [];
    }
}
