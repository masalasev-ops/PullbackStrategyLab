using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Proof that the scanners the checks are built on actually catch what they exist to catch,
/// and do not catch what they must not.
///
/// A test proving a check works has to be permanent rather than a break-and-revert done by
/// hand once. The violations here are written into the test, so the proof runs on every build
/// and the repository is never broken to produce it.
///
/// The failure mode being guarded against is under-reporting. A check that errors loudly gets
/// fixed because it blocks; a check that silently narrows its own scope keeps passing forever,
/// so what these tests mostly assert is that the scanners still see things.
/// </summary>
public sealed class CheckProofTests
{
    [Fact]
    public void The_clock_scanner_catches_a_direct_read()
    {
        const string source = """
            public sealed class Stage
            {
                public void Run()
                {
                    var at = DateTimeOffset.UtcNow;
                    var also = DateTime.Now;
                    var third = DateTime.Today;
                }
            }
            """;

        IReadOnlyList<ClockRead> reads = ClockReads.In(source);

        Assert.Equal(3, reads.Count);
        Assert.Contains(reads, r => r.Text.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal));
        Assert.Contains(reads, r => r.Text.Contains("DateTime.Today", StringComparison.Ordinal));
    }

    [Fact]
    public void The_clock_scanner_ignores_a_comment_that_names_the_ban()
    {
        const string source = """
            public sealed class Stage
            {
                // Direct DateTime.Now and DateTimeOffset.UtcNow are banned outside the clock.
                /// <summary>Never DateTime.UtcNow. Ask IClock.</summary>
                public void Run() => _clock.UtcNow.ToString();
            }
            """;

        // A check that fails on prose gets loosened the first time it does, and a loosened check
        // is worth less than the comment that explained it.
        Assert.Empty(ClockReads.In(source));
    }

    [Fact]
    public void The_write_scanner_attributes_a_statement_to_the_type_that_issues_it()
    {
        const string source = """
            public sealed class Alpha
            {
                public void One() => Run("INSERT INTO run_log (run_id) VALUES (@id);");
            }

            public sealed class Beta
            {
                public void Two() => Run("UPDATE run_log SET outcome = @outcome WHERE run_id = @id;");
            }
            """;

        IReadOnlyList<SourceWrite> writes = SourceWrites.InSource("proof.cs", source);

        Assert.Equal(2, writes.Count);
        Assert.Equal(("Alpha", "run_log", StoreOperation.Insert), Shape(writes[0]));
        Assert.Equal(("Beta", "run_log", StoreOperation.Update), Shape(writes[1]));
    }

    [Fact]
    public void An_upsert_counts_as_both_operations_on_the_table_the_insert_names()
    {
        const string source = """
            public sealed class Builder
            {
                public void One() => Run("INSERT INTO universe_member (ticker) VALUES (@t) ON CONFLICT (ticker) DO UPDATE SET removed_on = NULL;");
                public void Two() => Run("INSERT INTO universe_snapshot (as_of) VALUES (@d) ON CONFLICT (as_of) DO NOTHING;");
            }
            """;

        IReadOnlyList<SourceWrite> writes = SourceWrites.InSource("proof.cs", source);

        // Reading DO UPDATE as an insert alone is how a component acquires an undeclared update
        // on a table somebody else owns, and the word after DO UPDATE is SET rather than a table.
        Assert.Equal(3, writes.Count);
        Assert.Contains(writes, w => w.Table == "universe_member" && w.Operation == StoreOperation.Insert);
        Assert.Contains(writes, w => w.Table == "universe_member" && w.Operation == StoreOperation.Update);
        Assert.Contains(writes, w => w.Table == "universe_snapshot" && w.Operation == StoreOperation.Insert);
        Assert.DoesNotContain(writes, w => w.Table == "set");
        Assert.DoesNotContain(writes, w => w.Table == "universe_snapshot" && w.Operation == StoreOperation.Update);
    }

    [Fact]
    public void The_write_scanner_catches_a_delete_against_a_bar_table()
    {
        const string source = """
            public sealed class Tidy
            {
                public void Prune() => Run("DELETE FROM daily_bar WHERE bar_date < @cutoff;");
            }
            """;

        SourceWrite write = Assert.Single(SourceWrites.InSource("proof.cs", source));

        // Bars are append-only. A vendor correction arrives as a new row with a later
        // observed_at, never as an edit to the row that was wrong.
        Assert.True(write.IsDelete);
        Assert.Equal("daily_bar", write.Table);
    }

    [Fact]
    public void The_write_scanner_ignores_a_statement_described_in_a_comment()
    {
        const string source = """
            public sealed class Reader
            {
                // Reads take the latest observed_at, and never DELETE FROM daily_bar.
                /* An UPDATE daily_bar SET close = @close would be a defect. */
                public void Read() => Query("SELECT close FROM daily_bar WHERE ticker = @ticker;");
            }
            """;

        Assert.Empty(SourceWrites.InSource("proof.cs", source));
    }

    [Fact]
    public void Comment_stripping_leaves_strings_and_line_numbers_alone()
    {
        const string source = """
            var a = "http://not-a-comment/x";  // trailing
            var b = @"C:\keep\this";
            var c = 1;
            """;

        string stripped = CSharpSource.WithoutComments(source);

        Assert.Contains("http://not-a-comment/x", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing", stripped, StringComparison.Ordinal);
        Assert.Equal(source.Count(ch => ch == '\n'), stripped.Count(ch => ch == '\n'));
        Assert.Equal(source.Length, stripped.Length);
    }

    [Fact]
    public void A_citation_is_normalised_the_same_way_from_a_document_and_from_code()
    {
        // The same name, as it appears in HTML, in markdown with a trailing stop, and wrapped
        // across a line. One parser covers all three or the corpus needs three conventions.
        Assert.Equal(
            "Long and short are never pooled into one figure",
            Corpus.Normalise("Long and short are never pooled into one figure."));

        Assert.Equal(
            "Long and short are never pooled into one figure",
            Corpus.Normalise("Long and short are never\n  pooled into one figure"));

        Assert.Equal(
            "Long and short are never pooled into one figure",
            Corpus.Normalise("<b>Long and short</b> are never pooled into one figure"));
    }

    [Fact]
    public void A_schema_declaration_written_as_prose_still_parses_into_writers()
    {
        IReadOnlyList<Writer> writers = SchemaDeclarations.ParseWriters(
            "Insert LongSetupDetector / ShortSetupDetector, disjoint by `direction` "
            + "· Update SetupCapper (`capped_out`, `rank`) · Update Setup inspector (`agreement`)");

        Assert.Equal(4, writers.Count);
        Assert.Contains(writers, w => w.Operation == StoreOperation.Insert && w.Component == "LongSetupDetector");
        Assert.Contains(writers, w => w.Operation == StoreOperation.Insert && w.Component == "ShortSetupDetector");
        Assert.Contains(writers, w => w.Operation == StoreOperation.Update && w.Component == "SetupCapper");
        Assert.Contains(writers, w => w.Operation == StoreOperation.Update && w.Component == "Setup inspector");
        Assert.All(writers, w => Assert.True(w.Resolved));
    }

    [Fact]
    public void A_read_declaration_is_not_a_write()
    {
        IReadOnlyList<Writer> writers = SchemaDeclarations.ParseWriters(
            "Insert SignalAdmissionTest · Read SignalVectorizer, ContextPacker, SignalBackfiller");

        Writer only = Assert.Single(writers);
        Assert.Equal(StoreOperation.Insert, only.Operation);
        Assert.Equal("SignalAdmissionTest", only.Component);
    }

    [Fact]
    public void A_writer_the_component_catalogue_does_not_name_is_reported_unresolved_rather_than_guessed_at()
    {
        Writer only = Assert.Single(SchemaDeclarations.ParseWriters("Insert SomethingNobodyDeclared"));

        Assert.False(only.Resolved);
        Assert.Equal("SomethingNobodyDeclared", only.Component);
    }

    [Fact]
    public void A_table_located_by_heading_text_is_found_wherever_it_sits()
    {
        const string html = """
            <h2 id="first">Something else</h2>
            <table><tr><th>a</th></tr><tr><td>ignored</td></tr></table>
            <h2 id="wanted">The table wanted <span class="pill">label</span></h2>
            <table>
            <tr><th>Name</th><th>Value</th></tr>
            <tr><td>alpha</td><td>1</td></tr>
            <tr><td>beta</td><td>2</td></tr>
            </table>
            """;

        // Cited as "The table wanted", with the nested label excluded, which is how every
        // cross-document reference in this corpus names a heading.
        IReadOnlyList<IReadOnlyList<string>> rows = HtmlTable.BodyRowsUnder(html, "The table wanted label");

        Assert.Equal(2, rows.Count);
        Assert.Equal("alpha", rows[0][0]);
        Assert.Equal("2", rows[1][1]);
    }

    private static (string Type, string Table, StoreOperation Operation) Shape(SourceWrite write) =>
        (write.Type, write.Table, write.Operation);
    // ---- out of scope names the checkpoint that ends it ----------------------------------

    /// <summary>The plan as these proofs pretend it reads: 1.6 has landed, 2.6 has not, 9.9 does not exist.</summary>
    private static bool Scheduled(string checkpoint) => checkpoint is "1.6" or "2.6";

    private static bool Landed(string checkpoint) => checkpoint is "1.6";

    private static IReadOnlyList<string> Problems(params ArchitectureConformanceCheck.Claim[] claims) =>
        ArchitectureConformanceCheck.OutOfScopeProblems(claims, Scheduled, Landed);

    [Fact]
    public void An_out_of_scope_claim_closing_at_a_checkpoint_still_ahead_is_accepted()
    {
        Assert.Empty(Problems(
            ArchitectureConformanceCheck.Claim.OutOfScope("Component catalogue", "ScanEngine", "2.6")));
    }

    [Fact]
    public void An_out_of_scope_claim_with_no_checkpoint_is_caught()
    {
        // The failure mode the checkpoint exists to prevent: a claim that rests out of scope
        // forever, indistinguishable from one nobody got to.
        var orphan = new ArchitectureConformanceCheck.Claim(
            "Component catalogue", "ScanEngine", ArchitectureConformanceCheck.Deferred, "later", Closes: null);

        string problem = Assert.Single(Problems(orphan));
        Assert.Contains("names no checkpoint", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_scope_claim_naming_a_checkpoint_the_plan_does_not_have_is_caught()
    {
        string problem = Assert.Single(Problems(
            ArchitectureConformanceCheck.Claim.OutOfScope("The limits", "Risk per trade", "9.9")));

        Assert.Contains("BUILD_PLAN.md does not have", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_scope_claim_naming_a_checkpoint_that_has_landed_is_caught()
    {
        // The one the count is for. 1.6 is in PROGRESS, so a claim still deferred to it is a
        // claim that checkpoint shipped without bringing into scope, and nothing said so at the
        // time.
        string problem = Assert.Single(Problems(
            ArchitectureConformanceCheck.Claim.OutOfScope("Failure behaviour", "Unprocessed corporate action", "1.6")));

        Assert.Contains("already landed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_claim_that_is_not_out_of_scope_is_not_asked_for_a_checkpoint()
    {
        // A passing claim carries no checkpoint and must not be reported for it, or every green
        // run would be full of noise about claims that are already settled.
        Assert.Empty(Problems(
            ArchitectureConformanceCheck.Claim.Passed("Component catalogue", "RunLogger", "declared and registered"),
            ArchitectureConformanceCheck.Claim.NotExamined("The limits", "Open at once", "nothing reads this row yet")));
    }

    // ---- coverage-reported ----------------------------------------------------------------
    //
    // The check that says the other checks were there. Its own failure is the quietest one in
    // the corpus, so it is the one most worth being able to break on purpose: every case below
    // is a way a check can stop running while every run stays green.

    private static IReadOnlyList<string> RosterProblems(
        IReadOnlyList<CoverageReportedCheck.RosterRow> roster,
        IReadOnlyDictionary<string, string> implemented,
        IReadOnlyList<string> invoked,
        IReadOnlyList<string>? steps = null,
        IReadOnlySet<string>? reporting = null) =>
        CoverageReportedCheck.Problems(
            roster,
            implemented,
            reporting ?? implemented.Keys.ToHashSet(StringComparer.Ordinal),
            invoked,
            steps ?? [.. invoked.Select(i => "check-" + i)],
            Scheduled,
            Landed,
            "runs-on: [windows-latest, macos-latest]");

    private static CoverageReportedCheck.RosterRow Live(string name) =>
        new(name, CoverageReportedCheck.EveryRun);

    [Fact]
    public void A_roster_whose_rows_are_all_implemented_and_invoked_is_accepted()
    {
        Assert.Empty(RosterProblems(
            [Live("clock-usage"), new("point-in-time", "2.6"), new("two-platform", CoverageReportedCheck.TheMatrix)],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            ["clock-usage"]));
    }

    [Fact]
    public void A_declared_check_that_nothing_implements_is_caught()
    {
        // What coverage-reported itself was between 1.1 and 1.12: a row in the table, a paragraph
        // arguing for it, and no code anywhere.
        string problem = Assert.Single(RosterProblems(
            [Live("coverage-reported")],
            new Dictionary<string, string>(StringComparer.Ordinal),
            []));

        Assert.Contains("no test carries", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ci_step_invoking_a_check_that_does_not_exist_is_caught()
    {
        // The silent pass itself. dotnet test exits zero when the filter matches nothing, so this
        // step would run no test and report success for as long as nobody looked.
        var problems = RosterProblems(
            [Live("clock-usage")],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            ["clock-usage", "clock-usge"]);

        Assert.Contains(problems, p => p.Contains("would match no test", StringComparison.Ordinal));
    }

    [Fact]
    public void A_declared_check_that_ci_never_invokes_is_caught()
    {
        string problem = Assert.Single(RosterProblems(
            [Live("clock-usage")],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            []));

        Assert.Contains("invokes no such check", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_implemented_check_the_roster_does_not_declare_is_caught()
    {
        // The direction the corpus already argued for at 1.7, kept assertable rather than swept
        // once: a check that runs and is not declared is a property nobody wrote down.
        string problem = Assert.Single(RosterProblems(
            [],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            ["clock-usage"]));

        Assert.Contains("does not declare it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_states_no_coverage_is_caught()
    {
        string problem = Assert.Single(RosterProblems(
            [Live("clock-usage")],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            ["clock-usage"],
            reporting: new HashSet<string>(StringComparer.Ordinal)));

        Assert.Contains("does not construct CheckCoverage", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_deferred_to_a_checkpoint_that_has_landed_is_caught()
    {
        // 1.6 is in PROGRESS, so a check still waiting on it is one that checkpoint shipped
        // without building, and nothing said so at the time.
        string problem = Assert.Single(RosterProblems(
            [new("point-in-time", "1.6")],
            new Dictionary<string, string>(StringComparer.Ordinal),
            []));

        Assert.Contains("already records it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_deferred_to_a_checkpoint_the_plan_does_not_have_is_caught()
    {
        string problem = Assert.Single(RosterProblems(
            [new("point-in-time", "9.9")],
            new Dictionary<string, string>(StringComparer.Ordinal),
            []));

        Assert.Contains("no such checkpoint", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ci_step_whose_name_and_filter_disagree_is_caught()
    {
        var problems = RosterProblems(
            [Live("clock-usage")],
            new Dictionary<string, string> { ["clock-usage"] = "ClockUsageCheck.cs" },
            ["clock-usage"],
            steps: ["check-clock-usage", "check-store-portability"]);

        Assert.Contains(problems, p => p.Contains("have diverged", StringComparison.Ordinal));
    }

    // ---- every table in the document is placed -------------------------------------------

    [Fact]
    public void A_procedure_step_naming_the_same_stores_in_both_documents_is_accepted()
    {
        // Written differently on purpose. The two documents address different readers, so the
        // wording is allowed to differ and the substance is not.
        ArchitectureConformanceCheck.Claim claim = ArchitectureConformanceCheck.ProcedureStepClaim(
            "2",
            "A row count for every table the database holds, derived from the schema.",
            "Row counts for every table in the database, taken from the schema rather than from a list here.");

        Assert.Equal(ArchitectureConformanceCheck.Pass, claim.Verdict);
    }

    [Fact]
    public void A_procedure_step_naming_stores_the_other_document_does_not_is_caught()
    {
        // The 1.11 defect exactly, as it still stood in ARCHITECTURE.html at the 1.12 review:
        // the rehearsal corrected RUNBOOK.md and left the design source of truth telling an
        // operator to count five tables that do not exist, get zero, and report success.
        ArchitectureConformanceCheck.Claim claim = ArchitectureConformanceCheck.ProcedureStepClaim(
            "2",
            "Row counts for setup, setup_signal, forward_return, trade and variant.",
            "A row count for every table the store holds, derived from the schema.");

        Assert.Equal(ArchitectureConformanceCheck.Fail, claim.Verdict);
        Assert.Contains("disagreeing about what an operator does", claim.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_procedure_step_neither_document_names_anything_in_is_reported_as_unexamined()
    {
        // The seventh shape, as a case. Both operands empty is not agreement: it is a comparison
        // that did not happen, and returning Pass for it is how ten claims sat at a floor of ten
        // over a comparator that had compared nothing since 1.12 took the last table name out of
        // both documents in one commit.
        ArchitectureConformanceCheck.Claim claim = ArchitectureConformanceCheck.ProcedureStepClaim(
            "1",
            "Confirm no nightly or replay job is mid-run.",
            "Confirm no stage is mid-run.");

        Assert.Equal(ArchitectureConformanceCheck.Unexamined, claim.Verdict);
        Assert.Contains("compared on nothing", claim.Detail, StringComparison.Ordinal);
    }

    // ---- a confirmed value carries where it came from ------------------------------------
    //
    // CONFIRMED is the only tier whose producer is a person, so it is the only one that can lose
    // its provenance without a program noticing. These are the ways it goes wrong.

    private static FixtureReplayCheck.Expectation Confirmed(
        string id, string producedBy, string? note = null, string? voided = null) =>
        new(id, FixtureReplayCheck.Confirmed, "1.00", "1.12", producedBy, note, voided);

    [Fact]
    public void A_confirmed_value_naming_its_platform_and_the_date_it_was_read_is_accepted()
    {
        Assert.Empty(FixtureReplayCheck.WithoutProvenance(
            [Confirmed("indicators.LITE.ema50", "read from TradingView on 2026-08-26")]));
    }

    [Fact]
    public void A_confirmed_value_naming_no_platform_is_caught()
    {
        // "Checked against a chart" is what a confirmed value decays into a year later, when
        // nobody can say which chart or when, and it is then indistinguishable from a derived one.
        string problem = Assert.Single(FixtureReplayCheck.WithoutProvenance(
            [Confirmed("indicators.LITE.ema50", "checked against a chart and it agreed")]));

        Assert.Equal("indicators.LITE.ema50", problem);
    }

    [Fact]
    public void A_confirmed_value_naming_no_date_is_caught()
    {
        Assert.Single(FixtureReplayCheck.WithoutProvenance(
            [Confirmed("indicators.LITE.ema50", "read from TradingView")]));
    }

    [Fact]
    public void A_derived_value_is_not_asked_for_a_platform()
    {
        // Only CONFIRMED carries this burden. Asking it of every tier would make every green run
        // noisy about rows whose producer is a program and is already named.
        Assert.Empty(FixtureReplayCheck.WithoutProvenance(
        [
            new("indicators.LITE.ema50", FixtureReplayCheck.Derived, "1.00", "1.6",
                "tools/derive-indicators.py, over the fixture's own bars", null),
        ]));
    }

    [Fact]
    public void A_confirmed_daily_range_that_states_no_definition_is_caught()
    {
        // The comparison most likely to be made against something that is not the same quantity.
        string problem = Assert.Single(FixtureReplayCheck.WithoutARangeDefinition(
            [Confirmed("indicators.PAYO.adr20", "read from TradingView on 2026-08-26")]));

        Assert.Equal("indicators.PAYO.adr20", problem);
    }

    [Fact]
    public void A_confirmed_daily_range_recorded_void_is_accepted()
    {
        // Void rather than agreement: the platform reports a different quantity under the same
        // name, so somebody looked and no comparison was possible. That is worth keeping.
        Assert.Empty(FixtureReplayCheck.WithoutARangeDefinition(
        [
            Confirmed("indicators.PAYO.adr20", "read from TradingView on 2026-08-26",
                voided: "the platform computes sma(high,20)/sma(low,20)-1, which is not the mean of (high-low)/close"),
        ]));
    }

    // ---- every table in the document is placed -----------------------------------------------
    //
    // The proof the placement pass did not have. It replaced a hardcoded list of five tables with
    // a hardcoded list of seven claim tables plus ten exempt ones, and the whole improvement rests
    // on one property: a table on neither list reports unexamined and names itself. Without this
    // proof that is a partition by assertion, and a partition that has quietly become a filter
    // looks identical from every angle except this one.

    /// <summary>
    /// A document holding one table of each kind: read for claims, exempt by reason, deferred to a
    /// checkpoint, and placed by nobody. The last is the one the proof exists for.
    /// </summary>
    private const string FourKindsOfTable = """
        <h2>Component catalogue</h2>
        <table><tr><th>Name</th></tr><tr><td>RunLogger</td></tr></table>
        <h2>Vocabulary</h2>
        <table><tr><th>Term</th></tr><tr><td>setup</td></tr></table>
        <h2>What the pack contains</h2>
        <table><tr><th>Part</th></tr><tr><td>signals</td></tr></table>
        <h2>A table nobody placed</h2>
        <table><tr><th>Thing</th></tr><tr><td>alpha</td></tr></table>
        """;

    private static ArchitectureConformanceCheck.Claim Placement(string heading) =>
        Assert.Single(ArchitectureConformanceCheck.TablePlacementClaims(FourKindsOfTable),
            c => c.Subject == heading);

    [Fact]
    public void A_table_on_neither_list_is_unexamined_and_names_itself()
    {
        // The one that has to hold. A table appearing in neither ClaimTables nor TablesWithoutClaims
        // is not silently absent from the count, which is the state the report cannot show; it is
        // unexamined, which is the verdict that turns the phase report red.
        ArchitectureConformanceCheck.Claim orphan = Placement("A table nobody placed");

        Assert.Equal(ArchitectureConformanceCheck.Unexamined, orphan.Verdict);
        Assert.Equal("Tables in the document", orphan.Table);
        Assert.Contains("nobody placed", orphan.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_table_in_the_document_gets_exactly_one_claim()
    {
        // Absent is worse than unexamined, so the count of claims has to equal the count of tables.
        // A parser that stops early shortens this list rather than failing, and a placement pass
        // over three of four tables reads as complete coverage of the document.
        IReadOnlyList<ArchitectureConformanceCheck.Claim> claims =
            ArchitectureConformanceCheck.TablePlacementClaims(FourKindsOfTable);

        Assert.Equal(4, claims.Count);
        Assert.Equal(4, claims.Select(c => c.Subject).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_table_the_check_reads_for_claims_is_passed()
    {
        ArchitectureConformanceCheck.Claim read = Placement("Component catalogue");

        Assert.Equal(ArchitectureConformanceCheck.Pass, read.Verdict);
        Assert.Contains("read for claims", read.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_exempt_by_a_written_reason_is_passed_with_that_reason()
    {
        ArchitectureConformanceCheck.Claim exempt = Placement("Vocabulary");

        Assert.Equal(ArchitectureConformanceCheck.Pass, exempt.Verdict);
        Assert.Equal("definitions of terms, not a statement about the code", exempt.Detail);
    }

    [Fact]
    public void A_table_a_later_checkpoint_builds_is_out_of_scope_and_carries_that_checkpoint()
    {
        // And carrying it is what subjects it to the same rule a deferred row obeys: the plan has
        // to have the checkpoint and the record must not yet carry it. Placement claims go through
        // OutOfScopeProblems with every other claim, so a table exempted to a checkpoint that has
        // landed is caught there rather than resting exempt forever.
        ArchitectureConformanceCheck.Claim deferred = Placement("What the pack contains");

        Assert.Equal(ArchitectureConformanceCheck.Deferred, deferred.Verdict);
        Assert.Equal("6.4", deferred.Closes);

        string problem = Assert.Single(Problems(
            ArchitectureConformanceCheck.Claim.OutOfScope("Tables in the document", "What the pack contains", "1.6")));
        Assert.Contains("already landed", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_with_no_heading_above_it_is_unexamined_rather_than_dropped()
    {
        // The degenerate document. A table before the first heading has no heading text to place
        // it by, and the answer must be a verdict rather than a silent skip.
        ArchitectureConformanceCheck.Claim first = Assert.Single(
            ArchitectureConformanceCheck.TablePlacementClaims(
                "<table><tr><th>a</th></tr><tr><td>1</td></tr></table>"));

        Assert.Equal(ArchitectureConformanceCheck.Unexamined, first.Verdict);
    }

    // The examined floor. The narrowing reproduced here is the one that got through the 1.12
    // review by hand: BarAppendOnlyCheck.BarTables cut from three tables to one left the suite
    // passing, the phase report GREEN, and one summary number nobody compares eight lower.

    // The scopes bar-append-only actually names, in the shape the defect had: three bar tables
    // carrying the property, and forty-seven source files whose count is a fact about the corpus.
    [Fact]
    public void A_check_that_says_nothing_about_its_source_scans_fails()
    {
        // The half that makes the sweep complete. A check added later is asked the question rather
        // than being assumed to have none, because the four assertions that outlived their subjects
        // were all in things nobody had thought to ask it of.
        string missing = Assert.Single(CheckCoverage.ScanProblems(
            "a-new-check", [], noSourceScan: null, Backs, Jobs));

        Assert.Contains("declares neither a source-scan assertion nor NoSourceScan", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_check_that_declares_both_a_scan_and_no_source_scan_fails()
    {
        string both = Assert.Single(CheckCoverage.ScanProblems(
            "a-new-check",
            [new CheckCoverage.ScanAssertion("something read from the source", CheckCoverage.Backing.None("nothing yet"))],
            noSourceScan: "it reads no source",
            Backs,
            Jobs));

        Assert.Contains("also declares NoSourceScan", both, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backing_naming_a_test_that_does_not_exist_fails()
    {
        // The direction that matters. A backing which has gone stale reads as covered, so it is
        // worse than none: nothing distinguishes it from one that still holds.
        string stale = Assert.Single(CheckCoverage.ScanProblems(
            "a-new-check",
            [new CheckCoverage.ScanAssertion(
                "no delete against a bar table",
                CheckCoverage.Backing.Test("SomeTests.A_test_that_was_renamed_away", "it used to run this"))],
            noSourceScan: null,
            Backs,
            Jobs));

        Assert.Contains("no test by that name exists", stale, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backing_naming_a_job_the_workflow_does_not_have_fails()
    {
        string stale = Assert.Single(CheckCoverage.ScanProblems(
            "a-new-check",
            [new CheckCoverage.ScanAssertion(
                "path literals match the on-disk name",
                CheckCoverage.Backing.Runner("linux", "it used to run there"))],
            noSourceScan: null,
            Backs,
            Jobs));

        Assert.Contains("the workflow has no job by that name", stale, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unbacked_scan_is_reported_and_does_not_fail()
    {
        // Deliberate, and the part most likely to be read as a hole. The fix for an unbacked scan
        // is a behavioural test per scan, which is scheduled work; a rule that blocked on it would
        // be answered by writing Backing.None reasons that say nothing, and the list would still
        // be empty while nothing exercised anything.
        Assert.Empty(CheckCoverage.ScanProblems(
            "a-new-check",
            [new CheckCoverage.ScanAssertion(
                "every write belongs to its declared writer",
                CheckCoverage.Backing.None("nothing runs the pipeline and asks who wrote each row"))],
            noSourceScan: null,
            Backs,
            Jobs));
    }

    [Fact]
    public void The_two_name_sets_a_backing_resolves_against_are_populated()
    {
        // Stated in advance rather than left self-validating. Both sets are read from outside the
        // source text, and either one coming back empty would make every backing resolve to
        // nothing while every check kept passing, which is this mechanism's own failure mode.
        Assert.True(CheckCoverage.TestNames.Count >= 250,
            $"the assembly scan found {CheckCoverage.TestNames.Count} tests. It has held at least 250 since 2.10.");
        Assert.Contains("StoreTests.The_open_connection_reports_the_four_pragmas_from_schema", CheckCoverage.TestNames);
        // Equality rather than a floor, deliberately. A backing naming a job the workflow does not
        // have has to fail, so the set this resolves against is the workflow's whole job list and a
        // job added or renamed moves this line. That is the point: a job silently disappearing would
        // leave a Backing.Runner reading as covered while nothing exercised it.
        Assert.Equal(
            ["rehearsal", "slot-diagnostics", "slot-diagnostics-inverted", "suite"],
            CheckCoverage.WorkflowJobs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_sweep_finds_the_files_that_read_the_shipped_source()
    {
        // Handed an empty map of implemented checks, every scanning file is one nobody records.
        // That is the detection working; against the real map the same call is what leaves the
        // three scans written in ordinary tests on the list rather than out of sight.
        (IReadOnlyList<string> scanning, IReadOnlyList<string> outside) =
            CoverageReportedCheck.SourceScanningFiles(new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.True(scanning.Count >= 12,
            $"the sweep found {scanning.Count} files reading the shipped source. It has held at least twelve "
            + "since 2.11, so the pattern list stopped matching.");
        Assert.Equal(scanning.Count, outside.Count);
        Assert.Contains("src/PullbackStrategyLab.Tests/Checks/BarAppendOnlyCheck.cs", outside);
    }

    private static bool Backs(string test) => CheckCoverage.TestNames.Contains(test);

    private static bool Jobs(string job) => CheckCoverage.WorkflowJobs.Contains(job);

    private static CheckCoverage.Scope Property(string what, int count) => new(what, count, IsContext: false);

    private static CheckCoverage.Scope ContextScope(string what, int count) => new(what, count, IsContext: true);

    private static Dictionary<string, CheckCoverage.CheckFloors> Floors(
        string check,
        Dictionary<string, int> scopes,
        params string[] context) =>
        new(StringComparer.Ordinal) { [check] = new CheckCoverage.CheckFloors(scopes, context) };

    [Fact]
    public void The_examined_floor_catches_a_check_narrowing_below_it()
    {
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "bar-append-only",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["bar tables named by the check"] = 3 },
            "source files scanned");

        string narrowed = Assert.Single(CheckCoverage.Shortfalls(
            "bar-append-only",
            [Property("bar tables named by the check", 1), ContextScope("source files scanned", 47)],
            recorded));

        Assert.Contains("bar tables named by the check", narrowed, StringComparison.Ordinal);
        Assert.Contains("examined 1", narrowed, StringComparison.Ordinal);
        Assert.Contains("floor of 3", narrowed, StringComparison.Ordinal);
    }

    [Fact]
    public void Corpus_growth_no_longer_pays_for_a_narrowing()
    {
        // The falsification the phase 1 sign-off ran, as a permanent proof. Under one floor per
        // check this passed: bar-append-only cut from three bar tables to one, with five ordinary
        // new files added, examined 55 against a floor of 54, and the phase report went GREEN with
        // the total higher than the committed run. Per scope the growth cannot reach the property.
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "bar-append-only",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["bar tables named by the check"] = 3 },
            "source files scanned");

        Assert.NotEmpty(CheckCoverage.Shortfalls(
            "bar-append-only",
            [Property("bar tables named by the check", 1), ContextScope("source files scanned", 52)],
            recorded));
    }

    [Fact]
    public void Corpus_shrinkage_no_longer_raises_a_false_alarm()
    {
        // The other half, and it fired first in practice: deleting two string literals from one
        // test file dropped path-casing below its floor and turned it red for a reason that has
        // nothing to do with path casing. A guard that cries wolf gets suppressed, and a
        // suppressed guard is a dead one arrived at slowly.
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "path-casing",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["paths compared against the on-disk name"] = 27 },
            "string literals read");

        Assert.Empty(CheckCoverage.Shortfalls(
            "path-casing",
            [Property("paths compared against the on-disk name", 27), ContextScope("string literals read", 2410)],
            recorded));
    }

    [Fact]
    public void The_examined_floor_is_a_floor_rather_than_an_equality()
    {
        // Counts differ between platforms and grow with the corpus. A baseline demanding equality
        // would go red on a file being added.
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "path-casing",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["paths compared against the on-disk name"] = 27 });

        Assert.Empty(CheckCoverage.Shortfalls(
            "path-casing", [Property("paths compared against the on-disk name", 27)], recorded));
        Assert.Empty(CheckCoverage.Shortfalls(
            "path-casing", [Property("paths compared against the on-disk name", 28)], recorded));
        Assert.NotEmpty(CheckCoverage.Shortfalls(
            "path-casing", [Property("paths compared against the on-disk name", 26)], recorded));
    }

    [Fact]
    public void A_scope_that_stops_being_reported_is_caught()
    {
        // The direction a single total could never see. A scope renamed or dropped takes its
        // property with it, and the check keeps passing on whatever else it still counts.
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "bar-append-only",
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["bar tables named by the check"] = 3,
                ["writes found in the shipped source"] = 0,
            });

        string gone = Assert.Single(CheckCoverage.Shortfalls(
            "bar-append-only", [Property("bar tables named by the check", 3)], recorded));

        Assert.Contains("writes found in the shipped source", gone, StringComparison.Ordinal);
        Assert.Contains("narrowed to nothing", gone, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scope_reclassified_as_context_is_caught()
    {
        // Otherwise the repair defeats itself: moving a scope from Examined to Context would remove
        // its floor with a one-word diff nobody reads as a guard being deleted.
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "bar-append-only",
            new Dictionary<string, int>(StringComparer.Ordinal) { ["bar tables named by the check"] = 3 });

        IReadOnlyList<string> problems = CheckCoverage.Shortfalls(
            "bar-append-only", [ContextScope("bar tables named by the check", 3)], recorded);

        Assert.Contains(problems, p => p.Contains("is recorded as context by the check", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scope_with_no_floor_fails_rather_than_being_waved_through()
    {
        Dictionary<string, CheckCoverage.CheckFloors> recorded = Floors(
            "bar-append-only", new Dictionary<string, int>(StringComparer.Ordinal));

        string missing = Assert.Single(CheckCoverage.Shortfalls(
            "bar-append-only", [Property("a scope nobody floored", 4)], recorded));

        Assert.Contains("a scope nobody floored", missing, StringComparison.Ordinal);
        Assert.Contains("has no floor", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void An_admission_covering_nothing_still_counts_as_an_admission()
    {
        // PathCasingCheck records its no-work branch as NotExamined(..., 0, ...). Summing the
        // counts made that zero, so the record carried an unexamined line and the report said
        // "unexamined 0" on the same page. An admission that counts as silence is the failure the
        // split between unexamined and out of scope exists to prevent, reached from inside.
        var coverage = new CheckCoverage("a-proof", new NullOutput());
        coverage.NotExamined("paths compared against the on-disk name", 0, "nothing names a repository path yet");

        Assert.Equal(1, coverage.TotalUnexamined);
    }

    private sealed class NullOutput : Xunit.Abstractions.ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }

    [Fact]
    public void A_check_with_no_floor_recorded_fails_rather_than_being_waved_through()
    {
        // The obvious way to lose the whole mechanism is to add a check and no floor for it. That
        // has to fail, or the guard covers whatever was there when it was written and nothing
        // added since, which is the same silent narrowing one level up.
        string missing = Assert.Single(CheckCoverage.Shortfalls(
            "a-new-check",
            [Property("things it looked at", 9)],
            new Dictionary<string, CheckCoverage.CheckFloors>(StringComparer.Ordinal)));

        Assert.Contains("a-new-check", missing, StringComparison.Ordinal);
        Assert.Contains("fixtures/checks-baseline.json", missing, StringComparison.Ordinal);
        Assert.Contains("\"things it looked at\": 9", missing, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_check_the_roster_declares_has_a_floor_on_disk()
    {
        // Against the real baseline rather than a written one, because the failure this catches is
        // a file drifting out of step with the roster rather than a function being wrong.
        string[] missing =
        [
            .. CoverageReportedCheck.Roster()
                .Where(r => r.Runs == CoverageReportedCheck.EveryRun)
                .Select(r => r.Name)
                .Where(name => !CheckCoverage.Baseline.ContainsKey(name))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(missing.Length == 0,
            $"{missing.Length} check(s) the roster declares have no floor in fixtures/checks-baseline.json: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void A_floor_naming_a_check_that_does_not_run_is_caught_too()
    {
        // The other direction. A stale entry does no harm on its own, and it is how the file stops
        // being readable as the list of what is guarded.
        string[] stale =
        [
            .. CheckCoverage.Baseline.Keys
                .Where(name => !CoverageReportedCheck.Roster().Any(r => r.Name == name))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(stale.Length == 0,
            $"{stale.Length} floor(s) in fixtures/checks-baseline.json name no check in CLAUDE.md's roster: "
            + string.Join(", ", stale));
    }

    // Done condition seven, per checkpoint. The blind spot being closed is that one DERIVED
    // expectation anywhere satisfied the condition for every checkpoint in the fixture.

    private static readonly ArchitectureConformanceCheck.Obligation Open =
        new("1.1", "2.1", "the derived expectations those checkpoints predate");

    private static FixtureReplayCheck.Expectation Expectation(string id, string tier, string checkpoint) =>
        new(id, tier, "1", checkpoint, "a proof, not a run", null);

    /// <summary>
    /// A voided expectation verifies nothing, whatever tier it carries.
    ///
    /// `voidedBecause` says the subject no longer exists or can no longer be compared, and the run
    /// records such a row as void rather than as agreement. Counting it as independent would let a
    /// checkpoint satisfy done condition seven with a DERIVED row that compares nothing, which is the
    /// state that condition exists to make visible.
    ///
    /// Proved by hand rather than by voiding a real expectation, so the proof is permanent and the
    /// fixture is never broken to produce it.
    /// </summary>
    [Fact]
    public void A_voided_expectation_does_not_count_toward_a_checkpoint_being_independently_covered()
    {
        FixtureReplayCheck.Expectation live = Expectation("a.one", FixtureReplayCheck.Derived, "2.9");
        FixtureReplayCheck.Expectation voided = live with
        {
            Id = "a.two",
            VoidedBecause = "the platform stopped publishing the figure",
        };

        FixtureReplayCheck.CheckpointTier both = Assert.Single(
            FixtureReplayCheck.ByCheckpoint([live, voided]));

        Assert.Equal(2, both.Total);
        Assert.Equal(1, both.Independent);

        FixtureReplayCheck.CheckpointTier alone = Assert.Single(
            FixtureReplayCheck.ByCheckpoint([voided]));

        Assert.Equal(1, alone.Total);
        Assert.Equal(0, alone.Independent);
    }

    [Fact]
    public void One_derived_expectation_does_not_satisfy_the_condition_for_another_checkpoint()
    {
        FixtureReplayCheck.Expectation[] expectations =
        [
            Expectation("a.one", FixtureReplayCheck.Derived, "1.6"),
            Expectation("b.one", FixtureReplayCheck.Frozen, "1.4"),
        ];

        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            expectations, [], [Open], _ => false));

        Assert.Contains("1.4", problem, StringComparison.Ordinal);
        Assert.Contains("nothing permits it", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frozen_only_checkpoint_naming_an_open_obligation_is_permitted()
    {
        Assert.Empty(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("b.one", FixtureReplayCheck.Frozen, "1.4")],
            [new FixtureReplayCheck.Permit("1.4", "1.1", "whole-market counts")],
            [Open],
            _ => false));
    }

    [Fact]
    public void A_permit_naming_an_obligation_that_is_not_in_the_plan_permits_nothing()
    {
        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("b.one", FixtureReplayCheck.Frozen, "1.4")],
            [new FixtureReplayCheck.Permit("1.4", "1.2", "whole-market counts")],
            [Open],
            _ => false));

        Assert.Contains("no row raised there", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permit_whose_obligation_has_fallen_due_permits_nothing()
    {
        // The same rule an out-of-scope architecture claim obeys. An obligation due at a
        // checkpoint the record already carries is one that checkpoint shipped without closing.
        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("b.one", FixtureReplayCheck.Frozen, "1.4")],
            [new FixtureReplayCheck.Permit("1.4", "1.1", "whole-market counts")],
            [Open],
            checkpoint => checkpoint == "2.1"));

        Assert.Contains("PROGRESS already records 2.1", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permit_a_checkpoint_has_outgrown_is_reported_as_spent()
    {
        // Both directions, on the same reasoning writer-ownership is asserted both ways: a permit
        // left behind would quietly re-permit the checkpoint if its independent expectation were
        // ever removed.
        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("b.one", FixtureReplayCheck.Confirmed, "1.4")],
            [new FixtureReplayCheck.Permit("1.4", "1.1", "whole-market counts")],
            [Open],
            _ => false));

        Assert.Contains("permit is spent", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permit_for_a_checkpoint_the_fixture_does_not_reach_is_caught()
    {
        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("a.one", FixtureReplayCheck.Derived, "1.6")],
            [new FixtureReplayCheck.Permit("3.4", "1.1", "a checkpoint with nothing in the fixture")],
            [Open],
            _ => false));

        Assert.Contains("the diff never reaches", problem, StringComparison.Ordinal);
    }

    // ---- a malformed table row, and an ambiguous permit -----------------------------------

    // Both found at 2.1 and both the same shape: a lookup answering a narrower question than the
    // one it was asked, and reporting success. The first was a parser skipping a row that did not
    // fit; the second was a first-match lookup over a column that is not a key.

    private const string RaggedTable = """
        ## A table

        | Raised | Obligation | Due at |
        |---|---|---|
        | 1.1 | a row with all three cells | 2.1 |
        | 1.12 | a row missing its last cell |
        | 1.11 | another complete row | the move |
        """;

    private const string RectangularTable = """
        ## A table

        | Raised | Obligation | Due at |
        |---|---|---|
        | 1.1 | a row with all three cells | 2.1 |
        | 1.11 | another complete row | the move |
        """;

    [Fact]
    public void A_body_row_narrower_than_its_header_is_rejected_rather_than_dropped()
    {
        // The row that found this was BUILD_PLAN's own, carrying the per-scope floor obligation.
        // Two cells where the rest carry three, dropped by a `row.Count >= 3` guard, so the
        // obligation driving checkpoint 2.1 was absent from Schedule.Obligations entirely.
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => MarkdownTable.BodyRowsAfter(RaggedTable, "## A table"));

        Assert.Contains("header 3 cells wide", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("body row 2 cells wide", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1.12", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rectangular_table_is_read_whole()
    {
        // The other direction, so the rejection above is not simply a parser that stopped working.
        IReadOnlyList<IReadOnlyList<string>> rows = MarkdownTable.BodyRowsAfter(RectangularTable, "## A table");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(3, row.Count));
        Assert.Equal("the move", rows[1][^1]);
    }

    [Fact]
    public void A_wider_body_row_is_caught_too()
    {
        // Width, not a minimum. A guard written as "at least three" accepts a row that gained a
        // cell, and the last cell is the one every obligation's due point is read from.
        string? problem = MarkdownTable.RaggedRowProblem(
            [["1.1", "what", "2.1"], ["1.6", "what", "extra", "2.11"]], 3, "## A table");

        Assert.NotNull(problem);
        Assert.Contains("body row 4 cells wide", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_permit_naming_a_checkpoint_that_raised_two_obligations_is_ambiguous()
    {
        // Two rows raised at one checkpoint is legitimate: the table is keyed by who raised an
        // obligation, not by the obligation. The permit is what is wrong, because it uses `Raised`
        // as a key. BUILD_PLAN carries two rows raised at 1.12 today.
        ArchitectureConformanceCheck.Obligation[] two =
        [
            new("1.12", "2.2", "the out-of-scope naming rule"),
            new("1.12", "2.1", "the examined floor per scope"),
        ];

        string problem = Assert.Single(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("b.one", FixtureReplayCheck.Frozen, "1.4")],
            [new FixtureReplayCheck.Permit("1.4", "1.12", "whole-market counts")],
            two,
            _ => false));

        Assert.Contains("2 rows raised there", problem, StringComparison.Ordinal);
        Assert.Contains("not stated", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_obligations_raised_at_one_checkpoint_are_fine_when_no_permit_names_it()
    {
        // The table is not the defect, so nothing fires when the ambiguity is never consulted.
        Assert.Empty(FixtureReplayCheck.DoneConditionSevenProblems(
            [Expectation("a.one", FixtureReplayCheck.Derived, "1.6")],
            [],
            [new ArchitectureConformanceCheck.Obligation("1.12", "2.2", "one"),
             new ArchitectureConformanceCheck.Obligation("1.12", "2.1", "another")],
            _ => false));
    }

    // ---- an out-of-scope coverage item names what ends it -----------------------------------

    // The obligation raised at 1.12 and due at 2.2. An out-of-scope architecture claim has always
    // had to name the checkpoint that ends it; a coverage item carried free prose and nothing read
    // it, so its count read as permanent rather than as one that falls.

    private static CheckCoverage.Deferred Defer(string what, CheckCoverage.OutOfScopeReason reason) =>
        new(what, 1, reason);

    [Fact]
    public void A_deferral_to_a_checkpoint_that_has_landed_is_caught()
    {
        string problem = Assert.Single(CheckCoverage.DeferralProblems(
            "a-check",
            [Defer("a table nobody created", CheckCoverage.OutOfScopeReason.UntilCheckpoint("1.3", "why"))],
            _ => true,
            checkpoint => checkpoint == "1.3"));

        Assert.Contains("already records 1.3", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deferral_to_a_checkpoint_nobody_scheduled_is_caught()
    {
        string problem = Assert.Single(CheckCoverage.DeferralProblems(
            "a-check",
            [Defer("a table nobody created", CheckCoverage.OutOfScopeReason.UntilCheckpoint("9.9", "why"))],
            _ => false,
            _ => false));

        Assert.Contains("no such checkpoint", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deferral_to_an_open_checkpoint_is_fine()
    {
        Assert.Empty(CheckCoverage.DeferralProblems(
            "a-check",
            [Defer("a table nobody created", CheckCoverage.OutOfScopeReason.UntilCheckpoint("4.2", "why"))],
            _ => true,
            _ => false));
    }

    [Fact]
    public void A_priced_deferral_carries_its_price_and_is_not_asked_for_a_checkpoint()
    {
        // The half of the rule that does not transfer from the claim side. Two of fixture-replay's
        // exemptions close on a purchase rather than on a checkpoint, and read as prose they are
        // indistinguishable while differing by three orders of magnitude in cost.
        var expensive = CheckCoverage.OutOfScopeReason.UntilDecided(
            "1,900 vendor calls and about 130 MB committed for ever", "the whole-market screen");
        var cheap = CheckCoverage.OutOfScopeReason.UntilDecided(
            "one per-ticker vendor call at the next capture", "the floor's rejecting side");

        Assert.Empty(CheckCoverage.DeferralProblems("a-check", [Defer("x", expensive), Defer("y", cheap)], _ => false, _ => true));

        Assert.Contains("1,900", expensive.ToString(), StringComparison.Ordinal);
        Assert.Contains("one per-ticker", cheap.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(expensive.Price, cheap.Price);
    }

    [Fact]
    public void A_by_design_deferral_is_permanent_and_says_so()
    {
        var reason = CheckCoverage.OutOfScopeReason.ByDesign("a dated record is meant to say what was true then");

        Assert.True(reason.IsPermanent);
        Assert.Null(reason.Checkpoint);
        Assert.Null(reason.Price);
        Assert.Empty(CheckCoverage.DeferralProblems("a-check", [Defer("x", reason)], _ => false, _ => true));
    }

    [Fact]
    public void The_three_shapes_are_told_apart_in_the_record()
    {
        // What stops by-design swallowing the rule: the shapes are distinguishable in the record,
        // so the count of permanent exemptions can be read rather than absorbed into one number.
        Assert.StartsWith("closed by 4.2",
            CheckCoverage.OutOfScopeReason.UntilCheckpoint("4.2", "why").ToString(), StringComparison.Ordinal);
        Assert.StartsWith("rests on a decision nobody has taken",
            CheckCoverage.OutOfScopeReason.UntilDecided("a price", "why").ToString(), StringComparison.Ordinal);
        Assert.StartsWith("exempt by design",
            CheckCoverage.OutOfScopeReason.ByDesign("why").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_coverage_reason_re_asks_whether_the_obligation_has_fallen_due()
    {
        // The run is red either way when a permit has expired. What was wrong was the page: the
        // record beside the failure still read "permitted by", because the reason resolved the
        // obligation and stopped rather than asking the question the assertion above it asked.
        var permit = new FixtureReplayCheck.Permit("1.4", "1.1", "whole-market counts");

        Assert.Contains("permitted by the obligation raised at 1.1",
            FixtureReplayCheck.PermitReason([Open], permit, _ => false), StringComparison.Ordinal);

        Assert.Contains("the permission is spent",
            FixtureReplayCheck.PermitReason([Open], permit, checkpoint => checkpoint == "2.1"), StringComparison.Ordinal);
    }
}
