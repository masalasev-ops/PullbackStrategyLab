using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// What the entry minute resolved for each plan that triggered in a session, from 7.8.
///
/// <b>Bounded on the observation instant</b>, on the terms every other trading read is: the gate reads
/// these to decide what to place, and a replay of an old session that saw a resolution written after it
/// would place an order the night could not have.
/// see: A reader's signature does not establish point-in-time; the query does
/// </summary>
public static class EntryResolutionReader
{
    public static IReadOnlyList<StoredEntryResolution> ForLiveSession(
        SqliteConnection connection, DateOnly liveSession, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT plan_id, setup_id, variant_id, live_session, ticker, direction,
                   entry_minute, candle_minutes, level, level_value, armed_at, entry_price,
                   session_extreme, candle_extreme, stop_ceiling, stop_basis, stop_price,
                   stop_distance, stop_fraction, shares, risk_budget, risk_at_stake,
                   refused_because, observed_at
              FROM entry_resolution
             WHERE live_session = @live_session
               AND observed_at <= @observed_before
             ORDER BY entry_minute, ticker
            """;

        command.Parameters.AddWithValue("@live_session", StoreText.DateToStorageText(liveSession));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var rows = new List<StoredEntryResolution>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new StoredEntryResolution(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                StoreText.StorageTextToDate(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                StoreText.StorageTextToTimestamp(reader.GetString(6)),
                reader.GetInt32(7),
                reader.GetString(8),
                StoreText.StorageTextToPrice(reader.GetString(9)),
                StoreText.StorageTextToTimestamp(reader.GetString(10)),
                StoreText.StorageTextToPrice(reader.GetString(11)),
                StoreText.StorageTextToPrice(reader.GetString(12)),
                StoreText.StorageTextToPrice(reader.GetString(13)),
                StoreText.StorageTextToRatio(reader.GetString(14)),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : StoreText.StorageTextToPrice(reader.GetString(16)),
                reader.IsDBNull(17) ? null : StoreText.StorageTextToPrice(reader.GetString(17)),
                reader.IsDBNull(18) ? null : StoreText.StorageTextToRatio(reader.GetString(18)),
                reader.IsDBNull(19) ? null : reader.GetInt32(19),
                StoreText.StorageTextToPrice(reader.GetString(20)),
                reader.IsDBNull(21) ? null : StoreText.StorageTextToPrice(reader.GetString(21)),
                reader.IsDBNull(22) ? null : reader.GetString(22),
                StoreText.StorageTextToTimestamp(reader.GetString(23))));
        }

        return rows;
    }
}

/// <summary>One plan's entry, as the minute it was taken resolved it, or why the stop refused it.</summary>
public sealed record StoredEntryResolution(
    string PlanId,
    string SetupId,
    string VariantId,
    DateOnly LiveSession,
    string Ticker,
    string Direction,
    DateTimeOffset EntryMinute,
    int CandleMinutes,
    string Level,
    decimal LevelValue,
    DateTimeOffset ArmedAt,
    decimal EntryPrice,
    decimal SessionExtreme,
    decimal CandleExtreme,
    decimal StopCeiling,
    string? StopBasis,
    decimal? StopPrice,
    decimal? StopDistance,
    decimal? StopFraction,
    int? Shares,
    decimal RiskBudget,
    decimal? RiskAtStake,
    string? RefusedBecause,
    DateTimeOffset ObservedAt);
