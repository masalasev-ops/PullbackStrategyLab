using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
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
        pins.Add(Pin.Text("CLAUDE.md, the daily vendor call ceiling",
            claude.Contains("daily vendor call ceiling is 5,000", StringComparison.Ordinal),
            defaults.DailyCallCeiling == 5000, "the stated ceiling against the configured default"));
        pins.Add(Pin.Text("RUNBOOK.md, the nightly total against the ceiling",
            runbook.Contains("against a 5,000 ceiling", StringComparison.Ordinal),
            defaults.DailyCallCeiling == 5000, "the stated ceiling against the configured default"));

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

        IReadOnlyList<IReadOnlyList<string>> parameters = HtmlTable.BodyRowsUnder(architecture, "Authored parameters");

        foreach (Pin pin in pins)
        {
            coverage.Examined(pin.What, 1);
        }

        // Named so the number moves when a parameter gains a constant, rather than being a
        // literal somebody has to remember to decrement.
        string[] pinnedParameters = ["Daily API ceiling", "Price floor", "Liquidity floor, long"];
        coverage.NotExamined(
            "rows of the authored parameters table with no code constant yet",
            parameters.Count - pinnedParameters.Length,
            "the parameter arrives with the checkpoint that builds the component it governs");
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
