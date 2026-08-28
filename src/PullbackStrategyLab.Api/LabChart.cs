using System.Globalization;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Api;

/// <summary>
/// One stock's window: candles on the adjusted basis, the three averages drawn across them, and
/// the readout the lab acted on.
///
/// The averages are computed here from the stored bars, through the arithmetic in Core that
/// IndicatorEngine also calls. Nothing is written: a chart is a picture of one stock, and
/// writing an average for a session the lab was not running would be reconstructing evidence
/// the lab has no universe snapshot for.
/// see: The averages are one implementation, computed nightly and drawn on demand
///
/// The readout is read from the store rather than computed, so the page shows the number the
/// lab acted on beside the line it draws. Where both exist they are the same number, which is a
/// property worth being able to see rather than one to take on trust.
/// </summary>
public static class LabChart
{
    /// <summary>The default window, about a quarter, which is what a pullback is read against.</summary>
    public const int DefaultSessions = 60;

    /// <summary>
    /// The longest window the read surface will draw. Not a performance limit: a request for ten
    /// thousand sessions would be answered from bars nobody has, and a bounded answer says so.
    /// </summary>
    public const int MaximumSessions = 500;

    /// <summary>
    /// How many sessions of history the averages are computed over, beyond the window drawn.
    ///
    /// The same warm-up the engine refuses without, for the same reason: a fifty-day average
    /// seeded fifty sessions ago is still carrying its seed. Drawing a converged line means
    /// reading well behind the left edge of the picture and starting the line at the edge.
    /// </summary>
    public const int WarmupSessions = 150;

    public static ChartResponse Read(
        StoreConnectionFactory connections,
        string ticker,
        DateOnly asOf,
        int sessions,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        ticker = ticker.Trim().ToUpperInvariant();

        // The ask is kept as it arrived and the clamp is applied separately, so the response can
        // say it truncated. Requested used to be assigned the clamped value, which made a window
        // of 750 come back as "requested 500, drawn 500" with nothing anywhere recording that the
        // caller had asked for more. A field named Requested that reports what was served instead
        // cannot report a truncation, and a test pinned that reading in.
        int requested = sessions;
        sessions = Math.Clamp(sessions, 1, MaximumSessions);

        if (!connections.StoreExists)
        {
            return ChartResponse.Empty(ticker, sessions, "there is no store yet");
        }

        using SqliteConnection connection = connections.OpenReadOnly();

        // Read the window plus its warm-up in one go, then draw only the window. Reading the
        // window alone would draw a fifty-day average over sixty sessions, which is a line
        // still climbing out of its own seed for most of the picture.
        IReadOnlyList<StoredDailyBar> window =
            DailyBarReader.Read(connection, ticker, asOf, sessions + WarmupSessions, observedBefore);

        if (window.Count == 0)
        {
            return ChartResponse.Empty(ticker, sessions,
                $"no stored bars for {ticker} on or before {asOf:yyyy-MM-dd}");
        }

        // The adjusted basis, the same crossing the engine makes: the store holds an adjusted
        // close and a raw open, high and low, so the three are put on the adjusted basis through
        // each bar's own factor. A chart that mixed the two would show a split as a cliff.
        var adjustedClose = new decimal[window.Count];
        var bars = new ChartBar[window.Count];

        for (int i = 0; i < window.Count; i++)
        {
            StoredDailyBar bar = window[i];
            decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;

            adjustedClose[i] = bar.AdjustedClose;
            bars[i] = new ChartBar(
                bar.BarDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                bar.Open * factor,
                bar.High * factor,
                bar.Low * factor,
                bar.AdjustedClose,
                bar.Volume);
        }

        int drawnFrom = Math.Max(0, window.Count - sessions);

        ChartAverage Line(string name, int period) => new(
            name,
            period,
            [.. Averages.ExponentialSeries(adjustedClose, period, WarmupSessions).Skip(drawnFrom)]);

        StoredIndicators? readout = IndicatorDailyReader.Read(connection, ticker, asOf, asOf);

        return new ChartResponse(
            ticker,
            asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            requested,
            window.Count - drawnFrom,
            window.Count,
            [.. bars.Skip(drawnFrom)],
            [Line("ema9", 9), Line("ema21", 21), Line("ema50", 50)],
            readout is null
                ? null
                : new ChartReadout(
                    readout.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    readout.EmaShort,
                    readout.EmaMedium,
                    readout.EmaLong,
                    readout.AverageTrueRange,
                    readout.AverageDailyRange,
                    readout.DollarVolumeMedian,
                    readout.RangeAverage),
            null);
    }
}

/// <summary>
/// One stock's window as the read surface answers it. <paramref name="Nothing"/> is a reason
/// rather than an error: a ticker the store has never held is an ordinary thing to ask for.
/// </summary>
public sealed record ChartResponse(
    string Ticker,
    string AsOf,
    int Requested,
    int Drawn,
    int Read,
    IReadOnlyList<ChartBar> Bars,
    IReadOnlyList<ChartAverage> Averages,
    ChartReadout? Readout,
    string? Nothing)
{
    /// <summary>A window there was nothing to draw, with the reason in the field that says so.</summary>
    public static ChartResponse Empty(string ticker, int sessions, string why) =>
        new(ticker, string.Empty, sessions, 0, 0, [], [], null, why);
}

/// <summary>One session, on the adjusted basis. Prices are decimal on the wire as they are in the store.</summary>
public sealed record ChartBar(string Date, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>One average across the window, null at a session before it had converged.</summary>
public sealed record ChartAverage(string Name, int Period, IReadOnlyList<decimal?> Values);

/// <summary>
/// What the lab computed for this session and stored. The number it acted on, beside the line
/// the page draws.
/// </summary>
public sealed record ChartReadout(
    string AsOf,
    decimal Ema9,
    decimal Ema21,
    decimal Ema50,
    decimal Atr14,
    decimal Adr20,
    decimal DollarVolumeMedian,
    decimal RangeAverage);
