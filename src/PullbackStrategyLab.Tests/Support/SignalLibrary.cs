using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// SCHEMA.md's Signals section read as data: every signal, and whether it is active.
///
/// The library is declared once, in the document, for the same reason data ownership is: a second
/// list in code would be the same fact in two places and would drift. So the vectorizer is asserted
/// against the document rather than the document against a list somebody kept up to date.
///
/// Bounded by the section heading, and the bound is load-bearing. The tables elsewhere in SCHEMA
/// have the same row shape, and reading the whole file would count every column of every store as
/// a signal.
/// </summary>
public static partial class SignalLibrary
{
    /// <summary>Where the library is declared. Cited by heading text, never by position.</summary>
    public const string Heading = "## Signals";

    private const string NextSection = "## Trading — phase 4";

    /// <summary>Frozen on every setup by SignalVectorizer, or awaiting the checkpoint that supplies it.</summary>
    public const string Active = "active";

    /// <summary>Formula and columns settled, nothing computes it, and 6.1 backfills it when admitted.</summary>
    public const string Candidate = "candidate";

    // A cell may contain an escaped pipe, and two of the trade-geometry formulas do: absolute
    // value is written \|trigger − stop\|. Reading a cell as "anything but a pipe" stops at the
    // first of those and silently loses the row, which is how `stop_distance_ranges` and
    // `trigger_distance_ranges` went missing from the library on the first run of this parser.
    // Escaped pipes are part of the cell; bare ones separate cells.
    [GeneratedRegex(
        @"^\|\s*`(?<name>[a-z_0-9]+)`\s*\|(?<formula>(?:\\\||[^|])*)\|(?<columns>(?:\\\||[^|])*)\|(?<status>(?:\\\||[^|])*)\|",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SignalRow();

    /// <summary>Every signal the library declares, in the order the document lists them.</summary>
    public static IReadOnlyList<SignalDeclaration> All { get; } = Read();

    /// <summary>The active ones, which is what the vectorizer is measured against.</summary>
    public static IReadOnlyList<string> ActiveNames { get; } =
        [.. All.Where(s => s.Status.StartsWith(Active, StringComparison.Ordinal)).Select(s => s.Name)];

    private static IReadOnlyList<SignalDeclaration> Read()
    {
        string schema = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SCHEMA.md"));

        int start = schema.IndexOf(Heading, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"SCHEMA.md has no \"{Heading}\" section. The signal library is declared there and nowhere else, so a "
                + "reworded heading fails here rather than reading an empty library and passing.");
        }

        int end = schema.IndexOf(NextSection, start, StringComparison.Ordinal);
        string section = end < 0 ? schema[start..] : schema[start..end];

        SignalDeclaration[] signals =
        [
            .. SignalRow().Matches(section).Select(m => new SignalDeclaration(
                m.Groups["name"].Value,
                m.Groups["formula"].Value.Trim(),
                m.Groups["columns"].Value.Trim(),
                m.Groups["status"].Value.Trim()))
        ];

        // Stated in advance rather than left self-validating. A parser that stops matching returns
        // an empty library, and an empty library satisfies every assertion made against it: the
        // partition holds trivially and nothing is reported as missing. This parser has already
        // done it once, losing the two trade-geometry rows to an escaped pipe inside a formula.
        if (signals.Length < 30)
        {
            throw new InvalidOperationException(
                $"Only {signals.Length} signal(s) were parsed from SCHEMA.md's {Heading} section. The document declared "
                + "more than thirty when this was written, so a number this low means the parser stopped matching "
                + "rather than that the library shrank.");
        }

        return signals;
    }
}

/// <summary>One row of the library: what it is, what it reads, and whether anything computes it.</summary>
public sealed record SignalDeclaration(string Name, string Formula, string SourceColumns, string Status);
