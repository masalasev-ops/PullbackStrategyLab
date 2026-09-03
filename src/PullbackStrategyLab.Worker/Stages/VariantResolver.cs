using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Decides which rule versions are live tonight, and writes nothing.
///
/// <b>It owns no table, on the same terms SetupJournal and WatchlistPublisher own none.</b> What
/// versions are live on a night is a function of the register and the night's date: the versions
/// registered by the end of that session, in the generation in force. Storing that answer would be a
/// second copy of the register that could disagree with it, and the disagreement would be
/// undetectable because nothing downstream reads both. PlanBuilder asks the same reader at 18:30 and
/// gets the same answer.
///
/// <b>So what it does is check and report</b>, which is the only moment anybody would notice that
/// the register is empty and every plan the night is about to write belongs to nothing, or that a
/// generation has turned over. It runs at 18:28, after the cap and before the plans, because a night
/// that cannot answer this question should say so before the stage that depends on it runs rather
/// than after.
///
/// <b>A night with no baseline is reported partial rather than clean.</b> That is the state before
/// the freeze and it is a real state, not an error: the lab flags, records and measures setups
/// without any version existing. What it cannot do is fan a plan out, so the run says which it was.
/// </summary>
public sealed class VariantResolver
{
    public const string Name = "resolve-variants";

    /// <summary>Recorded where the register holds nothing, which is every night before the baseline is frozen.</summary>
    public const string NoVersionsRegistered =
        "the register holds no version for this session, so no plan can belong to one. That is the state "
        + "before the baseline is registered and it is not an error";

    /// <summary>Recorded where versions exist and none of them is the baseline of the generation in force.</summary>
    public const string NoBaseline =
        "versions are registered and none of them is this generation's baseline, so there is nothing for a "
        + "difference series to be measured against";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public VariantResolver(
        StoreConnectionFactory connections,
        RunLogger runLogger,
        IClock clock,
        IOptions<PullbackStrategyLabOptions> options)
    {
        _connections = connections;
        _runLogger = runLogger;
        _clock = clock;
        _options = options.Value;
    }

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        VariantResolution resolved = Resolve(asOf);

        Console.WriteLine(
            $"{Name}: session {resolved.AsOf:yyyy-MM-dd}, generation {resolved.Generation}, "
            + $"{resolved.Live.Count} version(s) live");

        foreach (StoredVariant variant in resolved.Live)
        {
            Console.WriteLine($"{Name}:   {variant.Describe()}");
        }

        Console.WriteLine(
            resolved.NothingBecause is null
                ? $"{Name}: {resolved.Outcome.ToStorageText()}, no rows written, a plan is fanned out to each"
                : $"{Name}: {resolved.Outcome.ToStorageText()}, {resolved.NothingBecause}");

        return resolved.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// The versions live for one session.
    ///
    /// The run entry is opened declaring no table, which is what makes "writes nothing" a property
    /// of the record rather than of the comment above: the scope measures rows written from the
    /// store, so a row appearing here would be counted and reported by the run.
    /// </summary>
    public VariantResolution Resolve(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name);

        IReadOnlyList<StoredVariant> live = VariantReader.LiveOn(connection, asOf, _options.SessionZone);
        StoredVariant? baseline = live.SingleOrDefault(v => v.Family == VariantFamily.Baseline);

        string? nothing = live.Count == 0
            ? NoVersionsRegistered
            : baseline is null ? NoBaseline : null;

        // Partial rather than failed. A night with no baseline is a night the lab still records
        // setups on, and a failed run would stop the slot and take the stages after it with it.
        RunOutcome outcome = nothing is null ? RunOutcome.Clean : RunOutcome.Partial;
        RunSummary summary = run.Complete(outcome);

        return new VariantResolution(
            asOf,
            live.Count == 0 ? 0 : live[0].Generation,
            live,
            summary.RowsWritten,
            outcome,
            nothing);
    }
}

/// <summary>Which versions a session fans its plans out to, as the stage reports it.</summary>
public sealed record VariantResolution(
    DateOnly AsOf,
    int Generation,
    IReadOnlyList<StoredVariant> Live,
    int RowsWritten,
    RunOutcome Outcome,
    string? NothingBecause);
