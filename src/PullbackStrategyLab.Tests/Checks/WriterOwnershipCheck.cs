using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Every store has exactly one declared writer per operation, verified in both directions
/// against SCHEMA.md: every writer in the code is declared, and every declared writer of a
/// store that exists today is present in the code.
///
/// The rule is one writer per table per operation, not one writer per table. Several stores
/// legitimately have two owners on different operations, and a rule that counts exceptions
/// gets longer every time the design is correct.
///
/// The second direction can only cover the stores that exist. Most of SCHEMA describes
/// machinery later phases build, and a declared writer for a table no migration has created
/// is reported as unexamined rather than passed.
/// </summary>
public sealed partial class WriterOwnershipCheck
{
    private readonly ITestOutputHelper _output;

    public WriterOwnershipCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "writer-ownership")]
    public void Every_write_in_the_code_is_declared_and_every_declared_writer_of_a_live_store_exists()
    {
        var coverage = new CheckCoverage("writer-ownership", _output);
        IReadOnlyList<StoreDeclaration> declared = SchemaDeclarations.Stores;
        var live = new HashSet<string>(SchemaDeclarations.TablesInMigrations, StringComparer.Ordinal);
        var failures = new List<string>();

        // Direction one: nothing writes a store the schema does not give it.
        foreach (SourceWrite write in SourceWrites.InProductionSource)
        {
            if (write.IsDelete)
            {
                failures.Add($"{write} — no store in SCHEMA declares a delete, and bars are append-only besides.");
                continue;
            }

            StoreDeclaration? store = declared.FirstOrDefault(s => string.Equals(s.Store, write.Table, StringComparison.Ordinal));
            if (store is null)
            {
                failures.Add($"{write} — {write.Table} is not declared in SCHEMA.md at all.");
                continue;
            }

            bool ownsIt = store.Writers.Any(w =>
                w.Operation == write.Operation && string.Equals(w.Component, write.Type, StringComparison.Ordinal));

            if (!ownsIt)
            {
                string owners = string.Join(", ", store.Writers.Select(w => $"{w.Operation} {w.Component}"));
                failures.Add($"{write} — SCHEMA declares {write.Table} as: {owners}.");
            }
        }

        // Direction two: every declared writer of a store that exists today, whose component has
        // been built, issues the statement it is declared for.
        var issued = new HashSet<string>(
            SourceWrites.InProductionSource.Select(w => $"{w.Type}/{w.Table}/{w.Operation}"),
            StringComparer.Ordinal);

        int declaredWritersExamined = 0;
        int unresolvedNames = 0;

        // The components whose write cannot be asserted yet, kept by name rather than counted.
        // A count cannot be grouped by the checkpoint that ends it, and grouping is the whole of
        // what the naming rule buys: the number falls as checkpoints land instead of resting.
        // Each carries the checkpoint that would end it where SCHEMA names one, which is how a
        // component that lands before the store it writes is deferred to the store's checkpoint
        // rather than to its own.
        var deferredWriters = new List<(string Component, string? BuiltAt)>();

        foreach (StoreDeclaration store in declared)
        {
            foreach (Writer writer in store.Writers)
            {
                if (!writer.Resolved)
                {
                    // SCHEMA names a writer the component catalogue does not contain. Counted and
                    // reported rather than assumed to be a typo or assumed to be fine.
                    unresolvedNames++;
                    continue;
                }

                if (!live.Contains(store.Store))
                {
                    // The store is the missing half. Where SCHEMA says which checkpoint creates it,
                    // that is the due point: ReplayHarness is built at 5.3 and `replay_result` is
                    // keyed on a proposal that does not exist until 6.6, so deferring to the
                    // component's own checkpoint would come due the day the component landed.
                    deferredWriters.Add((writer.Component, store.BuiltAt));
                    continue;
                }

                if (!SourceWrites.ProductionTypeNames.Contains(writer.Component))
                {
                    // The table exists and its writer does not, which is the ordinary shape when
                    // one checkpoint creates a store and a later one builds a component that
                    // updates it. Separated from the case below rather than folded into it,
                    // because that one is a defect and this one is a schedule.
                    deferredWriters.Add((writer.Component, null));
                    continue;
                }

                declaredWritersExamined++;
                if (!issued.Contains($"{writer.Component}/{store.Store}/{writer.Operation}"))
                {
                    failures.Add(
                        $"SCHEMA declares {writer.Operation} {writer.Component} on {store.Store}, and both the table and "
                        + $"the type exist, but {writer.Component} issues no such statement.");
                }
            }
        }

        // One writer per table per operation, asserted on the declaration itself. Where a store
        // legitimately has two, the declaration says how they are disjoint and this reads that
        // rather than carrying a list of tables to forgive. A hardcoded exception is a fact about
        // the checker; a declaration is a fact about the design, and a reader of SCHEMA finds
        // only one of them.
        int declaredDisjoint = 0;

        foreach (StoreDeclaration store in declared.Where(s => live.Contains(s.Store)))
        {
            foreach (IGrouping<StoreOperation, Writer> group in store.Writers.GroupBy(w => w.Operation))
            {
                int distinct = group.Select(w => w.Component).Distinct(StringComparer.Ordinal).Count();
                if (distinct <= 1)
                {
                    continue;
                }

                if (store.StatesDisjointness)
                {
                    declaredDisjoint++;
                    continue;
                }

                failures.Add(
                    $"SCHEMA declares {distinct} writers for {group.Key} on {store.Store} and does not say how they "
                    + "are disjoint: "
                    + string.Join(", ", group.Select(w => w.Component).Distinct(StringComparer.Ordinal)));
            }
        }

        coverage
            .Examined("stores declared in SCHEMA.md", declared.Count)
            .Examined("stores a migration has created", live.Count)
            .Context("source files read for store writes", SourceWrites.ProductionFilesRead)
            .Examined("writes found in the shipped source", SourceWrites.InProductionSource.Count)
            .Context("types declared in the shipped source", SourceWrites.ProductionTypeNames.Count)
            .Scan("every write in the shipped source belongs to the component SCHEMA declares for it",
                CheckCoverage.Backing.Test(
                    "OrderProvenanceCheck.A_row_written_outside_a_run_of_the_gate_is_caught",
                    "the behavioural form of this scan, for orders alone, which is where the corpus scheduled "
                    + "one. It runs the gate over an authored session, reads every row back and asks whether a "
                    + "run of that stage was open when it was written, then writes a row outside every run and "
                    + "requires the predicate to reject it. What it does not reach is the other twenty stores: a "
                    + "component issuing a write through a helper this scan does not recognise would still be "
                    + "attributed to nobody there, and the check would report a smaller set rather than fail"))
            .Examined("declared writers whose store and component both exist", declaredWritersExamined)
            .Examined("operations with more than one writer, where SCHEMA states the disjointness", declaredDisjoint)
            ;

        // Grouped by the checkpoint that builds the component, resolved from BUILD_PLAN rather
        // than described in prose. This is the obligation raised at 1.12: the claim side of the
        // report has always named a closing checkpoint, and the coverage side carried free text
        // that nothing read. Two stores fall out of it here, because 2.2 creates `setup` with
        // three of its four declared writers still unbuilt.
        var schedule = ArchitectureConformanceCheck.Schedule.Read();

        foreach (IGrouping<string, (string Component, string? BuiltAt)> group in deferredWriters
                     .GroupBy(
                         w => w.BuiltAt ?? schedule.CheckpointFor(w.Component) ?? "unplaced",
                         StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string[] names = [.. group.Select(w => w.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            if (string.Equals(group.Key, "unplaced", StringComparison.Ordinal))
            {
                // No checkpoint in BUILD_PLAN names the component. Unexamined rather than deferred:
                // a deferral to nothing never ends, and this is the one case the naming rule exists
                // to make visible rather than to absorb.
                coverage.NotExamined("declared writers whose component no checkpoint builds", names.Length,
                    "BUILD_PLAN.md places none of these: " + string.Join(", ", names));
                continue;
            }

            coverage.OutOfScope(
                $"declared writers arriving at {group.Key}",
                names.Length,
                CheckCoverage.OutOfScopeReason.UntilCheckpoint(group.Key,
                    "the store or the component arrives with that checkpoint: " + string.Join(", ", names)));
        }

        if (unresolvedNames > 0)
        {
            coverage.NotExamined("declared writers naming something outside the component catalogue", unresolvedNames,
                "the name could not be resolved to a catalogue component, so neither direction could be asserted for it");
        }

        // A table created under a quoted identifier is a table no scanner in this suite can see.
        // Every parser here and in `bar-append-only`, `price-storage-form` and `point-in-time` reads
        // an unquoted name after CREATE TABLE or INSERT INTO, so quoting one hides it from all four
        // at once. 4.6 nearly bought that with a name: `order` is a reserved word and the table was
        // written as `trade_order` for exactly this reason, so the next one fails here rather than
        // disappearing.
        foreach (Migration migration in MigrationRunner.All())
        {
            foreach (Match quoted in QuotedTable().Matches(migration.Sql))
            {
                failures.Add(
                    $"{migration.Name} creates a table as {quoted.Value.Trim()}, under a quoted identifier. Every "
                    + "scanner in this suite reads an unquoted name, so a table declared this way is invisible to "
                    + "all of them at once. Name it so it needs no quotes.");
            }
        }

        // Direction three: the columns. SCHEMA's column tables were read by nothing until 4.6, and
        // five columns were already missing when the 3.7 sign-off measured it, one of them since
        // 2.5. It is reconciled against a store built by running every migration rather than against
        // the migration text, so a column added by an ALTER, or one a rebuild dropped, is seen the
        // way the store sees it.
        (int tablesReconciled, int columnsReconciled) = ReconcileColumns(failures);

        coverage
            .Examined("tables whose columns SCHEMA declares and a built store confirms", tablesReconciled)
            .Examined("column declarations reconciled against the store, both ways", columnsReconciled)
            .Examined(
                "tables declared by shape rather than by a column table of their own",
                SchemaColumns.DeclaredByShape.Count);

        coverage.Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} writer-ownership failure(s):\n  " + string.Join("\n  ", failures));

        Assert.True(declared.Count >= 25,
            $"Only {declared.Count} stores were parsed from SCHEMA.md. The document declared more than that before any "
            + "code existed, so a number this low means the parser stopped matching rather than that the schema shrank.");

        Assert.True(SourceWrites.InProductionSource.Count > 0,
            "No store writes were found in the shipped source at all, which means the scanner stopped matching. "
            + "A check that examines nothing passes forever.");
    }

    /// <summary>A table created under a quoted, bracketed or backticked identifier.</summary>
    [GeneratedRegex(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[""`\[]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedTable();

    /// <summary>
    /// Every column the store holds is declared in SCHEMA, and every column SCHEMA declares is in the
    /// store.
    ///
    /// <b>Both directions, and the second is the one the obligation argued for.</b> Repairing the
    /// five columns that were missing would have left the sixth to arrive unnoticed, which is the
    /// same reasoning that makes the writer half of this check run both ways.
    ///
    /// <b>A table the migrations create and SCHEMA declares no columns for is a failure.</b> That is
    /// the shape a column arrives unnoticed through: not a column missing from a table SCHEMA
    /// describes, but a whole table nobody described. Six phase-4 tables were in that state when this
    /// was written.
    /// </summary>
    private static (int Tables, int Columns) ReconcileColumns(List<string> failures)
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        using SqliteConnection connection = connections.OpenReadOnly();

        int tables = 0;
        int columns = 0;

        foreach (string table in TablesInTheStore(connection))
        {
            IReadOnlySet<string> actual = ColumnsOf(connection, table);

            if (!SchemaColumns.Declared.TryGetValue(table, out IReadOnlySet<string>? declared))
            {
                failures.Add(
                    $"the migrations create {table} with {actual.Count} column(s) and SCHEMA.md declares none of "
                    + "them, so nothing reconciles what that table holds. Give it a section, or a "
                    + "\"Shape of\" line naming the table it shares a shape with.");
                continue;
            }

            tables++;
            columns += actual.Count;

            foreach (string undeclared in actual.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                failures.Add($"{table}.{undeclared} is in the store and SCHEMA.md does not declare it.");
            }

            foreach (string absent in declared.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                failures.Add($"SCHEMA.md declares {table}.{absent} and no migration creates it.");
            }
        }

        return (tables, columns);
    }

    private static IReadOnlyList<string> TablesInTheStore(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";

        var tables = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static IReadOnlySet<string> ColumnsOf(SqliteConnection connection, string table)
    {
        // The table name comes from sqlite_master, so it is the store naming itself.
        using SqliteCommand command = connection.CreateCommand();
        SqliteIdentifier.Validate(table);
        command.CommandText = $"PRAGMA table_info({table});";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
