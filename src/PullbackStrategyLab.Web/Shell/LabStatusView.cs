using System.Globalization;

namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The status band's contents as the shell renders them, and its own shape rather than the
/// Api's type.
///
/// The Web project references Core alone and reads through HTTP, so the two ends agree on a
/// wire format rather than on an assembly. That is the isolation being real rather than
/// declared: a page cannot acquire a store connection because there is no type here that could
/// hold one.
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public sealed record LabStatusView(
    bool Reachable,
    string? Unreachable,
    string Store,
    int SchemaVersion,
    int SchemaVersionExpected,
    string? Session,
    string? LastRunStage,
    string? LastRunOutcome,
    long UniverseMembers,
    long BarsStored,
    int CallsUsed,
    int DailyCallCeiling,
    string? MarketMood,
    int? PositionsOpen,
    int? ShortPositionsOpen,
    decimal? RiskAtStake)
{
    /// <summary>
    /// The band when the read surface did not answer.
    ///
    /// Shown rather than thrown. The Api and the pages are two hosts started separately, so one
    /// of them being down is an ordinary state of the machine rather than an error in the page,
    /// and a page that would not render without it would be a page nobody could use to find out
    /// what was wrong.
    /// </summary>
    public static LabStatusView Down(string why) =>
        new(false, why, "unreachable", 0, 0, null, null, null, 0, 0, 0, 0, null, null, null, null);

    public string SessionText => Session ?? "no session recorded";

    public string LastRunText => LastRunStage is null
        ? "nothing has run"
        : $"{LastRunStage} · {LastRunOutcome}";

    public string CallsText => string.Create(
        CultureInfo.InvariantCulture, $"{CallsUsed:N0} of {DailyCallCeiling:N0}");

    /// <summary>
    /// Whether the store is at a version other than the one the running build was written against.
    ///
    /// The version was already on the band and had nothing beside it, so the number was there to be
    /// read and there was nothing to read it against. On 2026-08-28 it said 30 while the build
    /// needed 32, four stages died on a column the store had not got, and the band carried the
    /// figure that would have said so all night.
    /// </summary>
    public bool SchemaBehind => Store == "ready" && SchemaVersion != SchemaVersionExpected;

    public string StoreText => Store switch
    {
        "ready" => string.Create(CultureInfo.InvariantCulture,
            $"schema {SchemaVersion} of {SchemaVersionExpected} · {UniverseMembers:N0} names · {BarsStored:N0} bars"),
        "no-store" => "no store yet, run tools/migrate",
        _ => "unreachable",
    };

    /// <summary>
    /// A figure the lab does not produce yet, with the checkpoint that will. Never a zero: a
    /// zero reads as "none open" where the truth is "positions are not a thing yet", and those
    /// are different statements.
    /// </summary>
    public static string Awaiting(string checkpoint) => "not until " + checkpoint;
}
