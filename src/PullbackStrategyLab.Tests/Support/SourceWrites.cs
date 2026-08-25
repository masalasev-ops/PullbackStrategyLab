using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Every write against a store that appears in the shipped source, attributed to the type
/// that issues it.
///
/// Attribution is by enclosing type rather than by file, because that is the unit SCHEMA
/// declares. A helper in another type issuing the same statement would be a second writer
/// of the same table, and the whole point of the rule is that there is exactly one.
/// </summary>
public static partial class SourceWrites
{
    [GeneratedRegex(@"INSERT\s+INTO\s+(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Insert();

    [GeneratedRegex(@"UPDATE\s+(?<table>[a-z_]+)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Update();

    [GeneratedRegex(@"DELETE\s+FROM\s+(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Delete();

    [GeneratedRegex(@"^\s*(?:public|internal|private|protected|sealed|static|abstract|partial|file|\s)*\b(?:class|record|struct|interface)\s+(?<name>\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    public static IReadOnlyList<SourceWrite> InProductionSource { get; } = Read(RepositoryLayout.ProductionSourceFiles);

    /// <summary>How many files were read, so the check can report what it covered rather than only what it found.</summary>
    public static int ProductionFilesRead => RepositoryLayout.ProductionSourceFiles.Count;

    private static IReadOnlyList<SourceWrite> Read(IReadOnlyList<string> files) =>
        files.SelectMany(f => InSource(RepositoryLayout.Relative(f), RepositoryLayout.Read(f))).ToArray();

    /// <summary>
    /// Every store write in one piece of source, with comments blanked out first so a comment
    /// describing a statement is not read as one. Public so a proof test can feed it source it
    /// wrote itself, rather than the check being proved by breaking the repository by hand once.
    /// </summary>
    public static IReadOnlyList<SourceWrite> InSource(string label, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(source);

        string text = CSharpSource.WithoutComments(source);
        Match[] types = TypeDeclaration().Matches(text).ToArray();
        var writes = new List<SourceWrite>();

        void Collect(Regex pattern, StoreOperation operation, bool isDelete)
        {
            foreach (Match match in pattern.Matches(text))
            {
                writes.Add(new SourceWrite(
                    label,
                    text.AsSpan(0, match.Index).Count('\n') + 1,
                    EnclosingType(types, match.Index),
                    match.Groups["table"].Value,
                    operation,
                    isDelete));
            }
        }

        Collect(Insert(), StoreOperation.Insert, isDelete: false);
        Collect(Update(), StoreOperation.Update, isDelete: false);

        // A delete has no declared operation anywhere in SCHEMA, and bars are append-only
        // besides, so any delete found is reported by whichever check reads this rather than
        // being silently dropped for having nowhere to belong.
        Collect(Delete(), StoreOperation.Update, isDelete: true);

        return writes.OrderBy(w => w.Line).ToArray();
    }

    private static string EnclosingType(IReadOnlyList<Match> types, int index)
    {
        string enclosing = "(none)";
        foreach (Match type in types)
        {
            if (type.Index > index)
            {
                break;
            }

            enclosing = type.Groups["name"].Value;
        }

        return enclosing;
    }
}

public sealed record SourceWrite(
    string File,
    int Line,
    string Type,
    string Table,
    StoreOperation Operation,
    bool IsDelete)
{
    public override string ToString() =>
        $"{File}:{Line}  {Type} {(IsDelete ? "DELETE" : Operation.ToString().ToUpperInvariant())} {Table}";
}
