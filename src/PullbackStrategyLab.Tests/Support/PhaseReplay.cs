using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Web.Pages;
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

    /// <summary>A read-only connection to the store this replay built, for a caller that wants rows.</summary>
    public SqliteConnection OpenStore() => _connections.OpenReadOnly();

    /// <summary>
    /// A writing connection to the same store, for a test that has to damage it.
    ///
    /// Narrow on purpose. What it is for is authoring a failure the captured data cannot produce, on
    /// the same terms as the synthetic split: a stored figure no reader can parse, so the detector's
    /// error path is exercised by the store rather than by a fault injected into the detector.
    /// </summary>
    public SqliteConnection OpenWrite() => _connections.OpenWrite();

    /// <summary>The long detector again over the store this replay built, for the same session.</summary>
    public DetectResult DetectLong() =>
        new LongSetupDetector(_connections, Logger(), _clock, _options).Detect(AsOf);

    /// <summary>The long detector in calibration mode, over the store this replay built.</summary>
    public CalibrationResult CalibrateLong(DateOnly from, DateOnly to) =>
        new LongSetupDetector(_connections, Logger(), _clock, _options).Calibrate(from, to);

    /// <summary>The short detector in calibration mode, likewise.</summary>
    public CalibrationResult CalibrateShort(DateOnly from, DateOnly to) =>
        new ShortSetupDetector(_connections, Logger(), _clock, _options).Calibrate(from, to);

    /// <summary>The short detector, likewise.</summary>
    public DetectResult DetectShort() =>
        new ShortSetupDetector(_connections, Logger(), _clock, _options).Detect(AsOf);

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

        // 8. The six mover scans, which the thrust signals read.
        ScanResult scans = new ScanEngine(_connections, Logger(), _clock, _options).Scan(AsOf);

        stages.Add(new StageRun(ScanEngine.Name, 0, scans.RowsWritten, scans.Outcome.ToStorageText()));
        Record("scans.members", scans.Members);
        Record("scans.measured", scans.Measured);
        Record("scans.shortOfHistory", scans.ShortOfHistory);
        Record("scans.hits", scans.Hits);
        Record("scans.inserted", scans.Inserted);

        // 9. The ladder grade, which writes a later observation of the same session rather than
        //    updating the row the engine wrote.
        TierResult tiers = new TierClassifier(_connections, Logger(), _clock, _options).Classify(AsOf);

        stages.Add(new StageRun(TierClassifier.Name, 0, tiers.RowsWritten, tiers.Outcome.ToStorageText()));
        Record("tiers.members", tiers.Members);
        Record("tiers.graded", tiers.Graded);
        Record("tiers.rising", tiers.Rising);
        Record("tiers.mixed", tiers.Mixed);
        Record("tiers.falling", tiers.Falling);
        Record("tiers.noIndicators", tiers.NoIndicators);

        // 10. The sector lookup, which three later stages read and which used to run after all
        //     three of them. RUNBOOK scheduled it at 19:00 while `clusters` at 18:15 and both
        //     detectors at 18:20 read what it writes, so on a live night a name newly surfaced by a
        //     scan had no industry when the cluster count was taken and no market capitalisation
        //     when `tradable-shortable` decided. Neither one errors: the cluster reads nought and
        //     the short check fails for want of a figure. This replay ran it first and so could
        //     never have shown it, which is the failure the stage order here exists to prevent.
        SectorResult sectors = new SectorResolver(Vendor, _connections, Logger(), _clock, _options)
            .ResolveAsync(AsOf, SectorResolver.DefaultLimit).GetAwaiter().GetResult();

        stages.Add(new StageRun(SectorResolver.Name, sectors.CallsUsed, sectors.RowsWritten, sectors.Outcome.ToStorageText()));
        Record("sectors.unresolved", sectors.Unresolved);
        Record("sectors.asked", sectors.Asked);
        Record("sectors.resolved", sectors.Resolved);

        // 11. The cluster count, then the market mood, then the two detectors.
        ClusterResult clusters = new ThemeClusterer(_connections, Logger(), _clock, _options).Count(AsOf);

        stages.Add(new StageRun(ThemeClusterer.Name, 0, clusters.RowsWritten, clusters.Outcome.ToStorageText()));
        Record("clusters.hits", clusters.Hits);
        Record("clusters.withIndustry", clusters.WithIndustry);
        Record("clusters.counted", clusters.Counted);
        Record("clusters.clustered", clusters.Clustered);

        RegimeResult regime = new RegimeLabeler(_connections, Logger(), _clock, _options).Label(AsOf);

        stages.Add(new StageRun(RegimeLabeler.Name, 0, regime.RowsWritten, regime.Outcome.ToStorageText()));
        Record("regime.indexesMeasured", regime.IndexesMeasured);
        Record("regime.indexesAbove", regime.IndexesAbove);
        Record("regime.longLadderCount", regime.LongLadderCount);
        Record("regime.shortLadderCount", regime.ShortLadderCount);
        Record("regime.indexScore", regime.IndexScore);
        Record("regime.breadthScore", regime.BreadthScore);
        measurements.Add(new Measurement("regime.label", regime.Label));

        DetectResult detected = new LongSetupDetector(_connections, Logger(), _clock, _options).Detect(AsOf);

        stages.Add(new StageRun(LongSetupDetector.Name, 0, detected.RowsWritten, detected.Outcome.ToStorageText()));
        Record("detect.long.members", detected.Members);
        Record("detect.long.examined", detected.Examined);
        Record("detect.long.belowFloor", detected.BelowFloor);
        Record("detect.long.recorded", detected.Recorded);
        Record("detect.long.passedAll", detected.PassedAll);

        DetectResult shorted = new ShortSetupDetector(_connections, Logger(), _clock, _options).Detect(AsOf);

        stages.Add(new StageRun(ShortSetupDetector.Name, 0, shorted.RowsWritten, shorted.Outcome.ToStorageText()));
        Record("detect.short.members", shorted.Members);
        Record("detect.short.examined", shorted.Examined);
        Record("detect.short.belowFloor", shorted.BelowFloor);
        Record("detect.short.recorded", shorted.Recorded);
        Record("detect.short.passedAll", shorted.PassedAll);

        // 12. The signal freeze, over one authored setup.
        //
        //    The detectors arrive at 2.6, so the fixture has no setup a detector produced. The row
        //    is authored, on the same terms as the synthetic split at 1.5: an AUTHORED input, said
        //    to be one, exercising a path the captured data cannot reach on its own. What is under
        //    test is the vectorizer, and it takes a setup as given.
        //
        //    IESC rather than any of the thirty, because it is the fixture's only name with a real
        //    corporate action inside the window. A signal read on the raw basis rather than the
        //    adjusted one is off by the split factor for that name and by nothing at all for the
        //    other twenty-nine.
        VectorizeResult vectorized = VectorizeAuthoredSetup();
        stages.Add(new StageRun(SignalVectorizer.Name, 0, vectorized.RowsWritten, vectorized.Outcome.ToStorageText()));
        Record("signals.setups", vectorized.Setups);
        Record("signals.frozen", vectorized.Written);
        Record("signals.absent", vectorized.Absent);

        // 13. The nightly cap, over whatever the night's detectors left.
        CapResult capped = new SetupCapper(_connections, Logger(), _clock, _options).Cap(AsOf);

        stages.Add(new StageRun(SetupCapper.Name, 0, capped.RowsWritten, capped.Outcome.ToStorageText()));
        Record("cap.setups", capped.Setups);
        Record("cap.candidates", capped.Candidates);
        Record("cap.longCandidates", capped.LongCandidates);
        Record("cap.shortCandidates", capped.ShortCandidates);
        Record("cap.longKept", capped.LongKept);
        Record("cap.shortKept", capped.ShortKept);
        Record("cap.cappedOut", capped.CappedOut);

        measurements.AddRange(CalibrationCounts());
        measurements.AddRange(CapFigures());
        measurements.AddRange(GeometryFigures());
        measurements.AddRange(IndexFigures());
        measurements.AddRange(ScanFigures());
        measurements.AddRange(CheckSidednessFigures());
        measurements.AddRange(SignalFigures());
        measurements.AddRange(IndicatorFigures());
        measurements.AddRange(LiquidityFloorFigures());
        measurements.AddRange(ChartFigures());
        measurements.AddRange(ReadSurfaceFigures());
        measurements.AddRange(GalleryFigures());

        // Last, because it writes a row into the store on purpose and nothing above it may see one.
        measurements.AddRange(PointInTimeFigures());

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
    /// What each tracker's history looks like once it is stored, per symbol rather than as the
    /// three totals the stage reports.
    ///
    /// The three totals 1.9 landed with are all FROZEN, which is regression detection: they say
    /// the stage still does what it did, and nothing about whether it did the right thing. These
    /// carry DERIVED expectations instead, produced by <c>tools/derive-indicators.py --index</c>
    /// reading the captured responses directly. That derivation costs no vendor call, since the
    /// same files the replay serves from are the ones it reads, so this is the cheapest of the
    /// checkpoints that were frozen-only and it is closed rather than carried.
    ///
    /// Per symbol on purpose. A total of 753 bars across three symbols holds while one symbol
    /// gains what another loses, and the first and last session and the last close are what a
    /// symbol mix-up, a window off by a session or a close read out of the adjusted column would
    /// actually move. The raw and adjusted close are taken at the first session rather than the
    /// last, because at the last captured session the two are equal for all three trackers and a
    /// pair read out of the wrong column would agree with itself.
    ///
    /// The three-year window is not what these test: the ingestor asks for three years, the
    /// capture holds one, and the vendor's own range is the narrower of the two, so the bound
    /// never bites here. That is stated rather than left for a later reader to discover, because
    /// an expectation believed to cover something it does not is worse than one that covers less
    /// and says so.
    /// </summary>
    private IReadOnlyList<Measurement> IndexFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        foreach (string symbol in _options.Value.IndexSymbols.Order(StringComparer.Ordinal))
        {
            IReadOnlyList<StoredDailyBar> history = IndexBarReader.Read(connection, symbol, AsOf, int.MaxValue);

            if (history.Count == 0)
            {
                // Named rather than skipped, on the same reasoning as a missing indicator row: an
                // expectation that quietly stops having a subject is how a diff stays green over
                // nothing.
                figures.Add(new Measurement($"index.{symbol}", "no bars"));
                continue;
            }

            figures.Add(new Measurement($"index.{symbol}.bars", history.Count.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"index.{symbol}.firstSession", Session(history[0].BarDate)));
            figures.Add(new Measurement($"index.{symbol}.lastSession", Session(history[^1].BarDate)));
            figures.Add(new Measurement($"index.{symbol}.firstClose", Figure(history[0].Close)));
            figures.Add(new Measurement($"index.{symbol}.firstAdjustedClose", Figure(history[0].AdjustedClose)));
            figures.Add(new Measurement($"index.{symbol}.lastClose", Figure(history[^1].Close)));
        }

        return figures;
    }

    /// <summary>
    /// Every fixture ticker's stored figures, to four decimal places.
    ///
    /// Four is what BUILD_PLAN asks the independent calculation to agree to, and it is the right
    /// place to stop: a fifteenth decimal of a decimal average is a fact about the order of
    /// operations rather than about the price.
    /// </summary>
    /// <summary>
    /// How each check came out across the fixture, pass and fail counted separately.
    ///
    /// A count of results diffed says the detector ran. It does not say whether a check was ever
    /// exercised on both sides, and thirty names on one session will fail most of them at the same
    /// early gate. A check with no passes, or no failures, is <b>one-sided</b>: the branch nobody
    /// reached is asserted by nothing, and "300 results diffed" reads as full coverage while six of
    /// the ten have only ever returned one answer.
    ///
    /// Named individually rather than counted, because the useful sentence is "held-floor and
    /// contraction are one-sided" and not "two checks are one-sided".
    /// </summary>
    private IReadOnlyList<Measurement> CheckSidednessFigures()
    {
        var figures = new List<Measurement>();

        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT direction, check_results FROM setup";

        // Keyed by direction and name rather than by name, because five of the twenty gate ids
        // appear on both lists and a dictionary keyed on the id alone would add a long `exit-tight`
        // pass to a short `exit-tight` fail and report the pair as two-sided. That is the pooling
        // rule arriving in the one place it is easiest to break by accident.
        // see: Long and short are never pooled into one figure
        var passes = new Dictionary<(string Direction, string Name), int>();
        var fails = new Dictionary<(string Direction, string Name), int>();

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string direction = reader.GetString(0);

                foreach (CheckResult result in
                         JsonSerializer.Deserialize<CheckResult[]>(reader.GetString(1), CheckJson) ?? [])
                {
                    Dictionary<(string, string), int> side = result.Passed ? passes : fails;
                    side[(direction, result.Name)] = side.GetValueOrDefault((direction, result.Name)) + 1;
                }
            }
        }

        // Per setup, per check, the verdict. This is what makes a changed gate show up as a named
        // difference rather than as a count moving: "dip-shape on HOOD went from fail to pass" is
        // actionable and "one more setup passed" is not.
        using (SqliteCommand perSetup = connection.CreateCommand())
        {
            perSetup.CommandText = "SELECT setup_id, check_results FROM setup ORDER BY setup_id";
            using SqliteDataReader rows = perSetup.ExecuteReader();

            while (rows.Read())
            {
                string setupId = rows.GetString(0);
                foreach (CheckResult result in
                         JsonSerializer.Deserialize<CheckResult[]>(rows.GetString(1), CheckJson) ?? [])
                {
                    figures.Add(new Measurement(
                        $"setup.{setupId}.{result.Name}", result.Passed ? "pass" : "fail"));
                }
            }
        }

        // The authored boundary cases, evaluated through the shipped rules and kept in a bucket of
        // their own. They are what answers whether both branches of a gate work; they say nothing
        // about the market and are never added to the counts above, which are the detectors' rows.
        // see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
        var authored = new Dictionary<(string Direction, string Name), (bool Pass, bool Fail)>();

        foreach (GateCases.GateCase gateCase in GateCases.All)
        {
            CheckResult verdict = GateCases.Evaluate(gateCase)
                .Single(r => string.Equals(r.Name, gateCase.Gate, StringComparison.Ordinal));

            figures.Add(new Measurement(gateCase.Id, verdict.Passed ? "pass" : "fail"));

            (bool pass, bool fail) = authored.GetValueOrDefault((gateCase.Direction, gateCase.Gate));
            authored[(gateCase.Direction, gateCase.Gate)] =
                (pass || verdict.Passed, fail || !verdict.Passed);
        }

        foreach ((string direction, IReadOnlyList<string> gates) in
                 new[] { ("long", SetupChecks.Long), ("short", SetupChecks.Short) })
        {
            var oneSided = new List<string>();

            foreach (string name in gates)
            {
                int passed = passes.GetValueOrDefault((direction, name));
                int failed = fails.GetValueOrDefault((direction, name));
                (bool authoredPass, bool authoredFail) = authored.GetValueOrDefault((direction, name));

                figures.Add(new Measurement(
                    $"check.{direction}.{name}.passed", passed.ToString(CultureInfo.InvariantCulture)));
                figures.Add(new Measurement(
                    $"check.{direction}.{name}.failed", failed.ToString(CultureInfo.InvariantCulture)));

                // Sidedness asks whether anything has ever exercised both branches, so it reads both
                // populations. The two counts above stay separate, so a reader can still see that a
                // gate the market never passed was passed by a case built to pass it.
                bool everPassed = passed > 0 || authoredPass;
                bool everFailed = failed > 0 || authoredFail;

                if (!everPassed || !everFailed)
                {
                    oneSided.Add(name);
                }
            }

            figures.Add(new Measurement($"check.{direction}.oneSided",
                oneSided.Count == 0 ? "none" : string.Join(" ", oneSided.Order(StringComparer.Ordinal))));
        }

        return figures;
    }

    /// <summary>
    /// The cap over candidate lists the captured day did not produce.
    ///
    /// The fixture records two setups and neither clears every gating check, so the live cap above
    /// caps nothing and its figures are all nought. A release rule that has only ever run on an empty
    /// list is a rule nothing has tested, and the arrangements that matter, both release directions
    /// and both sides overflowing, are the ones thirty names on one session cannot reach.
    ///
    /// AUTHORED, and about the rule rather than about the market: they say nothing about how many
    /// candidates a night has, which is what the calibration run measures.
    /// see: A released cap slot goes to the side that still has candidates
    /// </summary>
    /// <summary>
    /// The one-time calibration, run over the fixture's own seeded histories.
    ///
    /// <b>What this is for, stated plainly, because the numbers below are not what the checkpoint
    /// exists to produce.</b> The fixture holds thirty names and the scan breadth is fifty, so every
    /// measured name is in the top fifty of all six scans on every session: `thrust` passes on every
    /// row, its most recent hit is always the session itself, and every geometry check that reads a
    /// pullback therefore has no bars to read. The population is degenerate by construction and no
    /// threshold could be read off it. What runs here is the code path, diffed session by session, so
    /// a change to any gate or to the assembly of a reconstructed session shows up as a named
    /// difference. The distribution a threshold was actually set against came from the live universe
    /// and is recorded in PROGRESS.
    /// </summary>
    private IReadOnlyList<Measurement> CalibrationCounts()
    {
        var figures = new List<Measurement>();

        void Record(string id, object value) =>
            figures.Add(new Measurement(id, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));

        DateOnly[] sessions;
        using (SqliteConnection connection = OpenStore())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT bar_date FROM daily_bar ORDER BY bar_date";
            var dates = new List<DateOnly>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                dates.Add(StoreText.StorageTextToDate(reader.GetString(0)));
            }

            sessions = [.. dates];
        }

        // The first session with a whole warm-up behind it. Earlier ones have no figures at all and
        // a range that included them would report sessions the run could never have decided.
        DateOnly from = sessions[IndicatorEngine.WarmupSessions - 1];

        Record("calibration.storedSessions", sessions.Length);
        figures.Add(new Measurement("calibration.from", Session(from)));
        figures.Add(new Measurement("calibration.to", Session(AsOf)));

        CalibrationResult longSide = CalibrateLong(from, AsOf);
        CalibrationResult shortSide = CalibrateShort(from, AsOf);

        Record("calibration.sessions", longSide.Sessions);
        Record("calibration.warmupSessions", longSide.WarmupSessions);
        Record("calibration.membersListed", longSide.Listed);
        Record("calibration.membersWithHistory", longSide.Members);
        Record("calibration.scanBreadth", ScanEngine.Breadth);

        foreach ((string direction, CalibrationResult side) in
                 new[] { (SetupDirection.Long, longSide), (SetupDirection.Short, shortSide) })
        {
            NightlyCounts.Distribution recorded = NightlyCounts.Of([.. side.Nights.Select(n => n.Recorded)]);
            NightlyCounts.Distribution candidates = NightlyCounts.Of([.. side.Nights.Select(n => n.PassedAll)]);

            Record($"calibration.{direction}.recorded", side.Recorded);
            Record($"calibration.{direction}.passedAll", side.PassedAll);
            Record($"calibration.{direction}.errored", side.Errored);
            Record($"calibration.{direction}.recordedMedian", recorded.Median);
            Record($"calibration.{direction}.recordedHighest", recorded.Highest);
            Record($"calibration.{direction}.candidateMedian", candidates.Median);
            Record($"calibration.{direction}.candidateHighest", candidates.Highest);
            Record($"calibration.{direction}.emptyNights", candidates.EmptyNights);
        }

        // The property the checkpoint turns on, asserted as a figure rather than left to a test that
        // could be deleted: the evidence store is untouched by a run over history.
        using (SqliteConnection connection = OpenStore())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM calibration_setup";
            Record("calibration.rowsInCalibrationSetup", Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));

            command.CommandText = "SELECT COUNT(*) FROM setup WHERE as_of <> @as_of";
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            Record("calibration.setupRowsOutsideTheForwardNight",
                Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }

        return figures;
    }

    private static IReadOnlyList<Measurement> CapFigures()
    {
        var figures = new List<Measurement>();

        foreach (CapCases.Scenario scenario in CapCases.Scenarios)
        {
            (int takenLong, int takenShort) = NightlyCap.Take(scenario.Long, scenario.Short);

            figures.Add(new Measurement($"cap.{scenario.Name}.long", takenLong.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"cap.{scenario.Name}.short", takenShort.ToString(CultureInfo.InvariantCulture)));
        }

        // The ordering, as the sequence of setup ids each side comes back in. A sequence rather than
        // a count, because what a mis-sorted tiebreak moves is which name sits on the boundary and
        // not how many names there are.
        IReadOnlyList<NightlyCap.Placement> placements = NightlyCap.Apply(CapCases.OrderingCandidates);

        foreach (string direction in new[] { "long", "short" })
        {
            figures.Add(new Measurement(
                $"cap.ordering.{direction}",
                string.Join(
                    " ",
                    placements.Where(p => p.Direction == direction).OrderBy(p => p.Rank).Select(p => p.SetupId))));
        }

        return figures;
    }

    /// <summary>
    /// What <see cref="PullbackGeometry.Of"/> computes over windows the captured fixture cannot
    /// reach on its own.
    ///
    /// Every quantity of the record rather than the two the gates happen to read, because the
    /// method returns one shape and a caller reading half of it correctly can still be handed a
    /// wrong origin. The two prices are raw and the rest are adjusted, and both are pinned: reading
    /// one basis where the other was meant is the error this method carries a warning about, and it
    /// is silent because both numbers look reasonable.
    /// </summary>
    private IReadOnlyList<Measurement> GeometryFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        foreach (GeometryCases.GeometryCase geometryCase in GeometryCases.All)
        {
            PullbackGeometry.Pullback? shape = GeometryCases.Evaluate(connection, geometryCase);

            if (shape is null)
            {
                // A window that cannot support a shape is a real answer and is recorded as one. It
                // is not the same as a shape of no bars, which is what long-no-pullback-yet holds.
                figures.Add(new Measurement(geometryCase.Id, "no shape"));
                continue;
            }

            figures.Add(new Measurement(
                $"{geometryCase.Id}.extremeIndex",
                shape.ExtremeIndex.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement(
                $"{geometryCase.Id}.pullbackBars",
                shape.PullbackBars.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"{geometryCase.Id}.thrustOrigin", Figure(shape.ThrustOrigin)));
            figures.Add(new Measurement($"{geometryCase.Id}.thrustExtreme", Figure(shape.ThrustExtreme)));
            figures.Add(new Measurement($"{geometryCase.Id}.pullbackExtreme", Figure(shape.PullbackExtreme)));

            // Undefined rather than infinite, and recorded as a word so it cannot be read as a
            // number that happened to be small. A thrust of no size cannot be retraced by a
            // fraction of itself.
            figures.Add(new Measurement(
                $"{geometryCase.Id}.retraceDepth",
                shape.RetraceDepth is decimal retrace ? Figure(retrace) : "undefined"));

            figures.Add(new Measurement($"{geometryCase.Id}.trigger", Figure(shape.Trigger)));
            figures.Add(new Measurement($"{geometryCase.Id}.stop", Figure(shape.Stop)));
        }

        return figures;
    }

    private static readonly JsonSerializerOptions CheckJson = new(JsonSerializerDefaults.Web);

    /// <summary>How many ranks of each scan the fixture records, since thirty names cannot fill fifty.</summary>
    public const int ScanRanksRecorded = 3;

    private IReadOnlyList<Measurement> ScanFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        foreach (string scan in ScanEngine.Scans)
        {
            IReadOnlyList<StoredScanHit> hits = ScanHitReader.Read(connection, AsOf, scan);
            figures.Add(new Measurement($"scan.{scan}.hits", hits.Count.ToString(CultureInfo.InvariantCulture)));

            // The top few by name and by the magnitude they were ranked on, rather than a count.
            // A count says the scan ran; the ordering says it ranked on the right number in the
            // right direction, which is the half a wrong sign or a raw basis would leave looking
            // perfectly reasonable.
            for (int rank = 1; rank <= ScanRanksRecorded; rank++)
            {
                StoredScanHit? hit = hits.FirstOrDefault(h => h.Rank == rank);

                figures.Add(new Measurement(
                    $"scan.{scan}.rank{rank}",
                    hit is null ? "no hit" : hit.Ticker));

                figures.Add(new Measurement(
                    $"scan.{scan}.rank{rank}.magnitude",
                    hit is null ? "no hit" : Figure(hit.Magnitude)));
            }
        }

        return figures;
    }

    /// <summary>The fixture's authored setup: one name, one direction, one night.</summary>
    public const string AuthoredSetupTicker = "IESC";

    /// <summary>The trigger and the stop the authored setup carries, as raw prices.</summary>
    public const string AuthoredTrigger = "355.00";

    /// <summary>Stated beside the trigger so the geometry signals have a subject.</summary>
    public const string AuthoredStop = "348.50";

    private VectorizeResult VectorizeAuthoredSetup()
    {
        using (SqliteConnection connection = _connections.OpenWrite())
        {
            // The check results come from the shipped rules over the evidence the detector would
            // have assembled, not from a literal. An authored row carrying an invented verdict
            // would be a test of the test; what is authored here is the trigger and the stop, which
            // is the part a detector cannot supply for a name that has not pulled back.
            LongPullbackRules.LongEvidence? evidence =
                LongSetupDetector.Evidence(connection, AuthoredSetupTicker, AsOf);

            IReadOnlyList<CheckResult> results = evidence is null
                ? []
                : LongPullbackRules.Evaluate(evidence);

            using SqliteCommand setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                                   trigger_price, stop_price, stop_distance_ranges)
                VALUES (@setup_id, @as_of, @ticker, 'long', @check_results, @passed_all, @trigger, @stop, '0.2700')
                """;
            setup.Parameters.AddWithValue("@check_results", JsonSerializer.Serialize(results, CheckJson));
            setup.Parameters.AddWithValue("@passed_all", SetupChecks.PassedAll(results) ? 1 : 0);
            setup.Parameters.AddWithValue("@setup_id", $"{AuthoredSetupTicker}-long");
            setup.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            setup.Parameters.AddWithValue("@ticker", AuthoredSetupTicker);
            setup.Parameters.AddWithValue("@trigger", AuthoredTrigger);
            setup.Parameters.AddWithValue("@stop", AuthoredStop);
            setup.ExecuteNonQuery();
        }

        return new SignalVectorizer(_connections, Logger(), _clock, _options).Vectorize(AsOf);
    }

    private IReadOnlyList<Measurement> SignalFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        IReadOnlyList<StoredSetupSignal> frozen = SetupSignalReader.Read(connection, AsOf);

        foreach (StoredSetupSignal signal in frozen.OrderBy(s => s.SignalName, StringComparer.Ordinal))
        {
            // Rounded to four places where the value is a number, on the same terms as every other
            // figure here. The store keeps the full decimal, because a signal is evidence and
            // rounding it would be discarding what the night knew; the measurement rounds, because
            // a diff that turns on the twenty-eighth decimal place is a diff that fails on a
            // platform rather than on a defect. A signal whose value is a word passes through.
            // Counts stay whole and measurements round to four places. A rank of ten rendered as
            // 10.0000 reads as a figure taken to four places, which says something about precision
            // that is not true. Which signals are counts is declared on the vectorizer rather than
            // inferred from the value, because inference would read a price of 355.00 as a count.
            bool numeric = decimal.TryParse(
                signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number);

            string value = !numeric ? signal.Value
                : SignalVectorizer.Counts.Contains(signal.SignalName)
                    ? decimal.Truncate(number).ToString(CultureInfo.InvariantCulture)
                    : Figure(number);

            figures.Add(new Measurement($"signal.{signal.SetupId}.{signal.SignalName}", value));
        }

        // Named rather than skipped, on the same terms as a missing indicator row. A signal the
        // history could not support is a fact about the fixture, and one that quietly stopped being
        // produced would otherwise leave the diff green over a shrinking subject.
        foreach (string name in SignalVectorizer.Frozen.Order(StringComparer.Ordinal))
        {
            if (!frozen.Any(s => string.Equals(s.SignalName, name, StringComparison.Ordinal)))
            {
                figures.Add(new Measurement($"signal.{AuthoredSetupTicker}-long.{name}", "absent"));
            }
        }

        return figures;
    }

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
            figures.Add(new Measurement($"ladder.{ticker}", stored.LadderGrade ?? "ungraded"));
        }

        return figures;
    }

    /// <summary>
    /// The liquidity floor, measured over the twenty sessions it is defined on.
    ///
    /// The obligation raised at 1.7 was that the fixture screens the universe over one market
    /// day while the floor is a median over twenty, so the screen runs on a number the floor does
    /// not mean. That is true of the whole-market screen and it is not true of these names: the
    /// fixture holds 251 sessions for each of them, so the twenty-session median is computable
    /// here today and the floor comparison is the real one.
    ///
    /// So the obligation splits. The half that can be tested is tested here, over the per-ticker
    /// histories. The half that cannot is the whole-market screen, which needs twenty bulk days
    /// the fixture does not hold; <see cref="FixtureReplayCheck"/> records that as out of scope
    /// with the condition that would end it, rather than the two being carried as one open item
    /// that reads as though neither had been done.
    ///
    /// Measured from the stored bars rather than from the indicator row, and the difference is
    /// the window rather than the arithmetic. The indicator row's median is taken over the
    /// engine's own twenty-session tail of its warm-up window; this one is taken over the window
    /// the floor is defined on, selected here by a second call to the point-in-time reader. So
    /// what a disagreement between them would show is a window or a reader that stopped agreeing,
    /// not two implementations of a median.
    ///
    /// There is only one implementation of the median and this shares it deliberately.
    /// <c>UniverseBuilder.Median</c> forwards to <c>Averages.Median</c>, so calling it here means
    /// the figure is the one the screen would compute rather than a re-derivation that could
    /// agree with the definition and disagree with the code. The independent derivation of these
    /// values lives outside the solution, in <c>tools/derive-indicators.py</c>, which is where a
    /// second implementation belongs and the only place it can say anything.
    /// </summary>
    private IReadOnlyList<Measurement> LiquidityFloorFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();

        // The lab's own floors, not the replay's. _options narrows LiquidityWindowSessions to the
        // one market day the fixture holds so the whole-market screen can run at all, and reading
        // the window from there would measure a one-session median and call it the floor, which is
        // the exact confusion this section exists to remove.
        var floors = new UniverseOptions();
        var figures = new List<Measurement>();
        int measured = 0;
        int clearing = 0;
        int short_ = 0;

        foreach (string ticker in FixtureTickers.All.Order(StringComparer.Ordinal))
        {
            IReadOnlyList<StoredDailyBar> window =
                DailyBarReader.Read(connection, ticker, AsOf, floors.LiquidityWindowSessions);

            if (window.Count < floors.LiquidityWindowSessions)
            {
                // Named rather than skipped, on the same reasoning as a missing indicator row.
                figures.Add(new Measurement($"liquidity.{ticker}", "short of the window"));
                short_++;
                continue;
            }

            decimal median = UniverseBuilder.Median([.. window.Select(b => b.Close * b.Volume)]);
            bool clears = median >= floors.LiquidityFloorLong;

            measured++;
            if (clears)
            {
                clearing++;
            }

            figures.Add(new Measurement($"liquidity.{ticker}.medianDollarVolume20", Figure(median)));
            figures.Add(new Measurement($"liquidity.{ticker}.clearsTheFloor", clears ? "yes" : "no"));
        }

        // The trackers, measured against the same two floors and deliberately kept apart from the
        // names above.
        //
        // They are the only captured names that are not universe members, so they are the obvious
        // candidate for the floor's rejecting case, and they are the wrong one: they are excluded
        // because the symbol list types them ETF, and they clear both floors by between two and
        // three orders of magnitude. Recording them as floor rejections would file a type
        // rejection under the wrong heading and leave the floor's rejecting path still untested
        // while looking tested, which is worse than leaving it open.
        //
        // What they do pin is the filter that actually excludes them, against the strongest
        // possible case: a name that passes every floor and is still not admitted.
        int trackersClearingBothFloors = 0;

        foreach (string tracker in _options.Value.IndexSymbols.Order(StringComparer.Ordinal))
        {
            IReadOnlyList<StoredDailyBar> window =
                IndexBarReader.Read(connection, tracker, AsOf, floors.LiquidityWindowSessions);

            if (window.Count < floors.LiquidityWindowSessions)
            {
                figures.Add(new Measurement($"tracker.{tracker}", "short of the window"));
                continue;
            }

            decimal median = UniverseBuilder.Median([.. window.Select(b => b.Close * b.Volume)]);
            bool clearsBoth = median >= floors.LiquidityFloorLong && window[^1].Close >= floors.PriceFloor;

            if (clearsBoth)
            {
                trackersClearingBothFloors++;
            }

            figures.Add(new Measurement($"tracker.{tracker}.medianDollarVolume20", Figure(median)));
            figures.Add(new Measurement($"tracker.{tracker}.clearsBothFloors", clearsBoth ? "yes" : "no"));
            figures.Add(new Measurement($"tracker.{tracker}.isAUniverseMember",
                UniverseMembers().Contains(tracker, StringComparer.Ordinal) ? "yes" : "no"));
        }

        figures.Add(new Measurement("tracker.clearingBothFloors",
            trackersClearingBothFloors.ToString(CultureInfo.InvariantCulture)));

        figures.Add(new Measurement("liquidity.sessionsInTheWindow",
            floors.LiquidityWindowSessions.ToString(CultureInfo.InvariantCulture)));
        figures.Add(new Measurement("liquidity.tickersMeasured", measured.ToString(CultureInfo.InvariantCulture)));
        figures.Add(new Measurement("liquidity.clearingTheFloor", clearing.ToString(CultureInfo.InvariantCulture)));
        figures.Add(new Measurement("liquidity.belowTheFloor", (measured - clearing).ToString(CultureInfo.InvariantCulture)));
        figures.Add(new Measurement("liquidity.shortOfTheWindow", short_.ToString(CultureInfo.InvariantCulture)));

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

    /// <summary>
    /// What the read surface answers for one stock's window, and the property the chart page
    /// exists to make visible: the last point of every line it draws is the number the engine
    /// stored for that session.
    ///
    /// Frozen as three values and one word rather than as a comparison alone, so a run where
    /// both sides moved together still shows up. Two numbers that agree with each other and
    /// with nothing else is the failure a single "they matched" would hide.
    /// see: The averages are one implementation, computed nightly and drawn on demand
    /// </summary>
    private IReadOnlyList<Measurement> ReadSurfaceFigures()
    {
        const string Ticker = "IESC";

        ChartResponse chart = LabChart.Read(_connections, Ticker, AsOf, ChartSessions, _clock.UtcNow);

        decimal Drawn(string name) => chart.Averages.Single(a => a.Name == name).Values[^1]
            ?? throw new InvalidOperationException($"{name} has no value at the last drawn session.");

        bool agrees = chart.Readout is not null
            && Drawn("ema9") == chart.Readout.Ema9
            && Drawn("ema21") == chart.Readout.Ema21
            && Drawn("ema50") == chart.Readout.Ema50;

        return
        [
            new Measurement($"read.{Ticker}.drawn", chart.Drawn.ToString(CultureInfo.InvariantCulture)),
            new Measurement($"read.{Ticker}.read", chart.Read.ToString(CultureInfo.InvariantCulture)),
            new Measurement($"read.{Ticker}.drawnEma9", Figure(Drawn("ema9"))),
            new Measurement($"read.{Ticker}.drawnEma21", Figure(Drawn("ema21"))),
            new Measurement($"read.{Ticker}.drawnEma50", Figure(Drawn("ema50"))),
            new Measurement($"read.{Ticker}.drawnAgreesWithStored", agrees ? "yes" : "no"),
        ];
    }

    /// <summary>
    /// What the gallery is handed for the night, and the agreement rate on it.
    ///
    /// The rate is over the setups a person has looked at, not over the night: "two of three agreed"
    /// and "two agreed, one disagreed, and nobody has opened the third" are different facts, and the
    /// second is the one that says whether the review has happened.
    ///
    /// The counts stay per direction, because the gallery is the one screen where pooling them would
    /// be a single careless loop away.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    private IReadOnlyList<Measurement> GalleryFigures()
    {
        SetupsResponse night = new LabSetups(_connections).Read(AsOf, _clock.UtcNow);

        SetupView[] all = [.. night.Long, .. night.Short];
        int looked = all.Count(s => s.Agreement is not null);
        int agreed = all.Count(s => s.Agreement == "agree");

        // A thumbnail's geometry, laid by the component the chart page draws one large with. It is
        // the same lay over a shorter window, so a second implementation appearing here would show
        // as these numbers moving while the chart figures did not.
        SetupView? first = all.OrderBy(s => s.SetupId, StringComparer.Ordinal).FirstOrDefault();

        CandlestickGeometry thumbnail = CandlestickChart.Lay(
            first is null
                ? []
                : [.. first.Candles.Select(c => new Candle(
                    DateOnly.ParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    c.Open, c.High, c.Low, c.Close))],
            [],
            SetupsModel.Width,
            SetupsModel.Height);

        return
        [
            new Measurement("gallery.flagged", night.Flagged.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.long", night.Long.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.short", night.Short.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.checkNames", string.Join(" ", night.CheckNames)),
            new Measurement("gallery.lookedAt", looked.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.agreed", agreed.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.agreementRate", looked == 0
                ? "nobody has looked"
                : Figure((decimal)agreed / looked)),
            new Measurement("gallery.thumbnail", first?.SetupId ?? "no setup"),
            new Measurement("gallery.thumbnailCandles", thumbnail.Candles.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.thumbnailLastCentre", thumbnail.Candles.Count == 0
                ? "no candles"
                : Coordinate(thumbnail.Candles[^1].Centre)),
        ];
    }

    /// <summary>The ticker the future-dated correction is authored against, and the close it carries.</summary>
    public const string CorrectedTicker = "IESC";

    /// <summary>A close no real session produced, so a read returning it is unmistakable.</summary>
    public const string CorrectedClose = "999.00";

    /// <summary>
    /// A correction observed after the night, read from both sides of its own observation.
    ///
    /// AUTHORED, and it has to be: the captured day holds one evening's responses, so a vendor
    /// restating a figure the following evening is a case the fixture cannot contain. It is the same
    /// tier and the same reasoning as the synthetic split at 1.5.
    ///
    /// <b>Two figures rather than one verdict.</b> "The night did not see it" is satisfied perfectly
    /// by a read that returns nothing at all, and by a store that never took the row. Reading the
    /// same session from both sides of the correction's own instant is what makes the first figure
    /// mean the bound held rather than the row being missing.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    private IReadOnlyList<Measurement> PointInTimeFigures()
    {
        DateTimeOffset afterwards = _clock.UtcNow.AddDays(1);

        using (SqliteConnection write = _connections.OpenWrite())
        {
            using SqliteCommand correction = write.CreateCommand();
            correction.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                SELECT ticker, bar_date, open, high, low, @close, @close, volume, @observed_at
                  FROM daily_bar
                 WHERE ticker = @ticker AND bar_date = @as_of
                 LIMIT 1
                """;
            correction.Parameters.AddWithValue("@ticker", CorrectedTicker);
            correction.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            correction.Parameters.AddWithValue("@close", CorrectedClose);
            correction.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(afterwards));
            correction.ExecuteNonQuery();
        }

        using SqliteConnection read = _connections.OpenReadOnly();

        IReadOnlyList<StoredDailyBar> onTheNight = DailyBarReader.Read(read, CorrectedTicker, AsOf, 1);
        IReadOnlyList<StoredDailyBar> later = DailyBarReader.Read(read, CorrectedTicker, AsOf, 1, afterwards);

        return
        [
            new Measurement($"pointInTime.{CorrectedTicker}.onTheNight",
                onTheNight.Count == 0 ? "no bar" : Figure(onTheNight[^1].AdjustedClose)),
            new Measurement($"pointInTime.{CorrectedTicker}.afterwards",
                later.Count == 0 ? "no bar" : Figure(later[^1].AdjustedClose)),
            new Measurement($"pointInTime.{CorrectedTicker}.observations",
                Observations(read, CorrectedTicker, AsOf).ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>How many observations the store holds of one session, which must be two by now.</summary>
    private static int Observations(SqliteConnection connection, string ticker, DateOnly asOf)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM daily_bar WHERE ticker = @ticker AND bar_date = @as_of
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>A session, written the way the store and the vendor both write one.</summary>
    public static string Session(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
