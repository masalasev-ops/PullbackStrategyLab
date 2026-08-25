using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// SCHEMA.md read as data: every store, and its declared writer for each operation.
///
/// Data ownership is declared once, in SCHEMA.md, and nowhere else. Restating writers in
/// the architecture document would be the same fact in two places, which is how a corpus
/// starts to drift, so this parser reads the one declaration rather than a summary of it.
/// see: Data ownership is declared once, in SCHEMA.md
/// </summary>
public static partial class SchemaDeclarations
{
    [GeneratedRegex(@"^### `(?<store>[a-z_]+)`\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StoreHeading();

    /// <summary>A declaration line: the one that starts with an operation.</summary>
    [GeneratedRegex(@"^(?<line>(?:Insert|Update)\s+.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DeclarationLine();

    /// <summary>A store-level row in the phase 4 to 6 tables: name, grain, writer.</summary>
    [GeneratedRegex(@"^\|\s*`(?<store>[a-z_]+)`\s*\|(?<grain>[^|]*)\|(?<writer>[^|]*)\|", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StoreRow();

    [GeneratedRegex(@"^(?<op>Insert|Update)\s+(?<rest>.+)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex OperationPart();

    /// <summary>The component names ARCHITECTURE.html's catalogue defines, which is the vocabulary a writer is named from.</summary>
    public static IReadOnlyList<string> ComponentNames { get; } =
        HtmlTable.BodyRowsUnder(
                RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html")),
                "Component catalogue")
            .Select(r => r[0])
            .ToArray();

    // Declared after the vocabulary it is parsed against: static initialisers run in textual
    // order, and a parse against a null vocabulary would resolve nothing.
    public static IReadOnlyList<StoreDeclaration> Stores { get; } = Read();

    private static IReadOnlyList<StoreDeclaration> Read()
    {
        string schema = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SCHEMA.md"));
        var stores = new List<StoreDeclaration>();

        // The phases with columns: a `### `store`` heading, then the declaration line under it.
        MatchCollection headings = StoreHeading().Matches(schema);
        for (int i = 0; i < headings.Count; i++)
        {
            int start = headings[i].Index;
            int end = i + 1 < headings.Count ? headings[i + 1].Index : schema.Length;
            string section = schema[start..end];

            string[] declarations = DeclarationLine().Matches(section)
                .Select(m => m.Groups["line"].Value.Trim())
                .ToArray();

            stores.Add(new StoreDeclaration(
                headings[i].Groups["store"].Value,
                declarations.SelectMany(ParseWriters).ToArray()));
        }

        // The phases declared at store level, one row each. Bounded to the two sections that
        // declare stores that way: a column table elsewhere in the document has the same row
        // shape, and reading the whole file would count every column as a store.
        int storeLevelStart = schema.IndexOf("## Trading — phase 4", StringComparison.Ordinal);
        int storeLevelEnd = schema.IndexOf("## Cross-cutting", StringComparison.Ordinal);
        if (storeLevelStart < 0 || storeLevelEnd < storeLevelStart)
        {
            throw new InvalidOperationException(
                "SCHEMA.md no longer has the two store-level sections between \"## Trading\" and \"## Cross-cutting\". "
                + "The parser is bounded by heading text, so a reworded heading fails here rather than reading the wrong span.");
        }

        foreach (Match row in StoreRow().Matches(schema[storeLevelStart..storeLevelEnd]))
        {
            stores.Add(new StoreDeclaration(
                row.Groups["store"].Value,
                ParseWriters(row.Groups["writer"].Value.Trim())));
        }

        return stores;
    }

    /// <summary>
    /// A declaration reads as prose, so it is split on the separator and each part is matched
    /// against the component vocabulary rather than against a naming pattern. A part naming
    /// something the catalogue does not contain is returned as unresolved rather than guessed
    /// at, and the check reports it as unexamined.
    /// </summary>
    public static IReadOnlyList<Writer> ParseWriters(string declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var writers = new List<Writer>();

        foreach (string part in declaration.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match match = OperationPart().Match(part);
            if (!match.Success)
            {
                // Read declarations and PK notes share the line and are not writes.
                continue;
            }

            var operation = Enum.Parse<StoreOperation>(match.Groups["op"].Value, ignoreCase: true);
            string rest = match.Groups["rest"].Value;

            // Everything after the first punctuation is commentary on the write, not part of the name.
            int stop = rest.IndexOfAny([',', '.', '(']);
            string names = (stop < 0 ? rest : rest[..stop]).Trim();

            foreach (string candidate in names.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? component = ComponentNames
                    .Where(c => candidate.StartsWith(c, StringComparison.Ordinal))
                    .OrderByDescending(c => c.Length)
                    .FirstOrDefault();

                writers.Add(new Writer(operation, component ?? candidate, Resolved: component is not null));
            }
        }

        return writers;
    }

    /// <summary>Every table a migration in this build creates. What the check can examine today.</summary>
    public static IReadOnlyList<string> TablesInMigrations { get; } =
        PullbackStrategyLab.Data.MigrationRunner.All()
            .SelectMany(m => CreateTable().Matches(m.Sql).Select(x => x.Groups["table"].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();
}

public enum StoreOperation
{
    Insert,
    Update,
}

public sealed record Writer(StoreOperation Operation, string Component, bool Resolved);

public sealed record StoreDeclaration(string Store, IReadOnlyList<Writer> Writers);
