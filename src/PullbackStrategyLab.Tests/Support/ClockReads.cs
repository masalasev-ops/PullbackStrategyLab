using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Finds direct reads of the machine clock in C# source. Separate from the check that uses it
/// so a proof test can feed it source it wrote itself, rather than the check being proved by
/// breaking the repository by hand once and reverting.
/// </summary>
public static partial class ClockReads
{
    [GeneratedRegex(@"\bDateTime(?:Offset)?\s*\.\s*(?<member>Now|UtcNow|Today)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DirectRead();

    /// <summary>
    /// Every direct read in <paramref name="source"/>, with comments blanked out first. A
    /// comment naming a banned construct in order to explain the ban is not the code doing it.
    /// </summary>
    public static IReadOnlyList<ClockRead> In(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string code = CSharpSource.WithoutComments(source);

        return DirectRead().Matches(code)
            .Select(m => new ClockRead(code.AsSpan(0, m.Index).Count('\n') + 1, m.Value))
            .ToArray();
    }
}

public sealed record ClockRead(int Line, string Text);
