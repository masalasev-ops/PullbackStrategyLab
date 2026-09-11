using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The act that closes a generation: every open version of the generation in force is closed as
/// `unresolved`, its baseline is retired, and the next generation's baseline is registered, in one
/// transaction. From 7.6.
///
/// <b>It had no implementation until 7.6, and the rule it carries out was written from the start.</b>
/// The baseline is frozen, and editing it closes every open version as unresolved and starts a new
/// generation, because those versions were being compared against a rule that no longer exists. Until
/// this component nothing in the shipped source wrote `unresolved` at all, so the rule was a sentence
/// the register could express and nothing could carry out.
/// see: An approved proposal creates a new version from zero, and a running version is never edited
///
/// <b>The baseline gets a status of its own, and this is the component that defines it.</b> A baseline
/// is not a version in the sense the gate settles: its own pre-registration says it is not itself
/// accepted or rejected, being the arm every paired comparison subtracts. `unresolved` is wrong for it
/// too, since an unresolved version was never measured because its baseline went away, and the
/// baseline is what went away. So the closed baseline reads `retired`, and migration 065 holds that
/// status to the baseline alone.
/// see: A selection version's target is derived from the settling rule and is not typed
///
/// <b>Settled versions keep their answer.</b> A version accepted or rejected before the close was
/// measured against the baseline it was registered under, and that measurement stays true of that
/// generation. Only what is still open is closed.
///
/// <b>Manual, like registering a version.</b> Closing a generation is a decision, and a schedule that
/// could take it could end every running experiment on a night nobody chose. Generation 1 registers
/// through this act at 7.11 and not before.
/// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
/// </summary>
public sealed class GenerationCloser
{
    public const string Name = "close-generation";

    /// <summary>Shows what would be written and writes nothing, which is what an irreversible act deserves.</summary>
    public const string DryRunFlag = "--dry-run";

    /// <summary>What the next generation's baseline is, in words, as the admitter takes a baseline's.</summary>
    public const string DefinitionFlag = "--definition";

    /// <summary>What the next baseline is for. Typed, because the baseline is the arm and never settles.</summary>
    public const string TargetFlag = "--target";

    /// <summary>
    /// Registers generation 1's baseline with the definition and target this checkpoint writes, from
    /// 7.11, so the operator types the name and nothing else. The definition names both families,
    /// which is what makes generation 1's baseline whole: a version of it differs in one family and
    /// never in both.
    /// see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
    /// </summary>
    public const string GenerationOneFlag = "--generation-one";

    /// <summary>Generation 1's baseline in words, naming the selection gate set and the execution rule.</summary>
    public const string GenerationOneDefinition =
        "generation 1: selection by the gate lists SOURCES.md traces clause by clause, being the weekly screen, "
        + "the ladders, the thrust, the dip or bounce, contraction, the ceiling's confluence and the tradable "
        + "floors, with moves-enough, held-floor, no-reclaim and cluster recorded and never required; execution "
        + "by the entry rule, a flush into the hourly 9 and 21 averages taken on the first break of the previous "
        + "candle, the stop at the session's extreme switching to the entry candle's past 2% and refused past the "
        + "tighter of half the daily range and 5%, and by the exits, 15% trims at 3R and 5R, the long trail on "
        + "the 9-day average and the short held three sessions";

    /// <summary>What generation 1's baseline is for. Typed, as every baseline's is, because the baseline never settles.</summary>
    public const string GenerationOneTarget =
        "the reference every version of generation 1 is differenced against";

