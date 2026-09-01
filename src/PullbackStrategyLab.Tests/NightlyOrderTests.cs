using System.Text.RegularExpressions;
using PullbackStrategyLab.Tests.Support;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The replay runs the stages in the order RUNBOOK schedules them, and the two are checked against
/// each other rather than kept in step by hand.
///
/// <b>The sequence is itself under test, so a replay in a convenient order proves nothing.</b> An
/// action observed tonight has to block the averages until a refetch made after that observation has
/// landed; a scan hit has to exist before a detector can call it a thrust; a sector has to be
/// resolved before the stages that read it run. Every one of those is a property of the order, and a
/// replay free to choose its own order can hold all of them while a live night holds none.
///
/// It was not hypothetical. RUNBOOK ran `sectors` at 19:00 while `clusters` at 18:15 and both
/// detectors at 18:20 read what it writes, and this replay ran it before all three, so the fixture
/// could never have shown it. Neither consumer errors on a missing sector: the cluster count reads
/// nought and the short side's `tradable-shortable` fails for want of a market capitalisation. Both
/// look like ordinary quiet nights.
///
/// A subsequence rather than an equality, because the replay does not run every nightly stage: it
/// has no spread snapshots, no minute bars and no trading. What it may not do is run two stages in
/// the opposite order from the schedule an operator follows.
/// </summary>
public sealed class NightlyOrderTests
{
    /// <summary>Stages the replay runs that the nightly table does not schedule, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> NotNightly =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["universe-build"] = "the universe build is weekly rather than nightly",
            ["universe-build (floor lifted)"] = "the replay's second screen, which exists only here",
            ["backfill"] = "the one-time history seed, which the replay runs in front of the night",
            ["ceiling"] = "the win-rate bound is recomputed weekly rather than nightly, so RUNBOOK "
                + "schedules it under Every week and the nightly table correctly does not name it",
        };

    /// <summary>
    /// Stages whose place in a day's clock and place in a night's pipeline are different, with the
    /// reason. Their presence in the schedule is still asserted; only the ordering is exempt.
    ///
    /// <b>A separate list from <see cref="NotNightly"/>, deliberately.</b> That one holds stages a
    /// night does not run, and putting an ordering exemption in it would say something false about
    /// the schedule to buy a green run.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OrderedByTheSessionOffset =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spreads"] =
                "it runs inside the session, at 10:15 and 15:45, so on a day's clock it comes before every "
                + "evening stage in this table. What it samples is the names capped on the evening before, so "
                + "in one night's pipeline it comes after the cap it reads. Both are true and they are "
                + "statements about different days: the replay runs one logical night and RUNBOOK schedules "
                + "one wall clock. see: Minute bars are fetched for the session a plan was live in, never the "
                + "session it was written on",
        };

    [Fact]
    public void The_replay_runs_the_stages_in_the_order_the_runbook_schedules_them()
    {
        IReadOnlyList<string> scheduled = NightlySchedule();

        // Stated in advance. The table is parsed out of markdown, and a parser that stopped matching
        // would hand this test an empty schedule to assert a subsequence of, which anything is.
        Assert.True(scheduled.Count >= 10,
            $"Only {scheduled.Count} verb(s) parsed out of RUNBOOK's nightly table. A schedule this short means the "
            + "parser stopped matching rather than that the night got shorter.");

        using var replay = new PhaseReplay(RepositoryLayout.Fixtures);
        PhaseReplayResult result = replay.Run();

        string[] ran = [.. result.Stages.Select(s => s.Stage)];
        var problems = new List<string>();
        int position = -1;
        string? previous = null;

        foreach (string stage in ran)
        {
            if (NotNightly.ContainsKey(stage))
            {
                continue;
            }

            int at = scheduled.ToList().IndexOf(stage);

            if (at < 0)
            {
                problems.Add(
                    $"the replay runs \"{stage}\" and RUNBOOK's nightly table does not schedule it. Either the night "
                    + "gained a stage nobody wrote down, or this replay runs something a live night does not.");
                continue;
            }

            if (at < position && !OrderedByTheSessionOffset.ContainsKey(stage))
            {
                problems.Add(
                    $"the replay runs \"{stage}\" after \"{previous}\" and RUNBOOK schedules it before. A stage that "
                    + "reads what a later stage writes gets nothing, quietly.");
            }

            if (!OrderedByTheSessionOffset.ContainsKey(stage))
            {
                position = at;
                previous = stage;
            }
        }

        Assert.True(problems.Count == 0,
            $"{problems.Count} disagreement(s) between the replay and RUNBOOK's nightly order:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The verbs RUNBOOK's nightly table names, in schedule order.
    ///
    /// A row can name more than one verb, as "`clusters`, then `regime`" does, and the order inside
    /// the row is part of the schedule.
    /// </summary>
    private static IReadOnlyList<string> NightlySchedule()
    {
        string runbook = RepositoryLayout.Read(Path.Combine(RepositoryLayout.Docs, "RUNBOOK.md"));
        IReadOnlyList<IReadOnlyList<string>> rows =
            MarkdownTable.BodyRowsAfter(runbook, "The nightly job is one CLI entrypoint per stage");

        var verbs = new List<string>();

        foreach (IReadOnlyList<string> row in rows)
        {
            if (row.Count < 2)
            {
                continue;
            }

            foreach (Match match in Regex.Matches(row[1], "`([^`]+)`", RegexOptions.CultureInvariant))
            {
                string verb = match.Groups[1].Value;
                if (!verbs.Contains(verb, StringComparer.Ordinal))
                {
                    verbs.Add(verb);
                }
            }
        }

        return verbs;
    }
}
