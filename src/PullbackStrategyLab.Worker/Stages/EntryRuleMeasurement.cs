using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Generation 1's entry rule run over the calibration minutes, and the report of what it made of them,
/// from 7.9.
///
/// <b>The only look at the sourced rule before it goes live.</b> Every flagged calibration row is taken
/// to its entry session, the rule is watched over that session's minutes from the hourly history before
/// it, and the stop the entry resolves is set against the row's own ceiling. Per side the report says
/// how many rows produce an entry and why the rest did not, how the stops fall against the ceiling, and
/// the win-rate ceiling's bound over the stops that entered.
/// see: Order prices and the share count resolve at the entry minute
///
/// <b>It names the level set it was computed over</b>, the hourly averages alone, and says the anchored
/// average price was not evaluated and why, so a second report over the same minutes can sit beside it
/// once the anchor is ruled.
///
/// <b>On a store copy and never the live store</b>, like every reading of the calibration rows, and it
/// writes no row: the report is a file under the data root's reports folder, which is what the operator
/// reads. An operator verb and never a slot.
/// </summary>
public sealed class EntryRuleMeasurement
{
    public const string Name = "measure-entry-rule";

    /// <summary>The folder under the data root the report is written to.</summary>
    public const string ReportsDirectory = "reports";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;
    private readonly PullbackStrategyLabPaths _paths;