    /// <summary>Why the act refuses an empty register.</summary>
    public const string NothingToClose =
        "the register holds no version, so there is no generation to close. The first baseline is "
        + "registered by " + VariantAdmitter.Name;

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public GenerationCloser(
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

        // The name comes first and is never a flag's value: taking the first bare word anywhere would
        // read the definition as the name when the name is left off.
        string? nextBaseline = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        bool generationOne = args.Contains(GenerationOneFlag, StringComparer.Ordinal);
        string? definition = Flag(args, DefinitionFlag) ?? (generationOne ? GenerationOneDefinition : null);
        string? target = Flag(args, TargetFlag) ?? (generationOne ? GenerationOneTarget : null);
        bool dryRun = args.Contains(DryRunFlag, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(nextBaseline)
            || string.IsNullOrWhiteSpace(definition)
            || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                $"{Name}: name the next generation's baseline and say what it is. usage: {Name} <variant-id> "
                + $"{DefinitionFlag} \"<what it is>\" {TargetFlag} \"<what it is for>\" [{DryRunFlag}], or "
                + $"{Name} <variant-id> {GenerationOneFlag} [{DryRunFlag}] for generation 1's baseline");
            return 2;
        }

        GenerationClosure closure = Close(nextBaseline, definition, target, dryRun);

