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
}
