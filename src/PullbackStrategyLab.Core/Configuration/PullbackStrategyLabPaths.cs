using System.Globalization;
using System.Text.RegularExpressions;

namespace PullbackStrategyLab.Core.Configuration;

/// <summary>
/// Every path the lab composes, built from one configured data root through the platform
/// API. No drive letters, no backslash separators, and nothing here is ever written into
/// a database row.
///
/// The names under the root are lowercase. That is the runtime half of the case rule: the
/// data root and everything under it, the fixture, artifacts/ and tools/ are lowercase,
/// while .NET source and project directories keep the framework's PascalCase.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public sealed partial class PullbackStrategyLabPaths
{
    public const string StoreFileName = "pullbackstrategylab.db";
    public const string SnapshotDirectoryName = "snapshots";

    public PullbackStrategyLabPaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string StoreFile => Path.Combine(DataRoot, StoreFileName);

    public string SnapshotDirectory => Path.Combine(DataRoot, SnapshotDirectoryName);

    /// <summary>
    /// Where <c>tools/nightly.ps1</c> writes each night's log, which is the one record of a slot the
    /// worker never started. The script composes the same name from the same root, and
    /// <c>slot-roster</c> holds the two to one spelling.
    /// </summary>
    public const string LogDirectoryName = "logs";

    /// <summary>
    /// The file whose presence pauses every slot. The operator creates it, the script reads it
    /// before the tree guard, and a paused slot leaves a line in the night's log saying so.
    /// </summary>
    public const string PauseFileName = "paused";

    public string LogDirectory => Path.Combine(DataRoot, LogDirectoryName);

    /// <summary>One night's log, named for the session the way the script names it.</summary>
    public string NightLogFile(DateOnly session) =>
        Path.Combine(LogDirectory, "nightly-" + session.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    public string PauseFile => Path.Combine(DataRoot, PauseFileName);

    /// <summary>A snapshot named for the instant it was taken, in a form that sorts chronologically.</summary>
    public string SnapshotFile(DateTimeOffset takenAt) =>
        Path.Combine(SnapshotDirectory, $"pullbackstrategylab-{takenAt.UtcDateTime:yyyyMMdd-HHmmss}.db");

    /// <summary>
    /// Every snapshot in the directory, oldest first.
    ///
    /// Matched against the name this class generates rather than against <c>*.db</c>, and that is
    /// what makes the escape hatch work: retention only ever deletes files it could have written,
    /// so a snapshot renamed to anything else, <c>before-the-4.1-migration.db</c> say, is kept for
    /// as long as the operator wants it and is invisible to the policy. Named here rather than in
    /// the stage that prunes, because the class that composes the name is the one that can say
    /// which names are its own.
    /// </summary>
    public IReadOnlyList<string> SnapshotFiles()
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(SnapshotDirectory, "*.db")
                .Where(f => GeneratedSnapshotName().IsMatch(Path.GetFileName(f)))
                // The name carries the instant in a form that sorts chronologically, so ordinal
                // order is age order. Sorting on a file timestamp instead would reorder the set
                // whenever a copy or a restore touched one.
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The shape <see cref="SnapshotFile"/> produces, and nothing else.</summary>
    [GeneratedRegex(@"^pullbackstrategylab-\d{8}-\d{6}\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedSnapshotName();

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SnapshotDirectory);
    }
}
