using System.Diagnostics;
using PullbackStrategyLab.Core.Configuration;

namespace PullbackStrategyLab.Tests.Support;

/// <summary>
/// The Worker run the way the scheduler runs it: a process, one stage name, its own data root.
///
/// <b>Why a process rather than a call.</b> The guard the store-version claim is about lives in
/// <c>Main</c>, between the host being built and the stage being dispatched. A test that calls the
/// stage's own method, or the guard's own method, passes with that dispatch block deleted, which is
/// the shape of assertion-outliving-its-subject this corpus has now shipped five times. The only
/// subject that carries the claim is the entry point, and reaching it means running it.
///
/// The data root arrives as an environment variable on the child alone rather than through
/// <see cref="Environment.SetEnvironmentVariable(string,string)"/>, because that one is
/// process-wide and the suite runs test classes in parallel. The child's configuration order puts
/// environment variables last, so this wins over the <c>appsettings.json</c> beside the binary
/// (see: Secrets live in a gitignored appsettings.Secrets.json, registered before environment variables).
/// </summary>
public static class WorkerCli
{
    /// <summary>
    /// The key an environment variable uses for the data root, in the double-underscore form the
    /// configuration provider maps onto a section.
    /// </summary>
    public const string DataRootVariable = $"{PullbackStrategyLabOptions.SectionName}__DataRoot";

    /// <summary>The Worker's project directory, which is also its output directory's name.</summary>
    public const string WorkerProjectDirectoryName = "PullbackStrategyLab.Worker";

    /// <summary>The managed assembly the entry point is in.</summary>
    public const string WorkerAssemblyFileName = $"{WorkerProjectDirectoryName}.dll";

    /// <summary>What a run of the Worker produced. Both streams, because the refusal is on stderr.</summary>
    public sealed record Result(int ExitCode, string Out, string Error);

    /// <summary>
    /// Runs one stage against <paramref name="dataRoot"/> and waits for it.
    ///
    /// Invoked through the muxer with the managed assembly rather than through the apphost, because
    /// the apphost is <c>.exe</c> on one of the two platforms and has no extension on the other.
    /// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
    /// </summary>
    public static Result Run(string dataRoot, params string[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(args);

        string worker = WorkerAssembly();

        var start = new ProcessStartInfo(Muxer())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // Not the content root: the Worker sets its own from AppContext.BaseDirectory, exactly
            // so a scheduled task's working directory cannot decide which appsettings.json it reads.
            WorkingDirectory = Path.GetDirectoryName(worker)!,
        };

        start.ArgumentList.Add(worker);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[DataRootVariable] = dataRoot;

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"{Muxer()} did not start.");

        // Read before waiting. A stage that fills a pipe buffer while nothing drains it deadlocks,
        // and the store-version refusal is short only until somebody lengthens the message.
        Task<string> standardOut = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new Result(
            process.ExitCode,
            standardOut.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    /// <summary>
    /// The Worker assembly built alongside this test assembly.
    ///
    /// <b>Its own output directory rather than the copy beside the test binary.</b> The test project
    /// references the Worker, so <c>PullbackStrategyLab.Worker.dll</c> is copied next to the test
    /// binary along with its <c>deps.json</c>, and running that copy fails at the first line of
    /// <c>Main</c> on a missing <c>Microsoft.Extensions.Hosting</c>: the test project resolves that
    /// assembly through the ASP.NET Core shared framework and does not copy it, and the Worker,
    /// which is not an ASP.NET Core application, expects it beside itself.
    ///
    /// The tail is taken from this assembly's own path rather than written down, so the
    /// configuration and the target framework are whichever ones built the test. A Release run
    /// reaching into a Debug build would be a test asserting something about a binary nobody asked
    /// for, and it would do it silently.
    /// </summary>
    public static string WorkerAssembly()
    {
        // <solution>/src/PullbackStrategyLab.Tests/bin/<configuration>/<framework>
        var framework = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? configuration = framework.Parent;
        DirectoryInfo? bin = configuration?.Parent;
        DirectoryInfo? source = bin?.Parent?.Parent;

        if (bin is null || source is null || !string.Equals(bin.Name, "bin", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The test assembly at {AppContext.BaseDirectory} is not laid out as "
                + "<source>/<project>/bin/<configuration>/<framework>, so the Worker's output "
                + "directory cannot be composed from it.");
        }

        string worker = Path.Combine(
            source.FullName, WorkerProjectDirectoryName, bin.Name,
            configuration!.Name, framework.Name, WorkerAssemblyFileName);

        if (!File.Exists(worker))
        {
            throw new InvalidOperationException(
                $"{worker} does not exist. The test project references the Worker, so it is built "
                + "whenever the suite is; a missing assembly means the reference has gone rather "
                + "than that this run may be skipped.");
        }

        return worker;
    }

    /// <summary>
    /// The <c>dotnet</c> that is running this test, when the build told us, and the one on the path
    /// otherwise. MSBuild and <c>dotnet test</c> both set the variable; a bare runner may not.
    /// </summary>
    private static string Muxer()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(host) ? "dotnet" : host;
    }
}
