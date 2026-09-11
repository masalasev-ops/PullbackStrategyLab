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
/// for the vendor's quota day holding that evening, because `universe-build` alone spends about two
/// thousand calls and a cost set against the whole ceiling would read far too comfortable.
///
/// <b>Which names get minutes when the ceiling binds is the fetch's own order, and the report names
/// them.</b> The fetch asks for names in ticker order and stops at the ceiling, so where a night's
/// cost exceeds the headroom the names past the last one it could afford go without, and the report
/// lists them rather than leaving the order to be inferred.
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

    /// <summary>When the fetch runs on a night, which is what places the night in the vendor's quota day.</summary>
    public static readonly TimeOnly EveningOfTheNight = new(20, 30);

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
                + $"{night.CallCost} call(s) against a headroom of {night.Headroom}, the ceiling of {report.Ceiling} less "
                + $"{night.SpentElsewhere} the other stages spent; generation 0 flagged "
                + $"{night.GenerationZeroFlagged} at {night.GenerationZeroCallCost}"
                + (night.WithoutMinutes.Count == 0 ? string.Empty : $"; {night.WithoutMinutes.Count} name(s) would get no minutes"));
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

            // The evening the night's stages ran in, and what every stage but the fetch spent in the
            // vendor's day holding it.
            int spentElsewhere = RunLogger.CallsUsedOn(
                connection,
                VendorQuotaDay.Containing(SessionBoundaries.At(night, EveningOfTheNight, _options.SessionZone)),
                IntradayFetcher.Name);

            int headroom = Math.Max(0, ceiling - spentElsewhere);
            int affordable = headroom / EodhdClient.IntradayCost;

            nights.Add(new ForecastNight(
                night,
                longs.Length,
                shorts.Length,
                flagged.Length,
                flagged.Length * EodhdClient.IntradayCost,
                spentElsewhere,
                headroom,
                generationZero.Count,
                generationZero.Count * EodhdClient.IntradayCost,
                [.. flagged.Skip(affordable)]));
        }

        string path = Write(from, to, ceiling, nights);
        run.Complete(RunOutcome.Clean);

        return new GenerationOneForecastReport(from, to, ceiling, nights, path);
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
            order = "ticker order, the fetch's own, stopping where the headroom runs out",
            headroom = "the ceiling less what every other stage spent in the vendor's quota day holding the night's evening",
            nights = nights.Select(n => new
            {
                asOf = n.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                n.Long,
                n.Short,
                n.Flagged,
                n.CallCost,
                n.SpentElsewhere,
                n.Headroom,
                n.GenerationZeroFlagged,
                n.GenerationZeroCallCost,
                n.WithoutMinutes,
            }),
        };

        File.WriteAllText(
            Path.Combine(folder, stem + ".json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Generation 1's flagged count and minute cost, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{EodhdClient.IntradayCost} calls a name, in ticker order, against the ceiling of {ceiling} less what the day's other stages spent.");

        foreach (ForecastNight night in nights)
        {
            text.AppendLine(
                $"{night.AsOf:yyyy-MM-dd}: {night.Flagged} flagged ({night.Long} long, {night.Short} short), {night.CallCost} calls "
                + $"against a headroom of {night.Headroom}; generation 0 {night.GenerationZeroFlagged} at {night.GenerationZeroCallCost}"
                + (night.WithoutMinutes.Count == 0 ? string.Empty : $"; no minutes for {string.Join(" ", night.WithoutMinutes)}"));
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
    int SpentElsewhere,
    int Headroom,
    int GenerationZeroFlagged,
    int GenerationZeroCallCost,
    IReadOnlyList<string> WithoutMinutes);

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
