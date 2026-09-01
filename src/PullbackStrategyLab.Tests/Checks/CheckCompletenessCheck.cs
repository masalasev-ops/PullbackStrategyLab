using System.Text.Json;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every setup row has a result recorded for every check defined at its date.
///
/// The research loop exists to find which checks carry the strategy, and that is unanswerable if the
/// store only remembers the setups that passed or only the checks that ran before the first failure.
/// So a detector that short-circuits, or one that drops a check nobody reads, has to fail here.
/// see: Failed checks are recorded rather than discarded
///
/// <b>The check names come from ARCHITECTURE.html's gate lists, and the reconciliation runs both
/// ways.</b> A gate the detector does not run is a rule the document states and the lab does not
/// apply; a check the detector runs that no gate names is a rule the lab applies and the document
/// does not state. Either is a divergence between the strategy as written and the strategy as run,
/// and only reading one direction would catch half of them.
///
/// The gate list is read through <c>HtmlCheckList</c>, whose failure mode is worth naming because it
/// is silent: the heading lookup throws if the heading is reworded, but the id scan returns an empty
/// list if the markup changes shape. An empty list would make this check assert the detector against
/// nothing and pass, so the count is stated in advance.
/// </summary>
public sealed class CheckCompletenessCheck
{
    private readonly ITestOutputHelper _output;

    public CheckCompletenessCheck(ITestOutputHelper output) => _output = output;

    /// <summary>The heading over the long gate list, cited by text with its label included.</summary>
    public const string LongHeading = "The long checks buy";

    /// <summary>The heading over the short gate list.</summary>
    public const string ShortHeading = "The short checks sell";

    [Fact]
    [Trait("check", "check-completeness")]
    public void Every_setup_has_a_result_for_every_check_the_document_defines()
    {
        var coverage = new CheckCoverage("check-completeness", _output);
        string architecture = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        IReadOnlyList<string> longGates = HtmlCheckList.NamesUnder(architecture, LongHeading);
        IReadOnlyList<string> shortGates = HtmlCheckList.NamesUnder(architecture, ShortHeading);

        // Stated in advance rather than left self-validating. A gate list that came back empty would
        // make every assertion below hold trivially, which is this check's own failure mode.
        Assert.True(longGates.Count == 10,
            $"{longGates.Count} gate(s) under \"{LongHeading}\" in ARCHITECTURE.html, not ten. The parser reads a "
            + "span class rather than a table, so a change to that markup returns an empty list without erroring.");
        Assert.True(shortGates.Count == 10,
            $"{shortGates.Count} gate(s) under \"{ShortHeading}\" in ARCHITECTURE.html, not ten.");

        var problems = new List<string>();
        problems.AddRange(Divergences("long", longGates, SetupChecks.Long));
        problems.AddRange(Divergences("short", shortGates, SetupChecks.Short));

        // Every setup the replay produced, read back from the store the fixture run leaves behind.
        // A property asserted over hand-written rows would say the assertion works and nothing about
        // what the detector wrote.
        IReadOnlyList<StoredCheckResults> setups = ReadSetups();
        int rowsChecked = 0;

        foreach (StoredCheckResults setup in setups)
        {
            IReadOnlyList<string> expected = setup.Direction == "long" ? SetupChecks.Long : SetupChecks.Short;
            string[] missing = [.. expected.Where(name => !setup.Names.Contains(name))];
            string[] extra = [.. setup.Names.Where(name => !expected.Contains(name))];

            if (missing.Length > 0)
            {
                problems.Add($"{setup.SetupId} has no result for: {string.Join(", ", missing)}");
            }

            if (extra.Length > 0)
            {
                problems.Add($"{setup.SetupId} records a check no gate names: {string.Join(", ", extra)}");
            }

            rowsChecked++;
        }

        coverage
            .Examined("gates in the long check list", longGates.Count)
            .Examined("gates in the short check list", shortGates.Count)
            .Examined("checks the detectors declare", SetupChecks.Long.Count + SetupChecks.Short.Count)
            .Examined("setup rows read back from the replay store", rowsChecked)
            .Examined("check results across those rows", setups.Sum(s => s.Names.Count))
            .NoSourceScan(
                "it reads rows the detectors actually wrote in a run, and the names they declare, from the "
                + "compiled code. A detector that stopped recording a check leaves rows missing it rather than "
                + "text missing from a file");

        // The one gate that runs narrower than the document words it. Recorded here rather than in
        // the PROGRESS entry alone, because a later session reading a passing `reached-ceiling` has
        // no other way to know which of its three clauses were tested.
        //
        // <b>It named 4.4 until 4.4 landed, and the reason it moved is not that the clause is
        // missing.</b> VwapEngine computes the anchored level and `anchored_vwap` stores it. What
        // narrows the gate now is that the level exists only where the store holds minute bars back
        // to a row's own swing, and the fetch buys one session a night while a swing sits three to
        // twenty-seven sessions back. So the deferral follows the obligation that closes it rather
        // than resting at a checkpoint that has shipped.
        coverage.OutOfScope(
            "clauses of the short reached-ceiling gate the store cannot reach a level for",
            1,
            CheckCoverage.OutOfScopeReason.UntilCheckpoint(
                "4.5",
                "the third clause compares the price against the average price anchored to the swing the thrust ran "
                + "from, which is a volume-weighted average over minute bars from that swing forward. VwapEngine "
                + "computes it from 4.4 and IntradayFetcher buys one session a night, so a row whose swing sits "
                + "further back than the store reaches records two clauses and says which. Widening that window is "
                + "free in vendor calls and costly in rows, which is the fetch's decision and is carried to 4.5. "
                + "Approximating the anchored level from daily bars would put a plausible wrong number inside the "
                + "check that decides whether the bounce reached its ceiling"));

        if (setups.Count == 0)
        {
            coverage.NotExamined("setup rows read back from the replay store", 1,
                "the replay produced no setups, so the per-row half of this check asserted nothing");
        }

        coverage.Report();

        Assert.True(problems.Count == 0,
            $"{problems.Count} completeness problem(s):\n  " + string.Join("\n  ", problems.Take(20)));
    }

