namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The five screens, named once. The layout renders this list and nothing hardcodes a second
/// copy of it, so a page added without a nav entry is a page nobody can reach and a nav entry
/// without a page is a link that 404s, and a test can assert both against one list.
///
/// Five, matching the screens the architecture describes. The chart is not among them: it is
/// reached for a ticker rather than browsed to, and a sixth tab leading to a page that asks
/// "which stock?" is a tab nobody uses.
/// </summary>
public static class Navigation
{
    public static IReadOnlyList<NavigationItem> Items { get; } =
    [
        new("Watchlist", "/watchlist", "The morning screen, long and short divided", "4.1"),
        new("Setups", "/setups", "Last night's flagged setups as a gallery of marked-up charts", "2.9"),
        new("Journal", "/journal", "Closed trades with their loss causes", "4.11"),
        new("Scoreboard", "/scoreboard", "Is the pattern real, can the lab sort it, is the loop learning", "3.5"),
        new("Research", "/research", "Proposals, samples, targets, the holdout register", "5.5"),
    ];
}

/// <summary>
/// One nav entry. <paramref name="ArrivesAt"/> is the checkpoint that fills the page, and the
/// empty state says it out loud: a page that says what it is waiting for is honest, and a page
/// of invented rows is not.
/// </summary>
public sealed record NavigationItem(string Title, string Path, string What, string ArrivesAt);
