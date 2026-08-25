using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Web.Shell;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The nightly pipeline, run end to end over the captured fixture into a store of its own.
///
/// Every stage is the shipped stage and the vendor client is the shipped client; only the
/// transport is replaced. What comes back is a flat list of named measurements, which is what
/// the fixture diff compares against the committed expectations and what the phase report prints.
///
/// The order is RUNBOOK's nightly order, with the one-time history seed in front of it, because
/// the sequence is itself the thing under test: an action observed tonight has to block the
/// averages until a refetch made after that observation has landed, and running the stages in a
/// convenient order rather than the real one is how that property would pass without holding.
/// </summary>
public sealed class PhaseReplay : IDisposable
{
    /// <summary>
    /// The screening window the replay uses, and the one place it differs from a live night.
    ///
    /// A live screen takes the median dollar volume over twenty sessions, which is twenty bulk
    /// requests and about 130 MB of captured response. The fixture holds one market day, so the
    /// replay screens over one and the report says so in as many words. The twenty-session walk
    /// is covered by an authored case in UniverseBuilderTests; what the captured input buys here
    /// is the type filter, the two floors and the parsing, over the whole real market.
    /// </summary>
    public const int ReplayScreeningSessions = 1;

    /// <summary>
    /// How many sessions the fixture lays a chart over, and the box it lays them in. Sixty is
    /// about a quarter, which is the window the chart page opens on, and the box is the one the
    /// front door renders at.
    /// </summary>
    public const int ChartSessions = 60;

    public const int ChartWidth = 720;

    public const int ChartHeight = 260;

    private readonly TemporaryDirectory _root = new();
    private readonly FixtureVendorHandler _handler;
    private readonly HttpClient _http;
    private readonly StoreConnectionFactory _connections;
    private readonly FixedClock _clock;
    private readonly IOptions<PullbackStrategyLabOptions> _options;

