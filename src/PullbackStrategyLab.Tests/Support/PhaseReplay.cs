using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Api;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Web.Pages;
using PullbackStrategyLab.Web.Shell;
using PullbackStrategyLab.Worker.Vendor;

using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Tests.Checks;

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

        // A plan belongs to a version from 5.1 and the store's key says so, so the fixture
        // registers the baseline before anything writes a plan. The lab does not do this for
        // itself: registering a version is VariantAdmitter's, and a migration that seeded one
        // would start an experiment nobody chose to start.
        using (SqliteConnection seed = _connections.OpenWrite())
        {
            TestVersions.SeedBaseline(seed);
        }

        _http = new HttpClient(_handler) { BaseAddress = new Uri(new PullbackStrategyLabOptions().Vendor.BaseAddress) };
        Vendor = new EodhdClient(_http, WithToken());
    }

    /// <summary>
    /// The names the captured fixture holds a fundamentals response for, from the files rather than
    /// from a list in code. A response added to the capture is read without anyone remembering to
    /// add it here, which is the direction this kind of list usually fails in.
    /// </summary>
    private IReadOnlyList<string> CapturedFundamentalsTickers() =>
    [
        .. Directory.EnumerateFiles(_handler.Directory, "fundamentals-*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)["fundamentals-".Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// The names the captured quote response was asked for, read out of the manifest's own query
    /// rather than restated here, on the same grounds the fundamentals list is read from the
    /// directory: a list in code goes stale in the direction nobody notices.
    ///
    /// It is the names <b>asked for</b> and not the names answered, deliberately. One of them is
    /// missing from the response, which is the case a live pass has to tell from a name quoted with
    /// no bid, and a list derived from the answer could not hold it.
    /// </summary>
    private IReadOnlyList<string> CapturedQuoteTickers()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_handler.Directory, "manifest.json")));

        foreach (JsonElement response in manifest.RootElement.GetProperty("responses").EnumerateArray())
        {
            if (response.GetProperty("endpoint").GetString() != EodhdClient.UsQuotePath)
            {
                continue;
            }

            string query = response.GetProperty("query").GetString() ?? string.Empty;

            return
            [
                .. query["s=".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(symbol => symbol.Split('.')[0])
                    .Order(StringComparer.Ordinal),
            ];
        }

        return [];
    }

    /// <summary>
    /// A budget for a read that is not a stage's. The captured responses cost nothing to serve and
    /// charging them to a run entry would put figures in the run log for calls nobody made.
    /// </summary>
    private sealed class UncountedBudget : ICallBudget
    {
        public int CallsRemaining => int.MaxValue;

        public bool TryCountCall() => true;

        public bool TryCountCalls(int cost) => true;

        public void CountCall()
        {
        }
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

    /// <summary>
    /// The scoreboard build, over the store this replay holds now rather than as it stood.
    ///
    /// Exposed so a test can build the panels after a calibration run has filled the calibration
    /// table, which is the only way to ask behaviourally whether band 1 can see reconstructed rows.
    /// </summary>
    public ScoreboardResult BuildScoreboard() =>
        new ScoreboardBuilder(_connections, Logger(), _clock, _options).Build(AsOf);

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

        // Some figures are not counts. A sector is a string and comparing it as one is the point:
        // the defect that made this necessary was a field read as the wrong type.
        void RecordText(string id, string value) => measurements.Add(new Measurement(id, value));

        // One scalar out of the replay's own store, with the session and its end of day already
        // bound. Both parameters are always supplied, so a query naming neither is still valid and
        // one naming either cannot be written against the wrong instant by accident.
        int Scalar(string sql)
        {
            using SqliteConnection connection = _connections.OpenReadOnly();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
            command.Parameters.AddWithValue(
                "@end_of_session", StoreText.EndOfSession(AsOf, SessionBoundaries.UsEquities));
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

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

        // 3.9(d). Every hit the walk writes carries an observation stamp, and every one of them is
        // inside the session's own day. Two counts rather than one, because a column populated with
        // an instant outside the session would satisfy "not null" and be worse than a null.
        Record("scans.stamped", Scalar(
            "SELECT COUNT(*) FROM scan_hit WHERE as_of = @as_of AND observed_at IS NOT NULL"));
        Record("scans.stampedInsideTheSession", Scalar(
            "SELECT COUNT(*) FROM scan_hit WHERE as_of = @as_of AND observed_at IS NOT NULL "
            + "AND observed_at <= @end_of_session"));

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

        // The two figures 4.17 repaired, frozen so the repair is a fact about the pipeline rather
        // than a sentence in a record. `asked` counts a name before the request rather than after a
        // successful answer, so the skipped are inside the count they are stated as a subset of, and
        // `requests` is `asked` rather than `asked + skipped`: over this fixture nothing is skipped, so
        // the two readings agree here and would not on the night the repair was written for, where
        // 149 requests and 148 answers read as "148 asked of which 1 skipped".
        Record("sectors.skipped", sectors.Skipped);
        Record("sectors.requests", sectors.Requests);

        // 10a. Every captured fundamentals response read through the real client, including the
        //      ones no scan surfaced.
        //
        //      The resolver above only asks about names a scan hit, which is right for a nightly
        //      stage and leaves the interesting response unread: `fundamentals-MUZ.json` is the one
        //      the vendor answered 200 with two empty strings and a capitalisation of the string
        //      "NA", and it took the whole sector walk down on 2026-08-27. Thirty working examples
        //      and no failing one is how the parse came to be exercised thirty times against nothing
        //      that could go wrong, so every captured response is read here rather than only the
        //      ones tonight's scan happened to want.
        //
        //      Recorded as four figures a name so the diff names the field that moved rather than
        //      reporting one row unequal, and tiered DERIVED against the Python restatement in
        //      tools/derive-indicators.py --fundamentals, which reads the same bytes with a
        //      different language's JSON reader and shares no code with this one.
        //      see: Every fixture expectation records how it was produced, and only the independently derived ones verify anything
        foreach (string ticker in CapturedFundamentalsTickers())
        {
            VendorFundamentals? held = Vendor
                .GetFundamentalsAsync(ticker, new UncountedBudget()).GetAwaiter().GetResult().Value;

            RecordText($"fundamentals.{ticker}.held", held is null ? "no" : "yes");

            if (held is null)
            {
                continue;
            }

            RecordText($"fundamentals.{ticker}.sector", held.Sector ?? "-");
            RecordText($"fundamentals.{ticker}.industry", held.Industry ?? "-");
            RecordText($"fundamentals.{ticker}.marketCap", held.MarketCap?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

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

        // 13. The journal, which seals the night between the freeze and the cap. It writes nothing,
        //     so its stage row carries no rows written; what it produces is a verdict on whether the
        //     night's rows are complete, frozen, and untouched by anything that runs later.
        JournalResult sealed_ = new SetupJournal(_connections, Logger(), _clock, _options).Seal(AsOf);

        stages.Add(new StageRun(SetupJournal.Name, 0, sealed_.RowsWritten, sealed_.Outcome.ToStorageText()));
        Record("journal.setups", sealed_.Setups);
        Record("journal.withSignals", sealed_.WithSignals);
        Record("journal.breaches", sealed_.Breaches.Count);

        // 14. The control draw, before the cap, so the controls answer for the flagged population
        //     rather than for the sixty that survive truncation.
        ControlResult controls = new ControlSampler(_connections, Logger(), _clock, _options).Draw(AsOf);

        stages.Add(new StageRun(ControlSampler.Name, 0, controls.RowsWritten, controls.Outcome.ToStorageText()));
        Record("controls.setups", controls.Setups);
        Record("controls.pool", controls.Pool);
        Record("controls.loose", controls.Loose);
        Record("controls.tight", controls.Tight);
        Record("controls.shortOfFive", controls.ShortOfFive);

        // 15. The nightly cap, over whatever the night's detectors left.
        CapResult capped = new SetupCapper(_connections, Logger(), _clock, _options).Cap(AsOf);

        stages.Add(new StageRun(SetupCapper.Name, 0, capped.RowsWritten, capped.Outcome.ToStorageText()));
        Record("cap.setups", capped.Setups);
        Record("cap.candidates", capped.Candidates);
        Record("cap.longCandidates", capped.LongCandidates);
        Record("cap.shortCandidates", capped.ShortCandidates);
        Record("cap.longKept", capped.LongKept);
        Record("cap.shortKept", capped.ShortKept);
        Record("cap.cappedOut", capped.CappedOut);

        // 15a. The plans, one committed instruction per capped candidate, at 18:30.
        //
        //      <b>The fixture's own candidates are what this runs over, and none of them is a
        //      trade.</b> The funnel passes a median of nought a night, so a stage planning only
        //      passing rows would write nothing here and nothing on a live night; plans are written
        //      for capped candidates, which the fixture has. The refusal counts are the figures
        //      worth freezing: they say how many of the night's candidates carried no trade geometry
        //      and, separately, how many carried a trigger and a give-up point at the same price,
        //      which is the shape the 3.15 obligation named and 4.16 discharges.
        PlanRunResult plans = new PlanBuilder(_connections, Logger(), _clock, _options).Build(AsOf);

        stages.Add(new StageRun(PlanBuilder.Name, 0, plans.RowsWritten, plans.Outcome.ToStorageText()));
        Record("plans.candidates", plans.Candidates);
        Record("plans.planned", plans.Planned);
        Record("plans.refusedAbsentGeometry", plans.RefusedAbsentGeometry);
        Record("plans.refusedEqualPrices", plans.RefusedEqualPrices);
        Record("plans.refusedBelowOneShare", plans.RefusedBelowOneShare);

        // 15b. The minute bars, which run at 20:30 for the session that has just closed and resolve
        //      the setups flagged on the evening before it.
        //
        //      <b>Over this fixture it asks for nothing, and that is the expectation rather than a
        //      gap.</b> The fixture holds one market day and its setups are flagged on that same
        //      day, so no session before it flagged anything and no plan was live in it. The stage
        //      records a fetch of nothing rather than skipping, because a night with no row is
        //      indistinguishable from a night the scheduler never fired. A fixture that grows a
        //      second session turns these four figures into a real fetch without any edit here,
        //      which is what makes them worth freezing now.
        IntradayFetchResult minutes = new IntradayFetcher(Vendor, _connections, Logger(), _clock, _options)
            .FetchAsync(AsOf).GetAwaiter().GetResult();

        stages.Add(new StageRun(IntradayFetcher.Name, minutes.CallsUsed, minutes.RowsWritten, minutes.Outcome.ToStorageText()));
        Record("intraday.requested", minutes.Requested);
        Record("intraday.fetched", minutes.Fetched);
        Record("intraday.empty", minutes.Empty);
        Record("intraday.barsWritten", minutes.BarsWritten);
        Record("intraday.pairedWithPriorSession", minutes.SetupAsOf is null ? 0 : 1);

        // 15c. The spreads, which run inside the session rather than after it and carry the same
        //      offset as the minute bars. Over this fixture the pass asks for nothing for exactly
        //      the reason the fetch above does: one market day, setups flagged on it, no earlier
        //      session whose plans were live in it. Recorded rather than skipped, and the row it
        //      writes is what makes the sampling readable at all.
        SpreadPassResult spreads = new SpreadSnapshotter(Vendor, _connections, Logger(), _clock, _options)
            .SnapshotAsync(AsOf, SpreadSnapshotter.AfterOpenPass).GetAwaiter().GetResult();

        stages.Add(new StageRun(SpreadSnapshotter.Name, spreads.CallsUsed, spreads.RowsWritten, spreads.Outcome.ToStorageText()));
        Record("spread.requested", spreads.Requested);
        Record("spread.answered", spreads.Answered);
        Record("spread.quoted", spreads.Quoted);
        Record("spread.unquoted", spreads.Unquoted);
        Record("spread.pairedWithPriorSession", spreads.SetupAsOf is null ? 0 : 1);

        // 15c. The two averages, at 21:00 over the minutes the fetch stored at 20:30. Over this
        //      fixture it prices nothing for the same reason the two stages above ask for nothing,
        //      and the anchor figures are what make that readable: `anchorsAsked` against
        //      `anchorsPriced` is the state of the third ceiling clause on any night, and both being
        //      nought here says the night had no anchors rather than that it could not reach them.
        VwapRunResult vwap = new VwapEngine(_connections, Logger(), _clock, _options).Compute(AsOf);

        stages.Add(new StageRun(VwapEngine.Name, 0, vwap.RowsWritten, vwap.Outcome.ToStorageText()));
        Record("vwap.names", vwap.Names);
        Record("vwap.anchorsAsked", vwap.AnchorsAsked);
        Record("vwap.anchorsPriced", vwap.AnchorsPriced);
        Record("vwap.pairedWithPriorSession", vwap.SetupAsOf is null ? 0 : 1);

        // 15d. The replay, at 21:05, over the same stored minutes the averages ran on. It decides
        //      whether each plan resting in the session was touched and in which minute.
        //
        //      <b>Over this fixture nothing rests and nothing is walked, and the seven figures say
        //      which of those two it was.</b> The night's cap kept no candidate, so `plans.planned`
        //      is nought and `trade_plan` is empty; the fetch stored no bar, so the clock has no
        //      minute. Both are nought and they are different noughts: no plan resting is a clean
        //      night, and a plan resting with no minute to ask it against is a partial one. A
        //      fixture that grows a second session with a candidate in it turns these into real
        //      counts with no edit here, which is what makes them worth freezing now.
        TriggerRunResult triggers = new TriggerResolver(_connections, Logger(), _clock, _options)
            .Resolve(AsOf);

        stages.Add(new StageRun(TriggerResolver.Name, 0, triggers.RowsWritten, triggers.Outcome.ToStorageText()));
        Record("triggers.plans", triggers.Plans);
        Record("triggers.touched", triggers.Touched);
        Record("triggers.notTouched", triggers.NotTouched);
        Record("triggers.unresolvable", triggers.Unresolvable);
        Record("triggers.namesWalked", triggers.NamesWalked);
        Record("triggers.minutesWalked", triggers.MinutesWalked);
        Record("triggers.pairedWithPriorSession", triggers.SetupAsOf is null ? 0 : 1);

        // 15e. The caps, at 21:10, over the triggers the replay recorded.
        //
        //      <b>Nothing rested and nothing triggered, so the gate decides nothing, and the run row
        //      says which of those it was.</b> `triggers.touched` is nought, so there is no order to
        //      place or refuse. The four figures below are worth freezing because a blocked order is
        //      a row rather than an absence: the day the fixture holds a session with more triggers
        //      than slots, `placed` and `blocked` become real counts with no edit here, and
        //      `reduced` moves independently of both.
        OrderRunResult orders = new RiskGate(_connections, Logger(), _clock, _options).Apply(AsOf);

        stages.Add(new StageRun(RiskGate.Name, 0, orders.RowsWritten, orders.Outcome.ToStorageText()));
        Record("orders.triggers", orders.Triggers);
        Record("orders.placed", orders.Placed);
        Record("orders.reduced", orders.Reduced);
        Record("orders.blocked", orders.Blocked);

        // 15f. The fills, at 21:15, over the orders the gate placed.
        //
        //      <b>Nothing was placed, so the night prices nothing and says so.</b> `openAtStart` is
        //      the book RiskGate read at 21:10 and is the figure worth freezing hardest: a session
        //      that opened a position and did not close it is a session the next morning's fifth
        //      trigger is refused on, and it would move here first. `unfilled` is the other one: the
        //      fixture's own capture holds a name the vendor quoted with one side, so the day this
        //      fixture grows an order on such a name it becomes a real count with no edit to this
        //      file.
        //
        //      The session it walks was sampled once rather than twice, which is the degraded state
        //      the reader tells apart from unsampled, so the stage prices rather than refusing.
        FillRunResult fills = new PaperBroker(_connections, Logger(), _clock, _options).Fill(AsOf);

        stages.Add(new StageRun(PaperBroker.Name, 0, fills.RowsWritten, fills.Outcome.ToStorageText()));
        Record("fills.openAtStart", fills.OpenAtStart);
        Record("fills.ordersPlaced", fills.OrdersPlaced);
        Record("fills.entriesFilled", fills.EntriesFilled);
        Record("fills.entriesUnfilled", fills.EntriesUnfilled);
        Record("fills.gapped", fills.Gapped);
        Record("fills.slipped", fills.Slipped);

        // 15g. The two rule sets, at 21:20, over every position open at any point in the session.
        //
        //      <b>Nothing was open, so no rule ran and no exit was priced.</b> The three closing
        //      figures are counted apart on purpose: a night of trail exits is a different night
        //      from a night of stop-outs, and a single total would let the one that is a finding
        //      hide inside the one that is ordinary. `closedInTheirOwnSession` is the size of the
        //      approximation the caps make, being what RiskGate could not see at 21:10, and it is
        //      the figure that says how much a merge of the two stages would be worth.
        ManageRunResult managed =
            new PositionManager(_connections, Logger(), _clock, _options).Manage(AsOf);

        stages.Add(new StageRun(PositionManager.Name, 0, managed.RowsWritten, managed.Outcome.ToStorageText()));
        Record("manage.openAtStart", managed.OpenAtStart);
        Record("manage.longsManaged", managed.LongsManaged);
        Record("manage.shortsManaged", managed.ShortsManaged);
        Record("manage.closedGiveUp", managed.ClosedGiveUp);
        Record("manage.closedTrail", managed.ClosedTrail);
        Record("manage.closedReclaim", managed.ClosedReclaim);
        Record("manage.trimmed", managed.Trimmed);
        Record("manage.exitsArmed", managed.ExitsArmed);
        Record("manage.heldNoQuote", managed.HeldNoQuote);
        Record("manage.closedInTheirOwnSession", managed.ClosedInTheirOwnSession);
        Record("manage.openAtEnd", managed.OpenAtEnd);

        // 15h. The trades, at 21:25, over the positions the slot above closed.
        //
        //      <b>Nothing closed, so nothing was journalled.</b> `shortsCharged` is the figure worth
        //      freezing hardest, because it is the one place a nought and a nought mean different
        //      things: a short closed in the session it opened in was never held overnight and pays
        //      no borrow, so a fixture that grows a same-day short leaves this at nought while
        //      `shorts` moves, and one that grows an overnight short moves both.
        TradeRunResult journalled =
            new TradeJournal(_connections, Logger(), _clock, _options).Close(AsOf);

        stages.Add(new StageRun(TradeJournal.Name, 0, journalled.RowsWritten, journalled.Outcome.ToStorageText()));
        Record("trades.closedInSession", journalled.ClosedInSession);
        Record("trades.journalled", journalled.Journalled);
        Record("trades.longs", journalled.Longs);
        Record("trades.shorts", journalled.Shorts);
        Record("trades.shortsCharged", journalled.ShortsCharged);
        Record("trades.trimmed", journalled.Trimmed);
        Record("trades.armedExits", journalled.ArmedExits);

        // 15i. The audit, at 21:26, over the trades the slot above wrote.
        //
        //      <b>Nothing was journalled, so nothing was audited.</b> `tradesRead` against `audited`
        //      is the pair that says whether anything was refused: they differ on a rerun and on a
        //      trade missing a fill at one end, which is refused rather than filled with noughts.
        AuditRunResult audited =
            new PlanAudit(_connections, Logger(), _clock, _options).Audit(AsOf);

        stages.Add(new StageRun(PlanAudit.Name, 0, audited.RowsWritten, audited.Outcome.ToStorageText()));
        Record("audit.tradesRead", audited.TradesRead);
        Record("audit.audited", audited.Audited);
        Record("audit.longs", audited.Longs);
        Record("audit.shorts", audited.Shorts);
        Record("audit.reducedByACap", audited.ReducedByACap);
        Record("audit.gappedAtAnEnd", audited.GappedAtAnEnd);

        // The seam, read off the rows the detectors wrote rather than off the constant that names
        // it. Every short row on the fixture carries a `reached-ceiling` verdict, and which clause
        // set it records is the thing 3.6 counts the short side's twenty sessions by. The engine
        // above reached no anchor, so nothing here is the full disjunction; a fixture that grows a
        // second market day and a stored minute turns the last of these five off nought with no
        // edit to this file, which is what makes them worth freezing now.
        using (SqliteConnection verdicts = _connections.OpenReadOnly())
        {
            CeilingClauses[] sets = [.. CeilingVerdicts(verdicts, SetupReader.SetupTable)];

            // The five buckets are exhaustive over the population above, so the four below plus the
            // unevaluated ones add to it. A set of counts that does not add up leaves a state
            // nobody is reporting, which is how a fifth clause record would arrive unseen.
            Record("ceiling.shortRowsWithAVerdict", sets.Length);
            Record("ceiling.unrecorded", sets.Count(c => c == CeilingClauses.Unrecorded));
            Record("ceiling.notEvaluated", sets.Count(c => c == CeilingClauses.NotEvaluated));
            Record("ceiling.twoOfThree", sets.Count(c => c == CeilingClauses.TwoOfThree));
            Record("ceiling.anchorUnavailable", sets.Count(c => c == CeilingClauses.AnchorUnavailable));
            Record("ceiling.withTheAnchor", sets.Count(c => c == CeilingClauses.WithTheAnchor));
        }

        // And the sampling state the missed-snapshot behaviour turns on, read back through the
        // reader rather than counted here. One pass has run, so the session is degraded and not
        // complete, and it is not unsampled: three booleans that are the whole of the three cases.
        using (SqliteConnection sampled = _connections.OpenReadOnly())
        {
            SessionSampling sampling = SpreadSnapshotReader.SamplingOf(sampled, AsOf, AsOf, SessionBoundaries.UsEquities);
            Record("spread.passesRecorded", sampling.Passes.Count);
            Record("spread.sessionIsUnsampled", sampling.IsUnsampled ? 1 : 0);
            Record("spread.sessionIsDegraded", sampling.IsDegraded ? 1 : 0);
        }

        //      <b>And the arithmetic, over the captured quotes rather than over the pipeline.</b>
        //      The pass above asks for nothing, so nothing in it exercises the parse, the two-sided
        //      test or the basis-point computation, and freezing five zeros would be regression
        //      detection called verification. The capture holds one real response for thirty-one
        //      names, so the endpoint is read here the way the fundamentals are a few stages up:
        //      every captured name, whatever it answered, including the one it did not answer for.
        //      see: Fixture inputs record where they came from, and a path a live run exercises needs a captured one
        IReadOnlyList<VendorQuote> quotes = Vendor
            .GetQuotesAsync(CapturedQuoteTickers(), new UncountedBudget()).GetAwaiter().GetResult().Value ?? [];

        Record("spread.captured.asked", CapturedQuoteTickers().Count);
        Record("spread.captured.answered", quotes.Count);
        Record("spread.captured.twoSided", quotes.Count(q => q.IsUsable));

        foreach (VendorQuote quote in quotes.OrderBy(q => q.Ticker, StringComparer.Ordinal))
        {
            double? bps = SpreadSnapshotter.SpreadBasisPoints(quote);

            RecordText(
                $"spread.captured.{quote.Ticker}.bps",
                bps is double value ? value.ToString("F3", CultureInfo.InvariantCulture) : "-");
        }

        // 16. The forward fill, which is the one stage that reads bars dated after its subject's
        //     own date. Over the fixture it fills what the single captured night can support: the
        //     as-of is the last session the fixture holds, so no horizon has elapsed and the honest
        //     answer is nought written. Recorded anyway, because "nought outcomes and every horizon
        //     not yet elapsed" is a different fact from "the stage did not run".
        FillResult filled = new ForwardReturnFiller(_connections, Logger(), _clock, _options).Fill(AsOf);

        stages.Add(new StageRun(ForwardReturnFiller.Name, 0, filled.RowsWritten, filled.Outcome.ToStorageText()));
        Record("forward.subjects", filled.Subjects);
        Record("forward.written", filled.Written);
        Record("forward.notYetElapsed", filled.NotYetElapsed);
        Record("forward.setupsLaterThanTheCalendarStep", filled.SetupsLaterThanTheCalendarStep);
        Record("forward.controlsLaterThanTheCalendarStep", filled.ControlsLaterThanTheCalendarStep);
        Record("forward.excursionsUndefined", filled.ExcursionsUndefined);
        Record("forward.setupHorizonsCannotClose", filled.SetupHorizonsCannotClose);
        Record("forward.controlHorizonsCannotClose", filled.ControlHorizonsCannotClose);

        // 16b. The loss classification, at 21:35, after the forward returns because half of what it
        //      answers is one of them: what closed tonight, and what has since had a horizon close.
        //
        //      <b>Nothing closed, so neither pass had a subject.</b> `awaitingAftermath` is the one
        //      worth freezing hardest, because it is the figure that must never be read as
        //      `unclassified`: a row waiting on a horizon is the ordinary state of every loss for
        //      its first ten sessions, and a row that is unclassified is a finding about this
        //      component. The two sit two columns apart on the night's row for that reason.
        LossRunResult classified =
            new LossClassifier(_connections, Logger(), _clock, _options).Classify(AsOf);

        stages.Add(new StageRun(LossClassifier.Name, 0, classified.RowsWritten, classified.Outcome.ToStorageText()));
        Record("losses.closed", classified.LossesClosed);
        Record("losses.mechanismsWritten", classified.MechanismsWritten);
        Record("losses.gap", classified.Gap);
        Record("losses.ordinary", classified.Ordinary);
        Record("losses.awaitingAftermath", classified.AwaitingAftermath);
        Record("losses.aftermathsWritten", classified.AftermathsWritten);
        Record("losses.noise", classified.Noise);
        Record("losses.failedSetup", classified.FailedSetup);
        Record("losses.unclassified", classified.Unclassified);

        // 16c. The journal's read surface, over what every stage above wrote.
        //
        //      <b>Not a stage and not a night, which is why it is here rather than in the run
        //      list.</b> It is the answer a person opens the journal to, and it is the first thing
        //      that reads `trade`, `plan_audit` and `loss_class` together. `slotsTheCapsCouldNotSee`
        //      is the one figure it derives rather than carries: a sum over every `manage_run` the
        //      store holds, and the size of the approximation the caps make.
        JournalResponse journal = LabJournal.Read(_connections, AsOf, SessionBoundaries.UsEquities);

        Record("journal.longTrades", journal.Long.Count);
        Record("journal.shortTrades", journal.Short.Count);
        Record("journal.slotsTheCapsCouldNotSee", journal.SlotsTheCapsCouldNotSee);

        // 16d. The corpus itself, counted from the directory rather than from the list that names it.
        //
        //      <b>Read off the filesystem on purpose, which is what makes it a check on the list.</b>
        //      `RepositoryLayout.CorpusFiles` names the eight documents a citation can live in, and a
        //      ninth added to `/docs` without being added to that list would be a document nothing
        //      scans: `decision-resolves` and `no-superseded-citation` both read the list. This figure
        //      reads the directory, so the day the two disagree the fixture says so.
        //
        //      `artefacts` is nought and the nought is the subject. SCREENS.html was the ninth and
        //      was retired at 4.12 once the pages it drew existed, and a figure that could only ever
        //      be nought would be worth nothing; this one was 1 until that checkpoint.
        string[] documents =
        [
            .. Directory.EnumerateFiles(RepositoryLayout.Docs)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Order(StringComparer.Ordinal),
        ];

        Record("corpus.documents", documents.Length + 1);
        Record("corpus.artefacts", documents.Count(d => d.EndsWith(".html", StringComparison.Ordinal)
            && !d.StartsWith("ARCHITECTURE", StringComparison.Ordinal)));

        // 17. The scoreboard, last, because every panel it builds reads what the stages before it
        //     wrote. Over the fixture most panels are withheld, which is the honest answer for a
        //     lab with one night on file and no closed horizon.
        ScoreboardResult scored = new ScoreboardBuilder(_connections, Logger(), _clock, _options).Build(AsOf);

        stages.Add(new StageRun(ScoreboardBuilder.Name, 0, scored.RowsWritten, scored.Outcome.ToStorageText()));
        Record("scoreboard.panels", scored.Panels);
        Record("scoreboard.withInterval", scored.WithInterval);
        Record("scoreboard.withheld", scored.Withheld);
        Record("scoreboard.attempted", scored.Attempted);
        Record("scoreboard.skipped", scored.Skipped);

        // 3.9(e). Building the same date again writes nothing, and the run says so. Over the golden
        // fixture rather than only in a unit test, because the shape being guarded is a stage
        // reporting success on a store it did not change, and the fixture is the only place the
        // whole pipeline's store is the subject.
        ScoreboardResult rebuilt = new ScoreboardBuilder(_connections, Logger(), _clock, _options).Build(AsOf);

        Record("scoreboard.rebuild.attempted", rebuilt.Attempted);
        Record("scoreboard.rebuild.skipped", rebuilt.Skipped);
        RecordText("scoreboard.rebuild.outcome", rebuilt.Outcome.ToStorageText());

        // And the account-wide panels did not multiply, which is the half the no-op was hiding.
        Record("scoreboard.accountWideRows", Scalar(
            "SELECT COUNT(*) FROM scoreboard WHERE as_of = @as_of AND direction IS NULL"));

        // Which shortage is holding band 1 back, counted rather than described. Withholding is
        // settled by the session axis and the minimum sample by how much information the rows
        // carry, so a panel can be short of one and not the other, and the two figures apart are
        // what say which. Over the fixture every band 1 panel is short of both.
        foreach (Measurement figure in WithheldReasonFigures())
        {
            measurements.Add(figure);
        }

        measurements.AddRange(AccumulationFigures());
        measurements.AddRange(CalibrationCounts());
        measurements.AddRange(ForwardOutcomeFigures());
        measurements.AddRange(ControlFigures());
        measurements.AddRange(CeilingFigures());
        measurements.AddRange(IntervalFigures());
        measurements.AddRange(DispersionFigures());
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

        measurements.AddRange(ReplayFigures());
        measurements.AddRange(HoldoutFigures());
        measurements.AddRange(StoreIntegrityFigures());
        measurements.AddRange(CataloguePlacementFigures());
        measurements.AddRange(AuthoredParameterFigures());
        measurements.AddRange(ClauseFigures());
        measurements.AddRange(RuleFigures());

        // Last, and this comment governs this one call. It writes a row into the store on purpose,
        // so nothing above it may see one. That sentence stood alone until 3.12, when a new method
        // was added underneath it and inherited the probe silently; store.observationsAfterTheAsOf
        // is the same sentence measured instead of asserted in prose, and any figure that moves
        // below this line turns it red rather than waiting to be noticed.
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
            IReadOnlyList<StoredDailyBar> history = IndexBarReader.Read(connection, symbol, AsOf, int.MaxValue, SessionBoundaries.UsEquities);

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
        command.CommandText = "SELECT setup_id, direction, check_results FROM setup";

        // Keyed by direction and name rather than by name, because four of the twenty gate ids
        // appear on both lists, being `cluster`, `exit-tight`, `moves-enough` and `thrust`. A
        // dictionary keyed on the id alone would add a long `exit-tight` pass to a short
        // `exit-tight` fail and report the pair as two-sided. That is the pooling rule arriving in
        // the one place it is easiest to break by accident.
        // see: Long and short are never pooled into one figure
        var passes = new Dictionary<(string Direction, string Name), int>();
        var fails = new Dictionary<(string Direction, string Name), int>();

        // And the same two counters over the authored row, kept apart rather than added in.
        //
        // The row this harness inserts to give the vectorizer a subject arrives through the store
        // like any other, so until 3.0(e) it was counted into the figures beside two rows a detector
        // wrote. It bypasses the recording floor, which is why it carries `uptrend` failed on a
        // grade of `mixed`, and `uptrend` and `contraction` were the only two long gates the report
        // called two-sided: both were two-sided only because this row disagreed with a detector row.
        // Over detector-written rows alone every one of the twenty gate slots is one-sided.
        //
        // The gate cases were already kept out of these counters and said so. The authored setup row
        // was not, which is the same rule with one of its subjects missing.
        // see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
        var authoredPasses = new Dictionary<(string Direction, string Name), int>();
        var authoredFails = new Dictionary<(string Direction, string Name), int>();

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string setupId = reader.GetString(0);
                string direction = reader.GetString(1);
                bool isAuthoredRow = string.Equals(setupId, AuthoredSetupId, StringComparison.Ordinal);

                foreach (CheckResult result in
                         JsonSerializer.Deserialize<CheckResult[]>(reader.GetString(2), CheckJson) ?? [])
                {
                    Dictionary<(string, string), int> side = (isAuthoredRow, result.Passed) switch
                    {
                        (true, true) => authoredPasses,
                        (true, false) => authoredFails,
                        (false, true) => passes,
                        _ => fails,
                    };

                    side[(direction, result.Name)] = side.GetValueOrDefault((direction, result.Name)) + 1;
                }
            }
        }

        // Per setup, per check, the verdict. This is what makes a changed gate show up as a named
        // difference rather than as a count moving: "dip-shape on HOOD went from fail to pass" is
        // actionable and "one more setup passed" is not.
        using (SqliteCommand perSetup = connection.CreateCommand())
        {
            perSetup.CommandText =
                "SELECT setup_id, check_results, thrust_scan, thrust_session FROM setup ORDER BY setup_id";
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

                // Which scan produced the thrust, recorded from 3.0(b). Frozen here because the
                // correction at 3.0(c) changes what the geometry does with it, and a run that
                // cannot say which scan flagged a row cannot say whether the correction reached it.
                figures.Add(new Measurement(
                    $"setup.{setupId}.thrustScan",
                    rows.IsDBNull(2) ? "none" : rows.GetString(2)));
                figures.Add(new Measurement(
                    $"setup.{setupId}.thrustSession",
                    rows.IsDBNull(3) ? "none" : rows.GetString(3)));
            }
        }

        // The authored boundary cases, evaluated through the shipped rules and kept in a bucket of
        // their own. They are what answers whether both branches of a gate work; they say nothing
        // about the market and are never added to the counts above, which are the detectors' rows.
        // see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
        var authored = new Dictionary<(string Direction, string Name), (bool Pass, bool Fail)>();

        foreach (GateCases.GateCase gateCase in GateCases.All)
        {
            // First rather than Single, and the difference is the whole of a 2.12 finding. Removing
            // a gate's implementation at that sign-off failed `check-completeness`, which is the
            // property holding. It failed on "Sequence contains no matching element" and a stack
            // trace from this line, because the replay the check reads died before the comparison
            // that has a reconciliation message written for exactly this case. A crash and a named
            // failure are not the same artefact: one tells a later session which gate went missing,
            // the other tells it that something threw.
            CheckResult? verdict = GateCases.Evaluate(gateCase)
                .FirstOrDefault(r => string.Equals(r.Name, gateCase.Gate, StringComparison.Ordinal));

            if (verdict is null)
            {
                // Recorded as a value rather than thrown, so the run reaches `check-completeness`
                // and that check reconciles the gate lists by name and says which one is absent.
                figures.Add(new Measurement(gateCase.Id, "no result of that name"));
                continue;
            }

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

                int authoredRowPassed = authoredPasses.GetValueOrDefault((direction, name));
                int authoredRowFailed = authoredFails.GetValueOrDefault((direction, name));

                figures.Add(new Measurement(
                    $"check.{direction}.{name}.passed", passed.ToString(CultureInfo.InvariantCulture)));
                figures.Add(new Measurement(
                    $"check.{direction}.{name}.failed", failed.ToString(CultureInfo.InvariantCulture)));

                // The authored row's own verdicts, beside the detector's and never added to them.
                // Reported rather than dropped, because the row is a real thing the replay inserts
                // and a reader who cannot see it would wonder where a third setup went.
                figures.Add(new Measurement(
                    $"check.{direction}.{name}.authoredRowPassed",
                    authoredRowPassed.ToString(CultureInfo.InvariantCulture)));
                figures.Add(new Measurement(
                    $"check.{direction}.{name}.authoredRowFailed",
                    authoredRowFailed.ToString(CultureInfo.InvariantCulture)));

                // Sidedness asks whether anything has ever exercised both branches, so it reads
                // every population. The counts above stay separate, so a reader can still see that a
                // gate the market never passed was passed by a case built to pass it, and that a
                // gate the detectors never split was split by the row this harness inserted.
                bool everPassed = passed > 0 || authoredPass || authoredRowPassed > 0;
                bool everFailed = failed > 0 || authoredFail || authoredRowFailed > 0;

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

    /// <summary>
    /// What <see cref="ForwardOutcome.Of"/> computes for subjects the captured night cannot supply.
    ///
    /// The fixture's own as-of is the last session it holds, so the nightly fill has no elapsed
    /// horizon and writes nothing. These sit earlier in the same window, which is what gives the
    /// sign convention, the horizons and the holiday handling something to be measured on.
    /// </summary>
    private IReadOnlyList<Measurement> ForwardOutcomeFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        foreach (ForwardCases.ForwardCase forwardCase in ForwardCases.All)
        {
            IReadOnlyList<ForwardOutcome.Bar> path = ForwardCases.Path(connection, forwardCase);
            decimal atr = ForwardCases.AverageTrueRange(forwardCase);

            foreach (int horizon in ForwardOutcome.Horizons)
            {
                ForwardOutcome.Outcome? outcome = ForwardOutcome.Of(path, horizon, forwardCase.IsLong, atr);
                string id = $"{forwardCase.Id}.h{horizon.ToString(CultureInfo.InvariantCulture)}";

                if (outcome is null)
                {
                    figures.Add(new Measurement(id, "not yet elapsed"));
                    continue;
                }

                // The calendar horizon beside the session actually used. The pair is the done
                // condition: a follow-up that crossed a holiday says so rather than being silently
                // later than it claims.
                DateOnly intended = forwardCase.Date.AddDays(horizon);

                figures.Add(new Measurement($"{id}.intendedDate", Session(intended)));
                figures.Add(new Measurement($"{id}.actualDate", Session(outcome.ActualDate)));
                figures.Add(new Measurement(
                    $"{id}.slipped", intended == outcome.ActualDate ? "no" : "yes"));
                figures.Add(new Measurement($"{id}.returnSigned", Figure(outcome.ReturnSigned)));
                figures.Add(new Measurement(
                    $"{id}.mfeAtr",
                    outcome.MaximumFavourableExcursion is decimal mfe ? Figure(mfe) : "undefined"));
                figures.Add(new Measurement(
                    $"{id}.maeAtr",
                    outcome.MaximumAdverseExcursion is decimal mae ? Figure(mae) : "undefined"));
            }
        }

        return figures;
    }

    /// <summary>
    /// Which names each setup's controls actually were, in rank order, and how close the nearest was.
    ///
    /// A sequence rather than a count, on the same grounds the cap records its ordering: what a
    /// changed distance metric moves is which names sit in the five, not how many there are. Five
    /// controls drawn either way is the same number whether the match is good or arbitrary.
    /// </summary>
    private IReadOnlyList<Measurement> ControlFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        var figures = new List<Measurement>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT setup_id, control_set, control_ticker, rank, match_quality
              FROM control_setup
             ORDER BY setup_id, control_set, rank
            """;

        var drawn = new Dictionary<(string Setup, string Set), List<string>>();
        var nearest = new Dictionary<(string Setup, string Set), string>();

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));

                if (!drawn.TryGetValue(key, out List<string>? names))
                {
                    names = [];
                    drawn[key] = names;
                }

                names.Add(reader.GetString(2));

                // The best match's own distances, which is what says whether the pool had anything
                // close. A set of five drawn from a pool with nothing near it is still five.
                if (reader.GetInt32(3) == 1)
                {
                    nearest[key] = reader.GetString(4);
                }
            }
        }

        foreach ((string setup, string set) in drawn.Keys.OrderBy(k => k.Setup, StringComparer.Ordinal)
                     .ThenBy(k => k.Set, StringComparer.Ordinal))
        {
            figures.Add(new Measurement(
                $"controls.{setup}.{set}", string.Join(" ", drawn[(setup, set)])));
            figures.Add(new Measurement(
                $"controls.{setup}.{set}.nearest", nearest[(setup, set)]));
        }

        return figures;
    }

    /// <summary>
    /// The win-rate bound over authored outcome populations, which the fixture cannot supply.
    ///
    /// The captured night has no closed horizon, so the weekly bound over it is computed from
    /// nothing and correctly writes no row. The arithmetic still has to be exercised, and what it
    /// needs is populations rather than bars: a set of terminal returns and adverse excursions with
    /// a stop beside each. Those are authored the way the cap scenarios are, because the quantity
    /// under test is a rule over numbers rather than anything about the market.
    /// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
    /// </summary>
    private static IReadOnlyList<Measurement> CeilingFigures()
    {
        var figures = new List<Measurement>();

        foreach (CeilingCases.Scenario scenario in CeilingCases.All)
        {
            WinRateCeiling.Bound? bound = WinRateCeiling.Of(CeilingCases.Subjects(scenario));

            if (bound is null)
            {
                figures.Add(new Measurement($"ceiling.{scenario.Name}", "no bound"));
                continue;
            }

            figures.Add(new Measurement(
                $"ceiling.{scenario.Name}.subjects", bound.Subjects.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"ceiling.{scenario.Name}.bound", Figure(bound.Ceiling)));
            figures.Add(new Measurement($"ceiling.{scenario.Name}.achieved", Figure(bound.Achieved)));
            figures.Add(new Measurement($"ceiling.{scenario.Name}.gap", Figure(bound.Ceiling - bound.Achieved)));
        }

        return figures;
    }

    /// <summary>
    /// The interval over authored nightly series, which the fixture withholds on every panel.
    ///
    /// One night with no closed horizon produces no series, so band 1 is withheld everywhere and the
    /// block bootstrap runs on nothing. The failure this guards against is an interval that is too
    /// narrow, and a stage that never computes one cannot be.
    /// </summary>
    private static IReadOnlyList<Measurement> IntervalFigures()
    {
        var figures = new List<Measurement>();

        foreach (IntervalCases.Scenario scenario in IntervalCases.All)
        {
            IReadOnlyList<PairedInterval.Night> nights = IntervalCases.Nights(scenario);

            PairedInterval.Estimate? estimate = PairedInterval.Of(
                nights, MeasurementParameters.BootstrapBlockSessions, MeasurementParameters.BootstrapDraws);

            string id = $"interval.{scenario.Name}";

            if (estimate is null)
            {
                figures.Add(new Measurement(id, "withheld"));
                continue;
            }

            figures.Add(new Measurement($"{id}.mean", IntervalCases.Figure(estimate.Mean)));
            figures.Add(new Measurement($"{id}.low", IntervalCases.Figure(estimate.Low)));
            figures.Add(new Measurement($"{id}.high", IntervalCases.Figure(estimate.High)));
            figures.Add(new Measurement(
                $"{id}.clearsZero", estimate.Low > 0m ? "yes" : "no"));
            figures.Add(new Measurement(
                $"{id}.nights", estimate.Nights.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement(
                $"{id}.rows", estimate.Rows.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement(
                $"{id}.effective", estimate.EffectiveObservations.ToString(CultureInfo.InvariantCulture)));
        }

        return figures;
    }

    /// <summary>
    /// The dispersion of ten-session forward returns over the fixture's own bars, and the minimum
    /// sample that falls out of it.
    ///
    /// <b>This is the one number in the minimum-sample arithmetic that is a fact rather than a
    /// judgement</b>, and until now nothing had measured it. The corpus stated a minimum of paired
    /// setup observations detecting a two-point difference and read as a derived quantity from the
    /// day it was written; the dispersion the calculation turns on had never been taken over
    /// anything.
    ///
    /// <b>What it cannot say.</b> Thirty names over one year, hand-picked for liquidity and still
    /// listed at the end of it. A universe with delistings in it disperses further, so this figure is
    /// a floor on the real one and the minimum it produces is a floor on the real minimum. It is
    /// reported with its population attached for exactly that reason.
    /// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
    /// </summary>
    private IReadOnlyList<Measurement> DispersionFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();

        // Point in time on the replay's own as-of, the way every other read here is bounded. The
        // forward look inside the horizon is the exemption a forward return carries by definition,
        // and it is bounded by the bars this read returned rather than reaching past them.
        string bound = $"{AsOf:yyyy-MM-dd}T23:59:59.999Z";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.ticker, b.bar_date, b.adj_close
              FROM daily_bar b
             WHERE b.bar_date <= @as_of
               AND b.observed_at <= @bound
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= @bound)
             ORDER BY b.ticker, b.bar_date
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));
        command.Parameters.AddWithValue("@bound", bound);

        var series = new SortedDictionary<string, List<(DateOnly Date, decimal AdjustedClose)>>(
            StringComparer.Ordinal);

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string ticker = reader.GetString(0);

                if (!series.TryGetValue(ticker, out List<(DateOnly, decimal)>? bars))
                {
                    bars = [];
                    series[ticker] = bars;
                }

                bars.Add((
                    StoreText.StorageTextToDate(reader.GetString(1)),
                    StoreText.StorageTextToPrice(reader.GetString(2))));
            }
        }

        var bySession = new SortedDictionary<DateOnly, List<double>>();
        int names = 0;

        foreach (KeyValuePair<string, List<(DateOnly Date, decimal AdjustedClose)>> entry in series)
        {
            // A name with no more bars than the horizon has no forward return at all. The universe
            // rows the fixture carries one bar each for land here and are dropped, which is what
            // leaves the thirty names with history.
            if (entry.Value.Count <= MeasurementParameters.ScoringHorizonSessions)
            {
                continue;
            }

            names++;

            foreach ((DateOnly date, double value) in ForwardDispersion.Returns(
                entry.Value, MeasurementParameters.ScoringHorizonSessions))
            {
                if (!bySession.TryGetValue(date, out List<double>? returns))
                {
                    returns = [];
                    bySession[date] = returns;
                }

                returns.Add(value);
            }
        }

        ForwardDispersion.Measured? measured = ForwardDispersion.Of(
            [.. bySession.Select(s => new ForwardDispersion.Session(s.Key, s.Value))],
            MeasurementParameters.DispersionMinimumNames,
            MeasurementParameters.ControlsPerSet,
            names);

        if (measured is null)
        {
            return [new Measurement("dispersion", "no session carries a cross-section")];
        }

        return
        [
            new Measurement("dispersion.names", measured.Names.ToString(CultureInfo.InvariantCulture)),
            new Measurement("dispersion.sessions", measured.Sessions.ToString(CultureInfo.InvariantCulture)),
            new Measurement(
                "dispersion.observations", measured.Observations.ToString(CultureInfo.InvariantCulture)),
            new Measurement("dispersion.idiosyncratic", MinimumSample.Figure(measured.Idiosyncratic)),
            new Measurement("dispersion.pairedDifference", MinimumSample.Figure(measured.PairedDifference)),
            new Measurement(
                "minimumSample.effectiveObservations",
                MinimumSample.Of(measured.PairedDifference).ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// How many band 1 panels are short of sessions, and how many are short of evidence.
    ///
    /// <b>Two counts rather than one, because the two shortages are settled by different things.</b>
    /// The interval needs twenty sessions and no number of rows substitutes for them; the decision
    /// needs 262 effective observations and no number of sessions substitutes for those. A panel can
    /// be short of one and not the other, and a single "withheld" count could never say which.
    ///
    /// The population is deliberately not a third count. Band 1 reads the evidence store and a
    /// historical run writes to the calibration table, so it is settled by construction rather than
    /// by waiting, and it is asserted where that belongs rather than counted here.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    private IReadOnlyList<Measurement> WithheldReasonFigures()
    {
        using SqliteConnection connection = _connections.OpenReadOnly();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN withheld_because IS NOT NULL THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN n_effective < n_minimum THEN 1 ELSE 0 END), 0)
              FROM scoreboard
             WHERE panel LIKE 'band1.%'
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();

        return
        [
            new Measurement(
                "scoreboard.band1.shortOfSessions",
                reader.GetInt32(0).ToString(CultureInfo.InvariantCulture)),
            new Measurement(
                "scoreboard.band1.shortOfEvidence",
                reader.GetInt32(1).ToString(CultureInfo.InvariantCulture)),
        ];
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
            IReadOnlyList<StoredScanHit> hits = ScanHitReader.Read(connection, AsOf, scan, SessionBoundaries.UsEquities);
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

    /// <summary>
    /// The authored setup's id, which is how the sidedness counters tell it from a detector's row.
    ///
    /// No date prefix, unlike <c>LongSetupDetector.SetupId</c>, which is what makes it recognisable
    /// in a diff as well as here.
    /// </summary>
    public static string AuthoredSetupId => $"{AuthoredSetupTicker}-long";

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
                LongSetupDetector.Evidence(connection, AuthoredSetupTicker, AsOf, SessionBoundaries.UsEquities);

            IReadOnlyList<CheckResult> results = evidence is null
                ? []
                : LongPullbackRules.Evaluate(evidence);

            using SqliteCommand setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT INTO setup (setup_id, as_of, ticker, direction, check_results, passed_all,
                                   trigger_price, stop_price, stop_distance_ranges,
                                   thrust_scan, thrust_session)
                VALUES (@setup_id, @as_of, @ticker, 'long', @check_results, @passed_all, @trigger, @stop, '0.2700',
                        @thrust_scan, @thrust_session)
                """;

            // From the same evidence the check results come from, for the same reason. Left unset,
            // this row would read `none` where the detector's own rule resolves a hit, and the one
            // thing these columns exist for is splitting a population by scan family: a row that
            // says "no scan" when a scan is there is the split silently losing a row.
            setup.Parameters.AddWithValue("@thrust_scan", (object?)evidence?.ThrustScan ?? DBNull.Value);
            setup.Parameters.AddWithValue(
                "@thrust_session",
                evidence?.ThrustSession is DateOnly session
                    ? StoreText.DateToStorageText(session)
                    : (object)DBNull.Value);
            setup.Parameters.AddWithValue("@check_results", JsonSerializer.Serialize(results, CheckJson));
            setup.Parameters.AddWithValue("@passed_all", SetupChecks.PassedAll(results) ? 1 : 0);
            setup.Parameters.AddWithValue("@setup_id", AuthoredSetupId);
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

        IReadOnlyList<StoredSetupSignal> frozen = SetupSignalReader.Read(connection, AsOf, SessionBoundaries.UsEquities);

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
                DailyBarReader.Read(connection, ticker, AsOf, floors.LiquidityWindowSessions, SessionBoundaries.UsEquities);

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
                IndexBarReader.Read(connection, tracker, AsOf, floors.LiquidityWindowSessions, SessionBoundaries.UsEquities);

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

        ChartResponse chart = LabChart.Read(_connections, Ticker, AsOf, ChartSessions, _clock.UtcNow, SessionBoundaries.UsEquities);

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
        SetupsResponse night = new LabSetups(_connections).Read(AsOf, _clock.UtcNow, SessionBoundaries.UsEquities);

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
            // The plan's share count reaching the read surface, which is what the watchlist column
            // was missing for a checkpoint. Two figures rather than one: how many rows carry a
            // plan and how many shares they name between them. A count alone would read the same
            // over a night that planned nothing and a night whose plans all named nought shares,
            // and the second is the state the column exists to distinguish from an absence.
            new Measurement("gallery.planned", all.Count(s => s.PlannedShares is not null).ToString(CultureInfo.InvariantCulture)),
            new Measurement("gallery.plannedShares", all.Sum(s => s.PlannedShares ?? 0).ToString(CultureInfo.InvariantCulture)),
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
    /// What the store looks like once the pipeline has filled it and every migration has run.
    ///
    /// <b>The violation count is worthless without the population beside it.</b> Nought orphans over
    /// a store where nothing references anything is the answer an empty neighbourhood gives, and that
    /// is precisely the state migration 031 was verified in: <c>tools/ci.*</c> drops the store and
    /// migrates an empty one, <c>MigrationRowSurvivalTests</c> seeded <c>setup</c> and nothing
    /// pointing at it, and the rebuild failed the first time it met a store with rows. So the rows
    /// that point at the rebuilt table are counted and stated in the same breath as the nought.
    ///
    /// The check is SQLite's own <c>foreign_key_check</c> rather than anything in this solution,
    /// which is what makes the figure independent of the code that produced the store.
    /// </summary>
    /// <summary>
    /// What the run logger wrote, and what the session zone resolved to.
    ///
    /// <b>Two checkpoints whose deliverables the replay uses everywhere and measured nowhere.</b>
    /// 1.1 and 1.2 landed before the fixture existed and contributed no expectation to it, so both
    /// sat under a permit asking whether they could have contributed at all. They can, and these
    /// are the figures.
    ///
    /// <b>`runlog` is 1.1's, being RunLogger as the sole writer of `run_log`.</b> The count is over
    /// stage invocations rather than over stages, which is the half worth having: the harness
    /// tabulates twenty-one invocations in <see cref="PhaseReplayResult.Stages"/> and the logger
    /// wrote twenty-four, the difference being the two calibration detector runs and the withheld
    /// scoreboard rerun, none of which the harness's own list records. So a run entry going missing
    /// on a path the stage list does not cover is exactly what this figure sees and that list
    /// cannot.
    ///
    /// <b>`clock` is 1.2's, being the clock abstraction resolving an IANA identifier.</b> The
    /// session zone's end-of-day for the fixture's own as-of, in UTC. It is derived outside the
    /// solution from the same IANA identifier by `tools/derive-indicators.py --session`, which is a
    /// second reader of the same tzdata rather than a second copy of this arithmetic, and it is the
    /// figure that moves if `InvariantGlobalization` is ever flipped on: that setting is named in
    /// CLAUDE.md as the one that silently breaks IANA lookup, and until now nothing in the fixture
    /// would have changed if it had been.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    /// <summary>How many of the store's own tables declare a column of one name.</summary>
    private static int TablesCarrying(SqliteConnection connection, string column)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
              FROM sqlite_master m
             WHERE m.type = 'table'
               AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) c WHERE c.name = @column)
            """;

        command.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private IReadOnlyList<Measurement> StoreIntegrityFigures()
    {
        using SqliteConnection read = _connections.OpenReadOnly();

        return
        [
            new Measurement("runlog.entries",
                Rows(read, "run_log").ToString(CultureInfo.InvariantCulture)),
            new Measurement("runlog.distinctStages",
                DistinctStagesLogged(read).ToString(CultureInfo.InvariantCulture)),
            new Measurement("clock.sessionEndUtc",
                SessionBoundaries.EndOfSession(AsOf, _options.Value.SessionZone)
                    .ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new Measurement("store.schemaVersion",
                MigrationRunner.ReadUserVersion(read).ToString(CultureInfo.InvariantCulture)),
            new Measurement("store.rowsPointingAtSetup",
                (Rows(read, "setup_signal") + Rows(read, "control_setup"))
                    .ToString(CultureInfo.InvariantCulture)),
            new Measurement("store.foreignKeyViolations",
                MigrationRunner.ForeignKeyViolations(read).Length.ToString(CultureInfo.InvariantCulture)),
            new Measurement("store.observationsAfterTheAsOf",
                ObservationsLaterThan(read, _clock.UtcNow).ToString(CultureInfo.InvariantCulture)),

            // How many tables key on the plan rather than on the setup, which is the whole of what
            // the fan-out at 5.1 changed. Read from the built store rather than from the migration
            // text, so a later rebuild that dropped the column from one of them would move the
            // figure whatever the file that created it said.
            new Measurement("store.tablesKeyedOnThePlan",
                TablesCarrying(read, "plan_id").ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// The catalogue read against the build order, which is what 4.14 built and is the one figure
    /// that checkpoint produces.
    ///
    /// <b>It is a figure about the document rather than about the fixture's data</b>, on the
    /// precedent <c>store.schemaVersion</c> already sets: that one counts the migration files and
    /// this one counts the rows of two tables. What makes both worth freezing is the same thing,
    /// that they move for one reason and the reason is legible in the diff.
    ///
    /// <b>The one that carries the property is `unplaced`.</b> The other three are the population it
    /// was computed over, stated beside it rather than left to be inferred, because a count of nought
    /// unplaced means nothing without the count it was nought out of: a parser that read no rows
    /// would report nought unplaced too, and that is the shape this whole corpus keeps finding.
    /// </summary>
    /// <summary>
    /// What the night's stored checks record about their clauses, which is what 4.1 added.
    ///
    /// <b>Read out of the store rather than off the evaluated results.</b> A record gaining a
    /// property proves nothing about the evidence: the detector serialises what it evaluated and the
    /// read surface deserialises it back, and either end could drop the field with no test of the
    /// type noticing. These figures come from `check_results` as it was written.
    ///
    /// The one that carries the property is the count of clause verdicts. The two beside it are the
    /// population: a gate count with no clause count cannot tell "every gate recorded its clauses"
    /// from "every gate recorded an empty list".
    /// </summary>
    private IReadOnlyList<Measurement> ClauseFigures()
    {
        using SqliteConnection read = _connections.OpenReadOnly();
        using SqliteCommand command = read.CreateCommand();
        command.CommandText = "SELECT check_results FROM setup WHERE as_of = @as_of";
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(AsOf));

        int gates = 0;
        int withClauses = 0;
        int clauseVerdicts = 0;

        using (SqliteDataReader rows = command.ExecuteReader())
        {
            while (rows.Read())
            {
                foreach (CheckResult result in
                    JsonSerializer.Deserialize<CheckResult[]>(rows.GetString(0), ClauseJson) ?? [])
                {
                    gates++;

                    if (result.Clauses is not { Count: > 0 } clauses)
                    {
                        continue;
                    }

                    withClauses++;
                    clauseVerdicts += clauses.Count;
                }
            }
        }

        return
        [
            new Measurement("clauses.gatesRecorded", gates.ToString(CultureInfo.InvariantCulture)),
            new Measurement("clauses.gatesCarryingClauses", withClauses.ToString(CultureInfo.InvariantCulture)),
            new Measurement("clauses.verdictsRecorded", clauseVerdicts.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    private static readonly JsonSerializerOptions ClauseJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The clause set every stored short row's `reached-ceiling` verdict records, read back through
    /// the same function a later session would use.
    ///
    /// Parsed out of the stored JSON rather than counted as the detector writes it, because the
    /// property is about what a reader of the store can establish and not about what the writer
    /// intended. A row whose note failed to serialise would look identical on the writing side.
    /// </summary>
    private static IEnumerable<CeilingClauses> CeilingVerdicts(SqliteConnection connection, string table)
    {
        SqliteIdentifier.Validate(table);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT check_results FROM {table} WHERE direction = @direction";
        command.Parameters.AddWithValue("@direction", SetupDirection.Short);

        var sets = new List<CeilingClauses>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            IReadOnlyList<CheckResult> results = JsonSerializer.Deserialize<List<CheckResult>>(
                reader.GetString(0), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            CeilingClauses set = ShortPullbackRules.ClauseSetOf(results);

            if (set != CeilingClauses.NotFound)
            {
                sets.Add(set);
            }
        }

        return sets;
    }

    /// <summary>
    /// The authored-parameters table's own shape, on the precedent the catalogue figures set at
    /// 4.14: a figure about the document rather than about the fixture's data.
    ///
    /// <b>The property is `authored.open` and the three beside it are the population.</b> Nought
    /// open rows means nothing on its own, because a parser that read no rows reports nought too,
    /// which is why the total is recorded with it and why the check that reads this table refuses a
    /// row count below twenty-five. The filled and the citing counts separate two different things
    /// a filled row can be: one that states a value, and one that names the decision the value came
    /// from. A row filled without a citation is the shape 4.15 exists to avoid, so it is counted.
    /// </summary>
    private static IReadOnlyList<Measurement> AuthoredParameterFigures()
    {
        string architecture = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        IReadOnlyList<IReadOnlyList<string>> rows =
            HtmlTable.BodyRowsUnder(architecture, "Authored parameters");

        int open = rows.Count(r => r.Count > 0 && r[0].Contains("OPEN", StringComparison.Ordinal));
        int citing = rows.Count(r => r.Count > 3 && r[3].Contains("(see: ", StringComparison.Ordinal));

        return
        [
            new Measurement("authored.rows",
                rows.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement("authored.open",
                open.ToString(CultureInfo.InvariantCulture)),
            new Measurement("authored.filled",
                (rows.Count - open).ToString(CultureInfo.InvariantCulture)),
            new Measurement("authored.citingADecision",
                citing.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    private static IReadOnlyList<Measurement> CataloguePlacementFigures()
    {
        string architecture = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));

        IReadOnlyList<IReadOnlyList<string>> catalogue =
            HtmlTable.BodyRowsUnder(architecture, "Component catalogue");
        IReadOnlyList<IReadOnlyList<string>> buildOrder =
            HtmlTable.BodyRowsUnder(architecture, "Build order");

        string[] types =
            [.. catalogue.Select(r => r[0]).Where(n => !n.Contains(' ', StringComparison.Ordinal))];

        HashSet<string> named =
            [.. buildOrder.SelectMany(row => ArchitectureConformanceCheck.Schedule.NamesIn(row[1]))];

        int unplaced = types.Count(n => !named.Contains(n));

        return
        [
            new Measurement("catalogue.components",
                catalogue.Count.ToString(CultureInfo.InvariantCulture)),
            new Measurement("catalogue.componentTypes",
                types.Length.ToString(CultureInfo.InvariantCulture)),
            new Measurement("catalogue.screens",
                (catalogue.Count - types.Length).ToString(CultureInfo.InvariantCulture)),
            new Measurement("catalogue.unplacedInAnyBuildsRow",
                unplaced.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    /// <summary>
    /// The shape of the selection rule 5.0 wrote down: how many named thresholds each side's gate
    /// list compares, which is the number a version may move exactly one of.
    ///
    /// <b>A figure about the rule rather than about the fixture's data</b>, on the precedent
    /// <c>catalogue.unplacedInAnyBuildsRow</c> and <c>store.schemaVersion</c> set, and derived from
    /// the document rather than from the code: ARCHITECTURE's two check lists state each quantity a
    /// gate compares against a threshold, and the count per side is read off those lists by hand
    /// before the run. The identity test at 5.0(b) proves the detector reads these thresholds and no
    /// others, so the figure moving is a gate gaining or losing a comparison, which is the
    /// structural change this generation names out of scope.
    /// see: A selection rule is the gate list plus a named threshold per gate, and one implementation reads it for the detector and the harness alike
    /// </summary>
    /// <summary>
    /// The harness run over the fixture's own stored setups, which is 5.3's whole deliverable.
    ///
    /// <b>The acceptance run is the same walk a screen makes.</b> Nothing here is a rehearsal: the
    /// figures below come out of <c>ReplayHarness.Reproduce</c>, which is <c>Walk</c> with the
    /// candidate equal to the baseline, and every screen afterwards goes down the same path with
    /// the same per-row guard live.
    ///
    /// <b>The verdicts are the figures that carry the property, and they are checkable against a
    /// hand derivation.</b> Each <c>replay.verdict.*</c> is what the harness rebuilt for one
    /// judgeable gate of one stored row out of the frozen signals alone. The fixture already holds
    /// a <c>setup.*</c> expectation for that same gate on that same row, derived by hand at 2.6 and
    /// 2.7, so the two are the same verdict reached by two routes that share no line of code.
    ///
    /// <b>The set comparison here is empty against empty and is not what the acceptance claim rests
    /// on.</b> The captured day flagged one row a side and passed neither. A population where the
    /// baseline selects some rows and rejects others is authored in <c>ReplayHarnessTests</c>, and
    /// that is where exact reproduction of a non-empty selection is asserted
    /// (see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it).
    ///
    /// <b>The two sides are counted apart and the populations are named apart by the row id</b>
    /// (see: Long and short are never pooled into one figure).
    /// </summary>
    private IReadOnlyList<Measurement> ReplayFigures()
    {
        var figures = new List<Measurement>();
        var harness = new ReplayHarness(_connections, Logger(), _clock, _options);

        foreach (string direction in new[] { SetupDirection.Long, SetupDirection.Short })
        {
            ReplayScreening run = harness.Reproduce(direction, AsOf);

            figures.Add(new Measurement($"replay.{direction}.gatesRebuilt",
                run.GatesJudged.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.gatesReadBack",
                run.GatesReadBack.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.sessions",
                run.SessionsRead.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.rows",
                run.RowsExamined.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.baselineSelected",
                run.BaselineSelected.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.replaySelected",
                run.CandidateSelected.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.unjudgeable",
                run.Unjudgeable.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.unmeasuredGateVerdicts",
                run.UnmeasuredGateVerdicts.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.frozenYetUnmeasured",
                run.FrozenYetUnmeasured.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement($"replay.{direction}.disagreements",
                run.Disagreements.Count.ToString(CultureInfo.InvariantCulture)));

            // The done condition's own claim, which is about the selected set. Stated apart from the
            // disagreement count above rather than folded into it, because the two answer different
            // questions and this one is empty against empty over the captured day.
            figures.Add(new Measurement($"replay.{direction}.selectionsReproduced",
                run.SelectionsReproduced ? "yes" : "no"));
        }

        // One verdict per judgeable gate per stored row, which is what the acceptance claim is made
        // of. The row id carries its own population: the two dated ones are the captured day's and
        // `IESC-long` is the authored row.
        using SqliteConnection read = _connections.OpenReadOnly();

        foreach (StoredSetup setup in SetupReader.Read(read, AsOf)
                     .OrderBy(s => s.SetupId, StringComparer.Ordinal))
        {
            SelectionRule baseline = SelectionRule.For(setup.Direction);
            IReadOnlyDictionary<string, decimal> signals = FrozenSignalRow(read, setup);
            IReadOnlyList<CheckResult> recorded = RecordedChecks(setup);

            foreach (string gate in SelectionReplay.JudgeableGates(baseline))
            {
                CheckResult? night = recorded.FirstOrDefault(
                    r => string.Equals(r.Name, gate, StringComparison.Ordinal));

                // A gate the night recorded with no value made no comparison, so the harness reads
                // its verdict back rather than rebuilding it, and the figure says so. Reporting a
                // rebuilt pass or fail here would state a comparison the night never made.
                if (night is null || night.Value is null)
                {
                    figures.Add(new Measurement(
                        $"replay.verdict.{setup.SetupId}.{gate}", "not measured by the night"));
                    continue;
                }

                CheckResult? rebuilt = SelectionReplay.Judge(baseline, gate, signals);

                figures.Add(new Measurement(
                    $"replay.verdict.{setup.SetupId}.{gate}",
                    rebuilt is null ? "not judged" : rebuilt.Passed ? "pass" : "fail"));
            }
        }

        return figures;
    }

    /// <summary>The verdicts one stored row recorded, read back the way the harness reads them.</summary>
    private static IReadOnlyList<CheckResult> RecordedChecks(StoredSetup setup) =>
        JsonSerializer.Deserialize<List<CheckResult>>(
            setup.CheckResults, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    /// <summary>The direct signals one stored setup froze, read the way the harness reads them.</summary>
    private IReadOnlyDictionary<string, decimal> FrozenSignalRow(SqliteConnection read, StoredSetup setup)
    {
        var row = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (StoredSetupSignal signal in
                 SetupSignalReader.Read(read, setup.AsOf, _options.Value.SessionZone))
        {
            if (signal.SetupId != setup.SetupId
                || !SelectionReplay.DirectSignals.Contains(signal.SignalName)
                || !decimal.TryParse(
                    signal.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
            {
                continue;
            }

            row[signal.SignalName] = value;
        }

        return row;
    }

    /// <summary>
    /// The holdout register over the fixture, which is 5.4's deliverable and holds nothing.
    ///
    /// <b>Nothing is the correct answer and the figures say why.</b> The fixture's one market day is
    /// 2026-08-24, so no calendar quarter of forward-collected evidence has completed and no window
    /// can exist. What is asserted here is that the register reports that reason rather than an
    /// unexplained nought, and that the schedule it would fill with is the right eight quarters: the
    /// first window is named and dated even though it does not exist yet, so a register that named
    /// the wrong quarter would be visible on the day it filled rather than three months later.
    ///
    /// <b>The schedule figures are about the calendar and the register figures are about the
    /// store</b>, and they are kept apart for the reason the corpus keeps finding: a count of nought
    /// windows is compatible with any schedule at all.
    /// </summary>
    private IReadOnlyList<Measurement> HoldoutFigures()
    {
        var registry = new HoldoutRegistry(_connections, Logger(), _clock, _options);
        HoldoutRegisterState state = registry.Mature(AsOf);

        var figures = new List<Measurement>
        {
            new("holdout.capacity", HoldoutWindows.Capacity.ToString(CultureInfo.InvariantCulture)),
            new("holdout.monthsPerWindow", HoldoutWindows.MonthsPerWindow.ToString(CultureInfo.InvariantCulture)),
            new("holdout.firstSession", state.FirstSession is DateOnly first ? Session(first) : "none"),
            new("holdout.matured", state.Matured.ToString(CultureInfo.InvariantCulture)),
            new("holdout.recorded", state.Recorded.ToString(CultureInfo.InvariantCulture)),
            new("holdout.written", state.Written.ToString(CultureInfo.InvariantCulture)),
            new("holdout.spent", state.Spent.ToString(CultureInfo.InvariantCulture)),
            new("holdout.available", state.Available.ToString(CultureInfo.InvariantCulture)),
            new("holdout.missing", state.Missing.Count.ToString(CultureInfo.InvariantCulture)),
            new("holdout.exhausted", state.IsExhausted ? "yes" : "no"),
            new("holdout.outcome", state.Outcome.ToStorageText()),
            new("holdout.emptyBecause", EmptyReason(state.EmptyBecause)),
        };

        // The schedule the register would fill with, which exists whether or not any window does.
        if (state.FirstSession is DateOnly session)
        {
            IReadOnlyList<HoldoutWindow> schedule = HoldoutWindows.Schedule(session);

            figures.Add(new Measurement("holdout.schedule.windows",
                schedule.Count.ToString(CultureInfo.InvariantCulture)));
            figures.Add(new Measurement("holdout.schedule.first", schedule[0].WindowId));
            figures.Add(new Measurement("holdout.schedule.firstMatures", Session(schedule[0].MaturesOn)));
            figures.Add(new Measurement("holdout.schedule.last", schedule[^1].WindowId));
            figures.Add(new Measurement("holdout.schedule.lastMatures", Session(schedule[^1].MaturesOn)));
        }

        return figures;
    }

    /// <summary>
    /// Which of the four reasons a register gives, as a name rather than as the sentence.
    ///
    /// The sentences are prose and would put a paragraph in the expectations file; what the fixture
    /// needs to hold is which state the register is in, and a reason the check does not know reads
    /// as unnamed rather than as one of the four.
    /// </summary>
    private static string EmptyReason(string? because) =>
        because is null ? "none, a window is available"
        : because == HoldoutRegistry.NoSessionRecorded ? "no session recorded"
        : because == HoldoutRegistry.NoQuarterMaturedYet ? "no quarter matured yet"
        : because == HoldoutRegistry.NotRecorded ? "matured and not recorded"
        : because == HoldoutRegistry.EveryMaturedWindowSpent ? "every matured window spent"
        : "a reason this measurement does not name";

    private static IReadOnlyList<Measurement> RuleFigures() =>
    [
        new Measurement("rule.longThresholds",
            SelectionRule.Long.Thresholds.Count.ToString(CultureInfo.InvariantCulture)),
        new Measurement("rule.shortThresholds",
            SelectionRule.Short.Thresholds.Count.ToString(CultureInfo.InvariantCulture)),

        // How many of each side's thresholds a version may actually move, which is a smaller number
        // than the one above for two reasons that are counted together here and named apart in the
        // corpus: a threshold belonging to the execution or recorded family, and a threshold whose
        // gate cannot be judged from the frozen signals. The two sides are separate figures and are
        // never added (see: Long and short are never pooled into one figure).
        new Measurement("rule.longMovableThresholds",
            SelectionReplay.Movable(SelectionRule.Long).Count.ToString(CultureInfo.InvariantCulture)),
        new Measurement("rule.shortMovableThresholds",
            SelectionReplay.Movable(SelectionRule.Short).Count.ToString(CultureInfo.InvariantCulture)),
    ];

    /// <summary>
    /// Rows the store holds that were observed later than this run, which must be none while the
    /// point-in-time probe has not been written yet.
    ///
    /// <b>This is the comment above the call turned into a number.</b> "Nothing above it may see
    /// one" was prose for twelve checkpoints, and prose is what a new call sitting underneath it
    /// does not have to obey: 3.12 added one and inherited the probe, and no figure moved, so
    /// nothing failed. The three figures beside this one still cannot see the row, and this one
    /// exists so that the next figure added here cannot either without saying so.
    /// see: A gate handed an absent or degenerate quantity fails rather than passing
    /// </summary>
    private static int ObservationsLaterThan(SqliteConnection connection, DateTimeOffset instant)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM daily_bar WHERE observed_at > @instant";
        command.Parameters.AddWithValue("@instant", StoreText.TimestampToStorageText(instant));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>How many distinct stages left a run entry, which is the shape of the count above.</summary>
    private static int DistinctStagesLogged(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT stage) FROM run_log;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long Rows(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

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

        IReadOnlyList<StoredDailyBar> onTheNight = DailyBarReader.Read(read, CorrectedTicker, AsOf, 1, SessionBoundaries.UsEquities);
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

    /// <summary>
    /// A run of nights whose horizon has closed, driven through the real fill and the real
    /// scoreboard build, in a store of its own.
    ///
    /// <b>The half of phase 3 the captured fixture cannot reach.</b> One market day closes no
    /// ten-session horizon, so over the captured store `forward.written` is nought, every band 1
    /// panel is withheld, and everything past the flag is exercised by nothing. That is how a fill
    /// binding its subject kind to a literal survived twelve checkpoints: the query that came back
    /// empty was never run against a store that had anything to put in it.
    ///
    /// <b>Its own store, its own namespace, and nothing added to a captured figure.</b> The captured
    /// counts stay what they were and stay true of a one-night fixture. A figure over the two
    /// populations together would be a figure over neither.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    /// <summary>
    /// Computed once for the whole test process, because it is a pure function of authored inputs
    /// and the shipped stages.
    ///
    /// <b>Not an optimisation for its own sake.</b> Eight test files build a replay, and the
    /// population writes about twelve and a half thousand outcome rows through the real fill.
    /// Recomputing it per replay took the suite from 1m27 to 2m56 for figures that cannot differ
    /// between runs, and a suite that takes twice as long is a suite that gets run half as often.
    /// The measurements are immutable records, so sharing them across collections is safe, and
    /// <see cref="Lazy{T}"/> is thread-safe by default.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<Measurement>> Accumulation = new(BuildAccumulationFigures);

    private static IReadOnlyList<Measurement> AccumulationFigures() => Accumulation.Value;

    private static IReadOnlyList<Measurement> BuildAccumulationFigures()
    {
        using var population = new AccumulationPopulation();

        FillResult filled = population.Fill();
        ScoreboardResult built = population.Build();

        var figures = new List<Measurement>
        {
            new("accumulation.nights", AccumulationPopulation.Nights.ToString(CultureInfo.InvariantCulture)),
            new("accumulation.setups", filled.Subjects.ToString(CultureInfo.InvariantCulture)),
            new("accumulation.controls", filled.ControlSubjects.ToString(CultureInfo.InvariantCulture)),
            new("accumulation.forward.setupsWritten", filled.Written.ToString(CultureInfo.InvariantCulture)),
            new("accumulation.forward.controlsWritten", filled.ControlsWritten.ToString(CultureInfo.InvariantCulture)),
            new("accumulation.forward.setupOutcomeRows",
                population.Outcomes("setup").ToString(CultureInfo.InvariantCulture)),
            new("accumulation.forward.controlOutcomeRows",
                population.Outcomes("control").ToString(CultureInfo.InvariantCulture)),
            // Counted over band 1 alone rather than taken from ScoreboardResult.WithInterval, which
            // is over every panel the build wrote. The two agree today, because no other band carries
            // an interval, and an id that says band 1 over a figure computed across the page is the
            // fifth defect shape whether or not the number happens to match.
            new("accumulation.band1.panelsWithAnInterval",
                Band1WithAnInterval(population).ToString(CultureInfo.InvariantCulture)),
        };

        foreach (string direction in new[] { "long", "short" })
        {
            foreach (string set in new[] { "loose", "tight" })
            {
                AccumulationPopulation.Panel? panel = population.Band1(direction, set);
                string id = $"accumulation.band1.{direction}.{set}";

                if (panel is null)
                {
                    figures.Add(new Measurement(id, "no panel"));
                    continue;
                }

                figures.Add(new Measurement($"{id}.figure", panel.Figure));
                figures.Add(new Measurement($"{id}.low", panel.Low ?? "none"));
                figures.Add(new Measurement($"{id}.high", panel.High ?? "none"));
                figures.Add(new Measurement(
                    $"{id}.rows", panel.Rows.ToString(CultureInfo.InvariantCulture)));
                figures.Add(new Measurement(
                    $"{id}.effective",
                    panel.Effective?.ToString(CultureInfo.InvariantCulture) ?? "none"));

                // The other half of 3.6's trigger, frozen beside the count it was dropped next to.
                // A panel carrying an interval and no session count is the state this fixture could
                // not have told apart from a correct one: the effective count moves with the rows
                // and the session count does not, so an expectation over the first alone passes on
                // a build that discards the second.
                figures.Add(new Measurement(
                    $"{id}.sessions",
                    panel.Sessions?.ToString(CultureInfo.InvariantCulture) ?? "none"));
                figures.Add(new Measurement(
                    $"{id}.minimumSessions",
                    panel.MinimumSessions?.ToString(CultureInfo.InvariantCulture) ?? "none"));
            }
        }

        static int Band1WithAnInterval(AccumulationPopulation population) =>
            (from direction in new[] { "long", "short" }
             from set in new[] { "loose", "tight" }
             let panel = population.Band1(direction, set)
             where panel?.Low is not null
             select panel).Count();

        // The state the defect produced, read back from the producer. Every setup outcome closed and
        // no control outcome exists, which is the exact shape band 1 was in for the whole of phase 3
        // while the panel said the horizons had not closed. Frozen as the words rather than as a
        // count, because the words are what a person had to diagnose it from.
        figures.Add(new Measurement(
            "accumulation.starved.long.loose.withheldBecause",
            population.WithheldReasonWithNoControlOutcomes("long", "loose") ?? "no panel"));

        return figures;
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
