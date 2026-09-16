using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// What the experiment has done since it started, as at the date asked for: how many evenings it
/// holds, how many patterns it recorded on each side, how many plans it wrote, and how many of
/// those became trades.
///
/// <b>Every figure is per direction and none is a total.</b> A front page is where the pooling rule
/// is easiest to break, because "1,025 patterns found" reads better than "645 to buy and 380 to
/// sell short" and says something the lab does not mean. A short carries a borrow assumption and a
/// different risk, so the two are counted apart here exactly as they are counted apart everywhere
/// else (see: Long and short are never pooled into one figure).
///
/// <b>It reports the evenings it holds rather than the days since it started.</b> The lab has run
/// on seven evenings between 2026-08-27 and 2026-09-15 and was not running for the nine trading
/// days in between, so a page saying "recording since 27 August" would assert a continuity the
/// record does not have. The count, the first and the last are all stated, which is what lets a
/// reader see the gap rather than be told there is none.
///
/// <b>The funnel is the latest evening's and it is per side too.</b> Examined is what the detector
/// looked at, which is the rows it recorded plus the rows that missed the recording floor, so the
/// arithmetic is the store's rather than a figure copied out of a log nobody reads.
/// see: The Web project reads through the Api and never opens the store
/// </summary>
public static class LabExperiment
{
    public static ExperimentResponse Read(
        StoreConnectionFactory connections, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connections);

