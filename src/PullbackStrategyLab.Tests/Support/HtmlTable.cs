using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Locates a table in ARCHITECTURE.html by the text of the heading above it, never by
/// position. A parser anchored on position breaks on every insertion; anchored on heading
/// text it does not, and that is the whole reason the corpus dropped section numbers.
/// see: Headings carry no numbers, and anchors are slugs
/// </summary>
public static partial class HtmlTable
{
    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Row();

    [GeneratedRegex(@"<t(?<kind>[dh])[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Cell();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// The body rows of the first table following the heading whose text matches. Header
    /// rows, the ones made of th cells, are dropped: a table's shape is its data.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> BodyRowsUnder(string html, string headingText)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(headingText);

        int headingIndex = FindHeading(html, headingText);
        int tableStart = html.IndexOf("<table", headingIndex, StringComparison.OrdinalIgnoreCase);
        if (tableStart < 0)
        {
            throw new InvalidOperationException($"No table follows the heading \"{headingText}\".");
        }

        int tableEnd = html.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
        if (tableEnd < 0)
        {
            throw new InvalidOperationException($"The table under \"{headingText}\" is not closed.");
        }

        string table = html[tableStart..tableEnd];
        var rows = new List<IReadOnlyList<string>>();

        foreach (Match row in Row().Matches(table))
        {
            var cells = new List<string>();
            bool header = false;

            foreach (Match cell in Cell().Matches(row.Groups["row"].Value))
            {
                header |= cell.Groups["kind"].Value.Equals("h", StringComparison.OrdinalIgnoreCase);
                cells.Add(Text(cell.Groups["cell"].Value));
            }

            if (!header && cells.Count > 0)
            {
                rows.Add(cells);
            }
        }

        return rows;
    }

    /// <summary>
    /// The heading above every table in the document, in document order, one entry per table.
    ///
    /// What makes the set of tables enumerable rather than assumed. A check that reads five
    /// tables by name and is described as reading the document has narrowed its own scope to a
    /// list nobody maintains, and the tables it does not read produce no verdict at all: not
    /// pass, not fail, and not unexamined either, which is worse than any of them because
    /// unexamined is the verdict that blocks. Found at the 1.12 review, where twelve of the
    /// document's seventeen tables were invisible to the check that claims to read them.
    /// </summary>
    public static IReadOnlyList<string> HeadingOfEveryTable(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        (int Index, string Text)[] headings =
            [.. HeadingPattern().Matches(html).Select(h => (h.Index, Text(h.Groups["text"].Value)))];

        var perTable = new List<string>();

        foreach (Match table in TableOpen().Matches(html))
        {
            (int Index, string Text) above = headings.LastOrDefault(h => h.Index < table.Index);
            perTable.Add(above.Text ?? string.Empty);
        }

        return perTable;
    }

    [GeneratedRegex(@"<table[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableOpen();

    /// <summary>The index of a heading with this exact text, with any nested markup excluded.</summary>
    public static int FindHeading(string html, string headingText)
    {
        foreach (Match heading in HeadingPattern().Matches(html))
        {
            if (string.Equals(Text(heading.Groups["text"].Value), headingText, StringComparison.Ordinal))
            {
                return heading.Index;
            }
        }

        throw new InvalidOperationException(
            $"No heading reads \"{headingText}\". Cross-document references cite heading text, so a heading that has been " +
            "reworded breaks this loudly rather than resolving to the wrong place.");
    }

    [GeneratedRegex(@"<h(?<level>[1-4])[^>]*>(?<text>.*?)</h\k<level>>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    /// <summary>Cell or heading text with markup removed and whitespace collapsed.</summary>
    public static string Text(string markup) =>
        WhitespaceRun().Replace(Tag().Replace(markup, string.Empty), " ")
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Trim();
}
