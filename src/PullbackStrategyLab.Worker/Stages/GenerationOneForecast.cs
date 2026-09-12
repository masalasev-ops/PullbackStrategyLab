using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// What generation 1 would have flagged on nights the lab has already recorded, and what buying their
/// minutes would cost, stated before the switch rather than discovered on its first night. From 7.11.
///
/// <b>Why it is owed.</b> Generation 1 retires `exit-tight` at selection and records the slower names
/// `moves-enough` used to discard, so its nightly flagged count differs from generation 0's by a factor
/// nothing had measured, and <see cref="IntradayFetcher"/> spends
/// <see cref="EodhdClient.IntradayCost"/> calls a name against a daily ceiling. A switch whose first
/// night overran the ceiling would lose minutes that cannot be bought back as that night's evidence.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
///
/// <b>It runs generation 1's detector over each night's own snapshot</b>, the same
/// <see cref="GenerationOneDetector"/> the switch night uses, over the bars and figures the store held
/// on that night, so the count is the one the detector would have produced rather than a proxy for it.
/// A flagged name is one generation 1 records, either side, counted once, because that is the list the
/// fetch buys minutes for.
///
/// <b>The headroom is the ceiling less what the day's other stages spent</b>, read from the run log
/// for the vendor's quota day the fetch of this night's list falls in, because `universe-build` alone
/// spends about two thousand calls and a cost set against the whole ceiling would read far too
/// comfortable.
///
/// <b>A night's list is bought on the next session's evening, and pairing it with its own evening was
/// wrong until 7.14.</b> The 20:30 fetch buys the minutes of the session that has just closed for the
/// names flagged on the evening before it, so night N's list is bought on the evening of the session
/// after N. This read the quota day holding N's own evening, which is the day the previous night's
/// list spends in. Monday to Wednesday the two days hold much the same spend and the error is small;
/// a Friday's list was set against a Saturday UTC day holding almost nothing where its fetch really
/// spends beside Tuesday's `universe-build`. Found at the 7.12 sign-off, which could not repair it.
///
/// <b>An overrun does not cost the fetch's later names, and saying it did was the second half of the
/// same error.</b> The quota day opens at midnight UTC, which is 20:00 Eastern in summer, so the
/// 20:30 fetch is the first stage to spend in its day: `RunLogger` hands it nearly the whole ceiling
/// and it buys every name on the list. What the ceiling then cuts is the stages of the evening after
/// it, `universe-build` among them, and that is the one stage no rerun replaces. So the report states
/// what a night is over its headroom by and what that costs, and lists no names at all.
///
/// <b>The flagged count is one list across both sides, and that is the fetch's list rather than a
/// pooled figure.</b> A name flagged long and short is one name to buy minutes for, so its size and
/// its cost are counts of what is bought; the two sides' own counts are stated beside them and never
/// added into a figure about either (see: Long and short are never pooled into one figure).
///
/// <b>Manual, on a store copy, and it writes no table.</b> The report is a file under the data root,
/// on the terms <see cref="EntryRuleMeasurement"/> writes its own.
/// </summary>
public sealed class GenerationOneForecast
{
    public const string Name = "forecast-generation-one";

    /// <summary>When the fetch runs, which is what places a list in the vendor's quota day.</summary>
    public static readonly TimeOnly EveningOfTheNight = new(20, 30);

    /// <summary>How far forward the next recorded session is looked for before the pairing is assumed.</summary>
    public const int DaysToLookForTheNextSession = 10;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;
    private readonly PullbackStrategyLabPaths _paths;

