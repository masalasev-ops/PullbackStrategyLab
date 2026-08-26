using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No signal or check reads a value whose observation could not have been made by its own date.
///
/// <b>The single most important property in the system,</b> because breaking it produces an
/// encouraging result that means nothing. A replay that can see Tuesday's correction while answering
/// Monday's question does not answer Monday's question; it answers a question nobody can trade, and
/// every figure downstream inherits that without anything looking wrong.
///
/// Three halves, and the third is the one a convention could not hold.
///
/// <b>The readers.</b> Every public read in <c>PullbackStrategyLab.Data</c> takes a date, and none of
/// them offers an overload that does not. A read that could omit it would compile, run, and answer.
///
/// <b>The statements written by hand.</b> Stages and the read surface write SQL of their own, and a
/// statement selecting from a table that carries an observation stamp has to bound that stamp. This
/// is where the convention fails on its own: a reader whose signature demands a date proves nothing
/// about a query somebody wrote beside it, and three such queries were in the shipped source when
/// this check was written.
///
/// <b>The behaviour.</b> A row observed after the as-of instant is invisible, and the same row is
/// visible once the as-of moves past it. Both directions, because a reader that returned nothing at
/// all would satisfy the first.
/// </summary>
public sealed class PointInTimeCheck
{
    private readonly ITestOutputHelper _output;

