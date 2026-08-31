using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The two populations a measurement pass can read, and the fact that no third exists.
///
/// <b>The permission the reconstructed read rests on is that it writes no evidence row.</b> That is
/// asserted at run time by counting the evidence tables before and after, which is the right place
/// for it because it is a fact about the run. What is asserted here is the half a count cannot show:
/// that the two table sets are disjoint, that nothing names a table belonging to the other, and that
/// the shipped source declares an insert against every one of them so `writer-ownership` can see it.
/// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
/// </summary>
public sealed class SubjectTablesTests
{
    [Fact]
    public void The_two_populations_share_no_table()
    {
        string[] evidence =
        [
            SubjectTables.Evidence.Setup,
            SubjectTables.Evidence.Control,
            SubjectTables.Evidence.ForwardReturn,
        ];

        string[] calibration =
        [
            SubjectTables.Calibration.Setup,
            SubjectTables.Calibration.Control,
            SubjectTables.Calibration.ForwardReturn,
        ];

        Assert.Empty(evidence.Intersect(calibration, StringComparer.Ordinal));
        Assert.Equal(3, evidence.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, calibration.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Only_the_evidence_pair_reports_itself_as_evidence()
    {
        Assert.True(SubjectTables.Evidence.IsEvidence);
        Assert.False(SubjectTables.Calibration.IsEvidence);

        // Excursions are a property of the population rather than a setting a caller passes, so the
        // reconstructed pair can never be constructed claiming to have them.
        Assert.True(SubjectTables.Evidence.ExcursionsAvailable);
        Assert.False(SubjectTables.Calibration.ExcursionsAvailable);
    }

    [Fact]
    public void Every_table_of_both_populations_carries_a_literal_insert_in_the_shipped_source()
    {
        // <b>The regression that made this test worth writing.</b> Parameterising the two inserts by
        // table name left `INSERT INTO {tables.ForwardReturn}` in the source, which matches nothing
        // in `writer-ownership`'s scan: the writes found fell from 35 to 33 and two stores lost
        // their declared writer without anything failing except the floor. Written out, each table
        // is attributable to the stage that writes it.
        //
        // Asserted over the shipped source rather than over the check, because the property is that
        // the text a scanner reads is there to be read.
        string[] tables =
        [
            SubjectTables.Evidence.Control,
            SubjectTables.Evidence.ForwardReturn,
            SubjectTables.Calibration.Control,
            SubjectTables.Calibration.ForwardReturn,
        ];

        string source = string.Concat(
            RepositoryLayout.SourceFiles
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}PullbackStrategyLab.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        var missing = tables
            .Where(t => !source.Contains($"INSERT INTO {t}\n", StringComparison.Ordinal)
                     && !source.Contains($"INSERT INTO {t}\r\n", StringComparison.Ordinal)
                     && !source.Contains($"INSERT INTO {t} ", StringComparison.Ordinal))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"{missing.Length} table(s) have no literal INSERT in the shipped source, so nothing "
            + "attributes a writer to them: " + string.Join(", ", missing));
    }
}