    public EntryRuleMeasurement(
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

        EntryRuleReport report = Measure();

        foreach (EntryRuleReading.Side side in report.Sides)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Name}: {side.Direction}, {side.Rows} row(s): {side.Entered} entered, {side.Refused} refused, "
                + $"{side.NoReclaim} flushed and never reclaimed, {side.NoFlush} never flushed, {side.NoLevels} without a level, "
                + $"{side.NoMinutes} without minutes"));
        }

        Console.WriteLine($"{Name}: level set {EntryRuleReading.LevelSet}; {EntryRuleReading.AnchoredLevelNotEvaluated}");
        Console.WriteLine($"{Name}: report written to {report.Path}");
        return 0;
    }

    /// <summary>Run the rule over every flagged calibration row and write the report.</summary>
    public EntryRuleReport Measure()
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name);

        DateTimeOffset now = run.StartedAt;
        DateOnly asOf = _clock.SessionDate(now, _options.SessionZone);
        string zone = _options.SessionZone;

        DateOnly[] traded = [.. MinuteBackfiller.Sessions(connection, now)];
        var rows = new List<EntryRuleReading.Row>();

        foreach ((string setupId, string ticker, string direction, DateOnly flagged) in CalibrationRows(connection))
        {
            DateOnly entrySession = MinuteBackfiller.EntrySessionFor(flagged, traded);
            rows.Add(RowFor(connection, traded, setupId, ticker, direction, flagged, entrySession, asOf, zone));
        }

        EntryRuleReading.Side[] sides =
        [
            EntryRuleReading.Of(SetupDirection.Long, rows),
            EntryRuleReading.Of(SetupDirection.Short, rows),
        ];

        string path = Write(asOf, sides, rows);
        run.Complete(RunOutcome.Clean);

        return new EntryRuleReport(asOf, sides, rows, path);
    }

    private static EntryRuleReading.Row RowFor(
        SqliteConnection connection, DateOnly[] traded, string setupId, string ticker, string direction,
        DateOnly flagged, DateOnly entrySession, DateOnly asOf, string zone)
    {
        IReadOnlyList<StoredIntradayBar> minutes = CalibrationMinuteReader.Read(connection, ticker, entrySession, asOf, zone);

        if (minutes.Count == 0)
        {
            return Plain(EntryRuleReading.Outcome.NoMinutes);
        }

        var watch = new EntryWatch(
            direction,
            CalibrationMinuteReader.HourlyClosesBefore(connection, ticker, entrySession, asOf, zone),
            entrySession,
            zone);

        foreach (StoredIntradayBar bar in minutes)
        {
            if (watch.Observe(bar.OpenedAt, bar.High, bar.Low, bar.Close) is not null)
            {
                break;
            }
        }

        if (!watch.HadLevels)
        {
            return Plain(EntryRuleReading.Outcome.NoLevels);
        }

        if (watch.Entry is not EntryPoint entry)
        {
            return Plain(watch.Armed ? EntryRuleReading.Outcome.NoReclaim : EntryRuleReading.Outcome.NoFlush);
        }

        IndicatorValues? figures = FiguresOn(connection, ticker, flagged, zone);

        if (figures is null || figures.AverageDailyRange <= 0m)
        {
            return Plain(EntryRuleReading.Outcome.NoLevels);
        }

        decimal ceiling = EntryRule.CeilingFor(figures.AverageDailyRange);
        EntryStop.Resolution stop = EntryStop.Resolve(direction, entry, ceiling);

        EntryRuleReading.Outcome outcome = stop.RefusedBecause is null
            ? EntryRuleReading.Outcome.Entered
            : EntryRuleReading.Outcome.Refused;

        WinRateCeiling.Subject? subject = outcome == EntryRuleReading.Outcome.Entered
            ? SubjectFor(connection, traded, setupId, ticker, direction, figures, entry, stop, minutes, entrySession, zone)
            : null;

        return new EntryRuleReading.Row(setupId, direction, outcome, stop.RefusedBecause, stop.Basis, stop.Fraction, ceiling, subject);

        EntryRuleReading.Row Plain(EntryRuleReading.Outcome o) => new(setupId, direction, o, null, null, null, null, null);
    }

    /// <summary>
    /// The row's figures on the evening it was flagged, computed from the daily bars the store holds up
    /// to it by the engine's own arithmetic, since no nightly run wrote indicators for a calibration date.
    /// </summary>
    private static IndicatorValues? FiguresOn(SqliteConnection connection, string ticker, DateOnly flagged, string zone)
    {
        IReadOnlyList<StoredDailyBar> window = DailyBarReader.Read(connection, ticker, flagged, IndicatorEngine.WarmupSessions, zone);

        return window.Count < IndicatorEngine.EmaLongPeriod || window[^1].BarDate != flagged
            ? null
            : IndicatorEngine.Calculate(window);
    }

    /// <summary>
    /// The win-rate subject for an entered row: where the path went from the entry over the rest of the
    /// entry session and the scoring horizon's daily bars, against the stop the entry resolved, in daily
    /// ranges. Null where the horizon has not closed for the name in the store.
    /// </summary>
    private static WinRateCeiling.Subject? SubjectFor(
        SqliteConnection connection, DateOnly[] traded, string setupId, string ticker, string direction,
        IndicatorValues figures, EntryPoint entry, EntryStop.Resolution stop,
        IReadOnlyList<StoredIntradayBar> minutes, DateOnly entrySession, string zone)
    {
        int at = Array.BinarySearch(traded, entrySession);
        int horizonAt = at + MeasurementParameters.ScoringHorizonSessions;

        if (at < 0 || horizonAt >= traded.Length || stop.Distance is not decimal distance)
        {
            return null;
        }

        DateOnly horizon = traded[horizonAt];
        StoredDailyBar[] after =
        [
            .. DailyBarReader.Read(connection, ticker, horizon, MeasurementParameters.ScoringHorizonSessions, zone)
                .Where(b => b.BarDate > entrySession),
        ];

        if (after.Length == 0 || after[^1].BarDate != horizon)
        {
            return null;
        }

        EntryOutcome.Reading? reading = EntryOutcome.Of(
            direction,
            entry.Price,
            figures.AverageTrueRange,
            [.. minutes.Where(m => m.OpenedAt >= entry.Minute).Select(m => (m.High, m.Low))],
            [.. after.Select(b => (b.High, b.Low, b.Close))]);

        if (reading is null)
        {
            return null;
        }

        decimal rangeInPrice = figures.AverageDailyRange * entry.Price;

        return new WinRateCeiling.Subject(
            setupId, direction, reading.ReturnSigned, reading.MaximumAdverseExcursionAtr,
            figures.AverageTrueRange, rangeInPrice, distance / rangeInPrice);
    }

    private static IEnumerable<(string SetupId, string Ticker, string Direction, DateOnly AsOf)> CalibrationRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT setup_id, ticker, direction, as_of FROM calibration_setup ORDER BY as_of, ticker, direction;";

        var rows = new List<(string, string, string, DateOnly)>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), StoreText.StorageTextToDate(reader.GetString(3))));
        }

        return rows;
    }

    /// <summary>
    /// The report, as JSON and as text, under the data root. A path relative to the root is what goes in
    /// the run's own output, so nothing absolute is recorded.
    /// </summary>
    private string Write(DateOnly asOf, IReadOnlyList<EntryRuleReading.Side> sides, IReadOnlyList<EntryRuleReading.Row> rows)
    {
        string folder = Path.Combine(_paths.DataRoot, ReportsDirectory);
        Directory.CreateDirectory(folder);

        string stem = $"entry-rule-{asOf:yyyy-MM-dd}";

        var document = new
        {
            asOf = asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            levelSet = EntryRuleReading.LevelSet,
            anchoredLevel = EntryRuleReading.AnchoredLevelNotEvaluated,
            sides,
            rows = rows.Select(r => new { r.SetupId, r.Direction, outcome = r.Outcome.ToString(), r.RefusedBecause, r.StopBasis, r.StopFraction, r.Ceiling }),
        };

        File.WriteAllText(
            Path.Combine(folder, stem + ".json"),
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Generation 1's entry rule over the calibration minutes, as of {asOf:yyyy-MM-dd}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Level set: {EntryRuleReading.LevelSet}.");
        text.AppendLine(EntryRuleReading.AnchoredLevelNotEvaluated + ".");

        foreach (EntryRuleReading.Side side in sides)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{side.Direction}: {side.Rows} flagged row(s)");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  entered {side.Entered}, refused {side.Refused}, flushed and never reclaimed {side.NoReclaim}, "
                + $"never flushed {side.NoFlush}, without a level {side.NoLevels}, without minutes {side.NoMinutes}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  stops at the session extreme {side.StopsAtSessionExtreme}, at the entry candle {side.StopsAtEntryCandle}, "
                + $"past the ceiling {side.StopsPastCeiling}; median stop {Fraction(side.MedianStopFraction)} against a median ceiling "
                + $"of {Fraction(side.MedianCeiling)}");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  win-rate ceiling over {side.BoundSubjects} entered row(s) with a closed outcome: bound {Fraction(side.Bound)}, "
                + $"achieved {Fraction(side.Achieved)}");
        }

        File.WriteAllText(Path.Combine(folder, stem + ".txt"), text.ToString(), new UTF8Encoding(false));

        return Path.Combine(ReportsDirectory, stem + ".json");
    }

    private static string Fraction(decimal? value) =>
        value is decimal present ? present.ToString("P2", CultureInfo.InvariantCulture) : "none";
}

/// <summary>What one run of the measurement read, and where its report went, relative to the data root.</summary>
public sealed record EntryRuleReport(
    DateOnly AsOf,
    IReadOnlyList<EntryRuleReading.Side> Sides,
    IReadOnlyList<EntryRuleReading.Row> Rows,
    string Path);