        string date = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!connections.StoreExists)
        {
            return ExperimentResponse.Empty(date, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        IReadOnlyList<DateOnly> evenings = Evenings(connection, asOf);

        if (evenings.Count == 0)
        {
            return ExperimentResponse.Empty(date, "the store records no evening yet");
        }

        DateOnly latest = evenings[^1];

        return new ExperimentResponse(
            date,
            null,
            evenings.Count,
            evenings[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            latest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            LatestEveningGeneration(connection, asOf),
            Side(connection, asOf, sessionZone, SetupDirection.Long),
            Side(connection, asOf, sessionZone, SetupDirection.Short),
            Risk(connection, asOf, sessionZone));
    }

    /// <summary>
    /// The evenings the store holds a detection for, up to and including the date asked for. Read
    /// from <c>setup</c> rather than from the run log, because an evening whose detector ran and
    /// recorded nothing is not an evening of evidence.
    /// </summary>
    private static IReadOnlyList<DateOnly> Evenings(SqliteConnection connection, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT as_of FROM setup WHERE as_of <= @as_of ORDER BY as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        var evenings = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            evenings.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        return evenings;
    }

    /// <summary>
    /// One side's whole record, and the latest evening's funnel on that side. Never added to the
    /// other side's by this type or by anything that reads it.
    /// </summary>
    private static ExperimentSide Side(
        SqliteConnection connection, DateOnly asOf, string sessionZone, string direction)
    {
        // Each statement is one literal with its own bound in it. Written out rather than composed
        // from pieces because `point-in-time` reads a statement as it appears in the source, so a
        // bound arriving by concatenation is a bound the check cannot see, and a check that cannot
        // see a bound is one that would not notice its removal.
        int recorded = Count(connection,
            "SELECT COUNT(*) FROM setup WHERE as_of <= @as_of AND direction = @direction",
            asOf, direction);

        int plans = Count(connection,
            "SELECT COUNT(*) FROM trade_plan WHERE live_session <= @as_of AND direction = @direction AND observed_at <= @observed_before",
            asOf, direction, sessionZone);

        int orders = Count(connection,
            "SELECT COUNT(*) FROM trade_order WHERE live_session <= @as_of AND direction = @direction AND observed_at <= @observed_before",
            asOf, direction, sessionZone);

        int trades = Count(connection,
            "SELECT COUNT(*) FROM trade WHERE closed_session <= @as_of AND direction = @direction AND observed_at <= @observed_before",
            asOf, direction, sessionZone);

        return new ExperimentSide(
            direction, recorded, plans, orders, trades, Funnel(connection, asOf, sessionZone, direction));
    }

    /// <summary>
    /// The latest evening's three rungs on one side: what the detector looked at, what cleared the
    /// recording floor, and what passed every gating check.
    ///
    /// <b>Examined is derived rather than reported.</b> It is the rows recorded plus the rows that
    /// missed the floor, both of which the store holds, so the figure cannot drift from the two it
    /// is made of. The alternative was the count the detector prints to the night's log, and a
    /// figure a surface copies out of a log is a figure nothing reconciles.
    /// </summary>
    private static ExperimentFunnel Funnel(
        SqliteConnection connection, DateOnly asOf, string sessionZone, string direction)
    {
        DateOnly evening = LatestEvening(connection, asOf, direction);
        string date = evening.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        int recorded = Count(connection,
            "SELECT COUNT(*) FROM setup WHERE as_of = @as_of AND direction = @direction",
            evening, direction);

        // Bounded on the evening's own close, like every other stamped read here. A below-floor row
        // is written by the detector on the evening it examined, so the bound changes nothing today
        // and would be the thing that mattered the day a row is written late.
        int belowFloor = Count(connection,
            "SELECT COUNT(*) FROM below_floor WHERE as_of = @as_of AND direction = @direction AND observed_at <= @observed_before",
            evening, direction, sessionZone);

        int passed = Count(connection,
            "SELECT COUNT(*) FROM setup WHERE as_of = @as_of AND direction = @direction AND passed_all = 1",
            evening, direction);

        return new ExperimentFunnel(date, recorded + belowFloor, recorded, passed);
    }

    private static DateOnly LatestEvening(SqliteConnection connection, DateOnly asOf, string direction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT MAX(as_of) FROM setup WHERE as_of <= @as_of AND direction = @direction";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);

        object? answer = command.ExecuteScalar();

        return answer is string text ? StoreText.StorageTextToDate(text) : asOf;
    }

    /// <summary>
    /// What one plan is allowed to lose, read from the most recent plan rather than from
    /// configuration, because the figure a page states is the one the lab actually committed to.
    /// </summary>
    private static ExperimentRisk? Risk(SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT equity, risk_fraction, risk_budget
              FROM trade_plan
             WHERE live_session <= @as_of AND observed_at <= @observed_before
             ORDER BY live_session DESC, observed_at DESC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        // The fraction crosses through the ratio conversion and the two money columns through the
        // price one, because the two crossings are named for what they carry rather than for the
        // column type they happen to share (see: Prices are decimal in code and TEXT in storage).
        return reader.Read()
            ? new ExperimentRisk(
                StoreText.StorageTextToPrice(reader.GetString(0)),
                StoreText.StorageTextToRatio(reader.GetString(1)),
                StoreText.StorageTextToPrice(reader.GetString(2)))
            : null;
    }

    /// <summary>
    /// The generation the latest recorded evening scored under, read from the rows themselves.
    ///
    /// <b>Not read from the variant register, and the reason is that the register cannot answer it
    /// point in time.</b> A baseline row carries its current status and nothing records when that
    /// status changed, so asking the register which generation was open on a past date returns
    /// today's answer wearing that date's clothes: after generation 2 opened on 2026-09-16, the same
    /// query asked for 2026-09-15 found no open baseline at all and said generation 0, on a night
    /// whose rows all carry generation 1.
    ///
    /// Every setup row carries the generation that scored it, which is the fact a reader wants and
    /// the one the store keeps per night rather than per register.
    /// </summary>
    private static int LatestEveningGeneration(SqliteConnection connection, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(generation) FROM setup
             WHERE as_of = (SELECT MAX(as_of) FROM setup WHERE as_of <= @as_of)
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        object? answer = command.ExecuteScalar();

        return answer is long generation ? (int)generation : 0;
    }

    private static int Count(
        SqliteConnection connection, string sql, DateOnly asOf, string direction, string? sessionZone = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@direction", direction);

        if (sessionZone is not null)
        {
            command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));
        }

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The experiment's record as at one date. <paramref name="Long"/> and <paramref name="Short"/> are
/// separate fields rather than a list, so no caller can sum them without writing the addition out
/// and having it read back in review.
/// </summary>
public sealed record ExperimentResponse(
    string AsOf,
    string? Absent,
    int Evenings,
    string? FirstEvening,
    string? LatestEvening,
    int LatestEveningGeneration,
    ExperimentSide? Long,
    ExperimentSide? Short,
    ExperimentRisk? Risk)
{
    public static ExperimentResponse Empty(string asOf, string absent) =>
        new(asOf, absent, 0, null, null, 0, null, null, null);
}

/// <summary>One side's record. Never added to the other's.</summary>
public sealed record ExperimentSide(
    string Direction,
    int PatternsRecorded,
    int PlansWritten,
    int OrdersPlaced,
    int TradesClosed,
    ExperimentFunnel Funnel);

/// <summary>The latest evening's three rungs on one side.</summary>
public sealed record ExperimentFunnel(string Evening, int Examined, int Recorded, int Passed);

/// <summary>What one plan is allowed to lose, as the most recent plan recorded it.</summary>
public sealed record ExperimentRisk(decimal Equity, decimal Fraction, decimal Budget);
