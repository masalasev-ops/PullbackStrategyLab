namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The same idea for the markdown specs: a table located by the heading or the line above
/// it, read as rows of cells. Whitespace-tolerant, because a grep over markdown that is not
/// will pass on one machine's formatting and fail on another's.
///
/// <b>A body row narrower or wider than the header is rejected rather than returned.</b> Until
/// the 2.1 pass this returned whatever cells a line happened to have, and one caller guarded
/// with <c>if (row.Count &gt;= 3)</c>, so a malformed row read as an absent one. The row that
/// found it was BUILD_PLAN's own carried-obligations row for the per-scope floor: two cells where
/// every other row has three, silently outside <c>Schedule.Obligations</c>, so the obligation
/// driving a whole checkpoint could not be resolved by any permit and could not fall due.
///
/// A parser that silently discards malformed input is the same defect class as a check that
/// silently narrows its own scope, one level down: both answer a narrower question than the one
/// they were asked and both report success. The width comes from the table's own header rather
/// than from a number a caller passes, so a table that gains a column needs no edit here.
/// </summary>
public static class MarkdownTable
{
    /// <summary>
    /// The body rows of the first table that starts after <paramref name="afterText"/>.
    /// The header row and the separator row are dropped.
    ///
    /// Throws if any body row's cell count differs from the header's, naming the table, the row
    /// and both widths.
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
        int headerWidth = -1;

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
                // The separator is what identifies the row above it as the header, and its width
                // is the width every body row has to match. Taken from the header rather than from
                // the separator itself, because the two can disagree and the header is the one a
                // reader believes.
                headerWidth = rows.Count > 0 ? rows[^1].Count : cells.Length;
                rows.Clear();
                continue;
            }

            rows.Add(cells);
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No table body follows \"{afterText}\".");
        }

        // Skipped where the table carries no separator, which leaves nothing to call a header.
        // Silent only in that one case, and it is a shape this corpus does not use.
        if (headerWidth > 0)
        {
            string? ragged = RaggedRowProblem(rows, headerWidth, afterText);
            if (ragged is not null)
            {
                throw new InvalidOperationException(ragged);
            }
        }

        return rows;
    }

    /// <summary>
    /// What is wrong with the first body row whose width differs from the header's, or null if
    /// none does.
    ///
    /// Pure, and separated from the parse so the rejection can be proved against a table written
    /// by hand rather than against whatever the corpus happens to hold today. A guard nobody can
    /// break on purpose is a guard nobody knows the state of.
    /// </summary>
    public static string? RaggedRowProblem(IReadOnlyList<IReadOnlyList<string>> rows, int headerWidth, string afterText)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(headerWidth);

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Count == headerWidth)
            {
                continue;
            }

            string first = rows[i].Count > 0 ? rows[i][0] : "(empty)";

            return $"The table after \"{afterText}\" has a header {headerWidth} cells wide and a body row "
                + $"{rows[i].Count} cells wide: row {i + 1}, beginning \"{first}\". A row that does not match its "
                + "own header is malformed rather than absent, and a reader that skipped it would answer a narrower "
                + "question than the one it was asked while reporting success.";
        }

        return null;
    }
}
