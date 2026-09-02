using System.Text.RegularExpressions;
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
///
/// <b>The one exception this check carried was removed at 4.7 and the rule now reads as written.</b>
/// From 4.4 it admitted `UPDATE intraday_bar SET vwap_session`, named by table, by column and by
/// component, on the ground that the session average was computed locally and was never anything the
/// vendor sent. The write stopped at 4.7, because no reader for it was ever named and a running
/// session average is derivable from the stored minutes whenever one is wanted. So there is nothing
/// after the comma any more: no delete and no update against a bar table, in any component, on any
/// column. An exception that is gone is one nobody has to argue about the width of.
/// see: The session average is derived when it is wanted and is not stored on a bar
/// </summary>
public sealed partial class BarAppendOnlyCheck
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

        // The migrations, which the C# scan above cannot see and where every table rebuild in this
        // project actually lives.
        //
        // RepositoryLayout.SourceFiles is *.cs, so SourceWrites reads no SQL at all and this check
        // had never examined a migration. That is not hypothetical: 028 issues UPDATE
        // indicator_daily against a table SCHEMA declares append-only, 029 UPDATE scan_hit, 030
        // DELETE FROM scoreboard, and DROP TABLE plus re-INSERT is the established rebuild idiom in
        // 005 and 009. None of those touches a bar table, so the property held; nothing was
        // checking that it held. A future 031-daily-bar-rekey.sql losing rows would have passed
        // green, which is the whole of what the hard rule says CI greps for.
        MigrationWrite[] migrationMutations = [.. MigrationWrites().Where(w => bars.Contains(w.Table))];

        coverage
            .Examined("bar tables named by the check", BarTables.Count)
            .Examined("bar tables a migration has created", created.Length)
            .Context("source files scanned", SourceWrites.ProductionFilesRead)
            .Examined("writes found against a bar table", inserts.Length + mutations.Length)
            .Examined("migrations read for a delete, update or drop", MigrationCount)
            .Scan("no delete or update against a bar table exists in the shipped source",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_vendor_correction_arrives_as_a_new_row_and_the_original_stays",
                    "the ingestor is handed a corrected figure for a session it already stored, and the test "
                    + "asserts both rows are present afterwards. That is the property; this scan is the half "
                    + "that says no other component in the shipped source can undo it"))
            .Scan("no migration deletes, updates or drops a bar table",
                CheckCoverage.Backing.Test(
                    "MigrationRowSurvivalTests.Migration_005_rebuilds_both_tables_and_loses_no_row",
                    "a migration that rebuilds a table is applied to a populated store and every row is "
                    + "asserted present afterwards. That is what a rebuild must not cost; this scan is the "
                    + "half that says no migration takes the shortcut against a bar table"));

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

        Assert.True(migrationMutations.Length == 0,
            $"{migrationMutations.Length} migration statement(s) delete, update or drop a bar table:\n  "
            + string.Join("\n  ", migrationMutations.Select(m => $"{m.Migration}: {m.Statement} {m.Table}"))
            + "\n  A migration may add a column to a bar table and may not rewrite one. Rebuilding a bar table by "
            + "dropping and re-inserting it loses every observation the store held, and the rows it writes back "
            + "carry whatever stamp the rebuild gives them rather than the one the lab actually saw.");
    }

    /// <summary>One destructive statement in a migration, with the migration that carries it.</summary>
    public sealed record MigrationWrite(string Migration, string Statement, string Table);

    /// <summary>How many migrations were read, stated so a run that read none cannot look like a pass.</summary>
    public static int MigrationCount => PullbackStrategyLab.Data.MigrationRunner.All().Count;

    /// <summary>
    /// Every delete, update and drop any migration issues, by table.
    ///
    /// Comments are stripped first, on the same grounds the C# scan strips them: three migrations
    /// carry the sentence "Prices are TEXT holding a decimal, never REAL" and several explain the
    /// rebuild they are performing, so prose about a statement must not read as one.
    /// </summary>
    public static IReadOnlyList<MigrationWrite> MigrationWrites()
    {
        var writes = new List<MigrationWrite>();

        foreach (PullbackStrategyLab.Data.Migration migration in PullbackStrategyLab.Data.MigrationRunner.All())
        {
            string sql = SqlComment().Replace(migration.Sql, " ");

            foreach ((Regex pattern, string statement) in ((Regex, string)[])[
                (MigrationDelete(), "DELETE FROM"),
                (MigrationUpdate(), "UPDATE"),
                (MigrationDrop(), "DROP TABLE")])
            {
                foreach (Match match in pattern.Matches(sql))
                {
                    writes.Add(new MigrationWrite(migration.Name, statement, match.Groups["table"].Value));
                }
            }
        }

        return writes;
    }

    [GeneratedRegex(@"--[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex SqlComment();

    [GeneratedRegex(@"DELETE\s+FROM\s+(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MigrationDelete();

    [GeneratedRegex(@"(?<!\bDO\s{1,20})\bUPDATE\s+(?<table>[a-z_]+)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MigrationUpdate();

    [GeneratedRegex(@"DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MigrationDrop();

}
