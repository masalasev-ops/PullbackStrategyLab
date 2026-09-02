using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Every write against a store that appears in the shipped source, attributed to the type
/// that issues it.
///
/// Attribution is by enclosing type rather than by file, because that is the unit SCHEMA
/// declares. A helper in another type issuing the same statement would be a second writer
/// of the same table, and the whole point of the rule is that there is exactly one.
///
/// An upsert counts as both operations on the table it names. <c>ON CONFLICT DO UPDATE</c>
/// updates rows, and reading it as an insert alone is how a component acquires an undeclared
/// update on a table somebody else owns.
/// </summary>
public static partial class SourceWrites
{
    [GeneratedRegex(@"INSERT\s+INTO\s+(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Insert();

    /// <summary>
    /// A standalone update. The DO UPDATE of an upsert is excluded here and picked up from the
    /// insert instead, because the table it writes is the one the insert names, not the word
    /// that follows it.
    /// </summary>
    [GeneratedRegex(@"(?<!\bDO\s{1,20})UPDATE\s+(?<table>[a-z_]+)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Update();

    [GeneratedRegex(@"DELETE\s+FROM\s+(?<table>[a-z_]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Delete();

    [GeneratedRegex(@"ON\s+CONFLICT[\s\S]{0,400}?DO\s+UPDATE", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpsertTail();

    /// <summary>
    /// A type declaration at the start of a line: horizontal indent, then whole modifiers, then the
    /// keyword and the name.
    ///
    /// <b>Every quantifier here matches something no other quantifier in the pattern can</b>, and
    /// that is the whole of why it is written this way. It read
    /// <c>^\s*(?:public|...|\s)*\b(?:class|...)</c>, where <c>\s*</c> and the <c>\s</c> branch of
    /// the alternation both match the same whitespace, so a run of blank lines not ending in a type
    /// keyword can be divided between them in exponentially many ways and the engine tries them
    /// all. Comments are stripped before this runs, which is what turns a comment-heavy corpus into
    /// long runs of blank lines and made the input worst-case rather than unusual.
    ///
    /// <b>122 seconds over 97 files, against 74 milliseconds for this pattern</b>, over the same
    /// input and producing the same 254 names in the same order. It runs twice per process, so it
    /// was four minutes of every check that reads a store write and four minutes of the suite, or
    /// about twelve minutes of a twenty-minute CI run.
    ///
    /// The rule it is an instance of: no two quantifiers in one pattern may match the same
    /// character. Found on 2026-08-31 by timing a CI run rather than by reading the pattern,
    /// because it looks ordinary and its cost is invisible until the input has long whitespace
    /// runs in it.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file)[ \t]+)*(?:class|record|struct|interface)[ \t]+(?<name>\w+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclaration();

    public static IReadOnlyList<SourceWrite> InProductionSource { get; } = Read(RepositoryLayout.ProductionSourceFiles);

    /// <summary>
    /// Every type the shipped source declares. What tells a declared writer that has not been
    /// built yet apart from one that exists and has stopped writing: the first is unexamined,
    /// the second is a failure, and a check that could not separate them would have to treat
    /// both as passes.
    /// </summary>
    public static IReadOnlySet<string> ProductionTypeNames { get; } = RepositoryLayout.ProductionSourceFiles
        .SelectMany(f => TypeDeclaration()
            .Matches(CSharpSource.WithoutComments(RepositoryLayout.Read(f)))
            .Select(m => m.Groups["name"].Value))
        .ToHashSet(StringComparer.Ordinal);

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
        IReadOnlyList<TypeSpan> types = TypeSpans(source, text);
        var writes = new List<SourceWrite>();

        void Add(int index, string table, StoreOperation operation, bool isDelete) =>
            writes.Add(new SourceWrite(
                label,
                text.AsSpan(0, index).Count('\n') + 1,
                EnclosingType(types, index),
                table,
                operation,
                isDelete,
                StatementFrom(text, index)));

        foreach (Match match in Insert().Matches(text))
        {
            string table = match.Groups["table"].Value;
            Add(match.Index, table, StoreOperation.Insert, isDelete: false);

            if (UpsertTail().IsMatch(StatementFrom(text, match.Index)))
            {
                Add(match.Index, table, StoreOperation.Update, isDelete: false);
            }
        }

        foreach (Match match in Update().Matches(text))
        {
            Add(match.Index, match.Groups["table"].Value, StoreOperation.Update, isDelete: false);
        }

        // A delete has no declared operation anywhere in SCHEMA, and bars are append-only
        // besides, so any delete found is reported by whichever check reads this rather than
        // being silently dropped for having nowhere to belong.
        foreach (Match match in Delete().Matches(text))
        {
            Add(match.Index, match.Groups["table"].Value, StoreOperation.Update, isDelete: true);
        }

        return writes.OrderBy(w => w.Line).ThenBy(w => w.Operation).ToArray();
    }

    /// <summary>The rest of the statement an insert starts, so an upsert tail is read against its own insert.</summary>
    private static string StatementFrom(string text, int index)
    {
        int end = text.IndexOf(';', index);
        return end < 0 ? text[index..] : text[index..end];
    }

    /// <summary>
    /// The columns an UPDATE assigns, read from its own SET clause.
    ///
    /// For the one caller whose exception has to be narrower than a table: `bar-append-only` admits
    /// exactly one update against a bar table, and admitting it by table alone would admit every
    /// update against that table. A statement is the unit the property is about, so the exception is
    /// read from the statement.
    ///
    /// Everything from SET to the first WHERE, split on commas, taking the name left of each `=`. It
    /// returns nothing for a write that is not an update and nothing for one whose statement was not
    /// captured, so a caller comparing against an expected column fails closed rather than admitting
    /// a write it could not read.
    /// </summary>
    public static IReadOnlyList<string> ColumnsAssignedBy(SourceWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);

        Match set = SetClause().Match(write.Statement);

        if (!set.Success)
        {
            return [];
        }

        return
        [
            .. set.Groups["assignments"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.Split('=', 2)[0].Trim())
                .Where(c => c.Length > 0 && c.All(ch => char.IsLetterOrDigit(ch) || ch == '_')),
        ];
    }

    [GeneratedRegex(
        @"\bSET\s+(?<assignments>.*?)(?:\bWHERE\b|\bRETURNING\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SetClause();

    /// <summary>
    /// The type whose braces enclose <paramref name="index"/>, innermost first.
    ///
    /// <b>It took the last declaration starting before the write until 4.6, and never popped.</b> A
    /// nested type declared above a write reattributed that write to the nested type, so declaring
    /// <c>CheckRecomputer.Arguments</c> at the top of its class moved both <c>UPDATE setup</c>
    /// statements onto <c>Arguments</c> and turned `writer-ownership` red in both directions at once.
    /// That is the loud direction and it was loud by luck: this corpus puts nested types last in
    /// every file, so the only writes it had ever mis-attributed were ones SCHEMA declares for the
    /// enclosing type. Where a file holds two components and a write sits after the second
    /// declaration, the mis-attribution lands on a name SCHEMA does have and the check passes on the
    /// wrong subject, which is the direction that does not announce itself.
    /// </summary>
    private static string EnclosingType(IReadOnlyList<TypeSpan> types, int index)
    {
        string enclosing = "(none)";
        int narrowest = int.MaxValue;

        foreach (TypeSpan type in types)
        {
            if (type.Start > index || type.End < index)
            {
                continue;
            }

            // Innermost wins, which is what "enclosing" means when types nest. Measured by span
            // width rather than by declaration order, so a partial type declared twice in one file
            // cannot make the wider of the two look nearer.
            int width = type.End - type.Start;

            if (width < narrowest)
            {
                narrowest = width;
                enclosing = type.Name;
            }
        }

        return enclosing;
    }

    /// <summary>
    /// Every type declaration in one file with the span its braces cover.
    ///
    /// <paramref name="text"/> is the comment-blanked source the declarations are matched in, so a
    /// type named in a comment is not read as one. The braces are counted over
    /// <see cref="CSharpSource.WithoutCommentsOrLiterals"/> of the same source instead, because a
    /// brace inside a string literal is a character in a query rather than a scope: this corpus puts
    /// its SQL in raw string literals and its interpolation holes inside them. Both strings preserve
    /// the source's offsets, so one index reads correctly in either.
    ///
    /// A declaration whose body never closes, which is a file that does not compile, is given a span
    /// reaching the end of the file rather than being dropped: a write inside it is then attributed
    /// to something rather than to <c>(none)</c>, and the compiler is the thing that should be
    /// complaining.
    /// </summary>
    private static IReadOnlyList<TypeSpan> TypeSpans(string source, string text)
    {
        string braces = CSharpSource.WithoutCommentsOrLiterals(source);
        Match[] declarations = TypeDeclaration().Matches(text).ToArray();

        var spans = new List<TypeSpan>();
        var open = new Stack<int>();
        int next = 0;
        string? pending = null;

        for (int i = 0; i < braces.Length; i++)
        {
            while (next < declarations.Length && declarations[next].Index <= i)
            {
                // A declaration reached before its opening brace. Two in a row cannot happen in
                // compiling C#, and if it did the later one is the one whose brace comes next.
                pending = declarations[next].Groups["name"].Value;
                next++;
            }

            if (braces[i] == '{')
            {
                if (pending is not null)
                {
                    open.Push(spans.Count);
                    spans.Add(new TypeSpan(pending, i, braces.Length - 1));
                    pending = null;
                }
                else
                {
                    open.Push(-1);
                }

                continue;
            }

            if (braces[i] != '}' || open.Count == 0)
            {
                continue;
            }

            int slot = open.Pop();

            if (slot >= 0)
            {
                spans[slot] = spans[slot] with { End = i };
            }
        }

        return spans;
    }

    /// <summary>A type declaration and the span of source its braces cover.</summary>
    private sealed record TypeSpan(string Name, int Start, int End);
}

public sealed record SourceWrite(
    string File,
    int Line,
    string Type,
    string Table,
    StoreOperation Operation,
    bool IsDelete,
    string Statement = "")
{
    public override string ToString() =>
        $"{File}:{Line}  {Type} {(IsDelete ? "DELETE" : Operation.ToString().ToUpperInvariant())} {Table}";
}
