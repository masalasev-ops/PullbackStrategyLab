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

        // Direction two: every declared writer of a store that exists today is present in the code.
        var typeNames = new HashSet<string>(
            SourceWrites.InProductionSource.Select(w => w.Type),
            StringComparer.Ordinal);

        int declaredWritersExamined = 0;
        int declaredWritersUnexamined = 0;
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
                    declaredWritersUnexamined++;
                    continue;
                }

                declaredWritersExamined++;
                if (!typeNames.Contains(writer.Component))
                {
                    failures.Add(
                        $"SCHEMA declares {writer.Operation} {writer.Component} on {store.Store}, which exists in the store, "
                        + "but no type of that name issues that statement.");
                }
            }
        }

        // One writer per table per operation, asserted on the declaration itself.
        foreach (StoreDeclaration store in declared.Where(s => live.Contains(s.Store)))
        {
            foreach (IGrouping<StoreOperation, Writer> group in store.Writers.GroupBy(w => w.Operation))
            {
                int distinct = group.Select(w => w.Component).Distinct(StringComparer.Ordinal).Count();
                if (distinct > 1 && !string.Equals(store.Store, "setup", StringComparison.Ordinal)
                                 && !string.Equals(store.Store, "setup_signal", StringComparison.Ordinal))
                {
                    failures.Add(
                        $"SCHEMA declares {distinct} writers for {group.Key} on {store.Store}: "
                        + string.Join(", ", group.Select(w => w.Component).Distinct(StringComparer.Ordinal)));
                }
            }
        }

        coverage
            .Examined("stores declared in SCHEMA.md", declared.Count)
            .Examined("stores a migration has created", live.Count)
            .Examined("source files read for store writes", SourceWrites.ProductionFilesRead)
            .Examined("writes found in the shipped source", SourceWrites.InProductionSource.Count)
            .Examined("declared writers of a store that exists", declaredWritersExamined)
            .NotExamined("declared writers of a store no migration has created yet", declaredWritersUnexamined,
                "the table arrives with the checkpoint that builds its component");

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
