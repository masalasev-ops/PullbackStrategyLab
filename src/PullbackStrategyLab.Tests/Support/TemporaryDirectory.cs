namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// A directory of its own for one test, removed afterwards. Store tests open real files
/// rather than an in-memory database, because the four pragmas and the write-ahead log are
/// properties of a file and an in-memory store would pass without exercising any of them.
/// </summary>
public sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pullbackstrategylab-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Write(string name, string content) =>
        System.IO.File.WriteAllText(File(name), content);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file handle still open on a temporary directory is not worth failing a test over.
        }
    }
}
