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
            .NoSourceScan(
                "the migration text is the declaration itself. The store is built by executing exactly these "
                + "statements, so a column's affinity cannot differ from what the statement says, and removing "
                + "the declaration removes the column");

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
