using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Core.Measurement;
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

        // The daily call ceiling, stated in four places and held in one.
        pins.Add(Pin.Number("ARCHITECTURE.html, authored parameters, Daily API ceiling",
            ParameterNumber(architecture, "Daily API ceiling"), defaults.DailyCallCeiling, "PullbackStrategyLabOptions.DailyCallCeiling"));
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
            ParameterMoney(architecture, "Price floor"), defaults.Universe.PriceFloor, "UniverseOptions.PriceFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Liquidity floor, long",
            ParameterMoney(architecture, "Liquidity floor, long"), defaults.Universe.LiquidityFloorLong, "UniverseOptions.LiquidityFloorLong"));

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
            ParameterMoney(architecture, "Price floor, both sides"), LongPullbackRules.PriceFloor,
            "LongPullbackRules.PriceFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Liquidity floor, short",
            ParameterMoney(architecture, "Liquidity floor, short"), ShortPullbackRules.LiquidityFloor,
            "ShortPullbackRules.LiquidityFloor"));
        pins.Add(Pin.Money("ARCHITECTURE.html, authored parameters, Market cap floor, short",
            ParameterMoney(architecture, "Market cap floor, short"), ShortPullbackRules.MarketCapFloor,
            "ShortPullbackRules.MarketCapFloor"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Listing age floor, short",
            ParameterCell(architecture, "Listing age floor, short").Contains("90 sessions", StringComparison.Ordinal),
            ShortPullbackRules.MinimumSessionsListed == 90, "ShortPullbackRules.MinimumSessionsListed"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Daily range floor",
            ParameterCell(architecture, "Daily range floor").Contains("5%", StringComparison.Ordinal),
            LongPullbackRules.DailyRangeFloor == 0.05m, "LongPullbackRules.DailyRangeFloor"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Pullback shape",
            ParameterCell(architecture, "Pullback shape").Contains("2 to 7 bars, retrace at most 40%", StringComparison.Ordinal),
            LongPullbackRules.MinimumPullbackBars == 2
                && LongPullbackRules.MaximumPullbackBars == 7
                && LongPullbackRules.MaximumRetrace == 0.40m,
            "LongPullbackRules.MinimumPullbackBars, MaximumPullbackBars and MaximumRetrace"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Trigger reachability",
            ParameterCell(architecture, "Trigger reachability").Contains("Within 1.5 daily ranges", StringComparison.Ordinal),
            LongPullbackRules.TriggerReachRanges == 1.5m, "LongPullbackRules.TriggerReachRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Give-up distance cap",
            ParameterCell(architecture, "Give-up distance cap").Contains("0.5 daily ranges", StringComparison.Ordinal),
            LongPullbackRules.GiveUpRanges == 0.5m && ShortPullbackRules.GiveUpRanges == 0.5m,
            "LongPullbackRules.GiveUpRanges and ShortPullbackRules.GiveUpRanges"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Cluster threshold",
            ParameterCell(architecture, "Cluster threshold").Contains("2 names, same industry", StringComparison.Ordinal),
            LongPullbackRules.ClusterThreshold == 2, "LongPullbackRules.ClusterThreshold"));
        // Not a number, and pinned for the same reason the numbers are. Two rows of BUILD_PLAN say
        // the short side's twenty sessions start at 4.4 and point at the constant the store records
        // it in, so a reader can check the claim against the code rather than against the sentence
        // making it. A citation to a symbol that has been renamed reads exactly like a live one.
        pins.Add(Pin.Text("BUILD_PLAN.md, 3.6 and 4.4, the clause record the short seam is read from",
            CountOf(buildPlan, "ShortPullbackRules.ClausesRun") >= 2,
            ShortPullbackRules.ClausesRun.Contains("4.4", StringComparison.Ordinal)
                && ShortPullbackRules.ClauseSetOf(
                    [new CheckResult("reached-ceiling", true, 1m, ShortPullbackRules.ClausesRun)])
                    == CeilingClauses.TwoOfThree,
            "ShortPullbackRules.ClausesRun"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Squeeze test",
            ParameterCell(architecture, "Squeeze test").Contains("21-to-50-day gap against its own 20-session average", StringComparison.Ordinal),
            ShortPullbackRules.SqueezeWindowSessions == 20 && AverageGap.Window == 20,
            "ShortPullbackRules.SqueezeWindowSessions and AverageGap.Window"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Contraction test",
            ParameterCell(architecture, "Contraction test").Contains("Against the 20-day average range", StringComparison.Ordinal),
            IndicatorEngine.RangeWindow == 20, "IndicatorEngine.RangeWindow"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Scan breadth",
            ParameterCell(architecture, "Scan breadth").Contains("Top 50 per scan", StringComparison.Ordinal),
            ScanEngine.Breadth == 50, "ScanEngine.Breadth"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Nightly setup cap",
            ParameterCell(architecture, "Nightly setup cap")
                .Contains("60, split 40 long and 20 short", StringComparison.Ordinal),
            NightlyCap.Total == 60 && NightlyCap.LongAllocation == 40 && NightlyCap.ShortAllocation == 20,
            "NightlyCap.Total, LongAllocation and ShortAllocation"));
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Month-mover lookback",
            ParameterCell(architecture, "Month-mover lookback").Contains("20 sessions", StringComparison.Ordinal),
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
            ParameterCell(architecture, "Forward horizons").Contains("10", StringComparison.Ordinal),
            MeasurementParameters.ScoringHorizonSessions == 10,
            "MeasurementParameters.ScoringHorizonSessions"));

        // The minimum sample, and the four inputs it is derived from. Pinned in both documents that
        // state it, because the figure it replaced lived in three places and read as derived in all
        // of them while nothing had measured the one input that is a fact.
        pins.Add(Pin.Text("ARCHITECTURE.html, authored parameters, Selection variant sample",
            ParameterCell(architecture, "Selection variant sample")
                .Contains("262 effective paired setup observations", StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 262,
            "MeasurementParameters.MinimumEffectiveObservations"));

        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, 262 effective observations",
            decisions.Contains("**262 effective observations**", StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 262,
            "MeasurementParameters.MinimumEffectiveObservations"));

        // The name carries the figure, so the name is pinned too. A decision whose title states a
        // number and a body that states a different one would resolve, cite and read cleanly.
        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the figure in the decision's own name",
            decisions.Contains(
                "**The minimum sample is 262 effective observations, ratified at two points and 90% power**",
                StringComparison.Ordinal),
            MeasurementParameters.MinimumEffectiveObservations == 262,
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

        // The measured input, pinned against the fixture expectation rather than against a constant.
        // It is the one number here that is a fact rather than a judgement, so the thing it has to
        // agree with is the derivation, not a value typed into the source.
        pins.Add(Pin.Text("DECISIONS.md, the minimum sample, the measured paired dispersion",
            decisions.Contains("**0.099811**", StringComparison.Ordinal),
            MinimumSample.Of(0.099811d) == MeasurementParameters.MinimumEffectiveObservations,
            "the dispersion DECISIONS states, put through MinimumSample.Of"));

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

        // Named so the number moves when a parameter gains a constant, rather than being a
        // literal somebody has to remember to decrement.
        string[] pinnedParameters =
        [
            "Daily API ceiling", "Price floor", "Liquidity floor, long",
            "Liquidity floor, short", "Market cap floor, short",
            "Listing age floor, short", "Daily range floor", "Pullback shape", "Trigger reachability",
            "Give-up distance cap", "Cluster threshold", "Squeeze test", "Contraction test",
            "Scan breadth", "Month-mover lookback", "Nightly setup cap",
        ];
        coverage.OutOfScope(
            "rows of the authored parameters table with no code constant yet",
            parameters.Count - pinnedParameters.Length,
            CheckCoverage.OutOfScopeReason.UntilDecided(
                "mapping each authored parameter to the checkpoint that builds the component it governs",
                "each closes when its component is built, and which checkpoint that is has never been derived. The "
                + "mapping is by parameter name rather than by component name, which is why Schedule cannot resolve "
                + "it the way it resolves a catalogue row"));

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
    /// A money value as the table writes it: a dollar sign, digits, and an optional M or B.
    /// Parsed rather than matched as a string, so a table that says $20M and a constant that
    /// says 20,000,000 can be compared at all.
    /// </summary>
    private static decimal ParameterMoney(string architecture, string parameter)
    {
        string value = ParameterCell(architecture, parameter);
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

    private static string ParameterCell(string architecture, string parameter) =>
        HtmlTable.BodyRowsUnder(architecture, "Authored parameters")
            .Single(r => r[0].StartsWith(parameter, StringComparison.Ordinal))[1];

    private static int ParameterNumber(string architecture, string parameter)
    {
        string value = HtmlTable.BodyRowsUnder(architecture, "Authored parameters")
            .Single(r => r[0].StartsWith(parameter, StringComparison.Ordinal))[1];
        return int.Parse(
            new string(value.TakeWhile(c => char.IsDigit(c) || c == ',').ToArray()).Replace(",", string.Empty, StringComparison.Ordinal),
            CultureInfo.InvariantCulture);
    }

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
