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
    /// Empty today, and stated as a list rather than left implicit so the first entry has to be
    /// argued for in a diff. CLAUDE.md's rule has a second clause, that statistics are double, so
    /// a genuine statistic column belongs here with SCHEMA.md declaring it. A price never does.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Exempt { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
    };

    private readonly ITestOutputHelper _output;

    public PriceStorageFormCheck(ITestOutputHelper output) => _output = output;

    [GeneratedRegex(@"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<body>[^;]*)\)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^\s*(?<column>[A-Za-z_][A-Za-z0-9_]*)\s+(?<type>[A-Za-z][A-Za-z0-9_ ]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ColumnDeclaration();

    /// <summary>The clauses that open a table constraint rather than a column.</summary>
    private static readonly string[] Constraints =
        ["PRIMARY", "FOREIGN", "UNIQUE", "CHECK", "CONSTRAINT"];

    [Fact]
    [Trait("check", "price-storage-form")]
    public void No_migration_declares_a_column_with_real_affinity()
    {
        var coverage = new CheckCoverage("price-storage-form", _output);

        string migrations = Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations");
        string[] files = [.. Directory.EnumerateFiles(migrations, "*.sql").Order(StringComparer.Ordinal)];

        var offenders = new List<string>();
        int tables = 0;
        int columns = 0;

        foreach (string file in files)
        {
            string sql = RepositoryLayout.Read(file);

            foreach (Match table in CreateTable().Matches(sql))
            {
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
        }

        coverage
            .Context("migration files read", files.Length)
            .Examined("tables declared across them", tables)
            .Examined("column declarations checked for REAL affinity", columns)
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
