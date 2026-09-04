using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Detection;
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

    /// <summary>The side a selection version applies to. A version is one side's, because the two are never pooled.</summary>
    public const string DirectionFlag = "--direction";

    /// <summary>Which named threshold moves. One, and the assertion is what says so.</summary>
    public const string ThresholdFlag = "--threshold";

    /// <summary>What it moves to. Where it moves from is the baseline's and is never given.</summary>
    public const string ValueFlag = "--value";

    /// <summary>
    /// Why a definition may not be typed for a selection version.
    ///
    /// It is derived from the admission assertion, on the same grounds the minimum sample is derived
    /// from the family: a sentence somebody types can disagree with the columns beside it, and the
    /// day it does there is nothing to say which of the two the version is.
    /// </summary>
    public const string DefinitionIsDerived =
        "a selection version's definition is derived from the threshold it moves and is not typed. "
        + "Give " + DirectionFlag + ", " + ThresholdFlag + " and " + ValueFlag;

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
                + $"{TargetFlag} \"<what would settle it>\" and then, for a selection version, "
                + $"{DirectionFlag} <long|short> {ThresholdFlag} <name> {ValueFlag} <number>, or for the baseline "
                + $"{DefinitionFlag} \"<what it is>\". [{DryRunFlag}]");
            return 2;
        }

        if (!VariantFamily.All.Contains(family, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"{Name}: '{family}' is not a version family. One of {string.Join(", ", VariantFamily.All)}.");
            return 2;
        }

        if (family == VariantFamily.Execution)
        {
            Console.Error.WriteLine($"{Name}: {ExecutionRefused}.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine(
                $"{Name}: a version with no target is a row nothing can settle. Give {TargetFlag}.");
            return 2;
        }

        MovedThreshold? moved = null;

        if (family == VariantFamily.Selection)
        {
            if (!string.IsNullOrWhiteSpace(definition))
            {
                Console.Error.WriteLine($"{Name}: {DefinitionIsDerived}.");
                return 2;
            }

            AdmissionVerdict verdict = Assert(
                Flag(args, DirectionFlag), Flag(args, ThresholdFlag), Flag(args, ValueFlag), out string? malformed);

            if (malformed is not null)
            {
                Console.Error.WriteLine($"{Name}: {malformed}");
                return 2;
            }

            if (!verdict.IsAdmitted)
            {
                Console.Error.WriteLine($"{Name}: refused. {verdict.Reason}.");
                return 1;
            }

            moved = new MovedThreshold(
                Flag(args, DirectionFlag)!, verdict.Gate!, verdict.Threshold!, verdict.From!.Value, verdict.To!.Value);

            // Derived from the assertion rather than typed, so the sentence on the ledger and the
            // columns the scorer reads are one fact.
            definition = verdict.Reason;
        }
        else if (string.IsNullOrWhiteSpace(definition))
        {
            Console.Error.WriteLine(
                $"{Name}: a version with no definition is a row nothing can settle. Give {DefinitionFlag}.");
            return 2;
        }

        VariantAdmission admission = Admit(variantId, family, definition!, target, dryRun, moved);

        Console.WriteLine(
            $"{Name}: {admission.Variant.Describe()}");
        Console.WriteLine(
            $"{Name}: definition {admission.Variant.Definition}");
        Console.WriteLine(
            $"{Name}: target {admission.Variant.Target}");

        if (admission.Variant.Moved is MovedThreshold move)
        {
            Console.WriteLine($"{Name}: moves {move.Describe()}");
        }
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
        string variantId,
        string family,
        string definition,
        string target,
        bool dryRun = false,
        MovedThreshold? moved = null)
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
            now)
        {
            Moved = moved,
        };

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
                minimum_sample, minimum_sample_unit, status, resolved_at, created_at,
                direction, gate, threshold_name, threshold_from, threshold_to)
            VALUES (
                @variant_id, @generation, @family, @definition, @target,
                @minimum_sample, @minimum_sample_unit, @status, NULL, @created_at,
                @direction, @gate, @threshold_name, @threshold_from, @threshold_to);
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

        // Null together on the baseline and on anything that moves no threshold, which is what the
        // store's own five clauses require.
        MovedThreshold? moved = variant.Moved;
        command.Parameters.AddWithValue("@direction", (object?)moved?.Direction ?? DBNull.Value);
        command.Parameters.AddWithValue("@gate", (object?)moved?.Gate ?? DBNull.Value);
        command.Parameters.AddWithValue("@threshold_name", (object?)moved?.ThresholdName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@threshold_from",
            moved is null ? DBNull.Value : StoreText.ThresholdToStorageText(moved.From));
        command.Parameters.AddWithValue(
            "@threshold_to",
            moved is null ? DBNull.Value : StoreText.ThresholdToStorageText(moved.To));

        // Rows written are measured from the store by the run scope rather than reported here,
        // which is why nothing counts the result of this call.
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// What the admission assertion says about the move the operator named, with a message where
    /// what they named is not a move at all.
    ///
    /// <b>Malformed and refused are two answers, not one.</b> A direction that is not a side, or a
    /// value that is not a number, is an operator mistake and exits 2; a well-formed candidate the
    /// rule refuses is an answer about the version and exits 1. Collapsing them would make a typo
    /// read as a rejected experiment.
    /// </summary>
    private static AdmissionVerdict Assert(
        string? direction, string? threshold, string? value, out string? malformed)
    {
        malformed = null;

        if (direction != SetupDirection.Long && direction != SetupDirection.Short)
        {
            malformed = $"'{direction}' is not a side. {DirectionFlag} takes {SetupDirection.Long} or {SetupDirection.Short}.";
            return AdmissionVerdict.Refused(malformed);
        }

        if (string.IsNullOrWhiteSpace(threshold) || string.IsNullOrWhiteSpace(value))
        {
            malformed = $"give {ThresholdFlag} and {ValueFlag}: a selection version is one named threshold at one value.";
            return AdmissionVerdict.Refused(malformed);
        }

        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal moved))
        {
            malformed = $"'{value}' is not a number.";
            return AdmissionVerdict.Refused(malformed);
        }

        SelectionRule baseline = SelectionRule.For(direction);

        if (baseline.Find(threshold) is null)
        {
            malformed =
                $"the {direction} rule has no threshold named '{threshold}'. Its movable ones are "
                + string.Join(", ", SelectionReplay.Movable(baseline).Select(t => t.Name)) + ".";
            return AdmissionVerdict.Refused(malformed);
        }

        return SelectionReplay.AssertAdmissible(baseline.With(threshold, moved), baseline);
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
