using System.Globalization;
using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// Night-log lines rendered from <c>tools/nightly.ps1</c>'s own format strings, as the script would
/// write them.
///
/// <b>Rendered rather than typed, and that is the whole point of it.</b> The reconciliation reads a
/// line one program writes and another parses, and a test feeding it a line somebody typed would prove
/// the parser agrees with the test. PowerShell's <c>-f</c> operator is .NET's composite format, so a
/// format string lifted out of the script and handed to <see cref="string.Format(IFormatProvider, string, object[])"/>
/// produces the script's own line, and a script whose wording moves turns every test built on these
/// red rather than leaving them green over a line nothing writes any more.
/// </summary>
public static partial class SlotScriptLines
{
    private static readonly Lazy<string> Script = new(() =>
        RepositoryLayout.Read(Path.Combine(RepositoryLayout.Tools, "nightly.ps1")));

    [GeneratedRegex(@"""(?<format>\{0\}  \{1\})"" -f \(Get-Date -Format '(?<stamp>[^']+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex Stamped();

    [GeneratedRegex(@"Write-Line \(""(?<format>slot \{0\} starting,[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Starting();

    [GeneratedRegex(@"Write-Line \(""(?<format>\s*refusing: the tree is on '\{0\}'[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Refusing();

    [GeneratedRegex(@"Write-Line \(""(?<format>\s*did-not-run slot=\{0\} session=\{1\} because=\{2\})""", RegexOptions.CultureInvariant)]
    private static partial Regex DidNotRun();

    /// <summary>The script's structured did-not-run format, exactly as it is spelled there.</summary>
    public static string DidNotRunFormat => Lift(DidNotRun(), "the did-not-run line");

    /// <summary>The line a slot writes first, before any guard runs.</summary>
    public static string StartingLine(DateOnly session, string slot, string at) =>
        Stamp(session, at, Format(Lift(Starting(), "the starting line"), slot, "a-branch", "abc1234", "data/live"));

    /// <summary>The guard's refusal in the wording it has had since 4.2, naming no slot.</summary>
    public static string RefusingLine(DateOnly session, string at, string branch) =>
        Stamp(session, at, Format(Lift(Refusing(), "the refusal"), branch));

    /// <summary>The structured line a slot the script will not run leaves for the next night.</summary>
    public static string DidNotRunLine(DateOnly session, string slot, string at, string because) =>
        Stamp(session, at, Format(DidNotRunFormat, slot, session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), because));

    /// <summary>
    /// The three lines a refused slot leaves that the reconciliation reads: its start, the guard's
    /// refusal, and the structured line, in the order the script writes them.
    /// </summary>
    public static IEnumerable<string> Refused(DateOnly session, string slot, string at) =>
    [
        StartingLine(session, slot, at),
        RefusingLine(session, at, "phase-7-1-reconciliation"),
        DidNotRunLine(session, slot, at, "refused-by-tree-guard"),
    ];

    private static string Stamp(DateOnly session, string at, string text)
    {
        Match stamped = Stamped().Match(Script.Value);
        if (!stamped.Success)
        {
            throw new InvalidOperationException(
                "tools/nightly.ps1 no longer stamps its lines the way this helper reads, so no line rendered here "
                + "is one the script writes.");
        }

        string instant = session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + at + ":02";
        return string.Format(CultureInfo.InvariantCulture, stamped.Groups["format"].Value, instant, text);
    }

    private static string Lift(Regex pattern, string what)
    {
        Match match = pattern.Match(Script.Value);
        return match.Success
            ? match.Groups["format"].Value
            : throw new InvalidOperationException(
                $"tools/nightly.ps1 no longer writes {what} in a form this helper can lift, so the reconciliation "
                + "would be tested against a line the script does not write.");
    }

    private static string Format(string format, params object[] values) =>
        string.Format(CultureInfo.InvariantCulture, format, values);
}
