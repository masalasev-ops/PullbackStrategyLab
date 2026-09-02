using Microsoft.Data.Sqlite;
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
        builder.Services.AddSingleton<IntradayFetcher>();
        builder.Services.AddSingleton<SpreadSnapshotter>();
        builder.Services.AddSingleton<VwapEngine>();
        builder.Services.AddSingleton<WatchlistPublisher>();
        builder.Services.AddSingleton<PlanBuilder>();
        builder.Services.AddSingleton<TriggerResolver>();
        builder.Services.AddSingleton<RiskGate>();
        builder.Services.AddSingleton<PaperBroker>();
        builder.Services.AddSingleton<PositionManager>();
        builder.Services.AddSingleton<TradeJournal>();
        builder.Services.AddSingleton<PlanAudit>();
        builder.Services.AddSingleton<LossClassifier>();
        builder.Services.AddSingleton<SignalVectorizer>();
        builder.Services.AddSingleton<ScanEngine>();
        builder.Services.AddSingleton<TierClassifier>();
        builder.Services.AddSingleton<RegimeLabeler>();
        builder.Services.AddSingleton<ReconstructedRead>();
        builder.Services.AddSingleton<SectorResolver>();
        builder.Services.AddSingleton<ThemeClusterer>();
        builder.Services.AddSingleton<CheckRecomputer>();
        builder.Services.AddSingleton<LongSetupDetector>();
        builder.Services.AddSingleton<ShortSetupDetector>();
        builder.Services.AddSingleton<ScoreboardBuilder>();
        builder.Services.AddSingleton<CeilingCalculator>();
        builder.Services.AddSingleton<ControlSampler>();
        builder.Services.AddSingleton<ForwardReturnFiller>();
        builder.Services.AddSingleton<SetupJournal>();
        builder.Services.AddSingleton<SetupCapper>();
        builder.Services.AddSingleton<FixtureCapture>();
        builder.Services.AddSingleton<PhaseReportStage>();

        // Only the Worker holds a vendor client. The Api never calls the vendor and gets no key.
        // One instance behind two faces: the stages see the interface, and the fixture capture
        // needs the client itself because it stores responses verbatim rather than parsed.
        builder.Services.AddHttpClient<EodhdClient>();
        builder.Services.AddSingleton<IMarketDataVendor>(sp => sp.GetRequiredService<EodhdClient>());

        using IHost host = builder.Build();

        string stage = args[0];
        string[] rest = args[1..];

        // The store's schema version against the one this build carries, before any stage opens it.
        //
        // On 2026-08-28 migrations 031 and 032 landed and data/live was never migrated. detect-long,
        // vectorize, controls and cap each died on 'no such column: degraded_because', one slot after
        // the next, and the night produced no setups at all against inputs that were entirely clean.
        // Every message named a column, which says what broke and not why, and nothing anywhere said
        // the store was two migrations behind the code reading it.
        //
        // Refused here rather than inside each stage, because the property is about the store rather
        // than about any one stage's statements: a stage that adds a column requirement would
        // otherwise have to remember to bring a guard along with it, and the one that did not is
        // exactly how this was found.
        string? refusal = WhyThisStageCannotRun(
            stage, host.Services.GetRequiredService<StoreConnectionFactory>());
        if (refusal is not null)
        {
            Console.Error.WriteLine($"{stage}: {refusal}");
            return 1;
        }

        try
        {
            // Resolved and then run, so the resolution is a question anything can ask and the
            // running is the only thing that needs a host.
            return Arm(stage) is { } arm ? arm(host.Services, rest) : UnknownStage(stage);
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

    /// <summary>
    /// The three stages that run against a store at any version, and why each one has to.
    ///
    /// <c>migrate</c> is the repair itself. <c>snapshot-db</c> is the recovery path, and the RUNBOOK
    /// has it run before every migration, so a guard that refused it would refuse the one command
    /// standing between a behind store and an irreversible one. <c>list-stages</c> reads nothing.
    /// </summary>
    public static IReadOnlyList<string> RunsWhateverVersionTheStoreIsAt { get; } =
    [
        MigrateStage.Name,
        SnapshotStage.Name,
        "list-stages",
    ];

    /// <summary>
    /// Why this stage may not run against this store, or null when it may.
    ///
    /// The whole decision, so the exemptions are exercised by a test rather than read off the list:
    /// a guard whose escape hatch nothing asserts is a guard that can be widened silently.
    /// </summary>
    public static string? WhyThisStageCannotRun(string stage, StoreConnectionFactory connections)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(connections);

        return RunsWhateverVersionTheStoreIsAt.Contains(stage, StringComparer.Ordinal)
            ? null
            : WhyTheStoreCannotBeRead(connections);
    }

    /// <summary>
    /// Why the store cannot be read by this build, or null when it can.
    ///
    /// A store the build has never created is not behind: <c>migrate</c> creates it, and refusing
    /// here would refuse a first run. A store <em>ahead</em> of the build is refused on the same
    /// footing as one behind it, because an older binary run against a migrated store reads columns
    /// whose meaning has moved, which is the same fault with the sign changed and no louder.
    /// </summary>
    public static string? WhyTheStoreCannotBeRead(StoreConnectionFactory connections)
    {
        ArgumentNullException.ThrowIfNull(connections);

        if (!connections.StoreExists)
        {
            return null;
        }

        using SqliteConnection connection = connections.OpenReadOnly();
        return WhyTheStoreCannotBeRead(
            MigrationRunner.ReadUserVersion(connection), MigrationRunner.LatestVersion);
    }

    /// <summary>The comparison alone, so a test can state both numbers rather than build a store.</summary>
    public static string? WhyTheStoreCannotBeRead(int found, int needed) => found == needed
        ? null
        : found < needed
            ? $"the store is at schema {found} and this build needs {needed}. Run tools/migrate before "
              + "any stage, and read the night's log for the slots that already ran: a stage that "
              + "needed a column the store has not got has failed rather than written a partial night."
            : $"the store is at schema {found} and this build is written against {needed}. It has been "
              + "migrated by a newer build than this one, so a column this binary reads may no longer "
              // Not "Update the checkout": writer-ownership scans the shipped source for writes, and
              // that phrase reads as UPDATE against a table called "the". Prose about a thing must
              // not read as the thing, which is the rule the session-bound guard states for itself.
              + "mean what it did. Move the checkout forward rather than running against it.";

    /// <summary>
    /// Every stage name the worker can run, and what running it does.
    ///
    /// <b>A table rather than a switch inside <c>Main</c>, from 4.17, so the dispatch can be
    /// asked a question without being made to answer one.</b> `architecture-conformance` named a
    /// behavioural backing for its catalogue scan and the backing was itself a source scan: a
    /// test reading `Program.cs` as text and matching switch-arm shapes with a regex, which is a
    /// scan backed by a scan and was recorded as backed. The repair the row asks for is to invoke
    /// the dispatch once per advertised name and assert on what comes back, and a switch that
    /// runs the stage cannot be invoked that way: resolving a name would build a host, open a
    /// store and do a night's work.
    ///
    /// <b>So the table resolves and the caller runs.</b> A name it does not hold comes back null,
    /// which is what an authored bad name asserts against, and a name it holds comes back as the
    /// thing that would run. Nothing here touches a service provider until the caller invokes
    /// what it got.
    /// </summary>
    public static IReadOnlyDictionary<string, Func<IServiceProvider, string[], int>> Dispatch { get; } =
        new Dictionary<string, Func<IServiceProvider, string[], int>>(StringComparer.Ordinal)
        {
        [MigrateStage.Name] = (services, rest) => services.GetRequiredService<MigrateStage>().Run(rest),
        [SnapshotStage.Name] = (services, rest) => services.GetRequiredService<SnapshotStage>().Run(rest),
        [UniverseBuilder.Name] = (services, rest) => services.GetRequiredService<UniverseBuilder>().RunAsync(rest).GetAwaiter().GetResult(),
        [UniverseBuilder.DelistedName] = (services, rest) => services.GetRequiredService<UniverseBuilder>().RunDelistedAsync(rest).GetAwaiter().GetResult(),
        [DailyBarIngestor.Name] = (services, rest) => services.GetRequiredService<DailyBarIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
        [ActionIngestor.Name] = (services, rest) => services.GetRequiredService<ActionIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
        [DailyBarIngestor.BackfillName] = (services, rest) => services.GetRequiredService<DailyBarIngestor>().RunBackfillAsync(rest).GetAwaiter().GetResult(),
        [IndexIngestor.Name] = (services, rest) => services.GetRequiredService<IndexIngestor>().RunAsync(rest).GetAwaiter().GetResult(),
        [IntradayFetcher.Name] = (services, rest) => services.GetRequiredService<IntradayFetcher>().RunAsync(rest).GetAwaiter().GetResult(),
        [SpreadSnapshotter.Name] = (services, rest) => services.GetRequiredService<SpreadSnapshotter>().RunAsync(rest).GetAwaiter().GetResult(),
        [VwapEngine.Name] = (services, rest) => services.GetRequiredService<VwapEngine>().RunAsync(rest).GetAwaiter().GetResult(),
        [WatchlistPublisher.Name] = (services, rest) => services.GetRequiredService<WatchlistPublisher>().RunAsync(rest).GetAwaiter().GetResult(),
        [PlanBuilder.Name] = (services, rest) => services.GetRequiredService<PlanBuilder>().Run(rest),
        [TriggerResolver.Name] = (services, rest) => services.GetRequiredService<TriggerResolver>().Run(rest),
        [RiskGate.Name] = (services, rest) => services.GetRequiredService<RiskGate>().Run(rest),
        [PaperBroker.Name] = (services, rest) => services.GetRequiredService<PaperBroker>().Run(rest),
        [PositionManager.Name] = (services, rest) => services.GetRequiredService<PositionManager>().Run(rest),
        [TradeJournal.Name] = (services, rest) => services.GetRequiredService<TradeJournal>().Run(rest),
        [PlanAudit.Name] = (services, rest) => services.GetRequiredService<PlanAudit>().Run(rest),
        [LossClassifier.Name] = (services, rest) => services.GetRequiredService<LossClassifier>().Run(rest),
        [FixtureCapture.Name] = (services, rest) => services.GetRequiredService<FixtureCapture>().RunAsync(rest).GetAwaiter().GetResult(),
        [FixtureCapture.CaptureResponseName] = (services, rest) => services.GetRequiredService<FixtureCapture>().CaptureResponseAsync(rest).GetAwaiter().GetResult(),
        [IndicatorEngine.Name] = (services, rest) => services.GetRequiredService<IndicatorEngine>().Run(rest),
        [SignalVectorizer.Name] = (services, rest) => services.GetRequiredService<SignalVectorizer>().Run(rest),
        [ScanEngine.Name] = (services, rest) => services.GetRequiredService<ScanEngine>().Run(rest),
        [TierClassifier.Name] = (services, rest) => services.GetRequiredService<TierClassifier>().Run(rest),
        [RegimeLabeler.Name] = (services, rest) => services.GetRequiredService<RegimeLabeler>().Run(rest),
        [ReconstructedRead.Name] = (services, rest) => services.GetRequiredService<ReconstructedRead>().Run(rest),
        [SectorResolver.Name] = (services, rest) => services.GetRequiredService<SectorResolver>().RunAsync(rest).GetAwaiter().GetResult(),
        [ThemeClusterer.Name] = (services, rest) => services.GetRequiredService<ThemeClusterer>().Run(rest),
        [CheckRecomputer.Name] = (services, rest) => services.GetRequiredService<CheckRecomputer>().Run(rest),
        [LongSetupDetector.Name] = (services, rest) => services.GetRequiredService<LongSetupDetector>().Run(rest),
        [ShortSetupDetector.Name] = (services, rest) => services.GetRequiredService<ShortSetupDetector>().Run(rest),
        [SetupJournal.Name] = (services, rest) => services.GetRequiredService<SetupJournal>().Run(rest),
        [ScoreboardBuilder.Name] = (services, rest) => services.GetRequiredService<ScoreboardBuilder>().Run(rest),
        [CeilingCalculator.Name] = (services, rest) => services.GetRequiredService<CeilingCalculator>().Run(rest),
        [ControlSampler.Name] = (services, rest) => services.GetRequiredService<ControlSampler>().Run(rest),
        [ForwardReturnFiller.Name] = (services, rest) => services.GetRequiredService<ForwardReturnFiller>().Run(rest),
        [SetupCapper.Name] = (services, rest) => services.GetRequiredService<SetupCapper>().Run(rest),
        [PhaseReportStage.Name] = (services, rest) => services.GetRequiredService<PhaseReportStage>().Run(rest),
            ["list-stages"] = (_, _) => ListStages(),
        };

    /// <summary>What running one stage does, or null where this build has no such stage.</summary>
    public static Func<IServiceProvider, string[], int>? Arm(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        return Dispatch.TryGetValue(stage, out Func<IServiceProvider, string[], int>? arm) ? arm : null;
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
        UniverseBuilder.DelistedName,
        DailyBarIngestor.Name,
        ActionIngestor.Name,
        DailyBarIngestor.BackfillName,
        IndexIngestor.Name,
        IntradayFetcher.Name,
        SpreadSnapshotter.Name,
        VwapEngine.Name,
        WatchlistPublisher.Name,
        // Absent from this list from 4.16, when the stage was dispatched and registered and
        // never advertised, so `list-stages` and the usage text under-reported by one. Nothing
        // could see it: the reachability test asked only that every advertised stage has an arm,
        // and `architecture-conformance` treats this list as a second way of being registered
        // rather than as a roster to reconcile. It now reconciles in both directions.
        PlanBuilder.Name,
        TriggerResolver.Name,
        RiskGate.Name,
        PaperBroker.Name,
        PositionManager.Name,
        TradeJournal.Name,
        PlanAudit.Name,
        LossClassifier.Name,
        IndicatorEngine.Name,
        ScanEngine.Name,
        TierClassifier.Name,
        SectorResolver.Name,
        ThemeClusterer.Name,
        CheckRecomputer.Name,
        RegimeLabeler.Name,
        ReconstructedRead.Name,
        LongSetupDetector.Name,
        ShortSetupDetector.Name,
        SignalVectorizer.Name,
        SetupJournal.Name,
        ScoreboardBuilder.Name,
        CeilingCalculator.Name,
        ControlSampler.Name,
        ForwardReturnFiller.Name,
        SetupCapper.Name,
        FixtureCapture.Name,
        FixtureCapture.CaptureResponseName,
        PhaseReportStage.Name,
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