    public GenerationOneForecast(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options,
        PullbackStrategyLabPaths paths)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
        _paths = paths;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string[] dates = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];

        if (dates.Length < 2
            || !DateOnly.TryParseExact(dates[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly from)
            || !DateOnly.TryParseExact(dates[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly to))
        {
            Console.Error.WriteLine($"{Name}: name the nights. usage: {Name} <from yyyy-MM-dd> <to yyyy-MM-dd>");
            return 2;
        }

        GenerationOneForecastReport report = Forecast(from, to);

        foreach (ForecastNight night in report.Nights)
        {
            Console.WriteLine(
                $"{Name}: {night.AsOf:yyyy-MM-dd}, {night.Flagged} name(s) flagged ({night.Long} long, {night.Short} short), "
                + $"{night.CallCost} call(s) bought on {night.BoughtOn:yyyy-MM-dd} in quota day {night.QuotaDay:yyyy-MM-dd} "
                + $"against a headroom of {night.Headroom}, the ceiling of {report.Ceiling} less "
                + $"{night.SpentElsewhere} the other stages spent; generation 0 flagged "
                + $"{night.GenerationZeroFlagged} at {night.GenerationZeroCallCost}"
                + (night.BoughtOnIsRecorded ? string.Empty : "; no session is recorded after this night, so its evening is assumed")
                + (night.OverBy == 0 ? string.Empty : $"; over by {night.OverBy} call(s), which the next evening's first stages pay"));
        }

        Console.WriteLine($"{Name}: {report.Nights.Count} night(s), {report.NightsOverTheHeadroom} over the headroom");
        Console.WriteLine($"{Name}: report written to {report.Path}");
        return 0;
    }

    /// <summary>Every night from <paramref name="from"/> to <paramref name="to"/> that holds a universe snapshot.</summary>
    public GenerationOneForecastReport Forecast(DateOnly from, DateOnly to)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name);

        var detector = new GenerationOneDetector(_connections, Microsoft.Extensions.Options.Options.Create(_options));
        int ceiling = _options.DailyCallCeiling;
        var nights = new List<ForecastNight>();

        for (DateOnly night = from; night <= to; night = night.AddDays(1))
        {
            if (UniverseSnapshotReader.Members(connection, night).Count == 0)
            {
                continue;
            }

            GenerationOneNight evaluated = detector.Evaluate(night);

            string[] longs = [.. evaluated.Long.Where(v => v.Recorded(isLong: true)).Select(v => v.Ticker)];
            string[] shorts = [.. evaluated.Short.Where(v => v.Recorded(isLong: false)).Select(v => v.Ticker)];
            string[] flagged = [.. longs.Concat(shorts).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            IReadOnlyList<string> generationZero = IntradayFetcher.FlaggedNames(connection, night);

            // The evening this night's list is bought on, and the vendor day that evening's fetch
            // spends in, which is the day after it in UTC.
            (DateOnly buyingEvening, bool recorded) = NextSession(connection, night);
            VendorQuotaDay quotaDay = VendorQuotaDay.Containing(
                SessionBoundaries.At(buyingEvening, EveningOfTheNight, _options.SessionZone));

            int spentElsewhere = RunLogger.CallsUsedOn(connection, quotaDay, IntradayFetcher.Name);
            int headroom = Math.Max(0, ceiling - spentElsewhere);
            int callCost = flagged.Length * EodhdClient.IntradayCost;

            nights.Add(new ForecastNight(
                night,
                longs.Length,
                shorts.Length,
                flagged.Length,
                callCost,
                buyingEvening,
                recorded,
                quotaDay.Date,
                spentElsewhere,
                headroom,
                generationZero.Count,
                generationZero.Count * EodhdClient.IntradayCost,
                Math.Max(0, callCost - headroom)));
        }

        string path = Write(from, to, ceiling, nights);
        run.Complete(RunOutcome.Clean);

        return new GenerationOneForecastReport(from, to, ceiling, nights, path);
    }

    /// <summary>
    /// The session whose evening buys this night's list, and whether the store records it.
    ///
    /// <b>The store's own sessions rather than a calendar</b>, because the lab authors no market
    /// calendar and a Friday's list is bought on Monday's evening. Where the store records no session
    /// after the night, which is the ordinary state of the last night in a range, the next calendar
    /// day stands in and the report says so of that night rather than quietly reading a day of its
    /// own choosing. RUNBOOK's step 2 tells the operator to end the range one session past the last
    /// night they want read, which is what keeps that stand-in off the nights they are reading.
    /// </summary>
    private static (DateOnly Evening, bool Recorded) NextSession(SqliteConnection connection, DateOnly night)
    {
        for (int ahead = 1; ahead <= DaysToLookForTheNextSession; ahead++)
        {
            DateOnly candidate = night.AddDays(ahead);

            if (UniverseSnapshotReader.Members(connection, candidate).Count > 0)
            {
                return (candidate, true);
            }
        }

        return (night.AddDays(1), false);
    }

    private string Write(DateOnly from, DateOnly to, int ceiling, IReadOnlyList<ForecastNight> nights)
    {
        string folder = Path.Combine(_paths.DataRoot, EntryRuleMeasurement.ReportsDirectory);
        Directory.CreateDirectory(folder);

        string stem = $"generation-one-forecast-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}";

        var document = new
        {
            from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ceiling,
            callsPerName = EodhdClient.IntradayCost,
            boughtOn = "the evening of the session after the night, which is when the fetch buys that night's list",
            headroom = "the ceiling less what every other stage spent in the vendor's quota day the fetch falls in",
            overrun = "a night over its headroom costs the next evening's first stages, universe-build among them, "
                + "because the fetch is the first stage to spend in a quota day that opens at midnight UTC",
            nights = nights.Select(n => new
            {
                asOf = n.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                n.Long,
                n.Short,
                n.Flagged,
                n.CallCost,
                boughtOn = n.BoughtOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                boughtOnIsRecorded = n.BoughtOnIsRecorded,
                quotaDay = n.QuotaDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                n.SpentElsewhere,
                n.Headroom,
                n.GenerationZeroFlagged,
                n.GenerationZeroCallCost,
                n.OverBy,
            }),
        };

        File.WriteAllText(
            Path.Combine(folder, stem + ".json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Generation 1's flagged count and minute cost, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{EodhdClient.IntradayCost} calls a name, against the ceiling of {ceiling} less what the other stages spent in the quota day the fetch of that night's list falls in.");
        text.AppendLine(
            "A night's list is bought on the evening of the session after it, and a night over its headroom costs "
            + "the next evening's first stages rather than its own later names.");

        foreach (ForecastNight night in nights)
        {
            text.AppendLine(
                $"{night.AsOf:yyyy-MM-dd}: {night.Flagged} flagged ({night.Long} long, {night.Short} short), {night.CallCost} calls "
                + $"bought on {night.BoughtOn:yyyy-MM-dd}{(night.BoughtOnIsRecorded ? string.Empty : " (no session recorded after this night, so the evening is assumed)")} "
                + $"in quota day {night.QuotaDay:yyyy-MM-dd}, against a headroom of {night.Headroom}; "
                + $"generation 0 {night.GenerationZeroFlagged} at {night.GenerationZeroCallCost}"
                + (night.OverBy == 0 ? string.Empty : $"; over by {night.OverBy} call(s), which the next evening's first stages pay"));
        }

        File.WriteAllText(Path.Combine(folder, stem + ".txt"), text.ToString(), new UTF8Encoding(false));

        return Path.Combine(EntryRuleMeasurement.ReportsDirectory, stem + ".json");
    }
}

/// <summary>One night of the forecast, generation 1 beside what generation 0 flagged, per side and never pooled into one side.</summary>
public sealed record ForecastNight(
    DateOnly AsOf,
    int Long,
    int Short,
    int Flagged,
    int CallCost,
    DateOnly BoughtOn,
    bool BoughtOnIsRecorded,
    DateOnly QuotaDay,
    int SpentElsewhere,
    int Headroom,
    int GenerationZeroFlagged,
    int GenerationZeroCallCost,
    int OverBy);

/// <summary>The forecast over a range of nights, and where its report was written, relative to the data root.</summary>
public sealed record GenerationOneForecastReport(
    DateOnly From,
    DateOnly To,
    int Ceiling,
    IReadOnlyList<ForecastNight> Nights,
    string Path)
{
    public int NightsOverTheHeadroom => Nights.Count(n => n.CallCost > n.Headroom);
}
