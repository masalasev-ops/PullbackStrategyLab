namespace PullbackStrategyLab.Web.Shell;

/// <summary>
/// The six screens, named once. The layout renders this list and nothing hardcodes a second
/// copy of it, so a page added without a nav entry is a page nobody can reach and a nav entry
/// without a page is a link that 404s, and a test can assert both against one list.
///
/// The chart is not among them: it is reached for a ticker rather than browsed to, and a tab
/// leading to a page that asks "which stock?" is a tab nobody uses.
///
/// <b>Six from 6.8, and this comment said "matching the screens the architecture describes" while
/// the component catalogue held a sixth.</b> The pack comparison surface is a
/// screen: its subject is the set of pack versions, and every screen here is keyed on one night,
/// one setup or one trade, so hanging the comparison off any of them would key a comparison across
/// versions to one of those three. 6.0(b) settled that the catalogue is right and this list is
/// short; the entry arrives with the page, because a nav entry without a page is a link that 404s
/// and the test below asserts both against this one list.
/// see: The pack comparison surface is the sixth screen and the navigation says six
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
        new("Packs", "/packs", "Every evidence-pack version side by side, and what was proposed against each", "6.8"),
    ];
}

/// <summary>
/// One nav entry. <paramref name="ArrivesAt"/> is the checkpoint that fills the page, and the
/// empty state says it out loud: a page that says what it is waiting for is honest, and a page
/// of invented rows is not.
/// </summary>
public sealed record NavigationItem(string Title, string Path, string What, string ArrivesAt);
