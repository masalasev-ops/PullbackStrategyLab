using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// A detector run through the entry point against a store one migration short, and what came back.
///
/// <b>One subject, two readers.</b> <c>StoreVersionGuardTests</c> asserts this and the
/// architecture-conformance claim for "The store is at a schema version other than the build's"
/// asks the same question of it. The claim used to read <c>Program.cs</c> for four patterns, and
/// all four are satisfied inside <c>WhyThisStageCannotRun</c> and <c>WhyTheStoreCannotBeRead</c>
/// themselves: deleting the dispatch block that calls the guard left the claim green and a detector
/// free to run against a store two migrations behind it. The done condition asked for a store stood
/// up one migration short with a detector run against it, and nothing did that until 3.12.
/// see: Every phase ends in a generated phase report, not in a page somebody looks at
/// </summary>
public static class StoreVersionRefusal
{
    /// <summary>The stage the guard is asked about. A detector, because a detector is what died.</summary>
    public const string Stage = LongSetupDetector.Name;

    /// <summary>The session the run is asked for, so the arguments are a real night's arguments.</summary>
    public const string AsOf = "2026-08-28";

    /// <summary>
    /// What the run produced, and what the store held afterwards.
    ///
    /// <paramref name="RunRows"/> is the half that says <em>before</em>: a detector that reached its
    /// own code opens the store for writing and <c>RunLogger.Begin</c> puts a row in <c>run_log</c>
    /// before it reads anything. Nought rows is the refusal having happened ahead of the stage, and
    /// it is what turns red when the call site alone is deleted.
    /// </summary>
    public sealed record Outcome(int ExitCode, string Error, int RunRows, int Found, int Needed);

    /// <summary>
    /// Stands a store up at one migration short of this build, runs the detector through the CLI
    /// against it, and reads back what the store holds.
    /// </summary>
    public static Outcome OverAStoreOneMigrationShort()
    {
        using var root = new TemporaryDirectory();

        int needed = MigrationRunner.LatestVersion;
        int found = needed - 1;

        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        using (SqliteConnection connection = connections.OpenWrite())
        {
            new MigrationRunner(connections).Apply(connection, found);
        }

        WorkerCli.Result result = WorkerCli.Run(root.Path, Stage, AsOf);

        int runRows;
        using (SqliteConnection connection = connections.OpenReadOnly())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM run_log;";
            runRows = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Outcome(result.ExitCode, result.Error, runRows, found, needed);
    }

    /// <summary>
    /// Whether that run is the refusal the corpus describes, as one verdict for the claim to carry.
    ///
    /// The message is read for both numbers rather than for a phrase, because "the store is behind"
    /// is a sentence an operator cannot act on. <c>no such column</c> is refused explicitly: it is
    /// the error the live store gave on 2026-08-28 and it is what the guard is here to come before.
    /// </summary>
    public static bool IsTheRefusal(Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.ExitCode == 1
            && outcome.RunRows == 0
            && outcome.Error.Contains($"{Stage}:", StringComparison.Ordinal)
            && outcome.Error.Contains(
                outcome.Found.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && outcome.Error.Contains(
                outcome.Needed.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && outcome.Error.Contains("tools/migrate", StringComparison.Ordinal)
            && !outcome.Error.Contains("no such column", StringComparison.Ordinal);
    }
}
