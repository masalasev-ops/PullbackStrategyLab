using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Bars are append-only. Nothing in the lab deletes or updates a row in a bar table.
///
/// A vendor correction arrives as a new row with a later observed_at, and reads take the
/// latest observation at or before the as-of date. Editing the row instead would rewrite what
/// the lab saw on a night that has already been replayed, and nothing afterwards could detect
/// that it had happened. That is the difference between a replay and a story about one.
///
/// Separate from writer-ownership, which would also reject an undeclared write. This one names
/// the property rather than the paperwork, so a failure says what was broken instead of which
/// document disagreed.
/// </summary>
public sealed class BarAppendOnlyCheck
{
    private readonly ITestOutputHelper _output;

    public BarAppendOnlyCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every table holding bars. Named here rather than pattern-matched on the word "bar",
    /// because a table that stopped matching a pattern would leave the check quietly narrower.
    /// </summary>
    public static IReadOnlyList<string> BarTables { get; } = ["daily_bar", "index_bar", "intraday_bar"];

    [Fact]
    [Trait("check", "bar-append-only")]
    public void Nothing_deletes_or_updates_a_bar()
    {
        var coverage = new CheckCoverage("bar-append-only", _output);
        var bars = new HashSet<string>(BarTables, StringComparer.Ordinal);

        SourceWrite[] mutations = SourceWrites.InProductionSource
            .Where(w => bars.Contains(w.Table))
            .Where(w => w.IsDelete || w.Operation == StoreOperation.Update)
            .ToArray();

        SourceWrite[] inserts = SourceWrites.InProductionSource
            .Where(w => bars.Contains(w.Table) && !w.IsDelete && w.Operation == StoreOperation.Insert)
            .ToArray();

        string[] created = SchemaDeclarations.TablesInMigrations.Where(bars.Contains).ToArray();

        coverage
            .Examined("bar tables named by the check", BarTables.Count)
            .Examined("bar tables a migration has created", created.Length)
            .Context("source files scanned", SourceWrites.ProductionFilesRead)
            .Examined("writes found against a bar table", inserts.Length + mutations.Length)
            .Scan("no delete or update against a bar table exists in the shipped source",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_vendor_correction_arrives_as_a_new_row_and_the_original_stays",
                    "the ingestor is handed a corrected figure for a session it already stored, and the test "
                    + "asserts both rows are present afterwards. That is the property; this scan is the half "
                    + "that says no other component in the shipped source can undo it"));

        if (created.Length < BarTables.Count)
        {
            coverage.OutOfScope("bar tables no migration has created yet", BarTables.Count - created.Length,
                CheckCoverage.OutOfScopeReason.UntilCheckpoint("4.2",
                    "intraday_bar arrives with IntradayFetcher, and nothing can write a table that does not exist"));
        }

        coverage.Report();

        Assert.True(mutations.Length == 0,
            $"{mutations.Length} statement(s) delete or update a bar:\n  "
            + string.Join("\n  ", mutations.Select(m => m.ToString()))
            + "\n  A vendor correction is a new row with a later observed_at, never an edit.");

        // A deliberate tripwire. SCHEMA declares one legitimate update against a bar table:
        // VwapEngine writing vwap_session on intraday_bar at phase 4. When that table is
        // created this assertion fails, which forces the exception to be written into the
        // check by name and by column rather than the check being loosened until it passes.
        Assert.True(!created.Contains("intraday_bar"),
            "intraday_bar now exists. SCHEMA declares Update VwapEngine on vwap_session only, so this check needs that "
            + "one exception stated by name and by column. Widening it to allow any update against intraday_bar would "
            + "give away the property the check exists to hold.");
    }
}
