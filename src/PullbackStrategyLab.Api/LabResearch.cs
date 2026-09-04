using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the research ledger reads: the register of rule versions, each version's difference series,
/// and the holdout budget.
///
/// <b>It reads what the stages wrote and computes no result.</b> The nightly difference is
/// VariantScorer's and the settlement against a target is AcceptanceGate's at <b>6.7</b>, so nothing
/// here averages a series or compares one to a target. A read surface that did would be the
/// arithmetic the phase turns on, implemented a second time, with the page as the last place anybody
/// looked.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>The register is the one thing here that is computed, and it is computed by the same code the
/// stage runs.</b> <see cref="HoldoutRegister.Describe"/> lives in the Data assembly for exactly
/// this: which windows have matured is a function of the calendar and the store's earliest session,
/// the read surface cannot reference the Worker, and reading the last run row instead would have
/// reported the register through whether anything had scheduled the registry.
///
/// <b>The two sides are separate lists all the way to the page.</b> A version's long score and its
/// short score are never added, and the wire shape is what makes that structural rather than
/// remembered: there is no field here holding a figure over both.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class LabResearch
{
    /// <summary>The checkpoint that builds the twin-pair panel, which is the one part of this page nothing fills yet.</summary>
    public const string TwinPairsArriveAt = "6.3";

    /// <summary>What the ledger says where the register holds no version at all.</summary>
    public const string NoVersionRegistered =
        "no rule version has been registered, so there is nothing to difference against and nothing to "
        + "settle. The baseline is registered and frozen at 5.1";

    public static ResearchResponse Read(StoreConnectionFactory connections, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);

        string date = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!connections.StoreExists)
        {
            return ResearchResponse.Empty(date, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        IReadOnlyList<StoredVariant> registered = VariantReader.RegisteredBy(connection, asOf, sessionZone);
        IReadOnlyList<StoredVariantScore> scores = VariantScoreReader.ScoredBy(connection, asOf, sessionZone);
        HoldoutRegisterState register = HoldoutRegister.Describe(connection, asOf, sessionZone, written: 0);
        StoredScoreRun? lastRun = VariantScoreReader.LastRunBy(connection, asOf, sessionZone);

        // The generation in force, which is what "live" means on this page. Editing the baseline
        // closes every open version as unresolved and starts a new one, so a version is only ever
        // comparable to versions of its own and the ledger says which generation it is looking at.
        int? generation = registered.Count == 0 ? null : registered.Max(v => v.Generation);

        var versions = new List<VersionResponse>();

        foreach (StoredVariant variant in registered)
        {
            // Grouped by direction and never across it. A version carries one side today, because a
            // threshold belongs to one side's gate list and the store holds that as a CHECK, but the
            // shape is per side rather than per version so that a version touching both would report
            // two figures rather than one figure over a mixture.
            IReadOnlyList<SideResponse> sides =
                [.. scores
                    .Where(s => string.Equals(s.VariantId, variant.VariantId, StringComparison.Ordinal))
                    .GroupBy(s => s.Direction, StringComparer.Ordinal)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => Side(g.Key, [.. g.OrderBy(s => s.SessionDate)]))];

            versions.Add(new VersionResponse(
                variant.VariantId,
                variant.Generation,
                variant.Family,
                variant.Definition,
                variant.Target,
                variant.MinimumSample,
                variant.MinimumSampleUnit,
                variant.Status,
                variant.ResolvedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                variant.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                variant.IsBaseline,
                variant.Generation == generation,
                variant.Moved?.Direction,
                variant.Moved?.Gate,
                variant.Moved?.ThresholdName,
                variant.Moved is null ? null : StoreText.ThresholdToStorageText(variant.Moved.From),
                variant.Moved is null ? null : StoreText.ThresholdToStorageText(variant.Moved.To),
                variant.Moved?.Describe(),
                sides));
        }

        return new ResearchResponse(
            date,
            versions.Count == 0 ? NoVersionRegistered : null,
            generation,
            versions,
            Holdout(register),
            lastRun is null
                ? null
                : new ScoreRunResponse(
                    lastRun.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    lastRun.VersionsLive,
                    lastRun.VersionsScored,
                    lastRun.NightsScored,
                    lastRun.NightsWaiting,
                    lastRun.Longs,
                    lastRun.Shorts,
                    lastRun.Unscoreable,
                    lastRun.Outcome,
                    lastRun.StoppedBecause),
            TwinPairsArriveAt);
    }

    /// <summary>
    /// One side of one version: the nights it was differenced over, and the series itself.
    ///
    /// <b>Counts and the series, and nothing that combines them.</b> The nights are counted because
    /// a figure is never shown without its denominator; the differences are listed because the
    /// series is what the build order says is visible. What is not here is a mean over the nights:
    /// that is the settlement, it is AcceptanceGate's at <b>6.7</b>, and a page computing it first
    /// would be a second implementation of the answer.
    /// </summary>
    private static SideResponse Side(string direction, IReadOnlyList<StoredVariantScore> nights) =>
        new(
            direction,
            nights.Count,
            nights.Count(n => n.MeanDifference is not null),
            nights.Sum(n => n.Unscoreable),
            nights.Sum(n => n.BaselineOutsideCap),
            nights.Sum(n => n.VariantOutsideCap),
            [.. nights.Select(n => new NightResponse(
                n.SessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                n.HorizonDays,
                n.Flagged,
                n.BaselineSelected,
                n.VariantSelected,
                n.BothSelected,
                n.BaselineOnly,
                n.VariantOnly,
                n.BaselineMeanReturn,
                n.VariantMeanReturn,
                n.MeanDifference,
                n.Unscoreable,
                n.WithheldBecause))]);

    /// <summary>
    /// The holdout budget as the ledger shows it: the eight windows, what each was spent on, and
    /// why it holds nothing where it holds nothing.
    /// </summary>
    private static HoldoutResponse Holdout(HoldoutRegisterState register) =>
        new(
            HoldoutWindows.Capacity,
            register.Matured,
            register.Recorded,
            register.Spent,
            register.Available,
            register.FirstSession?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            register.EmptyBecause,
            register.IsExhausted,
            [.. register.Missing],
            [.. register.Register.Select(w => new WindowResponse(
                w.Window.WindowId,
                w.Window.Ordinal,
                w.Window.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.Window.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.Window.MaturesOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                w.Spend?.SpentOn,
                w.Spend?.Outcome,
                w.Spend?.SpentAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))]);
}