    public PhaseReplay(string fixtureDirectory)
    {
        _handler = new FixtureVendorHandler(fixtureDirectory);
        AsOf = _handler.AsOf;

        // The evening of the captured session, in UTC. Fixed, because a replay whose stored
        // observation instants moved with the wall clock would produce a different store on every
        // run and nothing could be frozen against it.
        _clock = new FixedClock(new DateTimeOffset(AsOf.Year, AsOf.Month, AsOf.Day, 22, 0, 0, TimeSpan.Zero));

        _options = Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            Universe = new UniverseOptions { LiquidityWindowSessions = ReplayScreeningSessions },
        });

        _connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(_root.Path));
        new MigrationRunner(_connections).Apply();

        _http = new HttpClient(_handler) { BaseAddress = new Uri(new PullbackStrategyLabOptions().Vendor.BaseAddress) };
        Vendor = new EodhdClient(_http, WithToken());
    }

    /// <summary>The session every stage runs for. The fixture's own date, never today's.</summary>
    public DateOnly AsOf { get; }

    public EodhdClient Vendor { get; }

    public FixtureVendorHandler Fixture => _handler;

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _root.Dispose();
    }

    /// <summary>
    /// Runs the pipeline and returns what each stage reported, plus the indicator figures for
    /// every fixture ticker that came out the far end.
    /// </summary>
    public PhaseReplayResult Run()
    {
        var measurements = new List<Measurement>();
        var stages = new List<StageRun>();

        void Record(string id, long value) =>
            measurements.Add(new Measurement(id, value.ToString(CultureInfo.InvariantCulture)));

        // 1. The tradable list, from the captured symbol list screened against the captured
        //    market day, with the lab's own floors. The screen's verdict on this market day.
        UniverseBuildResult screened = Build(_options).GetAwaiter().GetResult();

        stages.Add(new StageRun(UniverseBuilder.Name, screened.CallsUsed, screened.RowsWritten, screened.Outcome.ToStorageText()));
        Record("universe.listedCommonStock", screened.ListedCommonStock);
        Record("universe.screened", screened.Screened);
        Record("universe.sessionsScreened", screened.SessionsScreened);
        Record("universe.survivors", screened.Survivors);

        // 1b. The same stage again with the liquidity floor lifted, which is the universe the
        //     rest of the replay runs against.
        //
        //     The floor is a median over twenty sessions and the fixture holds one market day, so
        //     applying it here screens on a number that is not the number the floor means. It
        //     rejected the fixture's own control on a light day whose twenty-session median
        //     clears the floor three times over, and a fixture that quietly loses a name it was
        //     built to check is worse than one that admits more names than the lab would trade.
        //     So the screen's verdict is measured above and reported, and the run continues
        //     against the wider list. Nothing downstream depends on the floor: it exists to keep
        //     the per-ticker backfill inside the call budget, and a replay has no budget.
        UniverseBuildResult admitted = Build(WithoutTheLiquidityFloor()).GetAwaiter().GetResult();

        stages.Add(new StageRun(UniverseBuilder.Name + " (floor lifted)", admitted.CallsUsed, admitted.RowsWritten, admitted.Outcome.ToStorageText()));
        Record("universe.admittedWithoutTheLiquidityFloor", admitted.Survivors);
        Record("universe.rejectedByTheLiquidityFloor", admitted.Survivors - screened.Survivors);

        // 2. The history seed, RUNBOOK's step 4 narrowed to the names the fixture holds.
        //
        //    Narrowed again to the ones the screen actually admitted. A bar carries a foreign key
        //    to security and a name the screen rejected has no security row, so backfilling one
        //    is refused by the store rather than quietly stored: history exists for names the lab
        //    can trade. Which fixture names those are is a measurement of its own, because a
        //    ticker silently dropping out of the fixture is how a diff stays green over a
        //    shrinking subject.
        IReadOnlyList<string> members = UniverseMembers();
        IReadOnlyList<string> seedable = FixtureTickers.All
            .Where(members.Contains).Order(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> outside = FixtureTickers.All
            .Where(t => !members.Contains(t)).Order(StringComparer.Ordinal).ToArray();

        Record("fixture.tickersInUniverse", seedable.Count);
        measurements.Add(new Measurement("fixture.tickersOutsideUniverse",
            outside.Count == 0 ? "none" : string.Join(" ", outside)));

        var bars = new DailyBarIngestor(Vendor, _connections, Logger(), _clock, _options);
        BackfillResult seed = bars.BackfillAsync(BackfillSelection.Named, seedable, AsOf)
            .GetAwaiter().GetResult();

        stages.Add(new StageRun(DailyBarIngestor.BackfillName, seed.CallsUsed, seed.RowsWritten, seed.Outcome.ToStorageText()));
        Record("backfill.seed.selected", seed.Selected);
        Record("backfill.seed.barsPublished", seed.BarsPublished);
        Record("backfill.seed.inserted", seed.Inserted);

        // 3. The night proper, in RUNBOOK's order. Actions first, so a demand raised tonight is
        //    outstanding when the averages are computed and only a refetch made afterwards
        //    clears it.
        ActionIngestResult actions = new ActionIngestor(Vendor, _connections, Logger(), _clock, _options)
            .IngestAsync(AsOf).GetAwaiter().GetResult();

        stages.Add(new StageRun(ActionIngestor.Name, actions.CallsUsed, actions.RowsWritten, actions.Outcome.ToStorageText()));
        Record("actions.splitsPublished", actions.SplitsPublished);
        Record("actions.dividendsPublished", actions.DividendsPublished);
        Record("actions.inUniverse", actions.InUniverse);
        Record("actions.inserted", actions.Inserted);
        Record("actions.demandsRaised", actions.DemandsRaised);
        Record("actions.tickersBlocked", actions.TickersBlocked);

        // 4. The whole market's closes for the session. The fixture tickers already hold this
        //    date from their histories, so most of what comes back is already stored unchanged,
        //    which is the idempotence property stated as a number.
        DailyBarIngestResult bulk = bars.IngestAsync(AsOf).GetAwaiter().GetResult();

        stages.Add(new StageRun(DailyBarIngestor.Name, bulk.CallsUsed, bulk.RowsWritten, bulk.Outcome.ToStorageText()));
        Record("bars.published", bulk.Published);
        Record("bars.inUniverse", bulk.InUniverse);
        Record("bars.inserted", bulk.Inserted);
        Record("bars.unchanged", bulk.Unchanged);
        Record("bars.corrections", bulk.Corrections);

        // 5. The refetch that answers tonight's demands.
        BackfillResult rebuild = bars.BackfillAsync(BackfillSelection.TickersWithAnOpenDemand, [], AsOf)
            .GetAwaiter().GetResult();

        stages.Add(new StageRun("backfill --rebuild", rebuild.CallsUsed, rebuild.RowsWritten, rebuild.Outcome.ToStorageText()));
        Record("backfill.rebuild.selected", rebuild.Selected);
        Record("backfill.rebuild.inserted", rebuild.Inserted);

        // 6. The three trackers.
        IndexIngestResult index = new IndexIngestor(Vendor, _connections, Logger(), _clock, _options)
            .IngestAsync(AsOf).GetAwaiter().GetResult();

        stages.Add(new StageRun(IndexIngestor.Name, index.CallsUsed, index.RowsWritten, index.Outcome.ToStorageText()));
        Record("index.symbols", index.Symbols);
        Record("index.barsPublished", index.BarsPublished);
        Record("index.inserted", index.Inserted);

        // 7. The averages, which refuse for anything short of the warm-up or carrying a demand
        //    the window does not account for.
        IndicatorResult indicators = new IndicatorEngine(_connections, Logger(), _clock, _options).Compute(AsOf);

        stages.Add(new StageRun(IndicatorEngine.Name, indicators.CallsUsed, indicators.RowsWritten, indicators.Outcome.ToStorageText()));
        Record("indicators.members", indicators.Members);
        Record("indicators.computed", indicators.Computed);
        Record("indicators.recomputed", indicators.Recomputed);
        Record("indicators.shortOfWarmup", indicators.ShortOfWarmup);
        Record("indicators.blocked", indicators.Blocked);
        Record("indicators.demandsSatisfied", indicators.DemandsSatisfied);

        measurements.Add(new Measurement("fixture.actionsObserved", NamesFrom(
            "SELECT DISTINCT ticker FROM corporate_action ORDER BY ticker;")));
        measurements.Add(new Measurement("fixture.rebuildsStamped", NamesFrom(
            "SELECT DISTINCT ticker FROM indicator_rebuild WHERE rebuilt_at IS NOT NULL ORDER BY ticker;")));

        measurements.AddRange(IndicatorFigures());
        measurements.AddRange(ChartFigures());

        return new PhaseReplayResult(
            AsOf,
            _handler.Tier,
            _handler.Responses,
            _handler.Served.Distinct(StringComparer.Ordinal).Count(),
            _handler.MissesInsideACoveredEndpoint,
            _handler.MissesOnAnUncoveredEndpoint,
            ReplayScreeningSessions,
            stages,
            measurements);
    }

    /// <summary>
    /// Every fixture ticker's stored figures, to four decimal places.
    ///
    /// Four is what BUILD_PLAN asks the independent calculation to agree to, and it is the right
    /// place to stop: a fifteenth decimal of a decimal average is a fact about the order of
    /// operations rather than about the price.
    /// </summary>
    private IReadOnlyList<Measurement> IndicatorFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        foreach (string ticker in FixtureTickers.All.Order(StringComparer.Ordinal))
        {
            StoredIndicators? stored = IndicatorDailyReader.Latest(connection, ticker, AsOf);

            if (stored is null)
            {
                // Named rather than skipped. A ticker that fell out of the universe or short of
                // the warm-up is a fact about the fixture, and an expectation that quietly
                // vanished is how a diff stays green while its subject disappears.
                figures.Add(new Measurement($"indicators.{ticker}", "no row"));
                continue;
            }

            figures.Add(new Measurement($"indicators.{ticker}.ema9", Figure(stored.EmaShort)));
            figures.Add(new Measurement($"indicators.{ticker}.ema21", Figure(stored.EmaMedium)));
            figures.Add(new Measurement($"indicators.{ticker}.ema50", Figure(stored.EmaLong)));
            figures.Add(new Measurement($"indicators.{ticker}.atr14", Figure(stored.AverageTrueRange)));
            figures.Add(new Measurement($"indicators.{ticker}.adr20", Figure(stored.AverageDailyRange)));
            figures.Add(new Measurement($"indicators.{ticker}.medianDollarVolume", Figure(stored.DollarVolumeMedian)));
            figures.Add(new Measurement($"indicators.{ticker}.rangeAverage", Figure(stored.RangeAverage)));
        }

        return figures;
    }

    /// <summary>
    /// The shared chart's layout over one of the fixture's tickers.
    ///
    /// A chart is the one place where looking at the result is least reliable: a scale that
    /// clips an average, a body drawn upside down and an axis on the wrong step all look like a
    /// chart. So the geometry is frozen as numbers and derived independently, and the ticker is
    /// the one carrying a real split, because the adjusted basis is exactly what a chart can be
    /// wrong about while still looking right.
    /// </summary>
    private IReadOnlyList<Measurement> ChartFigures()
    {
        const string Ticker = "IESC";

        using SqliteConnection connection = _connections.OpenReadOnly();
        IReadOnlyList<StoredDailyBar> window =
            DailyBarReader.Read(connection, Ticker, AsOf, ChartSessions, _clock.UtcNow);

        // The adjusted basis, the same crossing the engine makes: the store holds an adjusted
        // close and a raw high and low, so the high and low are put on the adjusted basis
        // through each bar's own factor.
        var candles = window
            .Select(bar =>
            {
                decimal factor = bar.Close == 0m ? 1m : bar.AdjustedClose / bar.Close;
                decimal open = bar.Open * factor;
                return new Candle(bar.BarDate, open, bar.High * factor, bar.Low * factor, bar.AdjustedClose);
            })
            .ToArray();

        CandlestickGeometry chart = CandlestickChart.Lay(candles, [], ChartWidth, ChartHeight);

        return
        [
            new Measurement($"chart.{Ticker}.sessions", chart.Candles.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement($"chart.{Ticker}.low", Figure(chart.Low)),
            new Measurement($"chart.{Ticker}.high", Figure(chart.High)),
            new Measurement($"chart.{Ticker}.upCandles", chart.Candles.Count(c => c.Up).ToString(CultureInfo.InvariantCulture)),
            new Measurement($"chart.{Ticker}.priceTicks", chart.PriceTicks.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement($"chart.{Ticker}.firstTick", Figure(chart.PriceTicks[0].Price)),
            new Measurement($"chart.{Ticker}.lastTick", Figure(chart.PriceTicks[^1].Price)),
            new Measurement($"chart.{Ticker}.bodyWidth", Coordinate(chart.Candles[0].BodyWidth)),
            new Measurement($"chart.{Ticker}.firstCentre", Coordinate(chart.Candles[0].Centre)),
            new Measurement($"chart.{Ticker}.lastCentre", Coordinate(chart.Candles[^1].Centre)),
            new Measurement($"chart.{Ticker}.lastHighY", Coordinate(chart.Candles[^1].HighY)),
            new Measurement($"chart.{Ticker}.lastLowY", Coordinate(chart.Candles[^1].LowY)),
        ];
    }

    /// <summary>A screen coordinate, to two places. It is a length rather than a price.</summary>
    public static string Coordinate(double value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Figure(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The fixture's own names matching a query, as one space-separated value.
    ///
    /// Named rather than counted. A count says a demand was stamped; a name says which stock's
    /// history was put back on one basis, and that is the thing worth freezing, because the
    /// fixture was built around a ticker carrying a real split.
    /// </summary>
    private string NamesFrom(string sql)
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string ticker = reader.GetString(0);
            if (FixtureTickers.All.Contains(ticker, StringComparer.Ordinal))
            {
                names.Add(ticker);
            }
        }

        return names.Count == 0 ? "none" : string.Join(" ", names);
    }

    /// <summary>
    /// A copy of the store the replay built, folded into one file. Not part of the diff: it is
    /// what a person opens when a figure moved and the diff says which one but not why, and it
    /// is what the independent implementation is pointed at when a DERIVED expectation is
    /// produced.
    /// </summary>
    public void SnapshotTo(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);

        foreach (string stale in new[] { file, file + "-wal", file + "-shm" })
        {
            if (File.Exists(stale))
            {
                File.Delete(stale);
            }
        }

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $into;";
        command.Parameters.AddWithValue("$into", Path.GetFullPath(file));
        command.ExecuteNonQuery();
    }

    private Task<UniverseBuildResult> Build(IOptions<PullbackStrategyLabOptions> options) =>
        new UniverseBuilder(Vendor, _connections, Logger(), _clock, options).BuildAsync(AsOf);

    /// <summary>
    /// The same configuration with the liquidity floor at zero. The price floor stays: it is a
    /// property of one day's close and one day is what the fixture holds, so it means here
    /// exactly what it means on a live night.
    /// </summary>
    private IOptions<PullbackStrategyLabOptions> WithoutTheLiquidityFloor() =>
        Options.Create(new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            Universe = new UniverseOptions
            {
                LiquidityWindowSessions = ReplayScreeningSessions,
                LiquidityFloorLong = 0m,
            },
        });

    /// <summary>Who the screen admitted, read from the store rather than assumed from the fixture list.</summary>
    private IReadOnlyList<string> UniverseMembers()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ticker FROM universe_member WHERE removed_on IS NULL ORDER BY ticker;";

        var tickers = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            tickers.Add(reader.GetString(0));
        }

        return tickers;
    }

    private RunLogger Logger() => new(_clock, _options);

    /// <summary>
    /// The client refuses to run without a token, which is correct: a live run with no token
    /// would be a run against nothing. The fixture transport never sees it, and the value here
    /// is not a credential and could not be one.
    /// </summary>
    private IOptions<PullbackStrategyLabOptions> WithToken()
    {
        var options = new PullbackStrategyLabOptions
        {
            DataRoot = _root.Path,
            Universe = new UniverseOptions { LiquidityWindowSessions = ReplayScreeningSessions },
        };

        options.Vendor.ApiKey = "replayed-from-the-captured-fixture";
        return Options.Create(options);
    }
}

/// <summary>One named number the replay produced, as text so a diff never turns on a format.</summary>
public sealed record Measurement(string Id, string Value);

/// <summary>What one stage of the replay spent and wrote.</summary>
public sealed record StageRun(string Stage, int CallsUsed, int RowsWritten, string Outcome);

public sealed record PhaseReplayResult(
    DateOnly AsOf,
    string InputTier,
    int CapturedResponses,
    int ResponsesServed,
    IReadOnlyList<string> AskedOutsideTheFixture,
    IReadOnlyList<string> AskedOnAnUncoveredEndpoint,
    int ScreeningSessions,
    IReadOnlyList<StageRun> Stages,
    IReadOnlyList<Measurement> Measurements);
