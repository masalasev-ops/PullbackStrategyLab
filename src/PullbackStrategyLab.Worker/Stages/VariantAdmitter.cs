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
/// The human step: registers a rule version and writes its pre-registration, once.
///
/// <b>Manual, and it is the only stage in this worker that no slot runs.</b> Registering a version
/// is a decision rather than a nightly act, and a schedule that could do it would be a schedule that
/// could start an experiment nobody chose to start.
///
/// <b>It writes the target and the minimum sample at creation and nothing may write them again.</b>
/// Twenty worthless candidates give a 64% chance that at least one looks impressive by luck, so
/// pre-registration is the only defence, and a target that can move after the result is not a
/// target. AcceptanceGate writes `status` and `resolved_at` and has no path to either of these
/// columns; a second insert is refused by the key rather than by this stage remembering to check.
/// see: Targets and minimum samples are written at creation and are immutable
/// see: An approved proposal creates a new version from zero, and a running version is never edited
///
/// <b>The minimum sample is derived from the family rather than typed.</b> An operator who could
/// type it could type a different one, and the whole value of a pre-registered figure is that it is
/// the figure the corpus derived. The selection minimum is
/// <see cref="MeasurementParameters.MinimumEffectiveObservations"/>, derived at 5.0(b) against the
/// interval actually run over the flagged population's dispersion; the execution minimum is
/// <see cref="MeasurementParameters.ExecutionMinimumPairedTrades"/>, which is a row count and says
/// so on the row.
/// see: The minimum sample is 1802 effective observations, derived against the interval actually run over the flagged population's dispersion
/// </summary>
public sealed class VariantAdmitter
{
    public const string Name = "admit-variant";

    /// <summary>Shows what would be written and writes nothing, which is what an irreversible act deserves.</summary>
    public const string DryRunFlag = "--dry-run";

    public const string FamilyFlag = "--family";
    public const string DefinitionFlag = "--definition";
    public const string TargetFlag = "--target";

    /// <summary>
    /// Why an execution version is refused outright in this generation.
    ///
    /// Both routes by which such a version earns its place are closed, so admitting one would put a
    /// row in the register that cannot be screened, scored or resolved, and nothing closes such a
    /// row. Refused here rather than left to a person to remember, and the message names the
    /// condition that would reopen it.
    /// see: No execution variant is admitted in this generation, and the condition that would reopen it is named
    /// </summary>
    public const string ExecutionRefused =
        "no execution variant is admitted in this generation: it cannot be screened, because minute bars "
        + "exist only from the night capture began and the vendor sells no history to buy the gap back, and it "
        + "cannot accumulate, because R needs fills and the funnel passes a median of nought candidates a night. "
        + "What reopens it is the screenable population growing at one night a night, and a funnel that produces "
        + "a trade";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public VariantAdmitter(
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

        string? variantId = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        string family = Flag(args, FamilyFlag) ?? VariantFamily.Selection;
        string? definition = Flag(args, DefinitionFlag);
        string? target = Flag(args, TargetFlag);
        bool dryRun = args.Contains(DryRunFlag, StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(variantId))
        {
            Console.Error.WriteLine(
                $"{Name}: name the version. usage: {Name} <variant-id> {FamilyFlag} <{string.Join('|', VariantFamily.All)}> "
                + $"{DefinitionFlag} \"<what it changes>\" {TargetFlag} \"<what would settle it>\" [{DryRunFlag}]");
            return 2;
        }

        if (!VariantFamily.All.Contains(family, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"{Name}: '{family}' is not a version family. One of {string.Join(", ", VariantFamily.All)}.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(definition) || string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                $"{Name}: a version with no definition or no target is a row nothing can settle. "
                + $"Give both {DefinitionFlag} and {TargetFlag}.");
            return 2;
        }

        if (family == VariantFamily.Execution)
        {
            Console.Error.WriteLine($"{Name}: {ExecutionRefused}.");
            return 1;
        }

        VariantAdmission admission = Admit(variantId, family, definition, target, dryRun);

        Console.WriteLine(
            $"{Name}: {admission.Variant.Describe()}");
        Console.WriteLine(
            $"{Name}: definition {admission.Variant.Definition}");
        Console.WriteLine(
            $"{Name}: target {admission.Variant.Target}");
        Console.WriteLine(
            admission.Written
                ? $"{Name}: {admission.Outcome.ToStorageText()}, registered and immutable from now on"
                : $"{Name}: nothing written, {DryRunFlag} given. Run without it to register.");

        return admission.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// Registers one version, or reports what registering it would write.
    ///
    /// <b>The generation is read rather than given.</b> A version belongs to the generation in force
    /// at the moment it is registered, and that is the highest any registered version carries, or
    /// nought where the register is empty. An operator naming a generation could put a version in
    /// one whose baseline it was never compared against.
    /// </summary>
    public VariantAdmission Admit(
        string variantId, string family, string definition, string target, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "variant");

        DateTimeOffset now = _clock.UtcNow;
        DateOnly asOf = _clock.SessionDate(now, _options.SessionZone);

        IReadOnlyList<StoredVariant> registered =
            VariantReader.RegisteredBy(connection, asOf, _options.SessionZone);

        int generation = registered.Count == 0 ? 0 : registered.Max(v => v.Generation);
        int minimumSample = family == VariantFamily.Execution
            ? MeasurementParameters.ExecutionMinimumPairedTrades
            : MeasurementParameters.MinimumEffectiveObservations;

        var variant = new StoredVariant(
            variantId,
            generation,
            family,
            definition,
            target,
            minimumSample,
            MinimumSampleUnit.For(family),
            VariantStatus.Open,
            null,
            now);

        if (dryRun)
        {
            RunSummary dry = run.Complete(RunOutcome.Clean);
            return new VariantAdmission(variant, false, dry.RowsWritten, RunOutcome.Clean);
        }

        Insert(connection, variant);
        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new VariantAdmission(variant, true, summary.RowsWritten, RunOutcome.Clean);
    }

    private static void Insert(SqliteConnection connection, StoredVariant variant)
    {
        using SqliteCommand command = connection.CreateCommand();

        // No ON CONFLICT clause, deliberately. A second registration of one identifier is a mistake
        // somebody has to see, and swallowing it would let an operator believe a target had been
        // rewritten when the store had refused to rewrite it.
        command.CommandText = """
            INSERT INTO variant (
                variant_id, generation, family, definition, target,
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at)
            VALUES (
                @variant_id, @generation, @family, @definition, @target,
                @minimum_sample, @minimum_sample_unit, @status, NULL, @created_at);
            """;

        command.Parameters.AddWithValue("@variant_id", variant.VariantId);
        command.Parameters.AddWithValue("@generation", variant.Generation);
        command.Parameters.AddWithValue("@family", variant.Family);
        command.Parameters.AddWithValue("@definition", variant.Definition);
        command.Parameters.AddWithValue("@target", variant.Target);
        command.Parameters.AddWithValue("@minimum_sample", variant.MinimumSample);
        command.Parameters.AddWithValue("@minimum_sample_unit", variant.MinimumSampleUnit);
        command.Parameters.AddWithValue("@status", variant.Status);
        command.Parameters.AddWithValue(
            "@created_at", StoreText.TimestampToStorageText(variant.CreatedAt));

        // Rows written are measured from the store by the run scope rather than reported here,
        // which is why nothing counts the result of this call.
        command.ExecuteNonQuery();
    }

    private static string? Flag(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}

/// <summary>What registering a version wrote, or would have written.</summary>
public sealed record VariantAdmission(
    StoredVariant Variant,
    bool Written,
    int RowsWritten,
    RunOutcome Outcome);
