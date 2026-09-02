using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The columns SCHEMA.md declares for each table, read as data.
///
/// <b>Nothing read these until 4.6.</b> `writer-ownership` read SCHEMA for writers and the migrations
/// for which stores exist, and the column tables under each heading were read by nothing at all. Five
/// columns were already missing when the 3.7 sign-off measured it, one of them since 2.5, and
/// SCHEMA's own second line said "Complete for phases 1 to 3" with nothing deriving that claim. What
/// closes it is the reconciliation rather than the five repairs, on the grounds the corpus gives for
/// `writer-ownership` running both ways: repairing five leaves the sixth to arrive unnoticed.
///
/// <b>Three shapes of declaration, and all three are explicit.</b> A heading of the form
/// <c>### `table`</c> or <c>#### `table`</c> names the table its column table describes. A section
/// with a prose heading names its table with a <c>Columns of `table`.</c> line. A table that
/// deliberately shares another's shape says so with a <c>Shape of `table`: same as `other`</c> line,
/// optionally followed by <c>, less `a`, `b`</c> or <c>, with `p` in place of `q`</c>.
///
/// <b>The third shape is the one that had to be made exact.</b> `calibration_setup` said "Same shape
/// as `setup`" and was six columns short of it, and that sentence is what a reconciliation would have
/// read as licence to skip the table: a claim of sameness that is false is worse than no claim,
/// because it passes.
/// </summary>
public static partial class SchemaColumns
{
    /// <summary>A heading naming its table directly, at either depth.</summary>
    [GeneratedRegex(@"^#{3,4} `(?<table>[a-z_][a-z0-9_]*)`\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TableHeading();

    /// <summary>Any heading at all, which is what bounds a section.</summary>
    [GeneratedRegex(@"^#{2,4} ", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex AnyHeading();

    /// <summary>A prose-titled section naming the table it describes.</summary>
    [GeneratedRegex(@"Columns of `(?<table>[a-z_][a-z0-9_]*)`\.", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnsOf();

    /// <summary>A table declared as another's shape, with an optional stated difference.</summary>
    [GeneratedRegex(
        @"Shape of `(?<table>[a-z_][a-z0-9_]*)`: same as `(?<other>[a-z_][a-z0-9_]*)`(?<delta>[^.]*)\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex ShapeOf();

    /// <summary>The columns a row of a column table names, which is the first cell.</summary>
    [GeneratedRegex(@"^\|(?<first>[^|]*)\|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"`(?<name>[a-z_][a-z0-9_]*)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();

    /// <summary>
    /// Tables declared as another's shape, so a caller can report the two kinds apart.
    ///
    /// Declared before <see cref="Declared"/> on purpose: static initialisers run in textual order,
    /// and <see cref="Read"/> fills this as a side effect, so an initialiser below it would run
    /// afterwards and overwrite the answer with an empty one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DeclaredByShape { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Every table SCHEMA declares columns for, and the columns it declares.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Declared { get; } = Read();

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Read()
    {
        string schema = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SCHEMA.md"));

        var listed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, (string Other, string Delta)>(StringComparer.Ordinal);

        int[] bounds = [.. AnyHeading().Matches(schema).Select(m => m.Index), schema.Length];

        for (int i = 0; i + 1 < bounds.Length; i++)
        {
            string section = schema[bounds[i]..bounds[i + 1]];

            // A shape reference is resolved after every listed table is read, because the table it
            // points at may be declared later in the document.
            foreach (Match shape in ShapeOf().Matches(section))
            {
                shapes[shape.Groups["table"].Value] =
                    (shape.Groups["other"].Value, shape.Groups["delta"].Value);
            }

            string[] named =
            [
                .. TableHeading().Matches(section).Select(m => m.Groups["table"].Value),
                .. ColumnsOf().Matches(section).Select(m => m.Groups["table"].Value),
            ];

            if (named.Length == 0)
            {
                continue;
            }

            var columns = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match row in TableRow().Matches(section))
            {
                string first = row.Groups["first"].Value;

                // A separator row, and a row whose first cell is prose rather than column names.
                if (first.Contains("---", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match name in Backticked().Matches(first))
                {
                    columns.Add(name.Groups["name"].Value);
                }
            }

            if (columns.Count == 0)
            {
                continue;
            }

            foreach (string table in named.Distinct(StringComparer.Ordinal))
            {
                if (!listed.TryGetValue(table, out HashSet<string>? existing))
                {
                    listed[table] = columns;
                    continue;
                }

                existing.UnionWith(columns);
            }
        }

        var byShape = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string table, (string other, string delta)) in shapes)
        {
            if (!listed.TryGetValue(other, out HashSet<string>? source))
            {
                continue;
            }

            var columns = new HashSet<string>(source, StringComparer.Ordinal);

            // "less `a`, `b`" removes; "with `p` in place of `q`" renames. Both are stated in the
            // document rather than inferred, because a difference nobody wrote down is the one that
            // reads as sameness.
            foreach (Match clause in Regex.Matches(delta, @"less (?<names>[^,]*(?:,[^,]*)*)", RegexOptions.CultureInvariant))
            {
                foreach (Match name in Backticked().Matches(clause.Groups["names"].Value))
                {
                    columns.Remove(name.Groups["name"].Value);
                }
            }

            foreach (Match clause in Regex.Matches(
                delta, @"with `(?<added>[a-z_][a-z0-9_]*)` in place of `(?<removed>[a-z_][a-z0-9_]*)`",
                RegexOptions.CultureInvariant))
            {
                columns.Remove(clause.Groups["removed"].Value);
                columns.Add(clause.Groups["added"].Value);
            }

            listed[table] = columns;
            byShape[table] = other;
        }

        DeclaredByShape = byShape;

        return listed.ToDictionary(
            e => e.Key, e => (IReadOnlySet<string>)e.Value, StringComparer.Ordinal);
    }
}
