using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// What a pack version is, and the tripwire that fails one.
///
/// <b>A version is what the model was shown and judged under, and deliberately not what it was
/// shown about.</b> A pack cut on two different nights is the same version over different
/// evidence, which is the whole point of holding the version fixed while the evidence accumulates:
/// the success criterion is proposal hit rate by pack version, and a version that forked every
/// night would compare each proposal against a population of one.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
/// see: The evidence pack is versioned, and the success criterion is proposal hit rate by pack version
/// </summary>
public static class PackVersions
{
    /// <summary>
    /// The model identifier a pack carries while question 1 of the phase 6 sitting is open.
    ///
    /// <b>A named value rather than an empty string or a null.</b> The model is in the version
    /// because it is a confounder for the phase's own success criterion, so a pack cut before one
    /// is chosen has to say that in the tuple rather than leave the field blank: blank would read
    /// as "no model matters here" and would make version 1 and the first version cut under a real
    /// model look like a change of evidence rather than a change of confounder.
    /// see: The model is a frozen parameter of the pack version, and changing it forks the record
    ///
    /// <b>Choosing one forks the version, and that is the intended behaviour rather than a cost.</b>
    /// 6.5 replaces this with the configured identifier, the tuple changes, and the next cut is a
    /// new version. Every proposal made against this one stays attributable to a pack whose model
    /// was nobody's.
    /// </summary>
    public const string ModelNotChosen = "unchosen";

    /// <summary>
    /// The tuple, rendered canonically. This string is the version's identity and its fingerprint
    /// is taken over it.
    ///
    /// <b>Ordered and delimited explicitly, because this is the one string the whole scheme rests
    /// on.</b> The signal names are sorted ordinal rather than taken in library order, so a
    /// reordering of SCHEMA's Signals section that changes no signal does not fork the version. The
    /// section list is taken in document order, because for sections the order is what the model
    /// saw and a reordering is a different pack.
    /// </summary>
    public static string Canonical(PackVersionTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);

        var text = new StringBuilder();

        text.Append("sections=").Append(string.Join(",", tuple.Sections)).Append('\n');
        text.Append("screened=")
            .Append(string.Join(",", tuple.SignalsScreened.OrderBy(s => s, StringComparer.Ordinal)))
            .Append('\n');
        text.Append("correction=").Append(tuple.CorrectionForm).Append('\n');
        text.Append("level=").Append(tuple.Level.ToString("F9", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("familyWise=")
            .Append(CorrectionReading.Render(tuple.FamilyWiseThreshold))
            .Append('\n');
        text.Append("model=").Append(tuple.ModelIdentifier).Append('\n');

        return text.ToString();
    }

    /// <summary>
    /// The fingerprint of a tuple: SHA-256 of <see cref="Canonical"/>, lowercase hex.
    ///
    /// A hash rather than the canonical string itself, because the string carries the whole signal
    /// library and a proposal row citing it would carry the library on every row. The string is
    /// recoverable from the version row's own columns, so nothing is lost.
    /// </summary>
    public static string Fingerprint(PackVersionTuple tuple) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(tuple))));

    /// <summary>
    /// Whether a proposal citing <paramref name="citedSignals"/> fails the pack version it was made
    /// against.
    ///
    /// <b>The tripwire, and it fires on the citation rather than on the outcome.</b> One signal that
    /// cannot possibly matter sits in the conditional tables; it will show a spurious pattern in
    /// some deciles because everything does. A proposal citing it is evidence that the pack led the
    /// model to rummage, and that is knowable the moment the proposal is written rather than after
    /// a window is spent finding out.
    /// see: One meaningless signal is planted in the conditional tables
    ///
    /// <b>Comparison is ordinal and exact.</b> A near-match is not a citation of the null control,
    /// and treating one as such would fail a version over a signal whose name merely resembles it.
    /// </summary>
    public static bool CitesTheNullControl(IReadOnlyCollection<string> citedSignals)
    {
        ArgumentNullException.ThrowIfNull(citedSignals);
        return citedSignals.Contains(SignalLibrary.NullControl, StringComparer.Ordinal);
    }
}

/// <summary>
/// The identity of a pack version: what the model was shown and what it was judged under.
///
/// <b>The realised false-discovery bar is not in here, and that is a decision rather than an
/// oversight.</b> The family-wise threshold is determined by the level and the number of signals
/// screened, both of which are properties of the version. The false-discovery bar the step-up
/// yields depends on the p-values, which are a reading of the store on one night, so putting it in
/// the tuple would fork the version every time the evidence moved and would leave every version
/// holding one proposal.
/// see: The realised false-discovery bar is a reading of a pack and the version carries the procedure
/// </summary>
public sealed record PackVersionTuple(
    IReadOnlyList<string> Sections,
    IReadOnlyList<string> SignalsScreened,
    string CorrectionForm,
    double Level,
    double? FamilyWiseThreshold,
    string ModelIdentifier);
