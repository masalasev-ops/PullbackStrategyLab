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
public sealed class WriterOwnershipCheck
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
        int tableNotCreated = 0;
        int componentNotBuilt = 0;
        int unresolvedNames = 0;

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
                    tableNotCreated++;
                    continue;
                }

                if (!SourceWrites.ProductionTypeNames.Contains(writer.Component))
                {
                    // The table exists and its writer does not, which is the ordinary shape when
                    // one checkpoint creates a store and a later one builds a component that
                    // updates it. Separated from the case below rather than folded into it,
                    // because that one is a defect and this one is a schedule.
                    componentNotBuilt++;
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
            .Examined("declared writers whose store and component both exist", declaredWritersExamined)
            .Examined("operations with more than one writer, where SCHEMA states the disjointness", declaredDisjoint)
            .OutOfScope("declared writers of a store no migration has created yet", tableNotCreated,
                "the table arrives with the checkpoint that builds its component")
            .OutOfScope("declared writers whose component has not been built yet", componentNotBuilt,
                "the store exists and the component that writes it arrives at a later checkpoint");

        if (unresolvedNames > 0)
        {
            coverage.NotExamined("declared writers naming something outside the component catalogue", unresolvedNames,
                "the name could not be resolved to a catalogue component, so neither direction could be asserted for it");
        }

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
}
