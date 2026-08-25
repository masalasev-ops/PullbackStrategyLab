using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Worker.Stages;
using PullbackStrategyLab.Worker.Vendor;

namespace PullbackStrategyLab.Worker;

/// <summary>
/// One CLI entrypoint per job, invoked by Task Scheduler on Windows or launchd on macOS.
/// The application holds no timer logic and no scheduling of its own, which is what makes
/// a failed 18:00 stage easy to rerun by hand and what keeps the two platforms from needing
/// different code.
/// see: Every line of code runs unmodified on Windows and on Apple Silicon macOS
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            WriteUsage();
            return args.Length == 0 ? 2 : 0;
        }

        // The content root is where the binary sits, not where the shell happened to be.
        // Scheduling lives outside the application, and Task Scheduler and launchd each set a
        // working directory of their own choosing, so a configuration file found by the current
        // directory is a configuration file found on one machine and missed on the other.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.AddPullbackStrategyLabStore();
        builder.Services.AddSingleton<MigrateStage>();
        builder.Services.AddSingleton<SnapshotStage>();
        builder.Services.AddSingleton<UniverseBuilder>();
        builder.Services.AddSingleton<DailyBarIngestor>();
        builder.Services.AddSingleton<ActionIngestor>();
        builder.Services.AddSingleton<IndicatorEngine>();
        builder.Services.AddSingleton<IndexIngestor>();
        builder.Services.AddSingleton<FixtureCapture>();

        // Only the Worker holds a vendor client. The Api never calls the vendor and gets no key.
        // One instance behind two faces: the stages see the interface, and the fixture capture
        // needs the client itself because it stores responses verbatim rather than parsed.
        builder.Services.AddHttpClient<EodhdClient>();
        builder.Services.AddSingleton<IMarketDataVendor>(sp => sp.GetRequiredService<EodhdClient>());

        using IHost host = builder.Build();

        string stage = args[0];
        string[] rest = args[1..];

        try
        {
            return stage switch
            {
                MigrateStage.Name => host.Services.GetRequiredService<MigrateStage>().Run(rest),
                SnapshotStage.Name => host.Services.GetRequiredService<SnapshotStage>().Run(rest),
                UniverseBuilder.Name => host.Services.GetRequiredService<UniverseBuilder>().RunAsync(rest).GetAwaiter().GetResult(),
                DailyBarIngestor.Name => host.Services.GetRequiredService<DailyBarIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
                ActionIngestor.Name => host.Services.GetRequiredService<ActionIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
                DailyBarIngestor.BackfillName => host.Services.GetRequiredService<DailyBarIngestor>().RunBackfillAsync(rest).GetAwaiter().GetResult(),
                IndexIngestor.Name => host.Services.GetRequiredService<IndexIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
                FixtureCapture.Name => host.Services.GetRequiredService<FixtureCapture>().RunAsync(rest).GetAwaiter().GetResult(),
                IndicatorEngine.Name => host.Services.GetRequiredService<IndicatorEngine>().Run(rest),
                "list-stages" => ListStages(),
                _ => UnknownStage(stage),
            };
        }
        catch (Exception e)
        {
            // A stage that throws says so on stderr and exits non-zero. Nothing here
            // swallows an exception into a clean exit, because the scheduler only sees
            // the exit code.
            Console.Error.WriteLine($"{stage}: {e.Message}");
            return 1;
        }
    }

    private static int ListStages()
    {
        foreach (string name in StageNames)
        {
            Console.WriteLine(name);
        }

        return 0;
    }

    private static int UnknownStage(string stage)
    {
        Console.Error.WriteLine($"Unknown stage '{stage}'.");
        WriteUsage();
        return 2;
    }

    /// <summary>Every stage this build can run, which is what `list-stages` prints.</summary>
    public static IReadOnlyList<string> StageNames { get; } =
    [
        MigrateStage.Name,
        SnapshotStage.Name,
        UniverseBuilder.Name,
        DailyBarIngestor.Name,
        ActionIngestor.Name,
        DailyBarIngestor.BackfillName,
        IndexIngestor.Name,
        IndicatorEngine.Name,
        FixtureCapture.Name,
    ];

    private static void WriteUsage()
    {
        Console.Error.WriteLine("usage: PullbackStrategyLab.Worker <stage> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("stages:");
        foreach (string name in StageNames)
        {
            Console.Error.WriteLine($"  {name}");
        }

        Console.Error.WriteLine("  list-stages");
    }
}
