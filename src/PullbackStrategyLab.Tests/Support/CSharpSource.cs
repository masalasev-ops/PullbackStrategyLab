using System.Text;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// C# source with its comments blanked out, offsets preserved so a line number still lands
/// where it did.
///
/// The checks that scan source are asserting a property of the code, and a comment that names
/// a banned construct in order to explain the ban is not the code doing it. Stripping comments
/// is the difference between a check that stays enforceable and one whose first false positive
/// gets it loosened.
///
/// Strings are preserved, including verbatim and raw strings, because the SQL a check reads
/// lives in them.
/// </summary>
public static class CSharpSource
{
    public static string WithoutComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var output = new StringBuilder(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];

            // A raw string literal: three or more quotes, closed by the same number.
            if (c == '"' && Run(source, i, '"') >= 3)
            {
                int fence = Run(source, i, '"');
                int end = FindRawStringEnd(source, i + fence, fence);
                output.Append(source, i, end - i);
                i = end;
                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                int end = FindVerbatimStringEnd(source, i + 2);
                output.Append(source, i, end - i);
                i = end;
                continue;
            }

            if (c is '"' or '\'')
            {
                int end = FindSimpleLiteralEnd(source, i + 1, c);
                output.Append(source, i, end - i);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] is not ('\r' or '\n'))
                {
                    output.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    output.Append(source[i] is '\r' or '\n' ? source[i] : ' ');
                    i++;
                }

                // The closing */ itself.
                for (int k = 0; k < 2 && i < source.Length; k++, i++)
                {
                    output.Append(' ');
                }

                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static int Run(string source, int start, char c)
    {
        int n = 0;
        while (start + n < source.Length && source[start + n] == c)
        {
            n++;
        }

        return n;
    }

    private static int FindRawStringEnd(string source, int from, int fence)
    {
        int i = from;
        while (i < source.Length)
        {
            if (source[i] == '"' && Run(source, i, '"') >= fence)
            {
                return i + Run(source, i, '"');
            }

            i++;
        }

        return source.Length;
    }

    private static int FindVerbatimStringEnd(string source, int from)
    {
        int i = from;
        while (i < source.Length)
        {
            if (source[i] == '"')
            {
                // A doubled quote is an escaped quote, not the end.
                if (i + 1 < source.Length && source[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return source.Length;
    }

    private static int FindSimpleLiteralEnd(string source, int from, char quote)
    {
        int i = from;
        while (i < source.Length)
        {
            if (source[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (source[i] == quote)
            {
                return i + 1;
            }

            if (source[i] is '\r' or '\n')
            {
                // An unterminated literal on one line. Stop rather than swallowing the file.
                return i;
            }

            i++;
        }

        return source.Length;
    }
}
