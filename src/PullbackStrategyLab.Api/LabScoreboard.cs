using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the scoreboard reads. One day's panels, as they were stored.
///
/// <b>It reads what the builder wrote and computes nothing.</b> A read surface that recomputed a
/// bound or an interval would be a second implementation of the arithmetic the whole phase turns
/// on, and the two would eventually disagree with the page as the last place anybody looked. The
/// same argument the averages already won.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// <b>Long and short come back as separate lists.</b> Not one list with a direction column: any
/// figure that would require adding a long result to a short result is not displayed at all, and the
/// shape of the wire is what makes that easy rather than remembered.
/// see: Long and short are never pooled into one figure
/// </summary>
public static class LabScoreboard
{
    public static ScoreboardResponse Read(StoreConnectionFactory connections, DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(connections);

        if (!connections.StoreExists)
        {
            return ScoreboardResponse.Empty(asOf, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        var health = new List<PanelResponse>();
        var longSide = new List<PanelResponse>();
        var shortSide = new List<PanelResponse>();

        using SqliteCommand command = connection.CreateCommand();

        // The latest day at or before the one asked for, rather than that day exactly. A scoreboard
        // opened on a Sunday should show Friday rather than an empty page, and a page that showed
        // nothing would read as "the lab has measured nothing" instead of "no panels were built
        // today".
        // Both halves bound `computed_at`, and both need it. The inner query picks which day to
        // show and the outer reads that day's panels; a bound on one of them only would either pick
        // a day from panels the reader may not see, or read a later rebuild of the day it picked.
        // Latent rather than live until 3.8, because nothing had rebuilt a scoreboard for a past
        // date, and the repair this checkpoint adds is exactly the operation that does.
        command.CommandText = """
            SELECT panel, direction, figure, low, high, n_rows, n_effective, population, n_minimum,
                   withheld_because
              FROM scoreboard
             WHERE computed_at <= @computed_before
               AND as_of = (SELECT MAX(as_of)
                              FROM scoreboard
                             WHERE as_of <= @as_of AND computed_at <= @computed_before)
             ORDER BY panel, direction
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_before", StoreText.DateToStorageText(asOf) + "T23:59:59.999Z");

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            var panel = new PanelResponse(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? "population not recorded" : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));

            if (panel.Direction is null)
            {
                health.Add(panel);
            }
            else if (string.Equals(panel.Direction, "long", StringComparison.Ordinal))
            {
                longSide.Add(panel);
            }
            else
            {
                shortSide.Add(panel);
            }
        }

        if (health.Count == 0 && longSide.Count == 0 && shortSide.Count == 0)
        {
            return ScoreboardResponse.Empty(asOf, "no panels have been built yet");
        }

        return new ScoreboardResponse(asOf, null, health, longSide, shortSide);
    }
}

/// <summary>One day's panels, with the two sides apart on the wire.</summary>
public sealed record ScoreboardResponse(
    DateOnly AsOf,
    string? Absent,
    IReadOnlyList<PanelResponse> Health,
    IReadOnlyList<PanelResponse> Long,
    IReadOnlyList<PanelResponse> Short)
{
    public static ScoreboardResponse Empty(DateOnly asOf, string why) =>
        new(asOf, why, [], [], []);
}

/// <summary>
/// One panel. <c>Effective</c> is a different number from <c>Rows</c>, and is reported from the
/// first night rather than only once an interval exists: it is the number a checkpoint fires on, so
/// watching it climb is the point.
///
/// <c>Minimum</c> is what it has to reach, and is null on every panel no checkpoint fires on.
///
/// <c>WithheldBecause</c> is why a panel shows no figure, which is a different question from
/// whether the minimum is reached and is settled by a different thing: the interval needs
/// sessions, the decision needs evidence, and the two can disagree.
/// </summary>
public sealed record PanelResponse(
    string Name,
    string? Direction,
    string Figure,
    string? Low,
    string? High,
    int Rows,
    int? Effective,
    string Population,
    int? Minimum,
    string? WithheldBecause);
