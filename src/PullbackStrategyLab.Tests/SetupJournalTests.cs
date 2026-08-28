using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// A setup row is immutable after write, except by a correction the lateness bound admits, asserted four ways.
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
/// The columns that are legitimately written after the row exists are named here rather than
/// inferred: SCHEMA declares SetupCapper on `rank` and `capped_out`, LabSetups on `agreement` and
/// `agreement_note`, and CheckRecomputer on `check_results` and the two correction marks. A test
/// that banned every update would ban the ones the corpus allows, and would be deleted the first
/// time it was right about the wrong thing.
///
/// <b>The last of those is the one that needed a rule rather than a declaration</b>, because it is
/// the first later write to a column the detector owns. Immutability exists to stop a plan being
/// improved once its outcome is visible, and a value missing because an input stage died is not
/// that: nothing about the outcome is known and the repair uses only what existed on the night. So
/// the exemption is by file and by column, both checked, and the conditions on the permission
/// itself are behavioural and live in CheckRecomputerTests.
/// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
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

    /// <summary>The columns SCHEMA declares a later writer for, by name and with that writer.</summary>
    private static readonly Dictionary<string, string> WrittenLater = new(StringComparer.Ordinal)
    {
        ["rank"] = "SetupCapper",
        ["capped_out"] = "SetupCapper",
        ["agreement"] = "LabSetups",
        ["agreement_note"] = "LabSetups",
        ["check_results"] = "CheckRecomputer",
        ["corrected_at"] = "CheckRecomputer",
        ["corrected_because"] = "CheckRecomputer",
    };

    /// <summary>
    /// The one file permitted to write a column the detector owns, and the one column it may write.
    ///
    /// Exempted by name rather than by shortening the list above, because the list is the property.
    /// The permission is narrow and the narrowness is what makes it safe: a setup row is corrected
    /// only where the correction uses no information the night did not have, which reaches a
    /// recomputed verdict for a check the baseline records without requiring and nothing else. The
    /// conditions on that permission are behavioural and are asserted in CheckRecomputerTests. What
    /// is asserted here is the boundary: that no other file has the permission, and that this one
    /// does not reach past `check_results` to a price, a size or `passed_all`.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    private const string CorrectionFile = "src/PullbackStrategyLab.Worker/Stages/CheckRecomputer.cs";

    private const string CorrectableColumn = "check_results";

    [Fact]
    public void No_update_against_a_detector_owned_column_exists_in_the_shipped_source()
    {
        var offenders = new List<string>();
        int statements = 0;
        int exempted = 0;

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            string source = RepositoryLayout.Read(file);

            bool correcting = string.Equals(
                RepositoryLayout.Relative(file).Replace('\\', '/'),
                CorrectionFile,
                StringComparison.Ordinal);

            foreach (Match update in UpdateSetup().Matches(source))
            {
                statements++;
                string body = update.Groups["assignments"].Value;

                foreach (string column in DetectorOwned)
                {
                    if (!Regex.IsMatch(body, $@"\b{Regex.Escape(column)}\s*=", RegexOptions.CultureInvariant))
                    {
                        continue;
                    }

                    // The one exemption, checked rather than assumed: the correcting stage may write
                    // the recomputed verdict and may not reach past it to a price, a size or
                    // `passed_all`, which are the plan.
                    if (correcting && string.Equals(column, CorrectableColumn, StringComparison.Ordinal))
                    {
                        exempted++;
                        continue;
                    }

                    offenders.Add($"{RepositoryLayout.Relative(file)} updates setup.{column}");
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

        // And the exemption is exercised, so it is not a permission granted to nothing. An exemption
        // nobody uses reads as a rule with a hole in it rather than as one with a door, and it would
        // survive the correcting stage being deleted.
        Assert.Equal(1, exempted);
    }

    /// <summary>
    /// The columns written after the row exists are the ones SCHEMA declares and no others.
    ///
    /// The other direction of the same property. The test above says nothing may revise what the
    /// detector wrote; this one says the set of exceptions has not grown, which is how the first
    /// test would be defeated without touching it.
    /// </summary>
    [Fact]
    public void Only_the_columns_schema_declares_a_later_writer_for_are_ever_updated()
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