    public PointInTimeCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The tables carrying an observation stamp, and the column a read of them has to bound.
    ///
    /// Named here rather than derived from the migrations, and the trade is deliberate: a derivation
    /// would find every column ending in `_at` and would quietly stop finding one that was renamed,
    /// where a list that goes stale fails against the migration text in the test below.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Stamped { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["daily_bar"] = "observed_at",
            ["index_bar"] = "observed_at",
            ["corporate_action"] = "observed_at",
            ["indicator_daily"] = "computed_at",
            ["history_refetch"] = "refetched_at",
            ["security"] = "sector_resolved_at",
            ["setup_signal"] = "computed_at",
        };

    /// <summary>
    /// Statements that select from a stamped table and legitimately do not bound the stamp, by the
    /// file and the reason.
    ///
    /// Every entry is a read that is not answering a question as of a date. An exemption that could
    /// not say that about itself is a defect wearing a name, so each one states what it is instead.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Exempt { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IndicatorEngine.cs"] =
                "it asks which sessions have ever been computed, so it can skip them. The answer is about the "
                + "store's contents rather than about a night, and bounding it would recompute every session "
                + "the engine has already done.",
            ["MigrationRunner.cs"] =
                "migrations read and write structure rather than evidence.",
        };

    /// <summary>
    /// The one read that takes no date, by name and with the reason.
    ///
    /// Calibration mode reads membership as it stands today, deliberately, which is the survivorship
    /// bias its own table exists to quarantine. Exempting it by name rather than by shape is the
    /// whole point: a rule that let any read drop its date would let the next one drop it by
    /// accident, and this one is the only read in the lab that is entitled to.
    /// see: A calibration run reconstructs against current membership and computes its indicators in memory
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    public static IReadOnlyDictionary<string, string> DatelessByName { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UniverseSnapshotReader.CurrentMembers"] =
                "calibration mode reads membership as it stands today on purpose, and the rows it produces go to a "
                + "table nothing downstream reads. Its name says which mode it is for, and the evidence read beside "
                + "it takes a date like everything else.",
        };

    [Fact]
    [Trait("check", "point-in-time")]
    public void No_signal_or_check_reads_a_value_observed_after_its_own_date()
    {
        var coverage = new CheckCoverage("point-in-time", _output);
        var failures = new List<string>();

        // 1. The readers. Every public read takes a date, and there is no overload that omits it.
        Type[] readers =
        [
            typeof(DailyBarReader), typeof(IndexBarReader), typeof(IndicatorDailyReader),
            typeof(ScanHitReader), typeof(SetupReader), typeof(SetupSignalReader),
            typeof(SecurityReader), typeof(CorporateActionReader), typeof(UniverseSnapshotReader),
            typeof(RegimeReader),
        ];

        int readsExamined = 0;

        foreach (Type reader in readers)
        {
            foreach (MethodInfo method in reader
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && Reads(m.Name)))
            {
                readsExamined++;

                if (!method.GetParameters().Any(p => p.ParameterType == typeof(DateOnly)
                        || p.ParameterType == typeof(DateOnly?))
                    && !DatelessByName.ContainsKey($"{reader.Name}.{method.Name}"))
                {
                    failures.Add(
                        $"{reader.Name}.{method.Name} reads the store and takes no date, so it can answer with "
                        + "figures the lab could not have had.");
                }
            }
        }

        // 2. The statements written by hand, outside the readers. This is the half a signature
        //    cannot hold: a query beside a reader is not bound by the reader's shape.
        int statementsExamined = 0;
        int stampedStatements = 0;

        foreach (string file in RepositoryLayout.ProductionSourceFiles.Where(NotAReader))
        {
            string source = RepositoryLayout.Read(file);
            string name = Path.GetFileName(file);

            foreach (string statement in Statements(source))
            {
                statementsExamined++;

                foreach ((string table, string stamp) in Stamped)
                {
                    if (!SelectsFrom(statement, table))
                    {
                        continue;
                    }

                    stampedStatements++;

                    if (statement.Contains(stamp, StringComparison.Ordinal) || Exempt.ContainsKey(name))
                    {
                        continue;
                    }

                    failures.Add(
                        $"{name} selects from {table} without bounding {stamp}, so it can see an observation made "
                        + "after the date it is answering for.");
                }
            }
        }

        // 3. The behaviour, both directions.
        (bool hiddenBefore, bool visibleAfter) = FutureObservation();

        if (!hiddenBefore)
        {
            failures.Add("a bar observed after the as-of instant was visible to a read as of that date.");
        }

        if (!visibleAfter)
        {
            failures.Add(
                "the same bar was still invisible once the as-of moved past its observation, so the read is "
                + "returning nothing rather than bounding anything.");
        }

        coverage
            .Examined("public reads on the store's readers", readsExamined)
            .Examined("statements selecting from a stamped table", stampedStatements)
            .Examined("stamped tables the check knows about", Stamped.Count)
            .Examined("exempted files, each with its reason", Exempt.Count)
            .Examined("dateless reads exempted by name, each with its reason", DatelessByName.Count)
            .Examined("directions of the future-dated case", 2)
            .Context("SQL statements read across the shipped source", statementsExamined)
            .Scan("every public read on a store reader takes a date",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_bar_dated_after_the_as_of_date_is_invisible_to_a_read",
                    "a bar dated past the as-of is stored and then not returned, which is what a signature "
                    + "carrying a date is for. The signature is necessary and this is what it buys"))
            .Scan("every hand-written statement selecting from a stamped table bounds that stamp",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_read_sees_the_figure_that_had_been_observed_by_its_as_of_date_and_not_the_correction",
                    "the same session is read from both sides of a correction's instant and gives two figures. "
                    + "That is what a bound does; the scan is what says every statement written by hand beside a "
                    + "reader has one, which is the half four unbounded queries were on the wrong side of"));

        // Calibration mode reconstructs against membership as it stands today, deliberately, which
        // is why its rows go to a table nothing downstream reads. It is out of scope by design
        // rather than deferred: nothing can close it, because closing it would mean the lab had a
        // universe snapshot for a night it was not running.
        // see: The evidence store holds only setups flagged forward, never setups reconstructed from history
        coverage.OutOfScope(
            "reads made by a detector in calibration mode",
            DatelessByName.Count,
            CheckCoverage.OutOfScopeReason.ByDesign(
                "a calibration run reads membership as it stands today on purpose, which is the survivorship bias "
                + "its own table exists to quarantine. A point-in-time calibration run is not a stricter version of "
                + "this one; it is a run that cannot exist, because there is no record of who was listed on a night "
                + "the lab was not running"));

        coverage.Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} point-in-time failure(s):\n  " + string.Join("\n  ", failures));

        // Stated in advance, because every assertion above holds trivially over an empty sweep.
        Assert.True(readsExamined >= 15, $"Only {readsExamined} public read(s) found on the readers.");
        Assert.True(stampedStatements >= 5,
            $"Only {stampedStatements} statement(s) selecting from a stamped table were found outside the readers. "
            + "The scanner stopped matching rather than the source getting cleaner.");
    }

    /// <summary>
    /// The stamped-table list against the migrations, so a renamed column fails here.
    ///
    /// The list is named rather than derived, and this is what stops "named" turning into "stale":
    /// every table it claims exists and carries the column it claims, read from the SQL that creates
    /// them.
    /// </summary>
    [Fact]
    public void Every_stamped_table_the_check_names_carries_the_column_it_names()
    {
        string migrations = string.Concat(
            Directory.EnumerateFiles(
                Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations"), "*.sql")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        var missing = new List<string>();

        foreach ((string table, string stamp) in Stamped)
        {
            if (!migrations.Contains($"CREATE TABLE {table}", StringComparison.Ordinal))
            {
                missing.Add($"no migration creates {table}");
                continue;
            }

            if (!migrations.Contains(stamp, StringComparison.Ordinal))
            {
                missing.Add($"{table} is said to carry {stamp} and no migration mentions that column");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n  ", missing));
    }

    /// <summary>
    /// A bar observed tomorrow, read as of today and as of the day after.
    ///
    /// Written as a permanent case rather than as a break-and-revert, and asserted in both
    /// directions: a reader that returned nothing at all would satisfy "the future bar is invisible"
    /// perfectly.
    /// </summary>
    private static (bool HiddenBefore, bool VisibleAfter) FutureObservation()
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        var asOf = new DateOnly(2026, 8, 24);
        DateTimeOffset tomorrow = new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero);

        using SqliteConnection write = connections.OpenWrite();

        using (SqliteCommand security = write.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES ('AAAA', 'A', 'US', 'Common Stock', '2020-01-01')
                """;
            security.ExecuteNonQuery();
        }

        using (SqliteCommand bar = write.CreateCommand())
        {
            // The same session, twice: what the lab knew on the night, and a correction made the
            // following evening. The correction is the row a replay of that night must not see.
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES ('AAAA', '2026-08-24', '10.00', '11.00', '9.00', '10.50', '10.50', 1000, '2026-08-24T22:00:00.000Z'),
                       ('AAAA', '2026-08-24', '10.00', '11.00', '9.00', '99.00', '99.00', 1000, @tomorrow)
                """;
            bar.Parameters.AddWithValue("@tomorrow", StoreText.TimestampToStorageText(tomorrow));
            bar.ExecuteNonQuery();
        }

        using SqliteConnection read = connections.OpenReadOnly();

        StoredDailyBar onTheNight = DailyBarReader.Read(read, "AAAA", asOf, 1)[0];
        StoredDailyBar afterwards = DailyBarReader.Read(read, "AAAA", asOf, 1, tomorrow)[0];

        return (onTheNight.Close == 10.50m, afterwards.Close == 99.00m);
    }

    private static bool Reads(string method) =>
        method is "Read" or "ReadCalibration" or "ReadDate" or "Latest" or "Members"
            or "CurrentMembers" or "ForTicker" or "MarketCap" or "Industry" or "SessionsStored"
            or "Series" or "Open" or "Demands";

    private static bool NotAReader(string file) =>
        !file.Replace(Path.DirectorySeparatorChar, '/')
            .Contains("/PullbackStrategyLab.Data/", StringComparison.Ordinal);

    /// <summary>
    /// Whether a statement selects from this table, rather than merely mentioning its name.
    ///
    /// A word boundary on both sides, because `setup` is a prefix of `setup_signal` and a naive
    /// match would report every read of one as a read of the other.
    /// </summary>
    private static bool SelectsFrom(string statement, string table) =>
        Regex.IsMatch(statement, $@"\bFROM\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(statement, $@"\bJOIN\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every SQL statement in a source file, as the text between a command-text assignment and the
    /// end of its literal.
    ///
    /// Read whole rather than line by line, because the bound a statement puts on its stamp is
    /// usually several lines below the FROM clause and a per-line scan would report every one of
    /// them as unbounded.
    /// </summary>
    private static IEnumerable<string> Statements(string source)
    {
        foreach (Match match in Regex.Matches(
            source,
            """CommandText\s*=\s*(?<raw>"{3}(?<body>.*?)"{3}|"(?<line>(?:\\.|[^"])*)")""",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            string body = match.Groups["body"].Success ? match.Groups["body"].Value : match.Groups["line"].Value;

            if (body.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                yield return body;
            }
        }

        // And the conditional form, where a statement is chosen between two literals. Both arms are
        // statements and a scan that read only the assignment would see neither.
        foreach (Match match in Regex.Matches(
            source,
            """(?<body>"{3}\s*(?:SELECT|INSERT|UPDATE).*?"{3})""",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            if (match.Groups["body"].Value.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                yield return match.Groups["body"].Value;
            }
        }
    }
}
