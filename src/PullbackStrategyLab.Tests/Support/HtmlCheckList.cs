using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The two check lists in ARCHITECTURE.html are not tables. They are blocks, each carrying
/// the check's name in its own element, which is what makes the name the thing a detector
/// can be asserted against rather than a row position.
///
/// Located by heading text and bounded by the next heading of the same level, for the same
/// reason every other parser here is: an insertion must not silently change what is read.
/// see: Headings carry no numbers, and anchors are slugs
/// </summary>
public static partial class HtmlCheckList
{
    [GeneratedRegex(@"<span class=""gid"">(?<name>[^<]+)</span>", RegexOptions.CultureInvariant)]
    private static partial Regex CheckName();

    [GeneratedRegex(@"<h2[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex NextHeading();

    /// <summary>The check names listed under a heading, in the order the document lists them.</summary>
    public static IReadOnlyList<string> NamesUnder(string html, string headingText)
    {
        ArgumentNullException.ThrowIfNull(html);

        int start = HtmlTable.FindHeading(html, headingText);
        Match next = NextHeading().Match(html, start + 1);
        string section = next.Success ? html[start..next.Index] : html[start..];

        return CheckName().Matches(section)
            .Select(m => m.Groups["name"].Value.Trim())
            .ToArray();
    }
}
