namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// Every navigable surface, named once and in two groups. The layout renders these lists and
/// nothing hardcodes a second copy of them, so a page added without a nav entry is a page nobody
/// can reach and a nav entry without a page is a link that 404s, and a test asserts both against
/// these lists rather than against a number.
///
/// The chart is in neither group: it is reached for a ticker rather than browsed to, and a tab
/// leading to a page that asks "which stock?" is a tab nobody uses. It is the one routed page the
/// shell test names as deliberately absent.
///
/// <b>Two groups from 7.20, and the count is derived rather than declared.</b> The entry this
/// supersedes wrote "six" into its own name, so the name went stale the moment a surface was added.
/// What that entry established still holds and is why the pack comparison page is a screen of its
/// own: its subject is the set of pack versions, where every research screen below is keyed on one
/// night, one setup or one trade, so hanging the comparison off any of them would key a comparison
/// across versions to one of those three.
/// see: A surface a person opens is a screen, and the navigation names the way in apart from the research screens
/// </summary>
public static class Navigation
{
    /// <summary>
    /// The way in: what the experiment is, and what it is doing today. Read by somebody who does
    /// not yet know what the lab does, which is what separates them from the research screens
    /// rather than any difference in how they are built.
    /// </summary>
    public static IReadOnlyList<NavigationItem> WaysIn { get; } =
    [
        new("What this is", "/", "What the experiment is, how far it has got, and what would prove it wrong", "7.20"),
        new("Today", "/today", "What the lab is watching today, in plain words, and what it would risk", "7.20"),
    ];

    /// <summary>
    /// The research screens, each keyed on one night, one setup, one trade or the set of pack
    /// versions. Read by somebody who already knows what the lab does and wants the detail.
    /// </summary>
    public static IReadOnlyList<NavigationItem> Screens { get; } =
    [
        new("Watchlist", "/watchlist", "The morning screen, long and short divided", "4.1"),
        new("Setups", "/setups", "Last night's flagged setups as a gallery of marked-up charts", "2.9"),
        new("Journal", "/journal", "Closed trades with their loss causes", "4.11"),
        new("Scoreboard", "/scoreboard", "Is the pattern real, can the lab sort it, is the loop learning", "3.5"),
        new("Research", "/research", "Proposals, samples, targets, the holdout register", "5.5"),
        new("Packs", "/packs", "Every evidence-pack version side by side, and what was proposed against each", "6.8"),
    ];

    /// <summary>
    /// Both groups in the order the layout draws them, for the readers that want every surface
    /// without caring which group it is in.
    /// </summary>
    public static IReadOnlyList<NavigationItem> Items { get; } = [.. WaysIn, .. Screens];
}

/// <summary>
/// One nav entry. <paramref name="ArrivesAt"/> is the checkpoint that fills the page, and the
/// empty state says it out loud: a page that says what it is waiting for is honest, and a page
/// of invented rows is not.
/// </summary>
public sealed record NavigationItem(string Title, string Path, string What, string ArrivesAt);
