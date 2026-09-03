using System.Globalization;
using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No migration declares a column with REAL affinity.
///
/// CLAUDE.md states the rule in two halves and says why the second half is written down at all:
/// prices are decimal in code <b>and TEXT in storage</b>, because the rule can be satisfied
/// perfectly in code while the column underneath is still <c>REAL</c>. The C# half is visible in
/// review, since a <c>decimal</c> and a <c>double</c> do not silently substitute for one another.
/// The storage half is not visible anywhere: the code goes on handing SQLite a decimal, SQLite
/// goes on rounding it into a float, and every price comes back a few ulps from where it went in.
/// That was the last hard rule from the first day with nothing asserting it, found at the 1.12
/// review after <c>store-portability</c> closed the previous one at 1.11.
///
/// It compounds, which is why it is worth a check rather than a comment. <c>store-portability</c>
/// scans TEXT columns for absolute paths and says in its own source that TEXT is enough because
/// prices are TEXT in this store. A REAL price column would narrow that scan too, and neither
/// check would say anything.
///
/// Affinity rather than the exact word, because SQLite resolves <c>DOUBLE</c>, <c>FLOAT</c> and
/// anything containing REAL, FLOA or DOUB to the same storage class. Banning the literal string
/// REAL would leave <c>DOUBLE PRECISION</c> through, which is the same defect spelled to pass.
/// </summary>
public sealed partial class PriceStorageFormCheck
{
    /// <summary>
    /// Columns allowed REAL affinity, by table and column, each with the reason.
    ///
    /// Stated as a list rather than left implicit so each entry has to be argued for in a diff.
    /// CLAUDE.md's rule has a second clause, that statistics are double, so a genuine statistic
    /// column belongs here with SCHEMA.md declaring it. A price never does.
    ///
    /// It was empty until 4.3, and the first entry is the one the rule's second clause was written
    /// for: the whole table around it is money in TEXT and this one column is not money at all.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Exempt { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["spread_snapshot.spread_bps"] =
            "basis points of the mid, which is a ratio and not a money value. The two prices it is "
            + "computed from are TEXT in the same row, so nothing here crosses the boundary: the "
            + "decimal quantities stay decimal and the derived statistic is a double, which is the "
            + "second clause of the same rule. SCHEMA.md declares it REAL at the column and says why.",
        ["fill.spread_bps"] =
            "the same figure carried onto the fill it was charged on, so a fill says what it paid and "
            + "what that charge was computed from without a join. A ratio and not a money value, on "
            + "exactly the terms the column it is copied from is exempt.",
        ["position.fraction_at_entry"] =
            "the position's value as a fraction of the account, which is a ratio. The value itself is "
            + "TEXT in the same row and the account is a constant, so the decimal quantities stay "
            + "decimal and only the derived fraction is a double.",
        ["position.realised_r"] =
            "a result in R, being money divided by the money that was at risk. The two quantities it "
            + "is computed from are TEXT in the same row. It is the figure the whole lab is scored "
            + "on, and it is a ratio rather than an amount.",
        ["trade.result_r"] =
            "the same result after the borrow a short is charged, on exactly the terms the column it "
            + "is computed from is exempt. Both names stay because they are two numbers: equal on "
            + "every long and different by the borrow line on every short.",
        ["plan_audit.entry_difference_bps"] =
            "how far the entry missed the price its instruction named, as a fraction of that price. "
            + "A ratio and not a money value, and the money it is derived from is TEXT beside it in "
            + "the same row, so nothing here crosses the boundary. It is in basis points because six "
            + "cents on a six-dollar stock and six cents on a four-hundred-dollar one are two "
            + "different execution facts and the column is read across names.",
        ["plan_audit.exit_difference_bps"] =
            "the same figure at the other end of the trade, exempt on the same terms.",
        ["loss_class.result_r"] =
            "the trade's result carried onto the classification it explains, so a loss reads with the "
            + "figure being explained beside the explanation and without a join. A ratio on exactly "
            + "the terms the column it is copied from is exempt.",
        ["plan_audit.give_up_difference_bps"] =
            "the plan's stop against where the trade actually ended, as a fraction of that stop. A "
            + "different question from the two above rather than a third reading of them, and a "
            + "ratio on the same terms.",
    };

    private readonly ITestOutputHelper _output;

    public PriceStorageFormCheck(ITestOutputHelper output) => _output = output;

    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<body>[^;]*)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^\s*(?<column>[A-Za-z_][A-Za-z0-9_]*)\s+(?<type>[A-Za-z][A-Za-z0-9_ ]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ColumnDeclaration();

    /// <summary>
    /// A column added to an existing table, which is the other way a column arrives.
    ///
    /// <b>Parsed from 4.6, having been counted and not read since 3.7.</b> Thirteen columns had
    /// arrived this way and the guard on the storage half of the decimal rule could see none of
    /// them, which made a green here mean "every column declared at table creation" while reading as
    /// "every column". It is the statement form a later phase is most likely to add a money column
    /// with, and 4.6 is the phase where money columns start to matter.
    /// </summary>
    [GeneratedRegex(
        @"ALTER\s+TABLE\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+ADD\s+COLUMN\s+(?<declaration>[^;]*);",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex AddColumn();

    /// <summary>The clauses that open a table constraint rather than a column.</summary>
    private static readonly string[] Constraints =
        ["PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "CONSTRAINT"];

    /// <summary>A line comment, which is stripped before anything is matched.</summary>
    [GeneratedRegex(@"--[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    /// <summary>The keyword itself, counted so a table the parser could not read is visible.</summary>
    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableKeyword();

    /// <summary>
    /// The stored columns that hold a ratio rather than a price, each with the SCHEMA row that says
    /// so. A read of one of these through the price crossing is the other half of the decimal rule
    /// going quietly wrong: the value is identical, the name at the point of use is not, and the
    /// name is what stops a percentage being written where a fraction was meant.
    ///
    /// Hand-named rather than parsed from SCHEMA, because SCHEMA says "fraction" in a note on some
    /// rows and "in ATR" or "signed by direction" on others, and a list that has to be argued for
    /// in a diff is the shape every other exemption list in this file takes.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RatioColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["return_signed"] = "forward_return: a return, signed by direction, as a fraction of the close",
        ["mfe_atr"] = "forward_return: the best point reached, in ATR",
        ["mae_atr"] = "forward_return: the worst point reached, in ATR",
        ["bound"] = "ceiling_bound: the fraction a system with perfect foresight could have won",
        ["achieved"] = "ceiling_bound: the fraction actually won over the same rows",
        ["adr_20"] = "indicator_daily: the average daily range as a fraction, 0.068 not 6.8",
        ["stop_distance_ranges"] = "setup: the give-up distance in daily ranges",
        ["trigger_distance_ranges"] = "setup: the trigger distance in daily ranges",
        ["magnitude"] = "scan_hit: the ratio the rank was taken on",
        ["forward_return_signed"] = "loss_class: the ten-session return from the trigger, a fraction of it",
        ["one_r_in_return"] = "loss_class: one unit of risk as a fraction of the trigger",
        ["exit_return_signed"] = "loss_class: the return to the exit, a fraction of the trigger",
    };

    /// <summary>
    /// Every read of a ratio column in the shipped source goes through the ratio crossing.
    ///
    /// <b>The row raised at 3.5, made a scan rather than a repair.</b> `ScoreboardBuilder` read
    /// `return_signed`, `bound` and `achieved` through `StorageTextToPrice`, and `CeilingCalculator`
    /// read `return_signed`, `mae_atr`, `adr_20` and `stop_distance_ranges` the same way, for as long
    /// as those readers existed. The values are identical either way, so nothing was wrong and
    /// nothing could have said so; what was broken was the convention the decimal rule rests on,
    /// which is that a crossing is named for what it carries. Repaired at 5.8 and asserted here, so
    /// the next reader written the same way fails on the day it is written.
    ///
    /// A read is mapped to its column by position: the nearest preceding SELECT list in the same
    /// file, split at its top-level commas, at the ordinal the reader asks for. That is how the
    /// readers in this codebase are written, and the proof test exercises the mapping.
    /// </summary>
    [Fact]
    [Trait("check", "price-storage-form")]
    public void Every_ratio_column_is_read_through_the_ratio_crossing()
    {
        string[] files =
        [
            .. new[] { "PullbackStrategyLab.Worker", "PullbackStrategyLab.Api", "PullbackStrategyLab.Data", "PullbackStrategyLab.Web" }
                .SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Source, p), "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        var offences = new List<string>();
        int reads = 0;

        foreach (string file in files)
        {
            (int found, IReadOnlyList<string> wrong) = RatioReadsThroughThePriceCrossing(File.ReadAllText(file), RatioColumns.Keys);
            reads += found;
            offences.AddRange(wrong.Select(w => $"{RepositoryLayout.Relative(file)}: {w}"));
        }

        // Stated in advance: the shipped source reads well over fifty columns through the price
        // crossing, so a parser that stopped matching would find no offences and no reads.
        Assert.True(reads >= 50,
            $"only {reads} read(s) through the price crossing were found across the shipped source, so the "
            + "parser stopped matching rather than the reads going away.");

        Assert.True(offences.Count == 0,
            $"{offences.Count} ratio column(s) are read through the price crossing:\n  "
            + string.Join("\n  ", offences)
            + "\n  Read each through StoreText.StorageTextToRatio, which is named for what it carries.");
    }

    /// <summary>
    /// The reads through the price crossing in one source text, and those of them whose column is
    /// one of <paramref name="ratioColumns"/>, mapped by position against the nearest preceding
    /// SELECT list.
    /// </summary>
    public static (int Reads, IReadOnlyList<string> Offences) RatioReadsThroughThePriceCrossing(
        string source, IEnumerable<string> ratioColumns)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ratioColumns);

        HashSet<string> ratios = [.. ratioColumns];
        var offences = new List<string>();
        int reads = 0;

        foreach (Match read in PriceCrossingRead().Matches(source))
        {
            reads++;
            int ordinal = int.Parse(read.Groups["ordinal"].Value, CultureInfo.InvariantCulture);

            Match select = SelectList().Matches(source[..read.Index]).Cast<Match>().LastOrDefault()
                ?? Match.Empty;

            if (!select.Success)
            {
                continue;
            }

            IReadOnlyList<string> columns = SplitColumns(select.Groups["list"].Value);

            if (ordinal >= columns.Count)
            {
                continue;
            }

            string column = ColumnName(columns[ordinal]);

            if (ratios.Contains(column))
            {
                offences.Add($"{column} at ordinal {ordinal} of the SELECT before line "
                    + $"{source[..read.Index].Count(c => c == '\n') + 1} is read through StorageTextToPrice");
            }
        }

        return (reads, offences);
    }

    /// <summary>A SELECT list split at its top-level commas, so a function call inside one column stays one column.</summary>
    private static IReadOnlyList<string> SplitColumns(string list)
    {
        var columns = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    columns.Add(list[start..i]);
                    start = i + 1;
                    break;
            }
        }

        columns.Add(list[start..]);
        return columns;
    }

    /// <summary>
    /// The name a column reads back as: its alias where it has one, otherwise the last identifier
    /// in the expression with any table prefix removed.
    /// </summary>
    private static string ColumnName(string expression)
    {
        string trimmed = expression.Trim();
        Match alias = Regex.Match(trimmed, @"\sAS\s+(?<name>\w+)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (alias.Success)
        {
            return alias.Groups["name"].Value;
        }

        MatchCollection identifiers = Regex.Matches(trimmed, @"\b(?<name>[A-Za-z_]\w*)\b", RegexOptions.CultureInvariant);
        return identifiers.Count == 0 ? trimmed : identifiers[^1].Groups["name"].Value;
    }

    [GeneratedRegex(@"StorageTextToPrice\(\s*reader\.GetString\(\s*(?<ordinal>\d+)\s*\)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex PriceCrossingRead();

    [GeneratedRegex(@"\bSELECT\s+(?<list>.*?)\s+FROM\b", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SelectList();

    [Fact]
    [Trait("check", "price-storage-form")]
    public void No_migration_declares_a_column_with_real_affinity()
    {
        var coverage = new CheckCoverage("price-storage-form", _output);

        string migrations = Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations");
        string[] files = [.. Directory.EnumerateFiles(migrations, "*.sql").Order(StringComparer.Ordinal)];

        var offenders = new List<string>();
        var unread = new List<string>();
        int tables = 0;
        int columns = 0;

        foreach (string file in files)
        {
            // Comments are stripped before anything is matched, and that is not tidiness.
            // `CreateTable` bounds a table body with `[^;]*`, so one semicolon inside a comment in
            // the middle of a column list makes the whole table unmatchable: the pattern needs a
            // closing paren immediately before a semicolon and there is none before the comment's,
            // so the engine gives up on that table and moves to the next. It found this file's next
            // table, reported one offender instead of three, and stayed green on the two it never
            // read. Found at 4.7 by a `position` table whose comment said "says why; a filled one".
            string sql = LineComment().Replace(RepositoryLayout.Read(file), string.Empty);

            // Every table the keyword appears for, so a table the body pattern cannot read is a
            // failure rather than a silence. The count is what makes this check's own scope
            // assertable: a parser that stops matching narrows to nothing and says nothing.
            var declared = new List<string>();

            foreach (Match keyword in CreateTableKeyword().Matches(sql))
            {
                declared.Add(keyword.Groups["table"].Value);
            }

            var read = new List<string>();

            foreach (Match table in CreateTable().Matches(sql))
            {
                read.Add(table.Groups["table"].Value);
                tables++;
                string name = table.Groups["table"].Value;

                foreach (string line in table.Groups["body"].Value.Split('\n'))
                {
                    string trimmed = line.Trim().TrimEnd(',');
                    if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Constraints.Any(c => trimmed.StartsWith(c, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    Match declaration = ColumnDeclaration().Match(trimmed);
                    if (!declaration.Success)
                    {
                        continue;
                    }

                    columns++;
                    string column = declaration.Groups["column"].Value;
                    string type = declaration.Groups["type"].Value.Trim();

                    if (!HasRealAffinity(type))
                    {
                        continue;
                    }

                    string key = $"{name}.{column}";
                    if (Exempt.ContainsKey(key))
                    {
                        continue;
                    }

                    offenders.Add($"{RepositoryLayout.Relative(file)}: {key} is declared {type}, which has REAL affinity.");
                }
            }

            foreach (string missed in declared.Except(read, StringComparer.Ordinal))
            {
                unread.Add($"{RepositoryLayout.Relative(file)}: {missed}");
            }
        }

        // The other way a column arrives, read rather than counted from 4.6. It was counted and
        // deferred from 3.7, so a green here meant "every column declared at table creation" while
        // reading as "every column", and thirteen columns sat outside it.
        int addedLater = 0;

        foreach (string file in files)
        {
            foreach (Match added in AddColumn().Matches(RepositoryLayout.Read(file)))
            {
                addedLater++;
                columns++;

                string name = added.Groups["table"].Value;
                Match declaration = ColumnDeclaration().Match(added.Groups["declaration"].Value.Trim());

                if (!declaration.Success)
                {
                    offenders.Add(
                        $"{RepositoryLayout.Relative(file)}: an ALTER TABLE {name} ADD COLUMN declares no readable "
                        + "column and type, so its affinity was not checked rather than found correct.");
                    continue;
                }

                string column = declaration.Groups["column"].Value;
                string type = declaration.Groups["type"].Value.Trim();

                if (!HasRealAffinity(type) || Exempt.ContainsKey($"{name}.{column}"))
                {
                    continue;
                }

                offenders.Add(
                    $"{RepositoryLayout.Relative(file)}: {name}.{column} is added as {type}, which has REAL affinity.");
            }
        }

        coverage
            .Context("migration files read", files.Length)
            .Examined("tables declared across them", tables)
            .Examined("column declarations checked for REAL affinity", columns)
            .Examined("of those added by ALTER TABLE rather than declared at creation", addedLater)
            .Scan(
                "every ratio column a shipped statement selects is read back through the ratio crossing and not the price one",
                CheckCoverage.Backing.None(
                    "the two crossings parse the same text to the same decimal, so no behaviour differs when a "
                    + "ratio goes through the price one and no behavioural test can tell them apart. What the "
                    + "convention buys is the name at the point of use, which is what stops 6.8 being written "
                    + "where 0.068 was meant, and a name is only assertable by reading the source. The scan's "
                    + "own parsing is proved by CheckProofTests.The_crossing_scanner_maps_a_read_to_the_column_it_selects"));

        if (Exempt.Count > 0)
        {
            coverage.OutOfScope("columns exempted by name", Exempt.Count,
                CheckCoverage.OutOfScopeReason.ByDesign(
                    "each is exempt for a stated reason rather than pending anything: "
                    + string.Join("; ", Exempt.Select(e => $"{e.Key}: {e.Value}"))));
        }

        coverage.Report();

        // Before the offenders, because a table nobody read produces no offender and a green over it
        // is exactly the shape this corpus keeps finding. Reconciled per file against the keyword
        // rather than against a number, so the message names the table.
        Assert.True(unread.Count == 0,
            $"{unread.Count} table(s) appear in a migration and could not be read by this check, so no "
            + "column of theirs was examined and every one of them passed by not being looked at:\n  "
            + string.Join("\n  ", unread));

        // Stated in advance. A regex that stopped matching would find no columns and pass, which
        // is the shape of failure this whole set of checks exists to refuse.
        Assert.True(files.Length >= 10,
            $"Found {files.Length} migration files. There have been at least ten since 1.9, so the scan is looking in the wrong place.");
        Assert.True(columns >= 60,
            $"Parsed {columns} column declarations across {tables} tables. There have been at least sixty since 1.9, "
            + "so the parser stopped matching and this check examined almost nothing.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} column(s) are declared with REAL affinity:\n  "
            + string.Join("\n  ", offenders)
            + "\n  Prices and money are TEXT holding a decimal. A statistic that is genuinely a double belongs in "
            + $"{nameof(PriceStorageFormCheck)}.{nameof(Exempt)} with its reason, and declared as one in SCHEMA.md.");
    }

    /// <summary>
    /// SQLite's own rule: a declared type containing REAL, FLOA or DOUB gets REAL affinity,
    /// whatever else it says.
    /// </summary>
    private static bool HasRealAffinity(string declaredType) =>
        declaredType.Contains("REAL", StringComparison.OrdinalIgnoreCase)
        || declaredType.Contains("FLOA", StringComparison.OrdinalIgnoreCase)
        || declaredType.Contains("DOUB", StringComparison.OrdinalIgnoreCase);
}
