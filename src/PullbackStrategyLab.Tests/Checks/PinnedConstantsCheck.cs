using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// Numeric constants stated in the documents match the code constant they describe.
///
/// Read from the code rather than scraped from it: the value comes from the type the
/// application actually uses, so a change to the default fails here rather than passing
/// against a copy of itself.
///
/// The coverage line is the important half. Most of the authored-parameters table describes
/// machinery later phases build, and a parameter with no code to pin it to is reported as
/// unexamined rather than passed over in silence.
/// </summary>
public sealed class PinnedConstantsCheck
{
    private readonly ITestOutputHelper _output;

    public PinnedConstantsCheck(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("check", "pinned-constants")]
    public void Every_constant_stated_in_a_document_matches_the_code_it_describes()
    {
        var coverage = new CheckCoverage("pinned-constants", _output);
        string architecture = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "ARCHITECTURE.html"));
        string schema = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "SCHEMA.md"));
        string claude = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Root, "CLAUDE.md"));
        string buildPlan = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "BUILD_PLAN.md"));
        string runbook = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "RUNBOOK.md"));

        var defaults = new PullbackStrategyLabOptions();
        var pins = new List<Pin>();

        // Every read of the authored-parameters table goes through this, which records the row it
        // read, so the set of pinned rows is derived from the pins that were made rather than kept
        // in a second list. The second list existed until 4.18 and was seven rows stale: the caps
        // pinned at 4.6, the borrow rate, the horizons and the selection sample were all pinned and
        // still counted as unpinned, so the out-of-scope figure read 30 where the rows with no
        // constant were 23. A list beside the thing it counts is a count somebody has to remember.
        var table = new AuthoredParameters(architecture);

        // The daily call ceiling, stated in four places and held in one.
        pins.Add(Pin.Number("ARCHITECTURE.html, authored parameters, Daily API ceiling",
            table.Number("Daily API ceiling"), defaults.DailyCallCeiling, "PullbackStrategyLabOptions.DailyCallCeiling"));
        pins.Add(Pin.Text("ARCHITECTURE.html, data budget, the hard ceiling",
            architecture.Contains("Hard ceiling of 5,000 calls a day", StringComparison.Ordinal),
            defaults.DailyCallCeiling == 5000, "the stated ceiling against the configured default"));

        // Snapshot retention, which deletes files the operator cannot get back. RUNBOOK is where an
        // operator reads how far a restore can reach, so a number that drifted from the code there
        // would be a promise about recovery that the lab does not keep.
        pins.Add(Pin.Text("RUNBOOK.md, the snapshots kept against the configured default",
            runbook.Contains("keeps the last 7 snapshots", StringComparison.Ordinal),
            defaults.SnapshotsKept == 7, "the stated retention against the configured default"));
        pins.Add(Pin.Text("CLAUDE.md, the daily vendor call ceiling",
            claude.Contains("daily vendor call ceiling is 5,000", StringComparison.Ordinal),
            defaults.DailyCallCeiling == 5000, "the stated ceiling against the configured default"));
        pins.Add(Pin.Text("RUNBOOK.md, the nightly total against the ceiling",
            runbook.Contains("against a 5,000 ceiling", StringComparison.Ordinal),
            defaults.DailyCallCeiling == 5000, "the stated ceiling against the configured default"));

        // The workable band, which three documents state and one class holds. A band that drifted
        // in prose while the code kept the old numbers would be a threshold decision made against a
        // rule nobody is applying, and it is stated as words rather than as a figure in a table, so
        // nothing else here would catch it.
        pins.Add(Pin.Text("ARCHITECTURE.html, the workable nightly count band",
            architecture.Contains("outside roughly 5 to 60 on either side", StringComparison.Ordinal),
            NightlyCounts.BandLow == 5 && NightlyCounts.BandHigh == 60,
            "NightlyCounts.BandLow and NightlyCounts.BandHigh"));
        pins.Add(Pin.Text("BUILD_PLAN.md, the same band at the checkpoint that applies it",
            buildPlan.Contains("falls outside 5 to 60 per side", StringComparison.Ordinal),
            NightlyCounts.BandLow == 5 && NightlyCounts.BandHigh == 60,
            "NightlyCounts.BandLow and NightlyCounts.BandHigh"));

        // The four store pragmas, stated in SCHEMA and set at open in one place.
        string factory = RepositoryLayout.Read(
            Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "StoreConnectionFactory.cs"));
        pins.Add(Pin.Number("SCHEMA.md, store configuration, busy_timeout",
            PragmaNumber(schema, "busy_timeout"), StoreConnectionFactory.BusyTimeoutMilliseconds, "StoreConnectionFactory.BusyTimeoutMilliseconds"));
        pins.Add(Pin.Text("SCHEMA.md, store configuration, journal_mode",
            PragmaText(schema, "journal_mode") == "WAL",
            factory.Contains("PRAGMA journal_mode = WAL;", StringComparison.Ordinal), "the pragma set at open"));
        pins.Add(Pin.Text("SCHEMA.md, store configuration, synchronous",
            PragmaText(schema, "synchronous") == "NORMAL",
            factory.Contains("PRAGMA synchronous = NORMAL;", StringComparison.Ordinal), "the pragma set at open"));
        pins.Add(Pin.Text("SCHEMA.md, store configuration, foreign_keys",
            PragmaText(schema, "foreign_keys") == "ON",
            factory.Contains("PRAGMA foreign_keys = ON;", StringComparison.Ordinal), "the pragma set at open"));

        // The universe floors, stated in the authored parameters table and held in configuration.
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Price floor",
            table.Money("Price floor"), defaults.Universe.PriceFloor, "UniverseOptions.PriceFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Liquidity floor, long",
            table.Money("Liquidity floor, long"), defaults.Universe.LiquidityFloorLong, "UniverseOptions.LiquidityFloorLong"));

        // The three bulk request costs, stated in the data budget and held in the vendor client.
        // The budget is only meaningful if the cost a stage charges is the cost the table was
        // added up from, and a request whose price drifted would spend the ceiling faster than
        // any document said while every stage still reported staying inside it.
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, whole-market daily bars, cost",
            BudgetCost(architecture, "Whole-market daily bars"), EodhdClient.BulkEndOfDayCost, "EodhdClient.BulkEndOfDayCost"));
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, splits, cost",
            BudgetCost(architecture, "Splits, bulk"), EodhdClient.BulkSplitCost, "EodhdClient.BulkSplitCost"));
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, dividends, cost",
            BudgetCost(architecture, "Dividends, bulk"), EodhdClient.BulkDividendCost, "EodhdClient.BulkDividendCost"));
        // The minute bars, added at 4.2. It is the second largest consumer in the table and the one
        // whose cost is not one: the vendor prices intraday above the per-ticker daily endpoint, so
        // a budget that charged it one would under-count a full night's minute fetch by four fifths.
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, minute bars, cost",
            BudgetCost(architecture, "Minute bars for every flagged setup"),
            EodhdClient.IntradayCost,
            "EodhdClient.IntradayCost"));
        // The spreads, added at 4.3. The cost is one and the endpoint takes a batch, which is the
        // one place in this table where a request and a call are not the same unit in the same
        // direction: a budget charging a batch as one request would let a single call spend sixty
        // and report one, which is the accounting error the ceiling exists to catch arriving from
        // the direction no other endpoint can produce.
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, spread snapshots, cost",
            BudgetCost(architecture, "Spread snapshots"), EodhdClient.UsQuoteCost, "EodhdClient.UsQuoteCost"));
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, spread snapshots, calls a night",
            int.Parse(new string(BudgetCell(architecture, "Spread snapshots", 1).Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture),
            NightlyCap.Total * SpreadSnapshotter.Samples.Count * EodhdClient.UsQuoteCost,
            "NightlyCap.Total * SpreadSnapshotter.Samples.Count * EodhdClient.UsQuoteCost"));
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, index bars, cost",
            BudgetCost(architecture, "Index bars"), EodhdClient.DailyHistoryCost, "EodhdClient.DailyHistoryCost"));
        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, index bars, calls a night",
            int.Parse(new string(BudgetCell(architecture, "Index bars", 1).Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture),
            defaults.IndexSymbols.Count, "PullbackStrategyLabOptions.IndexSymbols.Count"));
        pins.Add(Pin.Text("SCHEMA.md, the three index symbols",
            schema.Contains("SPY, QQQ, IWM", StringComparison.Ordinal),
            defaults.IndexSymbols.SequenceEqual(["SPY", "QQQ", "IWM"], StringComparer.Ordinal),
            "PullbackStrategyLabOptions.IndexSymbols"));

        pins.Add(Pin.Number("ARCHITECTURE.html, data budget, history refetch, cost",
            BudgetCost(architecture, "History refetch"), EodhdClient.DailyHistoryCost, "EodhdClient.DailyHistoryCost"));

        // And the cadence beside the cost. A nightly cadence is only nightly if the nightly
        // invocation actually asks for dividends, so that is what the code side of this pin reads.
        pins.Add(Pin.Text("ARCHITECTURE.html, data budget, dividends, cadence",
            string.Equals(BudgetCadence(architecture, "Dividends, bulk"), "nightly", StringComparison.Ordinal),
            ActionIngestor.RequestsDividendsByDefault,
            "ActionIngestor.RequestsDividendsByDefault"));
        pins.Add(Pin.Text("ARCHITECTURE.html, data budget, splits, cadence",
            string.Equals(BudgetCadence(architecture, "Splits, bulk"), "nightly", StringComparison.Ordinal),
            true, "the splits request the stage always makes"));

        // The warm-up depth, stated in RUNBOOK's backfill notes and held by the engine that
        // refuses to write a row without it.
        pins.Add(Pin.Text("RUNBOOK.md, the backfill warm-up depth",
            runbook.Contains("The first 150 sessions are warm-up", StringComparison.Ordinal),
            IndicatorEngine.WarmupSessions == 150, "IndicatorEngine.WarmupSessions"));

        // The exponential average periods, named in the build plan's 1.6 deliverable.
        pins.Add(Pin.Text("BUILD_PLAN.md 1.6, the three exponential averages",
            buildPlan.Contains("EMA 9/21/50", StringComparison.Ordinal),
            IndicatorEngine.EmaShortPeriod == 9 && IndicatorEngine.EmaMediumPeriod == 21 && IndicatorEngine.EmaLongPeriod == 50,
            "IndicatorEngine.EmaShortPeriod, EmaMediumPeriod and EmaLongPeriod"));
        pins.Add(Pin.Text("BUILD_PLAN.md 1.6, the range and true range windows",
            buildPlan.Contains("ADR20, ATR14", StringComparison.Ordinal),
            IndicatorEngine.RangeWindow == 20 && IndicatorEngine.AtrPeriod == 14,
            "IndicatorEngine.RangeWindow and AtrPeriod"));

        // The session zone, named in the build plan and defaulted in configuration.
        pins.Add(Pin.Text("BUILD_PLAN.md 1.2, the session zone",
            buildPlan.Contains("America/New_York", StringComparison.Ordinal),
            string.Equals(defaults.SessionZone, "America/New_York", StringComparison.Ordinal),
            "PullbackStrategyLabOptions.SessionZone"));

        // The vendor, named in a decision and recorded in configuration so a store written
        // against one vendor is not silently read as another's.
        pins.Add(Pin.Text("DECISIONS.md, the vendor is EODHD",
            Corpus.DecisionNames.Any(n => n.StartsWith("The vendor is EODHD", StringComparison.Ordinal)),
            string.Equals(defaults.Vendor.Name, "EODHD", StringComparison.Ordinal),
            "VendorOptions.Name"));

        // Every shipped appsettings.json agrees with the defaults, so a host cannot start
        // under a ceiling the documents do not state.
        foreach (string project in new[] { "PullbackStrategyLab.Worker", "PullbackStrategyLab.Api", "PullbackStrategyLab.Web" })
        {
            string file = Path.Combine(RepositoryLayout.Source, project, "appsettings.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement section = document.RootElement.GetProperty(PullbackStrategyLabOptions.SectionName);
            pins.Add(Pin.Number($"{project}/appsettings.json, DailyCallCeiling",
                section.GetProperty("DailyCallCeiling").GetInt32(), defaults.DailyCallCeiling, "the configured default"));
            pins.Add(Pin.Text($"{project}/appsettings.json, SessionZone",
                string.Equals(section.GetProperty("SessionZone").GetString(), defaults.SessionZone, StringComparison.Ordinal),
                true, "the configured default"));
        }

        // The detection thresholds, both sides. Every one of these is a number the document states
        // and a constant the detectors decide on, and five of them are marked "phase 2 count check",
        // which means 2.11 may move them once. A threshold moved in the code and not in the table,
        // or the other way round, is exactly what that checkpoint is most likely to produce.
        // The same table row as the universe floor above, pinned a second time against a different
        // constant. Both are $5 and both are stated by that one row, and the two drifting apart
        // would mean the screen admits a name the detector will not trade or the reverse.
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Price floor, the detectors' side",
            table.Money("Price floor, both sides"), LongPullbackRules.PriceFloor,
            "LongPullbackRules.PriceFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Liquidity floor, short",
            table.Money("Liquidity floor, short"), ShortPullbackRules.LiquidityFloor,
            "ShortPullbackRules.LiquidityFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Market cap floor, short",
            table.Money("Market cap floor, short"), ShortPullbackRules.MarketCapFloor,
            "ShortPullbackRules.MarketCapFloor"));
        // The six limits, stated in two tables of ARCHITECTURE and held nowhere until 4.6. "The
        // limits" states them in plain terms and the authored-parameters table states them again with
        // their family, and the code held only the two PositionSizing needs. Both tables are read, so
        // the two statements of one number cannot drift from each other or from the component that
        // applies them.
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, risk per trade",
            LimitCell(architecture, "Risk per trade").Contains("0.75% of equity, so $750", StringComparison.Ordinal),
            PositionSizing.RiskPerTrade == 0.0075m && PositionSizing.RiskBudget == 750m,
            "PositionSizing.RiskPerTrade and RiskBudget"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Risk per trade",
            table.Cell("Risk per trade").Contains("0.75% of equity", StringComparison.Ordinal),
            PositionSizing.RiskPerTrade == 0.0075m, "PositionSizing.RiskPerTrade"));
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, give-up distance",
            LimitCell(architecture, "Give-up distance").Contains("At most half the daily range", StringComparison.Ordinal),
            RiskCaps.GiveUpDistanceRanges == 0.5m && LongPullbackRules.GiveUpRanges == RiskCaps.GiveUpDistanceRanges,
            "RiskCaps.GiveUpDistanceRanges against the detector's own GiveUpRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Give-up distance cap",
            table.Cell("Give-up distance cap").Contains("0.5 daily ranges", StringComparison.Ordinal),
            RiskCaps.GiveUpDistanceRanges == 0.5m, "RiskCaps.GiveUpDistanceRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, position size",
            LimitCell(architecture, "Position size").Contains("At most 35% of the account", StringComparison.Ordinal),
            RiskCaps.MaxPositionFraction == 0.35m, "RiskCaps.MaxPositionFraction"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Position cap",
            table.Cell("Position cap").Contains("35% of equity", StringComparison.Ordinal),
            RiskCaps.MaxPositionFraction == 0.35m, "RiskCaps.MaxPositionFraction"));
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, open at once",
            LimitCell(architecture, "Open at once").Contains("4 positions", StringComparison.Ordinal),
            RiskCaps.MaxOpenPositions == 4, "RiskCaps.MaxOpenPositions"));
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, open short positions",
            LimitCell(architecture, "Open short positions").Contains("2 of those 4", StringComparison.Ordinal),
            RiskCaps.MaxOpenShortPositions == 2 && RiskCaps.MaxOpenPositions == 4,
            "RiskCaps.MaxOpenShortPositions inside MaxOpenPositions"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Concurrent positions",
            table.Cell("Concurrent positions").Contains("4, of which at most 2 short", StringComparison.Ordinal),
            RiskCaps.MaxOpenPositions == 4 && RiskCaps.MaxOpenShortPositions == 2,
            "RiskCaps.MaxOpenPositions and MaxOpenShortPositions"));
        pins.Add(Pin.Text("ARCHITECTURE.html, the limits, total risk at stake",
            LimitCell(architecture, "Total risk at stake").Contains("3% of the account", StringComparison.Ordinal),
            RiskCaps.MaxTotalRiskFraction == 0.03m, "RiskCaps.MaxTotalRiskFraction"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Total risk at stake",
            table.Cell("Total risk at stake").Contains("3% of equity", StringComparison.Ordinal),
            RiskCaps.MaxTotalRiskFraction == 0.03m, "RiskCaps.MaxTotalRiskFraction"));

        // The borrow rate, stated in the short-checks prose and again in the authored parameters, and
        // held by a code constant from 4.7 because a position now records it. A rate that lived only
        // in a constant would restate every historical short at whatever the constant says today.
        pins.Add(Pin.Text("ARCHITECTURE.html, the short checks, assumed borrow rate",
            architecture.Contains("a flat borrow cost of <b>1.0% annualised</b> is deducted per calendar", StringComparison.Ordinal),
            BorrowAssumption.AnnualisedRate == 0.010m, "BorrowAssumption.AnnualisedRate"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Assumed borrow cost",
            table.Cell("Assumed borrow cost").Contains("1.0% annualised, per calendar day held", StringComparison.Ordinal),
            BorrowAssumption.AnnualisedRate == 0.010m, "BorrowAssumption.AnnualisedRate"));

        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Listing age floor, short",
            table.Cell("Listing age floor, short").Contains("90 sessions", StringComparison.Ordinal),
            ShortPullbackRules.MinimumSessionsListed == 90, "ShortPullbackRules.MinimumSessionsListed"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Daily range floor",
            table.Cell("Daily range floor").Contains("5%", StringComparison.Ordinal),
            LongPullbackRules.DailyRangeFloor == 0.05m, "LongPullbackRules.DailyRangeFloor"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Pullback shape",
            table.Cell("Pullback shape").Contains("2 to 7 bars, retrace at most 40%", StringComparison.Ordinal),
            LongPullbackRules.MinimumPullbackBars == 2
                && LongPullbackRules.MaximumPullbackBars == 7
                && LongPullbackRules.MaximumRetrace == 0.40m,
            "LongPullbackRules.MinimumPullbackBars, MaximumPullbackBars and MaximumRetrace"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Trigger reachability",
            table.Cell("Trigger reachability").Contains("Within 1.5 daily ranges", StringComparison.Ordinal),
            LongPullbackRules.TriggerReachRanges == 1.5m, "LongPullbackRules.TriggerReachRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Give-up distance cap",
            table.Cell("Give-up distance cap").Contains("0.5 daily ranges", StringComparison.Ordinal),
            LongPullbackRules.GiveUpRanges == 0.5m && ShortPullbackRules.GiveUpRanges == 0.5m,
            "LongPullbackRules.GiveUpRanges and ShortPullbackRules.GiveUpRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Cluster threshold",
            table.Cell("Cluster threshold").Contains("2 names, same industry", StringComparison.Ordinal),
            LongPullbackRules.ClusterThreshold == 2, "LongPullbackRules.ClusterThreshold"));
        // Not a number, and pinned for the same reason the numbers are. Two rows of BUILD_PLAN say
        // where the short side's twenty sessions start and point at the function that reads it off a
        // stored row, so a reader can check the claim against the code rather than against the
        // sentence making it. A citation to a symbol that has been renamed reads exactly like a live
        // one.
        //
        // <b>It pointed at ShortPullbackRules.ClausesRun until 4.4 and points at ClauseSetOf now.</b>
        // That constant is the record written before the anchored clause existed; nothing writes it
        // any more and its text is frozen, because it is the discriminator for every calibration row
        // already in the store. What a reader needs is the function that turns a stored note into a
        // clause set, and the property worth pinning is that the four sets stay four.
        pins.Add(Pin.Text("BUILD_PLAN.md, 3.6 and 4.4, the clause record the short seam is read from",
            CountOf(buildPlan, "ShortPullbackRules.ClauseSetOf") >= 2,
            ShortPullbackRules.ClauseSetOf(
                    [new CheckResult("reached-ceiling", true, 1m, ShortPullbackRules.ClausesRun)])
                    == CeilingClauses.TwoOfThree
                && new[]
                {
                    ShortPullbackRules.ClausesRun,
                    ShortPullbackRules.ClausesRunWithTheAnchor,
                    ShortPullbackRules.ClausesRunWithoutTheAnchor,
                    ShortPullbackRules.ClausesRunInReconstruction,
                }
                .Select(note => ShortPullbackRules.ClauseSetOf(
                    [new CheckResult("reached-ceiling", true, 1m, note)]))
                .Distinct()
                .Count() == 4,
            "ShortPullbackRules.ClauseSetOf"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Squeeze test",
            table.Cell("Squeeze test").Contains("21-to-50-day gap against its own 20-session average", StringComparison.Ordinal),
            ShortPullbackRules.SqueezeWindowSessions == 20 && AverageGap.Window == 20,
            "ShortPullbackRules.SqueezeWindowSessions and AverageGap.Window"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Contraction test",
            table.Cell("Contraction test").Contains("Against the 20-day average range", StringComparison.Ordinal),
            IndicatorEngine.RangeWindow == 20, "IndicatorEngine.RangeWindow"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Scan breadth",
            table.Cell("Scan breadth").Contains("Top 50 per scan", StringComparison.Ordinal),
            ScanEngine.Breadth == 50, "ScanEngine.Breadth"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Nightly setup cap",
            table.Cell("Nightly setup cap")
                .Contains("60, split 40 long and 20 short", StringComparison.Ordinal),
            NightlyCap.Total == 60 && NightlyCap.LongAllocation == 40 && NightlyCap.ShortAllocation == 20,
            "NightlyCap.Total, LongAllocation and ShortAllocation"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Month-mover lookback",
            table.Cell("Month-mover lookback").Contains("20 sessions", StringComparison.Ordinal),
            ScanEngine.MonthWindow == 20, "ScanEngine.MonthWindow"));

        // The three numbers the 3.0 spec pass authored. Each is stated in a decision, so each is
        // pinned against the constant rather than left as prose that agrees with the code today.
        string decisions = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "DECISIONS.md"));

        pins.Add(Pin.Text("DECISIONS.md, the control draw, five per set",
            decisions.Contains("five per set", StringComparison.OrdinalIgnoreCase),
            MeasurementParameters.ControlsPerSet == 5, "MeasurementParameters.ControlsPerSet"));

        pins.Add(Pin.Text("DECISIONS.md, the interval, a block length of ten sessions",
            decisions.Contains("block length of ten sessions", StringComparison.Ordinal),
            MeasurementParameters.BootstrapBlockSessions == 10,
            "MeasurementParameters.BootstrapBlockSessions"));

        pins.Add(Pin.Text("DECISIONS.md, the interval, ten thousand draws",
            decisions.Contains("ten thousand draws", StringComparison.Ordinal),
            MeasurementParameters.BootstrapDraws == 10_000, "MeasurementParameters.BootstrapDraws"));

        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Forward horizons",
            table.Cell("Forward horizons").Contains("10", StringComparison.Ordinal),
            MeasurementParameters.ScoringHorizonSessions == 10,
            "MeasurementParameters.ScoringHorizonSessions"));

        // The minimum sample, and the four inputs it is derived from. Pinned in both documents that
        // state it, because the figure it replaced lived in three places and read as derived in all
        // of them while nothing had measured the one input that is a fact.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Selection variant sample",
            table.Cell("Selection variant sample")
                .Contains("1802 effective paired setup observations", StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 1802,
            "MeasurementParameters.MinimumEffectiveObservations"));

        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, 1802 effective observations",
            decisions.Contains("**1802 effective observations**", StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 1802,
            "MeasurementParameters.MinimumEffectiveObservations"));

        // The execution family's minimum, pinned at 5.1 by VariantAdmitter existing to write it.
        // A row count rather than an effective figure, and the store carries the unit beside it so
        // the two minima cannot be read as comparable.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Execution variant sample",
            table.Cell("Execution variant sample")
                .Contains("200 paired trades", StringComparison.Ordinal),
            MeasurementParameters.ExecutionMinimumPairedTrades == 200,
            "MeasurementParameters.ExecutionMinimumPairedTrades"));

        // Accounts, which is a cap's scope rather than a cap. Every limit RiskGate applies is
        // counted within one book and a book belongs to a version, so two versions holding one name
        // are two positions neither of which can see the other. The document states it in words and
        // the constant is what the gate's scoping is written against.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Accounts",
            table.Cell("Accounts").Contains("One per version, both directions", StringComparison.Ordinal),
            RiskCaps.AccountsPerVersion == 1,
            "RiskCaps.AccountsPerVersion"));

        // The name carries the figure, so the name is pinned too. A decision whose title states a
        // number and a body that states a different one would resolve, cite and read cleanly.
        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the figure in the decision's own name",
            decisions.Contains(
                "**The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion**",
                StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 1802,
            "MeasurementParameters.MinimumEffectiveObservations"));

        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, two points of forward return",
            decisions.Contains("two points of ten-day forward return", StringComparison.Ordinal),
            MeasurementParameters.DetectableDifference == 0.02d,
            "MeasurementParameters.DetectableDifference"));

        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the two-sided 95% critical value",
            decisions.Contains("1.959964", StringComparison.Ordinal),
            MinimumSample.ZAlphaTwoSided95 == 1.959964d, "MinimumSample.ZAlphaTwoSided95"));

        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the 90% power critical value",
            decisions.Contains("1.281552", StringComparison.Ordinal),
            MinimumSample.ZBetaPower90 == 1.281552d, "MinimumSample.ZBetaPower90"));

        // The measured input, pinned against the derivation's own normal-theory step rather than
        // against a constant. It is the one number here that is a fact rather than a judgement, and
        // since 5.0(b) the pin carries the bootstrap's factor on top of it, so what has to agree is
        // the dispersion put through the arithmetic against the step the decision states, and the
        // pin has to be at least that.
        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the measured paired dispersion",
            decisions.Contains("**0.188681**", StringComparison.Ordinal),
            MinimumSample.Of(0.188681d) == 936 && MeasurementParameters.MinimumEffectiveObservations >= 936,
            "the flagged dispersion DECISIONS states, put through MinimumSample.Of, and the pin above it"));

        // The trading rows 4.15 answered and 4.4, 4.7, 4.8, 4.10 and 4.16 built. Every one of them
        // was pinned by nothing until 4.18: 4.15's row listed the mapping of each row to the checkpoint
        // that builds its component as a deliverable, its entry never mentioned it, and the rows sat
        // under a priced exemption with every component landed. Three of them are pinned a second
        // time by architecture-conformance's management-table and loss-table claims, which read the
        // cells of those tables; these read the authored-parameters row, so the two statements of one
        // number cannot part.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Starting equity",
            table.Cell("Starting equity").Contains("$100,000 notional, fixed", StringComparison.Ordinal),
            PositionSizing.NotionalEquity == 100_000m, "PositionSizing.NotionalEquity"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Long exit, trailing average",
            table.Cell("Long exit, trailing average").Contains("A daily close below the 9-day average, filling at the next open. Active from entry", StringComparison.Ordinal),
            IndicatorEngine.EmaShortPeriod == 9
                && LongExitRules.TrailArmedBy(adjustedClose: 99m, nineDayAverage: 100m)
                && !LongExitRules.TrailArmedBy(adjustedClose: 100m, nineDayAverage: 100m),
            "IndicatorEngine.EmaShortPeriod and LongExitRules.TrailArmedBy, strictly below"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Short exit, trim fraction",
            table.Cell("Short exit, trim fraction").Contains("15% of the planned position, once, at 3R", StringComparison.Ordinal),
            ShortExitRules.TrimFraction == 0.15m
                && ShortExitRules.TrimAt == 3m
                && ShortExitRules.TrimShares(plannedShares: 150, heldShares: 150) == 22,
            "ShortExitRules.TrimFraction, TrimAt and TrimShares of the planned count"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Short exit, the hourly grid",
            table.Cell("Short exit, the hourly grid").Contains("Six complete hourly bars, and the closing remainder is not one", StringComparison.Ordinal),
            HourlyGrid.CompleteBars == 6
                && HourlyGrid.HasStub
                && HourlyGrid.StubOpen is TimeOnly stub
                && !HourlyGrid.IsHourlyClose(stub)
                && HourlyGrid.Opens[0] == SessionBoundaries.RegularSessionOpen,
            "HourlyGrid.CompleteBars, StubOpen and IsHourlyClose, anchored to the session open"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Entry slippage",
            table.Cell("Entry slippage").Contains("The whole captured spread, the wrong way, both sides", StringComparison.Ordinal),
            FillModel.Entry(SetupDirection.Long, 100m, openedThrough: null, spreadBasisPoints: 100d).Price == 101m
                && FillModel.Entry(SetupDirection.Short, 100m, openedThrough: null, spreadBasisPoints: 100d).Price == 99m,
            "FillModel.Entry, charging the whole spread against the order on both sides"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Exit slippage",
            table.Cell("Exit slippage").Contains("the whole captured spread, both directions, trail and give-up alike", StringComparison.Ordinal),
            FillModel.Exit(SetupDirection.Long, 100m, openedThrough: null, spreadBasisPoints: 100d).Price == 99m
                && FillModel.Exit(SetupDirection.Short, 100m, openedThrough: null, spreadBasisPoints: 100d).Price == 101m,
            "FillModel.Exit, charging the whole spread against the order on both sides"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Gap-through fill price",
            table.Cell("Gap-through fill price").Contains("The open of the minute bar the order would otherwise have filled in. Not slipped again", StringComparison.Ordinal),
            FillModel.Exit(SetupDirection.Long, 95m, openedThrough: 88m, spreadBasisPoints: 100d) is { Price: 88m, Slippage: 0m, Basis: FillModel.Gapped }
                && FillModel.OpenedThrough(SetupDirection.Long, isExit: true, restingPrice: 95m, open: 88m)
                && !FillModel.OpenedThrough(SetupDirection.Long, isExit: true, restingPrice: 95m, open: 96m),
            "FillModel.Exit at the open with no slippage, and OpenedThrough on the adverse side only"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Trigger confirmation",
            table.Cell("Trigger confirmation").Contains("Touched. A minute bar's high reaching it long, its low short. No margin", StringComparison.Ordinal),
            TriggerTouch.Reached(SetupDirection.Long, triggerPrice: 100m, high: 100m, low: 99m)
                && !TriggerTouch.Reached(SetupDirection.Long, triggerPrice: 100m, high: 99.99m, low: 99m)
                && TriggerTouch.Reached(SetupDirection.Short, triggerPrice: 100m, high: 101m, low: 100m)
                && !TriggerTouch.Reached(SetupDirection.Short, triggerPrice: 100m, high: 101m, low: 100.01m),
            "TriggerTouch.Reached, at the touch and not one cent short of it"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Loss cause boundary",
            table.Cell("Loss cause boundary, noise against failed setup").Contains("+1R on the direction-signed ten-day return from the trigger", StringComparison.Ordinal),
            LossCause.AftermathOf(signedReturn: 0.05m, oneRInReturn: 0.05m) == LossAftermath.Noise
                && LossCause.AftermathOf(signedReturn: 0.0499m, oneRInReturn: 0.05m) == LossAftermath.FailedSetup
                && LossCause.OneRInReturn(giveUpDistance: 5m, triggerPrice: 100m) == 0.05m
                && LossClassifier.HorizonDays == 10,
            "LossCause.AftermathOf at one R, OneRInReturn over the trigger, and LossClassifier.HorizonDays"));
        // The order prices, which this table stated from 4.15 and nothing read until 4.18: PlanBuilder
        // copied the screening pair into the plan and the row rested under a priced exemption. Pinned
        // against the derivation, both sides, with the offset the row states.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Trigger and stop derivation",
            table.Cell("Trigger and stop derivation").Contains("regular-hours extremes, read off its daily bar, with the give-up point 0.1 ADR beyond its extreme, both sides", StringComparison.Ordinal),
            OrderPrices.GiveUpOffsetInRanges == 0.1m
                && OrderPrices.For(SetupDirection.Long, 104m, 101m, 5m) is { Trigger: 104m, GiveUp: 100.5m }
                && OrderPrices.For(SetupDirection.Short, 52m, 49m, 2.5m) is { Trigger: 49m, GiveUp: 52.25m },
            "OrderPrices.GiveUpOffsetInRanges and OrderPrices.For on both sides"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Lateness bound",
            table.Cell("Lateness bound").Contains("24 hours", StringComparison.Ordinal),
            MeasurementParameters.LatenessBoundHours == 24, "MeasurementParameters.LatenessBoundHours"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Regime label",
            table.Cell("Regime label").Contains("risk-on at +2, risk-off at minus 2, mixed otherwise", StringComparison.Ordinal),
            MarketMood.LabelFor(1, 1) == MarketMood.RiskOn
                && MarketMood.LabelFor(-1, -1) == MarketMood.RiskOff
                && MarketMood.LabelFor(1, 0) == MarketMood.Mixed
                && MarketMood.LabelFor(-1, 0) == MarketMood.Mixed,
            "MarketMood.LabelFor at the two sums and between them"));

        IReadOnlyList<IReadOnlyList<string>> parameters = HtmlTable.BodyRowsUnder(architecture, "Authored parameters");

        foreach (Pin pin in pins)
        {
            coverage.Examined(pin.What, 1);
        }

        // Most pins read the constant from the compiled code, so the value is the value. Four do
        // not: the store pragmas are matched against the text of the statements in the factory,
        // which is a claim about what the connection does made by reading how it is opened.
        coverage.Scan("the four store pragmas, matched against the statements StoreConnectionFactory issues",
            CheckCoverage.Backing.Test(
                "StoreTests.The_open_connection_reports_the_four_pragmas_from_schema",
                "the test opens a connection and asks it what each pragma is set to, which is the only thing "
                + "that distinguishes a statement issued from a statement present in the file"));

        // Every row of the table is pinned, deferred to the checkpoint that builds the component it
        // governs, or exempted by name with the reason no constant can carry it. Nothing else is
        // admitted: a row in none of the three fails, and a row deferred to a checkpoint that has
        // landed fails, which is the mapping 4.15's row listed as a deliverable and 4.18 built. The
        // set of pinned rows is derived from the pins above, so a pin added or removed moves this
        // rather than a list beside it.
        ArchitectureConformanceCheck.Schedule schedule = ArchitectureConformanceCheck.Schedule.Read();
        string[] rows = [.. parameters.Select(r => HtmlTable.Text(r[0]).Trim())];

        coverage.Examined("rows of the authored parameters table placed as pinned, deferred or exempt", rows.Length);

        (IReadOnlyList<Placement> placements, IReadOnlyList<string> problems) = Place(
            rows, table.WasPinned, RowsDeferredToACheckpoint, RowsNoConstantCanCarry, schedule.HasLanded);

        foreach (Placement placement in placements)
        {
            if (placement.Checkpoint is string checkpoint)
            {
                coverage.OutOfScope(
                    $"authored parameter \"{placement.Row}\", whose component {placement.Component} arrives at {checkpoint}", 1,
                    CheckCoverage.OutOfScopeReason.UntilCheckpoint(checkpoint,
                        "the row states a value the component built at that checkpoint will hold, and nothing holds it yet"));
            }
            else if (placement.Why is string why)
            {
                coverage.OutOfScope($"authored parameter \"{placement.Row}\", exempt by name", 1,
                    CheckCoverage.OutOfScopeReason.ByDesign(why));
            }
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} row(s) of the authored-parameters table rest on nothing:\n  " + string.Join("\n  ", problems));

        IReadOnlyList<IReadOnlyList<string>> budget = HtmlTable.BodyRowsUnder(architecture, "Data budget");
        string[] pinnedBudgetRows =
            ["Whole-market daily bars", "Splits, bulk", "Dividends, bulk", "Index bars", "History refetch", "Daily total"];
        coverage.OutOfScope(
            "rows of the data budget whose request has not been built yet",
            budget.Count - pinnedBudgetRows.Length,
            CheckCoverage.OutOfScopeReason.UntilDecided(
                "mapping each data-budget row to the checkpoint that makes the request",
                "the request arrives with the checkpoint that makes it, and that mapping has never been derived. No "
                + "row of this table is unexaminable by design any more: every one states a cost per request and a "
                + "cadence separately"));

        coverage.Report();

        string[] wrong = pins.Where(p => !p.Holds).Select(p => p.Describe()).ToArray();

        Assert.True(wrong.Length == 0,
            $"{wrong.Length} constant(s) stated in a document no longer match the code:\n  " + string.Join("\n  ", wrong));

        Assert.True(parameters.Count >= 25,
            $"Only {parameters.Count} authored parameters were parsed. The table held more than that before any code "
            + "existed, so a number this low means the parser stopped matching.");
    }

    /// <summary>
    /// The cost-per-request column of one data budget row. Separate from the calls-a-night
    /// column beside it, which is what a job contributes to a night and is an average wherever
    /// the cadence is not nightly.
    ///
    /// A tilde is the document saying the figure is an estimate, and an estimate cannot pin a
    /// constant. A row that acquires one fails here rather than being quietly dropped from what
    /// the check examines.
    /// </summary>
    private static int BudgetCost(string architecture, string job)
    {
        string value = BudgetCell(architecture, job, column: 2);

        Assert.False(value.Contains('~', StringComparison.Ordinal),
            $"The data budget states \"{value}\" as the cost per request for {job}, which is an estimate. "
            + "An estimate cannot pin a constant, so either the figure is a cost or the row is unexamined.");

        return int.Parse(new string(value.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
    }

    private static string BudgetCadence(string architecture, string job) =>
        BudgetCell(architecture, job, column: 3);

    private static string BudgetCell(string architecture, string job, int column) =>
        HtmlTable.BodyRowsUnder(architecture, "Data budget")
            .Single(r => r[0].StartsWith(job, StringComparison.Ordinal))[column].Trim();

    /// <summary>A row of "The limits", which states the same six numbers in plain terms.</summary>
    private static string LimitCell(string architecture, string limit) =>
        HtmlTable.BodyRowsUnder(architecture, "The limits")
            .Single(r => r[0].StartsWith(limit, StringComparison.Ordinal))[1];

    private static string PragmaCell(string schema, string pragma) =>
        MarkdownTable.BodyRowsAfter(schema, "## Store configuration")
            .Single(r => r[0].Contains(pragma, StringComparison.Ordinal))[1];

    private static int PragmaNumber(string schema, string pragma) =>
        int.Parse(
            new string(PragmaCell(schema, pragma).Where(char.IsDigit).ToArray()),
            CultureInfo.InvariantCulture);

    private static string PragmaText(string schema, string pragma) =>
        PragmaCell(schema, pragma).Trim('`', ' ');

    /// <summary>How many times a document names something. Two rows have to, so one is not enough.</summary>
    private static int CountOf(string document, string needle)
    {
        int count = 0;
        int at = document.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = document.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Where one row of the authored-parameters table rests: on a pin, on a checkpoint, or on a stated reason.</summary>
    public sealed record Placement(string Row, string? Checkpoint, string? Component, string? Why)
    {
        public bool IsPinned => Checkpoint is null && Why is null;
    }

    /// <summary>
    /// Every row placed as pinned, deferred or exempt, and every row that is none of the three or
    /// is deferred to a checkpoint that has landed.
    ///
    /// Pure, and separated from the run so it can be proved against rows written by hand: the live
    /// table exercises the pinned, deferred and exempt dispositions and none of the failing ones,
    /// which is the population a guard's proof has to state rather than accept.
    /// </summary>
    public static (IReadOnlyList<Placement> Placed, IReadOnlyList<string> Problems) Place(
        IReadOnlyList<string> rows,
        Func<string, bool> wasPinned,
        IReadOnlyList<(string Row, string Checkpoint, string Component)> deferred,
        IReadOnlyList<(string Row, string Why)> exempt,
        Func<string, bool> hasLanded)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(wasPinned);
        ArgumentNullException.ThrowIfNull(deferred);
        ArgumentNullException.ThrowIfNull(exempt);
        ArgumentNullException.ThrowIfNull(hasLanded);

        string[] pinnedRows = [.. rows.Where(wasPinned)];
        string[] unpinnedRows = [.. rows.Where(r => !wasPinned(r))];

        var placed = new List<Placement>(pinnedRows.Select(r => new Placement(r, null, null, null)));
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach ((string row, string checkpoint, string component) in deferred)
        {
            string? match = unpinnedRows.SingleOrDefault(r => r.StartsWith(row, StringComparison.Ordinal));

            if (match is null)
            {
                problems.Add(pinnedRows.Any(r => r.StartsWith(row, StringComparison.Ordinal))
                    ? $"\"{row}\" is pinned and still deferred to {checkpoint}. Remove the deferral: a row that has "
                        + "its constant is not waiting for anything."
                    : $"\"{row}\" is deferred to {checkpoint} and is not a row of the authored-parameters table.");
                continue;
            }

            claimed.Add(match);

            if (hasLanded(checkpoint))
            {
                problems.Add(
                    $"\"{match}\" is deferred to {checkpoint}, which PROGRESS records as landed, and no pin reads it. "
                    + $"{component} exists now, so the row is pinned against it or the deferral is wrong.");
                continue;
            }

            placed.Add(new Placement(match, checkpoint, component, null));
        }

        foreach ((string row, string why) in exempt)
        {
            string? match = unpinnedRows.SingleOrDefault(r => r.StartsWith(row, StringComparison.Ordinal));

            if (match is null)
            {
                problems.Add($"\"{row}\" is exempted by name and is not an unpinned row of the authored-parameters table.");
                continue;
            }

            claimed.Add(match);
            placed.Add(new Placement(match, null, null, why));
        }

        foreach (string row in unpinnedRows.Where(r => !claimed.Contains(r)))
        {
            problems.Add(
                $"\"{row}\" is a row of the authored-parameters table that no pin reads, no checkpoint is named for, "
                + "and no exemption names. Pin it, defer it to the checkpoint that builds its component, or say why "
                + "no constant can carry it.");
        }

        return (placed, problems);
    }

    /// <summary>
    /// The rows whose component has not been built, each mapped to the checkpoint whose deliverable
    /// builds it, which is the mapping 4.15 listed and 4.18 derived.
    ///
    /// By row name rather than by component name, because a row names a value and not the type that
    /// holds it, which is why <c>Schedule.CheckpointFor</c> cannot resolve it the way it resolves a
    /// catalogue row. The checkpoint has to be one BUILD_PLAN has and PROGRESS does not yet record,
    /// on the rule every deferral obeys, so a component landing without its row being pinned turns
    /// this check red rather than leaving the row resting.
    /// </summary>
    public static IReadOnlyList<(string Row, string Checkpoint, string Component)> RowsDeferredToACheckpoint { get; } =
    [
        ("Holdout windows", "5.4", "HoldoutRegistry"),
        ("Twin-pair threshold", "6.3", "TwinPairFinder"),
        ("Signal correlation limit", "6.2", "SignalAdmissionTest"),
        ("Researcher model", "6.5", "ResearcherSeat"),
        ("Researcher cadence", "6.5", "ResearcherSeat"),
    ];

    /// <summary>
    /// The rows that state no number and no threshold, each with the reason no constant can carry
    /// it and where the property it states is held instead.
    ///
    /// Exempt by name rather than by shape, so a row added later that states a rule in words has to
    /// be placed here on purpose, and the count of these is reported apart so it is seen growing.
    /// </summary>
    public static IReadOnlyList<(string Row, string Why)> RowsNoConstantCanCarry { get; } =
    [
        ("Short exit, trim into support",
            "it records that a clause was dropped from the baseline rather than a value, so there is no constant to "
            + "read; a scan for a support level finding nothing would be the vacuous pass, and the drop is recorded as a "
            + "decision so the next reader finds a choice rather than an omission"),
        ("Long exit, the other side of the comparison",
            "it states which of two rules ends a long, in words and not as a number; architecture-conformance's "
            + "management-table claim asserts the same sentence against ExitReason.First and the two rule types"),
        ("When the trail takes over from the fixed stop",
            "it states that no handover exists, which is the absence of a threshold rather than one; the same "
            + "management-table claim asserts that both rules are live from the entry and that a tie resolves as a give-up"),
        ("Screen and cap ranking",
            "it states an ordering and a tiebreak rather than a number; SetupCapper's tests hold the ordering over "
            + "authored rows, and a scan of the sentence against the code would compare prose with prose"),
    ];

    /// <summary>
    /// The authored-parameters table, read through one place that remembers which rows were read.
    ///
    /// A pin is made by reading a row's value cell, so the rows read are the rows pinned, and that
    /// set is what the placement below derives from rather than a list kept beside it.
    /// </summary>
    private sealed class AuthoredParameters
    {
        private readonly IReadOnlyList<IReadOnlyList<string>> _rows;
        private readonly HashSet<string> _read = new(StringComparer.Ordinal);

        public AuthoredParameters(string architecture)
        {
            _rows = HtmlTable.BodyRowsUnder(architecture, "Authored parameters");
        }

        /// <summary>The value cell of the row whose name starts with <paramref name="parameter"/>.</summary>
        public string Cell(string parameter)
        {
            _read.Add(parameter);
            return _rows.Single(r => r[0].StartsWith(parameter, StringComparison.Ordinal))[1];
        }

        /// <summary>
        /// A money value as the table writes it: a dollar sign, digits, and an optional M or B.
        /// Parsed rather than matched as a string, so a table that says $20M and a constant that
        /// says 20,000,000 can be compared at all.
        /// </summary>
        public decimal Money(string parameter)
        {
            string value = Cell(parameter);
            Match match = Regex.Match(value, @"\$(?<n>[\d,.]+)\s*(?<scale>[MB])?", RegexOptions.CultureInvariant);
            Assert.True(match.Success, $"No money value in {value}.");

            decimal number = decimal.Parse(
                match.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                CultureInfo.InvariantCulture);

            return match.Groups["scale"].Value switch
            {
                "M" => number * 1_000_000m,
                "B" => number * 1_000_000_000m,
                _ => number,
            };
        }

        public int Number(string parameter)
        {
            string value = Cell(parameter);
            return int.Parse(
                new string(value.TakeWhile(c => char.IsDigit(c) || c == ',').ToArray()).Replace(",", string.Empty, StringComparison.Ordinal),
                CultureInfo.InvariantCulture);
        }

        /// <summary>Whether a pin read this row, by the same prefix rule the reads use.</summary>
        public bool WasPinned(string row) =>
            _read.Any(read => row.StartsWith(read, StringComparison.Ordinal));
    }

    private sealed record Pin(string What, bool Holds, string Detail)
    {
        public static Pin Number(string what, int stated, int inCode, string codeName) =>
            new(what, stated == inCode, $"document states {stated}, {codeName} is {inCode}");

        public static Pin Money(string what, decimal stated, decimal inCode, string codeName) =>
            new(what, stated == inCode, $"document states {stated}, {codeName} is {inCode}");

        public static Pin Text(string what, bool statedInDocument, bool holdsInCode, string codeName) =>
            new(what, statedInDocument && holdsInCode,
                $"document states it: {statedInDocument}; {codeName} agrees: {holdsInCode}");

        public string Describe() => $"{What} — {Detail}";
    }
}
