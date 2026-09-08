using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the pack comparison surface reads: every evidence-pack version, side by side, with what was
/// proposed against each.
///
/// <b>It is a screen and not a panel on one of the others, and that is why it is here.</b> Every
/// other screen this lab has is keyed on one night, one setup or one trade. The subject of this one
/// is the set of pack versions, so hanging it off any of the five would key a comparison across
/// versions to one of those three, and a reader would be comparing versions through a window onto a
/// single night (see: The pack comparison surface is the sixth screen and the navigation says six).
///
/// <b>The version tuple is the whole of what it compares.</b> A version pins what the model was
/// shown, so two versions differing in one component are two different questions asked of the model,
/// and the surface's job is to make that difference legible rather than to summarise it away
/// (see: A pack version pins what the model saw, and byte-stability is what makes that claim
/// checkable).
///
/// <b>It computes no rate.</b> The hit rate by pack version is band 3's, computed by the scoreboard
/// builder and read from the store. This page shows the counts the rate is over, so a reader can see
/// which version a rate came from; a page that computed its own would be a second implementation of
/// the project's stated success criterion (see: The averages are one implementation, computed
/// nightly and drawn on demand).
/// </summary>
public static class LabPacks
{
    /// <summary>What the surface says where no pack has ever been cut.</summary>
    public const string NoVersion =
        "no evidence pack has been cut, so there is no version to compare. The packer runs weekly on "
        + "Saturday morning and writes a version the first time it renders a pack";

    public static PacksResponse Read(
        StoreConnectionFactory connections, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);

        string date = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!connections.StoreExists)
        {
            return PacksResponse.Empty(date, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        IReadOnlyList<StoredPackVersion> versions =
            PackVersionReader.Read(connection, asOf, sessionZone);

        if (versions.Count == 0)
        {
            return PacksResponse.Empty(date, NoVersion);
        }

        IReadOnlyList<StoredProposal> proposals =
            ProposalReader.Read(connection, asOf, sessionZone);

        StoredPackRun? lastRun = PackVersionReader.LatestRun(connection, asOf, sessionZone);

        var rows = new List<PackVersionResponse>();

        foreach (StoredPackVersion version in versions)
        {
            IReadOnlyList<StoredProposal> against =
                [.. proposals.Where(p => p.PackVersion == version.Version)];

            rows.Add(new PackVersionResponse(
                version.Version,
                version.Fingerprint,
                version.Sections,
                version.SignalsScreened,
                version.SignalsScreenedCount,
                version.CorrectionForm,
                version.CorrectionLevel,
                version.FamilyWiseThreshold,
                version.ModelIdentifier,
                against.Count,

                // Counted by outcome and never summed into one figure. A version that abstained
                // every week and one that was never asked have the same proposal count, and a total
                // would make them read alike (see: Abstention is a valid recorded proposal outcome).
                against.Count(p => string.Equals(p.Outcome, "proposed", StringComparison.Ordinal)),
                against.Count(p => string.Equals(p.Outcome, "requested", StringComparison.Ordinal)),
                against.Count(p => string.Equals(p.Outcome, "abstained", StringComparison.Ordinal)),
                against.Count(p =>
                    !string.Equals(p.Outcome, "proposed", StringComparison.Ordinal)
                    && !string.Equals(p.Outcome, "requested", StringComparison.Ordinal)
                    && !string.Equals(p.Outcome, "abstained", StringComparison.Ordinal)),

                // Where each one stands in the funnel, as the set rather than as a count. Three
                // dispositions summing to one total would read identically however the proposals
                // were sorted between them.
                [.. against.Select(p => p.Status).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        }

        return new PacksResponse(
            date,
            null,
            rows,
            lastRun is null
                ? null
                : new PackCutResponse(
                    lastRun.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    lastRun.Version,
                    lastRun.SectionsRendered,
                    lastRun.SectionsEmpty,
                    lastRun.SignalsScreened,
                    lastRun.NullControlPlanted,
                    lastRun.Outcome,
                    lastRun.RefusedBecause));
    }
}

/// <summary>Every pack version, and what the last cut of a pack held.</summary>
public sealed record PacksResponse(
    string AsOf,
    string? Absent,
    IReadOnlyList<PackVersionResponse> Versions,
    PackCutResponse? LastCut)
{
    public static PacksResponse Empty(string asOf, string why) => new(asOf, why, [], null);
}

/// <summary>
/// One pack version: the tuple that defines it, and what was proposed against it.
///
/// The tuple's components are separate fields rather than one rendered string, because what a reader
/// of this page is doing is finding the one component two versions differ in.
/// </summary>
public sealed record PackVersionResponse(
    int Version,
    string Fingerprint,
    string Sections,
    string SignalsScreened,
    int SignalsScreenedCount,
    string CorrectionForm,
    double CorrectionLevel,
    double? FamilyWiseThreshold,
    string ModelIdentifier,
    int Proposals,
    int RuleChanges,
    int SignalRequests,
    int Abstentions,
    int NoAnswer,
    IReadOnlyList<string> Statuses);

/// <summary>
/// The last cut of a pack, which is how a version with no cut behind it is told from one nobody has
/// been shown.
///
/// <c>SectionsEmpty</c> against <c>SectionsRendered</c> is what makes an almost-empty pack legible
/// as almost-empty rather than as a pack.
/// </summary>
public sealed record PackCutResponse(
    string AsOf,
    int? Version,
    int SectionsRendered,
    int SectionsEmpty,
    int SignalsScreened,
    bool NullControlPlanted,
    string Outcome,
    string? RefusedBecause);