        if (closure.RefusedBecause is string refused)
        {
            Console.Error.WriteLine($"{Name}: refused. {refused}.");
            return 1;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}: generation {closure.ClosedGeneration} closes. {closure.Unresolved.Count} open version(s) "
            + $"become {VariantStatus.Unresolved}, baseline {closure.RetiredBaseline} becomes {VariantStatus.Retired}, "
            + $"{closure.SettledKept} settled version(s) keep their answer"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}: generation {closure.ClosedGeneration + 1} opens with baseline {closure.NextBaseline}"));
        Console.WriteLine(
            closure.Written
                ? $"{Name}: {closure.Outcome.ToStorageText()}, written in one transaction"
                : $"{Name}: nothing written, {DryRunFlag} given. Run without it to close the generation.");

        return closure.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Closes the generation in force and registers the next one's baseline, or says what doing so
    /// would write.
    ///
    /// <b>The generation is read rather than given</b>, on the admitter's terms: the one in force is
    /// the highest any registered version carries. An operator naming one could close a generation
    /// that is not running, which would leave the running one open under a baseline it had outlived.
    ///
    /// <b>One transaction, and the order inside it is not a choice.</b> A night reading the register
    /// between the close and the registration would find a generation with no open baseline and fan a
    /// plan out to nothing, and one reading between the registration and the close would find two
    /// generations open at once.
    /// </summary>
    public GenerationClosure Close(string nextBaselineId, string definition, string target, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextBaselineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "variant");

        DateTimeOffset now = _clock.UtcNow;
        DateOnly asOf = _clock.SessionDate(now, _options.SessionZone);

        IReadOnlyList<StoredVariant> registered =
            VariantReader.RegisteredBy(connection, asOf, _options.SessionZone);

        if (registered.Count == 0)
        {
            run.Complete(RunOutcome.Failed);
            return GenerationClosure.Refused(NothingToClose);
        }

        int closing = registered.Max(v => v.Generation);
        IReadOnlyList<StoredVariant> generation = [.. registered.Where(v => v.Generation == closing)];

        StoredVariant? baseline = generation.SingleOrDefault(v => v.IsBaseline);

        if (baseline is null || baseline.Status != VariantStatus.Open)
        {
            string why = string.Create(
                CultureInfo.InvariantCulture,
                $"generation {closing} has no open baseline, so there is no rule in force whose edit this would be");
            run.Complete(RunOutcome.Failed);
            return GenerationClosure.Refused(why);
        }

        if (registered.Any(v => string.Equals(v.VariantId, nextBaselineId, StringComparison.Ordinal)))
        {
            string why = $"'{nextBaselineId}' is already registered, and a version is never edited";
            run.Complete(RunOutcome.Failed);
            return GenerationClosure.Refused(why);
        }

        IReadOnlyList<string> unresolved =
        [
            .. generation
                .Where(v => !v.IsBaseline && v.Status == VariantStatus.Open)
                .Select(v => v.VariantId),
        ];

        int settledKept = generation.Count(v => !v.IsBaseline && v.Status != VariantStatus.Open);

        var next = new StoredVariant(
            nextBaselineId,
            closing + 1,
            VariantFamily.Baseline,
            definition,
            target,
            MeasurementParameters.MinimumEffectiveObservations,
            MinimumSampleUnit.For(VariantFamily.Baseline),
            VariantStatus.Open,
            null,
            now);

        if (dryRun)
        {
            run.Complete(RunOutcome.Clean);
            return new GenerationClosure(closing, unresolved, baseline.VariantId, nextBaselineId, settledKept, false, RunOutcome.Clean, null);
        }

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            CloseOpenVersions(connection, transaction, closing, now);
            RetireBaseline(connection, transaction, baseline.VariantId, now);
            RegisterNextBaseline(connection, transaction, next);
            transaction.Commit();
        }

        run.Complete(RunOutcome.Clean);
        return new GenerationClosure(closing, unresolved, baseline.VariantId, nextBaselineId, settledKept, true, RunOutcome.Clean, null);
    }

    /// <summary>
    /// Every open version of the closing generation, as unresolved. Two columns, the ones AcceptanceGate
    /// also writes, and never on a version the gate has already settled.
    /// </summary>
    private static void CloseOpenVersions(
        SqliteConnection connection, SqliteTransaction transaction, int generation, DateTimeOffset now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE variant
               SET status = 'unresolved',
                   resolved_at = @resolved_at
             WHERE generation = @generation
               AND family <> 'baseline'
               AND status = 'open';
            """;

        command.Parameters.AddWithValue("@resolved_at", StoreText.TimestampToStorageText(now));
        command.Parameters.AddWithValue("@generation", generation);
        command.ExecuteNonQuery();
    }

    /// <summary>The closing generation's baseline, as retired, which the store permits on a baseline alone.</summary>
    private static void RetireBaseline(
        SqliteConnection connection, SqliteTransaction transaction, string variantId, DateTimeOffset now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE variant
               SET status = 'retired',
                   resolved_at = @resolved_at
             WHERE variant_id = @variant_id
               AND family = 'baseline'
               AND status = 'open';
            """;

        command.Parameters.AddWithValue("@resolved_at", StoreText.TimestampToStorageText(now));
        command.Parameters.AddWithValue("@variant_id", variantId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The next generation's baseline, the first row of a generation no row carries yet, which is what
    /// keeps this insert disjoint from VariantAdmitter's: the admitter only ever writes into the
    /// generation in force.
    /// </summary>
    private static void RegisterNextBaseline(
        SqliteConnection connection, SqliteTransaction transaction, StoredVariant next)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
            VALUES (
                @variant_id, @generation, 'baseline', @definition, @target,
                @minimum_sample, @minimum_sample_unit, 'open', NULL, @created_at);
            """;

        command.Parameters.AddWithValue("@variant_id", next.VariantId);
        command.Parameters.AddWithValue("@generation", next.Generation);
        command.Parameters.AddWithValue("@definition", next.Definition);
        command.Parameters.AddWithValue("@target", next.Target);
        command.Parameters.AddWithValue("@minimum_sample", next.MinimumSample);
        command.Parameters.AddWithValue("@minimum_sample_unit", next.MinimumSampleUnit);
        command.Parameters.AddWithValue("@created_at", StoreText.TimestampToStorageText(next.CreatedAt));
        command.ExecuteNonQuery();
    }

    private static string? Flag(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}

/// <summary>What closing a generation wrote, or would have written, or why it refused.</summary>
public sealed record GenerationClosure(
    int ClosedGeneration,
    IReadOnlyList<string> Unresolved,
    string? RetiredBaseline,
    string? NextBaseline,
    int SettledKept,
    bool Written,
    RunOutcome Outcome,
    string? RefusedBecause)
{
    public static GenerationClosure Refused(string because) =>
        new(-1, [], null, null, 0, false, RunOutcome.Failed, because);
}