/// <summary>
/// The research ledger on the wire.
///
/// <paramref name="Absent"/> is why the register holds nothing, and it is a different sentence from
/// the holdout register's own reason: a lab with no version registered and a lab whose budget has
/// not begun are two states and this page shows both at once.
/// </summary>
public sealed record ResearchResponse(
    string AsOf,
    string? Absent,
    int? Generation,
    IReadOnlyList<VersionResponse> Versions,
    HoldoutResponse Holdout,
    ScoreRunResponse? LastScoreRun,
    string TwinPairsArriveAt)
{
    public static ResearchResponse Empty(string asOf, string why) =>
        new(asOf, why, null, [],
            new HoldoutResponse(HoldoutWindows.Capacity, 0, 0, 0, 0, null, why, false, [], []),
            null, LabResearch.TwinPairsArriveAt);
}

/// <summary>
/// One registered version, with its pre-registration and its per-side series.
///
/// <paramref name="Live"/> is whether it belongs to the generation in force. A version of an earlier
/// generation is still readable and is no longer fanned out to, which is a different fact from being
/// resolved.
/// </summary>
public sealed record VersionResponse(
    string VariantId,
    int Generation,
    string Family,
    string Definition,
    string Target,
    int MinimumSample,
    string MinimumSampleUnit,
    string Status,
    string? ResolvedAt,
    string CreatedAt,
    bool IsBaseline,
    bool Live,
    string? Direction,
    string? Gate,
    string? ThresholdName,
    string? ThresholdFrom,
    string? ThresholdTo,
    string? Moved,
    IReadOnlyList<SideResponse> Sides);

/// <summary>
/// One side of one version. There is no field here over both sides and that is the point of the
/// shape.
/// </summary>
public sealed record SideResponse(
    string Direction,
    int NightsScored,
    int NightsCarryingADifference,
    int Unscoreable,
    int BaselineOutsideCap,
    int VariantOutsideCap,
    IReadOnlyList<NightResponse> Nights);

/// <summary>
/// One night of the difference series. The three returns are text because they are decimals in the
/// store, and they are null together on exactly the nights carrying a reason.
/// </summary>
public sealed record NightResponse(
    string SessionDate,
    int HorizonDays,
    int Flagged,
    int BaselineSelected,
    int VariantSelected,
    int BothSelected,
    int BaselineOnly,
    int VariantOnly,
    string? BaselineMeanReturn,
    string? VariantMeanReturn,
    string? MeanDifference,
    int Unscoreable,
    string? WithheldBecause);

/// <summary>The holdout budget: what exists, what is spent, and why it holds nothing where it does.</summary>
public sealed record HoldoutResponse(
    int Capacity,
    int Matured,
    int Recorded,
    int Spent,
    int Available,
    string? FirstSession,
    string? EmptyBecause,
    bool Exhausted,
    IReadOnlyList<string> Missing,
    IReadOnlyList<WindowResponse> Windows);

/// <summary>One holdout window, and the spend on it where it carries one.</summary>
public sealed record WindowResponse(
    string WindowId,
    int Ordinal,
    string Start,
    string End,
    string MaturesOn,
    string? SpentOn,
    string? Outcome,
    string? SpentAt);

/// <summary>What the last scoring run settled, which is how a night that scored nothing is told from one that never ran.</summary>
public sealed record ScoreRunResponse(
    string SessionDate,
    int VersionsLive,
    int VersionsScored,
    int NightsScored,
    int NightsWaiting,
    int Longs,
    int Shorts,
    int Unscoreable,
    string Outcome,
    string? StoppedBecause);
