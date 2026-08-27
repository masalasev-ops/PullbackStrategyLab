using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A setup row is immutable after write, asserted four ways.
///
/// <b>Four rather than one, because each covers a different way the property dies.</b> The pattern
/// is 2.2's, for the frozen signal row, and it is repeated here because the subject is the same
/// shape: a row written once by one component and read for months by everything else.
///
/// A rerun writes nothing, so a night run twice does not double or revise. The store's own key
/// refuses a second write, so the property does not rest on the detector remembering to check. No
/// `UPDATE` against a detector-owned column exists in the shipped source, so a component added later
/// cannot quietly acquire the ability. And the journal notices at runtime what CI cannot see, being
/// a column written out of order on a live night.
///
/// The three columns that are legitimately written after the row exists are named here rather than
/// inferred: SCHEMA declares SetupCapper on `rank` and `capped_out` and LabSetups on `agreement` and
/// `agreement_note`, and those are the only ones. A test that banned every update would ban the two
/// the corpus allows, and would be deleted the first time it was right about the wrong thing.
/// </summary>
public sealed partial class SetupJournalTests
{
    /// <summary>
    /// The columns the detectors own, which nothing may revise after the row is written.
    ///
    /// Stated as a list rather than as "everything except the four" so that a column added later is
    /// not silently covered by an exemption written before it existed.
    /// </summary>
    private static readonly string[] DetectorOwned =
    [
        "setup_id", "as_of", "ticker", "direction", "check_results", "passed_all",
        "trigger_price", "stop_price", "stop_distance_ranges", "thrust_scan", "thrust_session",
    ];

    /// <summary>The four columns SCHEMA declares a later writer for, by name and with that writer.</summary>
    private static readonly Dictionary<string, string> WrittenLater = new(StringComparer.Ordinal)
    {
        ["rank"] = "SetupCapper",
        ["capped_out"] = "SetupCapper",
        ["agreement"] = "LabSetups",
        ["agreement_note"] = "LabSetups",
    };

    [Fact]
    public void No_update_against_a_detector_owned_column_exists_in_the_shipped_source()
    {
        var offenders = new List<string>();
        int statements = 0;

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            string source = RepositoryLayout.Read(file);

            foreach (Match update in UpdateSetup().Matches(source))
            {
                statements++;
                string body = update.Groups["assignments"].Value;

                foreach (string column in DetectorOwned)
                {
                    if (Regex.IsMatch(body, $@"\b{Regex.Escape(column)}\s*=", RegexOptions.CultureInvariant))
                    {
                        offenders.Add($"{RepositoryLayout.Relative(file)} updates setup.{column}");
                    }
                }
            }
        }

        // Stated in advance. A pattern that stopped matching would find no statements and no
        // offenders, and "no offenders" would read exactly like the property holding.
        Assert.True(statements >= 2,
            $"Only {statements} UPDATE statement(s) against `setup` were found in the shipped source. "
            + "The capper writes two columns and the read surface writes two more, so a count below "
            + "two means the pattern stopped matching rather than that the updates went away.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} update(s) touch a column the detector owns:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The columns written after the row exists are the four SCHEMA declares and no others.
    ///
    /// The other direction of the same property. The test above says nothing may revise what the
    /// detector wrote; this one says the set of exceptions has not grown, which is how the first
    /// test would be defeated without touching it.
    /// </summary>
    [Fact]
    public void Only_the_four_columns_schema_declares_a_later_writer_for_are_ever_updated()
    {
        var written = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            foreach (Match update in UpdateSetup().Matches(RepositoryLayout.Read(file)))
            {
                foreach (Match assignment in Assignment().Matches(update.Groups["assignments"].Value))
                {
                    written.Add(assignment.Groups["column"].Value);
                }
            }
        }

        string[] undeclared = [.. written.Where(c => !WrittenLater.ContainsKey(c)).Order(StringComparer.Ordinal)];

        Assert.True(undeclared.Length == 0,
            $"{undeclared.Length} column(s) of `setup` are updated and SCHEMA declares no later writer "
            + $"for them:\n  {string.Join("\n  ", undeclared)}\n"
            + "Either the update is a defect, or SCHEMA is owed a declaration and this list is owed "
            + "the name of the component that makes it.");

        Assert.True(written.Count > 0,
            "No updated columns were found at all, which means the assignment pattern stopped "
            + "matching and this test is asserting over an empty set.");
    }

    // The SET clause alone, from `UPDATE setup ... SET` to the `WHERE` that ends it.
    //
    // Bounded on WHERE rather than on the end of the statement, and the bound is the whole
    // correctness of this test. Every one of these updates keys on `WHERE setup_id = @setup_id`, so
    // a pattern that swallowed the predicate reports `setup_id` as a column both writers write,
    // which it is not: it is the column they match on. Both tests said exactly that on their first
    // run, which is what a pattern this shape does when nobody looks at what it caught.
    [GeneratedRegex(
        @"UPDATE\s+setup\b[^;]*?\bSET\b(?<assignments>.*?)(?=\bWHERE\b|""""""|;)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex UpdateSetup();

    // A column being assigned a parameter, anchored on a word boundary so the `set` inside
    // `setup_id` cannot start a match and hand back `up_id` as a column name. It did.
    [GeneratedRegex(@"(?<column>\b[a-z_]+\b)\s*=\s*@", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();
}
