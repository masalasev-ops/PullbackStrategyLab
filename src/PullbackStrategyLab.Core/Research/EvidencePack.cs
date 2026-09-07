using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The evidence pack: what the researcher is shown, in the nine sections the architecture declares.
///
/// <b>Byte-stable by construction rather than by care.</b> Two runs of the packer at one commit,
/// over one store state, for one as-of produce byte-identical output. That is what makes "this
/// proposal was made against this pack" a statement anybody can verify rather than take on trust,
/// and without it the success criterion compares two things that were never fixed.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
///
/// <b>Four things break it, and each is closed here rather than remembered.</b> Every ordering is
/// total and explicit, so no section rests on the order a set or a dictionary happened to enumerate
/// and every sort names a tiebreak. No instant of generation appears in the body; the as-of does,
/// being an input. Every number is rendered through the invariant culture, so a decimal is the same
/// string on both machines. Line endings are LF, written literally rather than through
/// <c>Environment.NewLine</c>, which is the one that would differ between the two platforms this
/// lab runs on and would differ silently.
///
/// <b>Every section renders, including the empty ones, and each carries its own count.</b> A
/// section over an empty population is legible as empty rather than as absent, which is the rule
/// every panel in this lab already holds. Five of the nine rest on outcomes that have not closed,
/// so this is the ordinary case today rather than an edge.
/// </summary>
public sealed record EvidencePack(
    DateOnly AsOf,
    int Version,
    string Fingerprint,
    IReadOnlyList<RenderedSection> Sections)
{
    /// <summary>The line ending, literal rather than the platform's.</summary>
    public const string LineEnding = "\n";

    /// <summary>How many of the nine sections had nothing to say on the night this was cut.</summary>
    public int SectionsEmpty => Sections.Count(s => s.Count == 0);

    /// <summary>
    /// The pack body: the text the researcher is shown.
    ///
    /// The header carries the as-of, the version and the fingerprint, and nothing else. In
    /// particular it does not carry when the pack was generated, because that would differ between
    /// two runs of one commit over one store state and is exactly the property being held.
    /// </summary>
    public string Render()
    {
        var body = new StringBuilder();

        body.Append("EVIDENCE PACK").Append(LineEnding);
        body.Append("as of: ").Append(AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(LineEnding);
        body.Append("pack version: ").Append(Version.ToString(CultureInfo.InvariantCulture)).Append(LineEnding);
        body.Append("fingerprint: ").Append(Fingerprint).Append(LineEnding);

        foreach (RenderedSection section in Sections)
        {
            body.Append(LineEnding);
            body.Append("## ").Append(section.Name).Append(LineEnding);

            // The count is on the section's own header line, so a section over an empty population
            // reads as empty rather than as absent and a reader never has to count the lines to
            // find out which it was.
            body.Append("rows: ").Append(section.Count.ToString(CultureInfo.InvariantCulture)).Append(LineEnding);

            if (section.Count == 0)
            {
                body.Append("empty: ").Append(section.EmptyBecause).Append(LineEnding);
                continue;
            }

            foreach (string line in section.Lines)
            {
                body.Append(line).Append(LineEnding);
            }
        }

        return body.ToString();
    }

    /// <summary>SHA-256 of the rendered body, lowercase hex. What two runs are compared on.</summary>
    public string BodyDigest() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Render())));

    /// <summary>The body's size in bytes, as UTF-8, which is how it is stored and sent.</summary>
    public int BodyBytes() => Encoding.UTF8.GetByteCount(Render());
}

/// <summary>
/// One section as it will appear: its name, how many rows it holds, the rows themselves, and where
/// it holds none, why.
///
/// <b>The count and the reason are not the same field and an empty section carries both.</b> Nought
/// twin pairs because the window held four setups and nought because the thresholds refused two
/// hundred are different statements, and a section showing only a nought cannot tell them apart.
/// That is the same rule the twin run row was built around one checkpoint earlier.
/// </summary>
public sealed record RenderedSection(string Name, int Count, IReadOnlyList<string> Lines, string? EmptyBecause)
{
    /// <summary>A section with rows in it.</summary>
    public static RenderedSection Of(string name, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new RenderedSection(name, lines.Count, lines, null);
    }

    /// <summary>
    /// A section with nothing in it, which says why.
    ///
    /// The reason is required rather than optional, because the whole value of rendering an empty
    /// section is the sentence beside the nought.
    /// </summary>
    public static RenderedSection Empty(string name, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);
        return new RenderedSection(name, 0, [], because);
    }
}
