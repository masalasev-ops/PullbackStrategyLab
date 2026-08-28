using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Reads the corpus the way the checks need it: the decision names, and every citation of
/// one, from the documents and from the source alike. Same citation string either way, so
/// one parser covers both.
/// </summary>
public static partial class Corpus
{
    public const string PreviouslyDecidedHeading = "## Previously decided";

    /// <summary>A decision is a bold-only line. The topic groupings are headings; the decisions under them are bold lines.</summary>
    [GeneratedRegex(@"^\*\*(?<name>.+?)\*\*[ \t]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BoldOnlyLine();

    /// <summary>The document form: `(see: Name)`.</summary>
    [GeneratedRegex(@"\(\s*see:\s*(?<name>[^)]{1,300}?)\s*\)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentCitation();

    /// <summary>
    /// The code form: a comment marker, the word, and the name to the end of the line.
    /// A closing backtick ends it too, because the corpus quotes this form inside one and a
    /// markdown paragraph is a single line, so "to the end of the line" would swallow the
    /// rest of the sentence.
    /// </summary>
    [GeneratedRegex(@"//+\s*see:\s*(?<name>[^\r\n`]{1,300})", RegexOptions.CultureInvariant)]
    private static partial Regex CodeCitation();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    public static IReadOnlyList<string> DecisionNames { get; } = ReadDecisionNames(current: true);

    public static IReadOnlyList<string> SupersededDecisionNames { get; } = ReadDecisionNames(current: false);

    /// <summary>Every citation in the corpus and in the source, with where it was found.</summary>
    public static IReadOnlyList<Citation> Citations { get; } = ReadCitations();

    /// <summary>
    /// Whitespace-tolerant, because a citation in markdown may wrap a line and one in HTML
    /// may carry inline markup. Backticks go, because the corpus quotes the code form inside
    /// them. A trailing full stop goes, because decision names carry no terminal punctuation
    /// and a citation at the end of a sentence would otherwise never resolve.
    /// </summary>
    public static string Normalise(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string stripped = HtmlTag().Replace(text, string.Empty)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("\u2019", "'", StringComparison.Ordinal);
        return WhitespaceRun().Replace(stripped, " ").Trim().TrimEnd('.').Trim();
    }

    private static IReadOnlyList<string> ReadDecisionNames(bool current)
    {
        string text = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "DECISIONS.md"));
        int split = text.IndexOf(PreviouslyDecidedHeading, StringComparison.Ordinal);
        if (split < 0)
        {
            throw new InvalidOperationException(
                $"DECISIONS.md has no '{PreviouslyDecidedHeading}' heading. A superseded decision has nowhere to move to, " +
                "and no-superseded-citation has nothing to read.");
        }

        string section = current ? text[..split] : text[split..];
        return BoldOnlyLine().Matches(section)
            .Select(m => Normalise(m.Groups["name"].Value))
            .Where(name => name.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<Citation> ReadCitations()
    {
        var citations = new List<Citation>();

        // Every tracked text file, not a named list. The named list was read in one direction and
        // the corpus grew past it: ten migrations, six files under the web project and four at the
        // root carry a citation and none of them was scanned.
        foreach (string file in RepositoryLayout.TrackedTextFiles)
        {
            string text = RepositoryLayout.Read(file);
            Regex pattern = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? CodeCitation()
                : DocumentCitation();

            foreach (Match match in pattern.Matches(text))
            {
                citations.Add(new Citation(
                    RepositoryLayout.Relative(file),
                    LineOf(text, match.Index),
                    Normalise(match.Groups["name"].Value)));
            }

            // A document can carry the code form too, where it quotes an example.
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in CodeCitation().Matches(text))
                {
                    citations.Add(new Citation(
                        RepositoryLayout.Relative(file),
                        LineOf(text, match.Index),
                        Normalise(match.Groups["name"].Value)));
                }
            }
        }

        return citations;
    }

    private static int LineOf(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;
}

public sealed record Citation(string File, int Line, string Name)
{
    public override string ToString() => $"{File}:{Line}  see: {Name}";
}
