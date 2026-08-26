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
public sealed class PullbackStrategyLabPaths
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

    /// <summary>A snapshot named for the instant it was taken, in a form that sorts chronologically.</summary>
    public string SnapshotFile(DateTimeOffset takenAt) =>
        Path.Combine(SnapshotDirectory, $"pullbackstrategylab-{takenAt.UtcDateTime:yyyyMMdd-HHmmss}.db");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(SnapshotDirectory);
    }
}