    /// <summary>
    /// Where the document's gates and a detector's checks disagree, in both directions.
    ///
    /// Pure and separated from the run so it can be proved against lists written by hand.
    /// </summary>
    public static IReadOnlyList<string> Divergences(
        string direction,
        IReadOnlyList<string> gates,
        IReadOnlyList<string> declared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(declared);

        var problems = new List<string>();

        foreach (string gate in gates.Where(g => !declared.Contains(g, StringComparer.Ordinal)))
        {
            problems.Add(
                $"ARCHITECTURE.html defines the {direction} gate \"{gate}\" and the detector runs no such check. "
                + "The document states the strategy and the lab does not apply it.");
        }

        foreach (string check in declared.Where(d => !gates.Contains(d, StringComparer.Ordinal)))
        {
            problems.Add(
                $"the {direction} detector runs \"{check}\" and no gate in ARCHITECTURE.html names it. "
                + "The lab applies a rule the document does not state.");
        }

        return problems;
    }

    /// <summary>
    /// The setups a replay of its own produced.
    ///
    /// Its own rather than the store `fixture-replay` leaves behind, which was the first attempt and
    /// which fails intermittently: the two checks run in the same assembly and the file is held open
    /// by whichever got there first. A shared artefact between two checks is a coupling neither one
    /// declares, and it fails on timing rather than on the property.
    /// </summary>
    private static IReadOnlyList<StoredCheckResults> ReadSetups()
    {
        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        replay.Run();

        using Microsoft.Data.Sqlite.SqliteConnection connection = replay.OpenStore();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setup_id, direction, check_results FROM setup";

        var setups = new List<StoredCheckResults>();
        using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            CheckResult[] results =
                JsonSerializer.Deserialize<CheckResult[]>(reader.GetString(2), Json) ?? [];

            setups.Add(new StoredCheckResults(
                reader.GetString(0),
                reader.GetString(1),
                [.. results.Select(r => r.Name)]));
        }

        return setups;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredCheckResults(string SetupId, string Direction, IReadOnlyList<string> Names);
}
