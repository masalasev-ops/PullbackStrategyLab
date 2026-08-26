namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The same idea for the markdown specs: a table located by the heading or the line above
/// it, read as rows of cells. Whitespace-tolerant, because a grep over markdown that is not
/// will pass on one machine's formatting and fail on another's.
/// </summary>
public static class MarkdownTable
{
    /// <summary>
    /// The body rows of the first table that starts after <paramref name="afterText"/>.
    /// The header row and the separator row are dropped.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> BodyRowsAfter(string markdown, string afterText)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterText);

        int start = markdown.IndexOf(afterText, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"\"{afterText}\" does not appear in the document.");
        }

        string[] lines = markdown[start..].Split('\n');
        var rows = new List<IReadOnlyList<string>>();
        bool inTable = false;

        foreach (string raw in lines)
        {
            string line = raw.Trim();

            if (!line.StartsWith('|'))
            {
                if (inTable)
                {
                    break;
                }

                continue;
            }

            inTable = true;
            string[] cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

            // The header row, then the --- separator, then the body.
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':')))
            {
                rows.Clear();
                continue;
            }

            rows.Add(cells);
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No table body follows \"{afterText}\".");
        }

        return rows;
    }
}
