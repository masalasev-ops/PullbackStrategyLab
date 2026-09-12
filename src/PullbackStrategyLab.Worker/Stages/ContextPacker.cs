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
/// Builds the versioned evidence pack the researcher is shown, and records what that cut held.
///
/// <b>Aggregates only, and the reason is not cost.</b> At these rates the raw version is
/// affordable. A model handed raw rows will find patterns by rummaging, which is precisely what
/// pre-registration and the planted null exist to prevent, so the pack carries counts, deciles and
/// summaries and never the rows underneath.
///
/// <b>All ten sections are rendered, including the ones with nothing in them.</b> Five of the ten
/// rest on outcomes that have not closed and will render empty for months. Each carries its own
/// count and, where that count is nought, the sentence saying which shape of nothing it was, so an
/// almost-empty pack is legible as almost-empty rather than as a pack.
///
/// <b>The version is what the model was shown and judged under, and never what it was shown
/// about.</b> Two cuts on different nights over different evidence are the same version, which is
/// what makes proposal hit rate by pack version a comparison of anything at all. The realised
/// false-discovery bar is reported in the body and stays out of the tuple for that reason.
/// see: The realised false-discovery bar is a reading of a pack and the version carries the procedure
///
/// <b>Byte-stable across runs at one commit over one store state.</b> Every read this stage makes
/// is ordered explicitly, every number goes through the invariant culture, and no instant of
/// generation reaches the body. The run row carries the digest, so two cuts can be compared from
/// the store rather than only from a test holding two strings.
/// see: A pack version pins what the model saw, and byte-stability is what makes that claim checkable
/// </summary>
public sealed class ContextPacker
{
    public const string Name = "build-pack";

    /// <summary>Why a section over an unclosed horizon holds nothing. The commonest state today.</summary>
    public const string NoClosedOutcome =
        "no setup's scoring horizon has closed, so there is nothing yet to summarise";

    /// <summary>Why the ceiling section holds nothing: the bound has never been computed.</summary>
    public const string NoCeilingBound =
        "no ceiling bound has been computed, so there is no achieved-against-bound figure to state";

    /// <summary>Why the loss section holds nothing: nothing has been traded, so nothing has lost.</summary>
    public const string NoClosedLoss =
        "no trade has closed, so no loss has been classified";

    /// <summary>Why the twin section holds nothing: the finder has never run for this session.</summary>
    public const string NoTwinRun =
        "the twin finder has not run for this session, so there is no window to report";

    /// <summary>Why the variant section holds nothing: no proposal has ever been made.</summary>
    public const string NoProposalHistory =
        "no proposal has been made, so there is no history of what one predicted against what happened";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ContextPacker(
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

    /// <summary><c>build-pack [as-of]</c>.</summary>
    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        PackResult result = Build(asOf);

        if (result.RefusedBecause is string refused)
        {
            // No pack, no version, no ask. The night says which section it could not build rather
            // than cutting a short pack that would read as complete.
            //
            // **The exit code is what was recorded, not the fact of refusing.** A refusal the
            // design requires exits 0, because the scheduler's red is the operator's signal that
            // something needs looking at and this needs nothing. A section that broke still exits 1.
            Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, no pack was built, {refused}");
            Console.WriteLine(
                result.Outcome == RunOutcome.Failed
                    ? $"{Name}: a section could not be built, so this is a fault and not a refusal the design requires"
                    : $"{Name}: a refusal the design requires, so nothing here needs looking at");
            Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");
            return result.Outcome == RunOutcome.Failed ? 1 : 0;
        }

        Console.WriteLine(
            $"{Name}: as of {asOf:yyyy-MM-dd}, pack version {result.Version} "
            + $"({(result.VersionIsNew ? "new" : "reused")}), fingerprint {result.Fingerprint[..12]}");
        Console.WriteLine(
            $"{Name}: {result.Pack.Sections.Count} section(s) rendered, {result.Pack.SectionsEmpty} empty, "
            + $"{result.BodyBytes} byte(s), digest {result.BodyDigest[..12]}");
        Console.WriteLine(
            $"{Name}: long population {result.LongSetups} setup(s), short population {result.ShortSetups}, never added");
        Console.WriteLine(
            $"{Name}: {result.SignalsScreened} signal(s) screened, null control "
            + $"{(result.NullControlPlanted ? "planted" : "ABSENT")}");

        foreach (RenderedSection section in result.Pack.Sections.Where(s => s.Count == 0))
        {
            Console.WriteLine($"{Name}: section \"{section.Name}\" is empty, {section.EmptyBecause}");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One cut of the pack.</summary>
    public PackResult Build(DateOnly asOf)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "pack_version");

        DateTimeOffset observedAt = run.StartedAt;

        // Read before the version is settled, because the screened set is what the tuple is built
        // from and it comes out of the library.
        IReadOnlyList<StoredSignalDefinition> library =
            SignalDefinitionReader.Read(connection, asOf, _options.SessionZone);

        IReadOnlyList<ScoredSetup> longScored =
            ScoredSetupReader.Read(connection, SetupDirection.Long, asOf, _options.SessionZone);
        IReadOnlyList<ScoredSetup> shortScored =
            ScoredSetupReader.Read(connection, SetupDirection.Short, asOf, _options.SessionZone);

        // Screened, never shown: every signal the library declares is screened, including the
        // planted null, because the correction has to be paid on everything that was looked at.
        // see: The correction threshold scales with signals screened, not signals shown
        IReadOnlyList<string> screened = [.. library.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal)];

        // No claim has been measured, so the step-up has no p-values and there is no realised bar.
        // Passed explicitly rather than defaulted, so the empty case is a stated input.
        CorrectionReading correction = MultipleComparison.Read(screened.Count, []);

        var tuple = new PackVersionTuple(
            PackSections.Names,
            screened,
            correction.Form,
            correction.Level,
            correction.FamilyWiseThreshold,
            PackVersions.ModelNotChosen);

        string fingerprint = PackVersions.Fingerprint(tuple);

        IReadOnlyList<RenderedSection> sections;

        try
        {
            sections = BuildSections(connection, asOf, library, longScored, shortScored, correction);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // **A pack missing a section is not a smaller pack.** The correction is computed over
            // the signals screened, so a pack that dropped a section would carry a threshold for a
            // set it did not screen and every claim against it would be judged against the wrong
            // number. So nothing is written, no version is registered, no ask is made, and the
            // night records which section it could not build.
            return Refuse(connection, run, asOf, observedAt, failure, failure is DesignedRefusal);
        }

        int version;
        bool isNew;
        int rowsWritten;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            int? existing = PackVersionReader.VersionFor(connection, fingerprint);
            isNew = existing is null;
            version = existing ?? NextVersion(connection, transaction);

            if (isNew)
            {
                InsertVersion(connection, transaction, version, fingerprint, tuple, correction, observedAt);
            }

            var pack = new EvidencePack(asOf, version, fingerprint, sections);

            InsertRun(connection, transaction, asOf, version, pack, correction,
                longScored.Count, shortScored.Count, library, observedAt);

            transaction.Commit();

            RunSummary summary = run.Complete(RunOutcome.Clean);
            rowsWritten = summary.RowsWritten;

            return new PackResult(
                asOf, version, isNew, fingerprint, pack, pack.BodyDigest(), pack.BodyBytes(),
                longScored.Count, shortScored.Count, correction.SignalsScreened,
                library.Any(d => d.IsNullControl), rowsWritten, RunOutcome.Clean);
        }
    }

    /// <summary>
    /// A night that could not build every section: no pack, no version, and a row saying why.
    ///
    /// <b>The reason names the section rather than only the exception.</b> "The pack could not be
    /// built" is the same sentence whichever section failed, and the point of recording it is that
    /// a person reading a run of refusals can see whether it is one section every week or a
    /// different one each time.
    /// </summary>
    private static PackResult Refuse(
        SqliteConnection connection,
        RunScope run,
        DateOnly asOf,
        DateTimeOffset observedAt,
        Exception failure,
        bool byDesign)
    {
        string because = failure.Message;

        // **A refusal the design requires is not a failure and is not recorded as one.** It is the
        // stage doing what the corpus told it to, for as long as the condition holds, and the first
        // one holds until generation 1's gate set is written as a selection rule, which has no date
        // on it. Recorded as `failed` it puts a red beside a correct outcome every Saturday, which
        // is how a red beside a wrong one stops being read. `partial` is what `ResearcherSeat`
        // already records for this same event, so the two stages now agree rather than describing
        // one morning two ways.
        RunOutcome outcome = byDesign ? RunOutcome.Partial : RunOutcome.Failed;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pack_run
                    (as_of, version, body_digest, body_bytes, sections_rendered, sections_empty,
                     refused_because, signals_screened, false_discovery_bar, long_setups, short_setups,
                     null_control_planted, outcome, observed_at)
                VALUES
                    (@as_of, NULL, NULL, NULL, 0, 0, @refused_because, 0, NULL, 0, 0, 0, @outcome, @observed_at)
                """;

            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@refused_because", because);
            command.Parameters.AddWithValue("@outcome", outcome.ToStorageText());
            command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));
            command.ExecuteNonQuery();

            transaction.Commit();
        }

        RunSummary summary = run.Complete(outcome);

        return new PackResult(
            asOf, Version: 0, VersionIsNew: false, Fingerprint: string.Empty,
            Pack: new EvidencePack(asOf, 0, string.Empty, []),
            BodyDigest: string.Empty, BodyBytes: 0,
            LongSetups: 0, ShortSetups: 0, SignalsScreened: 0, NullControlPlanted: false,
            RowsWritten: summary.RowsWritten, Outcome: outcome)
        {
            RefusedBecause = because,
        };
    }

    /// <summary>
    /// The ten, in the order the document lists them.
    ///
    /// One method per section would read better and would let the list and the builders drift, so
    /// the switch is over <see cref="PackSections.Declared"/> itself: a section added to the
    /// declaration with no builder throws here rather than being silently dropped from the pack.
    /// </summary>
    private IReadOnlyList<RenderedSection> BuildSections(
        SqliteConnection connection,
        DateOnly asOf,
        IReadOnlyList<StoredSignalDefinition> library,
        IReadOnlyList<ScoredSetup> longScored,
        IReadOnlyList<ScoredSetup> shortScored,
        CorrectionReading correction)
    {
        var sections = new List<RenderedSection>();

        foreach (PackSection declared in PackSections.Declared)
        {
            try
            {
                sections.Add(BuildOne(declared, connection, asOf, library, longScored, shortScored, correction));
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // Named here rather than at the catch above, because "the pack could not be built"
                // is the same sentence whichever section failed and the section is the half a
                // reader of a run of refusals actually needs.
                //
                // **The kind survives the wrapping.** A refusal the design requires and a section
                // that broke read identically once both are an InvalidOperationException naming a
                // section, and the whole point of telling them apart is lost at the one line that
                // re-throws. So the wrapper preserves what it was handed.
                string named = $"the \"{declared.Name}\" section could not be built: {failure.Message}";

                throw failure is DesignedRefusal
                    ? new DesignedRefusal(named, failure)
                    : new InvalidOperationException(named, failure);
            }
        }

        return sections;
    }

    private RenderedSection BuildOne(
        PackSection declared,
        SqliteConnection connection,
        DateOnly asOf,
        IReadOnlyList<StoredSignalDefinition> library,
        IReadOnlyList<ScoredSetup> longScored,
        IReadOnlyList<ScoredSetup> shortScored,
        CorrectionReading correction)
    {
        {
            return declared.Name switch
            {
                "Rule in force" => RuleInForce(connection, asOf),
                "Population" => Population(connection, asOf),
                "Loss taxonomy" => LossTaxonomy(connection, asOf),
                "Ceiling gap" => CeilingGap(connection, asOf),
                "Signal conditionals" => SignalConditionals(longScored, shortScored),
                "Multiple comparison" => MultipleComparisonSection(correction),
                "Twin pairs" => TwinPairsSection(connection, asOf),
                "Variant history" => VariantHistory(connection, asOf),
                "Signal library" => SignalLibrarySection(library),
                "Planted null" => PlantedNull(library),
                _ => throw new InvalidOperationException(
                    $"PackSections declares \"{declared.Name}\" and this packer has no builder for it. A section "
                    + "declared and not built would be missing from every pack while the document went on "
                    + "describing it."),
            };
        }
    }

    /// <summary>
    /// The version running, and every threshold it holds per side.
    ///
    /// <b>This is the value a proposal moves from, and until 6.5 the pack did not carry it.</b> A
    /// proposal is the direction, the gate, the threshold name, the value it moves from and the
    /// value it moves to; the first four are nameable from the library and the fifth was in no
    /// section. `Variant history` is every past proposal and excludes the baseline by design, so the
    /// rule actually running was the one thing the pack never showed and a model asked for a
    /// proposal against it abstained, correctly, twice.
    /// see: The rule in force is the pack's first section, because a proposal moves a threshold from a value
    ///
    /// <b>The rule is read from the register and applied to the baseline, rather than printed from
    /// the constants.</b> The two are the same number today because nothing has been accepted, and
    /// they stop being the same the day the gate at 6.7 accepts a version. Printing the constants
    /// would go on being right until then and then be wrong with nothing to say so.
    ///
    /// <b>Every threshold is shown and the family says which may move.</b> Execution and recorded
    /// thresholds are in the list because a proposal naming one is refused for a reason the model
    /// can read here, rather than refused at the registry over a field it was never told about.
    /// see: Generation 1 opens with no version admissible in either family, and what reopens each is named
    /// </summary>
    private RenderedSection RuleInForce(SqliteConnection connection, DateOnly asOf)
    {
        // The baseline of the generation in force, from 7.11, rather than the first in the register's
        // order, which after a closed generation is the retired one.
        IReadOnlyList<StoredVariant> registered = VariantReader.LiveOn(connection, asOf, _options.SessionZone);

        StoredVariant? baseline = registered.FirstOrDefault(v => v.IsBaseline);

        // Once generation 1 is in force there is no selection rule a proposal could move a threshold
        // of, so the section cannot say what the model is asked to move, and a pack without it is not
        // a smaller pack. The refusal names why, and no ask is made.
        // see: Generation 1 opens with no version admissible in either family, and what reopens each is named
        if (baseline is not null && baseline.Generation >= GenerationOneChecks.Generation)
        {
            throw new DesignedRefusal(VariantAdmitter.GenerationOneSelectionRefused);
        }

        // Ordered by id so two cuts over one store state name the same version in the same words,
        // and accepted only: an open version is accumulating rather than running.
        IReadOnlyList<StoredVariant> accepted = [.. registered
            .Where(v => !v.IsBaseline && v.Status == VariantStatus.Accepted)
            .OrderBy(v => v.VariantId, StringComparer.Ordinal)];

        var lines = new List<string>
        {
            baseline is null
                ? "in force: the baseline, unregistered, so the version register names no row for it"
                : $"in force: {baseline.VariantId} (generation {Int(baseline.Generation)}, "
                  + $"{baseline.Family}, {baseline.Status})",
        };

        foreach (StoredVariant version in accepted)
        {
            lines.Add($"accepted over it: {version.VariantId}, {version.Moved?.Describe() ?? "no threshold moved"}");
        }

        foreach (string direction in (string[])[SetupDirection.Long, SetupDirection.Short])
        {
            SelectionRule rule = accepted
                .Where(v => v.Moved is MovedThreshold m && m.Direction == direction)
                .Aggregate(
                    SelectionRule.For(direction),
                    (current, v) => current.With(v.Moved!.ThresholdName, v.Moved.To));

            lines.Add($"{direction} gates: {string.Join(" ", rule.Gates)}");

            // Ordered by name with the gate as the tiebreak, because two sides share a threshold
            // name and one side can hold the same name under two gates.
            foreach (RuleThreshold threshold in rule.Thresholds
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ThenBy(t => t.Gate, StringComparer.Ordinal))
            {
                lines.Add(
                    $"{direction} {threshold.Name} (gate {threshold.Gate}, "
                    + $"{threshold.Family.ToString().ToLowerInvariant()}): {Money(threshold.Value)}");
            }

            lines.Add(
                $"{direction} movable by a selection proposal: "
                + $"{Int(rule.Thresholds.Count(t => t.Family == ThresholdFamily.Selection))} "
                + $"of {Int(rule.Thresholds.Count)}");
        }

        return RenderedSection.Of("Rule in force", lines);
    }

    /// <summary>
    /// Count, span, regime, setups per night, long against short.
    ///
    /// The two sides are two lines and are never added into one. A pack stating "367 setups" over a
    /// population that is two thirds long would let a proposal reason about a shape it cannot see.
    /// see: Long and short are never pooled into one figure
    ///
    /// <b>It said that from 6.4 and added the two sides anyway, in three of its own lines.</b>
    /// `setups`, `setups per session` and `passed every gate` were each a figure over both sides,
    /// under this comment, and the pack is the one surface written to be reasoned from rather than
    /// read. Found at the 7.12 sign-off and repaired at 7.13, before the first live cut. The rates
    /// are each over their own side's sessions, because a side that started later would otherwise be
    /// divided by nights it was never detected on.
    /// </summary>
    private RenderedSection Population(SqliteConnection connection, DateOnly asOf)
    {
        // The accumulated population rather than one session's, which is what the section is about.
        // Reading the single-session method here would have reported nought on every date the store
        // held no setups while the store held hundreds, and the pack would have said so in a
        // sentence a reader would have believed.
        SetupPopulation population = SetupReader.PopulationTo(connection, asOf);

        if (population.IsEmpty)
        {
            return RenderedSection.Empty("Population", "no setup has been flagged, so there is no population");
        }

        StoredRegime? regime = RegimeReader.Read(connection, asOf);

        var lines = new List<string>
        {
            $"long setups: {Int(population.Longs)} over {Int(population.LongSessions)} session(s)",
            $"short setups: {Int(population.Shorts)} over {Int(population.ShortSessions)} session(s)",
            $"long setups per session: {PerSession(population.Longs, population.LongSessions)}",
            $"short setups per session: {PerSession(population.Shorts, population.ShortSessions)}",
            $"long passed every gate: {Int(population.PassedEveryGateLong)}",
            $"short passed every gate: {Int(population.PassedEveryGateShort)}",
            $"sessions with a setup on either side: {Int(population.Sessions)}",
            $"span: {population.FirstSession:yyyy-MM-dd} to {population.LastSession:yyyy-MM-dd}",
            $"regime: {regime?.Label ?? "unlabelled"}",
        };

        return RenderedSection.Of("Population", lines);
    }

    /// <summary>
    /// One side's setups a session, or "none" where that side has no session to divide by.
    ///
    /// <b>A side with no row has no rate rather than a rate of nought.</b> Nought over nought is not
    /// a smaller number, and the section states each rate over its own side's sessions, so a side the
    /// store has never recorded has nothing to be per.
    /// </summary>
    private static string PerSession(int setups, int sessions) =>
        sessions == 0 ? "none" : Ratio((double)setups / sessions);

    /// <summary>The four causes and unclassified, with counts. Empty until a trade closes at a loss.</summary>
    private RenderedSection LossTaxonomy(SqliteConnection connection, DateOnly asOf)
    {
        IReadOnlyList<StoredLossClass> losses = LossClassReader.All(connection, asOf, _options.SessionZone);

        if (losses.Count == 0)
        {
            return RenderedSection.Empty("Loss taxonomy", NoClosedLoss);
        }

        // Grouped by mechanism, ordered by name rather than by count, so two packs over the same
        // evidence order the lines the same way whatever the counts happen to be.
        var lines = losses
            .GroupBy(l => l.Mechanism, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}: {Int(g.Count())} "
                + $"(long {Int(g.Count(l => l.Direction == SetupDirection.Long))}, "
                + $"short {Int(g.Count(l => l.Direction == SetupDirection.Short))})")
            .ToList();

        lines.Add($"awaiting a horizon: {Int(losses.Count(l => l.AwaitsItsHorizon))}");

        return RenderedSection.Of("Loss taxonomy", lines);
    }

    /// <summary>Achieved win rate against the computed bound, per side and never pooled.</summary>
    private RenderedSection CeilingGap(SqliteConnection connection, DateOnly asOf)
    {
        IReadOnlyList<StoredCeilingBound> bounds =
            CeilingBoundReader.Read(connection, asOf, _options.SessionZone);

        if (bounds.Count == 0)
        {
            return RenderedSection.Empty("Ceiling gap", NoCeilingBound);
        }

        return RenderedSection.Of("Ceiling gap",
            [.. bounds.Select(b =>
                $"{b.Direction}: achieved {Money(b.Achieved)} against a bound of {Money(b.Bound)}, "
                + $"gap {Money(b.Gap)} over {Int(b.Subjects)} subject(s) at {Int(b.HorizonDays)} day(s)")]);
    }

    /// <summary>
    /// Each signal in deciles against forward return, per side.
    ///
    /// <b>Empty today, and the reason is the calendar.</b> A decile table needs closed outcomes and
    /// no setup's scoring horizon has closed, so both sides are empty and the section says so once
    /// rather than printing two headers over nothing.
    /// </summary>
    private static RenderedSection SignalConditionals(
        IReadOnlyList<ScoredSetup> longScored, IReadOnlyList<ScoredSetup> shortScored)
    {
        if (longScored.Count == 0 && shortScored.Count == 0)
        {
            return RenderedSection.Empty("Signal conditionals", NoClosedOutcome);
        }

        var lines = new List<string>();

        foreach ((string direction, IReadOnlyList<ScoredSetup> scored) in
            (( string, IReadOnlyList<ScoredSetup>)[])[(SetupDirection.Long, longScored), (SetupDirection.Short, shortScored)])
        {
            if (scored.Count == 0)
            {
                lines.Add($"{direction}: no setup with a closed outcome, so no decile table");
                continue;
            }

            // Signals common to every row, ordered by name: a signal present on some rows and not
            // others would make each decile a different population.
            IReadOnlyList<string> signals = ScoredSetupReader.CommonSignals(
                scored, [.. SignalLibrary.Declared.Select(s => s.Name)]);

            foreach (string signal in signals.OrderBy(s => s, StringComparer.Ordinal))
            {
                lines.AddRange(Deciles(direction, signal, scored));
            }
        }

        return lines.Count == 0
            ? RenderedSection.Empty("Signal conditionals", NoClosedOutcome)
            : RenderedSection.Of("Signal conditionals", lines);
    }

    /// <summary>
    /// One signal's deciles on one side, with the sample count on every line.
    ///
    /// The count is on each line rather than on the header, because deciles over an uneven
    /// population have uneven bins and a header count would describe none of them.
    /// </summary>
    private static IEnumerable<string> Deciles(
        string direction, string signal, IReadOnlyList<ScoredSetup> scored)
    {
        // Ordered by value with the setup id as the tiebreak, so two rows carrying the same value
        // fall into the same decile in the same order on both machines.
        ScoredSetup[] ordered = [.. scored
            .OrderBy(s => s.Values[signal])
            .ThenBy(s => s.SetupId, StringComparer.Ordinal)];

        for (int decile = 0; decile < 10; decile++)
        {
            int from = decile * ordered.Length / 10;
            int to = (decile + 1) * ordered.Length / 10;

            if (to <= from)
            {
                continue;
            }

            ScoredSetup[] bin = ordered[from..to];

            yield return $"{direction} {signal} decile {Int(decile + 1)}: "
                + $"n {Int(bin.Length)}, mean outcome {Ratio(bin.Average(s => s.Outcome))}";
        }
    }

    /// <summary>
    /// How many signals were screened, and the two thresholds.
    ///
    /// Screened rather than shown, stated in the line itself so the pack cannot be read as having
    /// paid the correction on the subset a proposal ends up citing.
    /// see: The correction threshold scales with signals screened, not signals shown
    /// </summary>
    private static RenderedSection MultipleComparisonSection(CorrectionReading correction) =>
        RenderedSection.Of("Multiple comparison",
        [
            $"signals screened: {Int(correction.SignalsScreened)}",
            $"correction: {correction.Form}",
            $"level: {Ratio(correction.Level)}",
            $"false-discovery threshold: {CorrectionReading.Render(correction.FalseDiscoveryThreshold)}",
            $"family-wise threshold: {CorrectionReading.Render(correction.FamilyWiseThreshold)}",
            "a claim admitted under false-discovery control is provisional against the family-wise "
            + "figure recorded beside it",
        ]);

    /// <summary>
    /// Every qualifying pair of near-identical setups with divergent outcomes, per direction, and
    /// what each side's window held.
    ///
    /// The window figure is stated whether or not a pair was found, on the terms 6.3 set: a count
    /// of nought with no window beside it cannot say whether the thresholds refused everything or
    /// whether there was nothing to look at.
    ///
    /// <b>Every pair rather than a sample, and the document said a sample until 2026-09-08.</b> A
    /// cap is a number, and the only basis for one is a distribution of how many pairs a run
    /// qualifies, which a trailing window that has never been full cannot supply. The count line
    /// above the pairs is what makes a section too large to read visible on the pack it happens on
    /// see: The twin-pairs section is uncapped until the window has been full once
    /// </summary>
    private RenderedSection TwinPairsSection(SqliteConnection connection, DateOnly asOf)
    {
        IReadOnlyList<TwinSideReading> sides = TwinPairReader.Read(connection, asOf, _options.SessionZone);

        if (sides.Count == 0)
        {
            return RenderedSection.Empty("Twin pairs", NoTwinRun);
        }

        var lines = new List<string>();

        foreach (TwinSideReading side in sides)
        {
            lines.Add($"{side.Direction}: {Int(side.PairsFound)} pair(s) over a window of "
                + $"{Int(side.WindowSetups)} of {Int(side.WindowWanted)} setup(s), "
                + $"{Int(side.SignalsCompared)} signal(s) compared, {Int(side.CandidatePairs)} candidate(s)");

            if (side.EmptyBecause is string why)
            {
                lines.Add($"{side.Direction}: found none, {why}");
            }

            lines.AddRange(side.Pairs.Select(p =>
                $"{side.Direction} pair {p.PairId}: distance {Ratio(p.Distance)}, "
                + $"gap {Ratio(p.GapPoints)} point(s)"));
        }

        return RenderedSection.Of("Twin pairs", lines);
    }

    /// <summary>Every past proposal, what it predicted, what happened. Empty until one exists.</summary>
    private RenderedSection VariantHistory(SqliteConnection connection, DateOnly asOf)
    {
        IReadOnlyList<StoredVariant> variants = VariantReader.RegisteredBy(connection, asOf, _options.SessionZone);

        // The baseline is a version and is not a proposal: it was never predicted and never tested,
        // so counting it here would state a history of one where the history is none.
        IReadOnlyList<StoredVariant> proposed = [.. variants
            .Where(v => !v.IsBaseline)
            .OrderBy(v => v.VariantId, StringComparer.Ordinal)];

        if (proposed.Count == 0)
        {
            return RenderedSection.Empty("Variant history", NoProposalHistory);
        }

        return RenderedSection.Of("Variant history",
            [.. proposed.Select(v =>
                $"{v.VariantId}: {v.Moved?.Describe() ?? "no threshold moved"}, target {v.Target}, "
                + $"minimum sample {Int(v.MinimumSample)} {v.MinimumSampleUnit}, status {v.Status}")]);
    }

    /// <summary>
    /// The explicit list of computable signals, so the model knows what it may reference and asks
    /// when something is missing.
    ///
    /// Read from the store rather than from <see cref="SignalLibrary"/>, because the store carries
    /// the verdicts and the list in code carries only the specification. `signal-library`
    /// reconciles the two, so reading either is safe and reading the store is the one that can say
    /// what has been admitted.
    /// </summary>
    private static RenderedSection SignalLibrarySection(IReadOnlyList<StoredSignalDefinition> library)
    {
        if (library.Count == 0)
        {
            return RenderedSection.Empty("Signal library",
                "the library has not been seeded into the store, so there is no list to show");
        }

        return RenderedSection.Of("Signal library",
            [.. library.Select(d =>
                $"{d.Name}: {d.Status}, long {d.LongOutcome ?? "undecided"}, "
                + $"short {d.ShortOutcome ?? "undecided"}")]);
    }

    /// <summary>
    /// The one meaningless signal, named as itself.
    ///
    /// <b>It is in the pack under its own heading as well as in the conditional tables.</b> The
    /// tripwire works by a proposal citing it, and a section that named it would defeat the point
    /// if it also said it was meaningless; so this section states that a null control is planted
    /// and states which signal it is, without a word about what it means. A model reading the pack
    /// sees a signal in the deciles like any other.
    /// see: One meaningless signal is planted in the conditional tables
    /// </summary>
    private static RenderedSection PlantedNull(IReadOnlyList<StoredSignalDefinition> library)
    {
        StoredSignalDefinition? planted = library.FirstOrDefault(d => d.IsNullControl);

        if (planted is null)
        {
            return RenderedSection.Empty("Planted null",
                "no signal in the library carries the null-control flag, so the tripwire is not armed");
        }

        return RenderedSection.Of("Planted null", [$"control: {planted.Name}"]);
    }

    private static int NextVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM pack_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void InsertVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        string fingerprint,
        PackVersionTuple tuple,
        CorrectionReading correction,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pack_version
                (version, fingerprint, sections, signals_screened, signals_screened_count,
                 correction_form, correction_level, family_wise_threshold, model_identifier, created_at)
            VALUES
                (@version, @fingerprint, @sections, @signals_screened, @signals_screened_count,
                 @correction_form, @correction_level, @family_wise_threshold, @model_identifier, @created_at)
            """;

        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@sections", string.Join(",", tuple.Sections));
        command.Parameters.AddWithValue("@signals_screened", string.Join(",", tuple.SignalsScreened));
        command.Parameters.AddWithValue("@signals_screened_count", tuple.SignalsScreened.Count);
        command.Parameters.AddWithValue("@correction_form", tuple.CorrectionForm);
        command.Parameters.AddWithValue("@correction_level", StoreText.StatisticToStorageText(tuple.Level));
        command.Parameters.AddWithValue("@family_wise_threshold",
            correction.FamilyWiseThreshold is double fw
                ? StoreText.StatisticToStorageText(fw)
                : DBNull.Value);
        command.Parameters.AddWithValue("@model_identifier", tuple.ModelIdentifier);
        command.Parameters.AddWithValue("@created_at", StoreText.TimestampToStorageText(observedAt));

        command.ExecuteNonQuery();
    }

    private static void InsertRun(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly asOf,
        int version,
        EvidencePack pack,
        CorrectionReading correction,
        int longSetups,
        int shortSetups,
        IReadOnlyList<StoredSignalDefinition> library,
        DateTimeOffset observedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pack_run
                (as_of, version, body_digest, body_bytes, sections_rendered, sections_empty,
                 signals_screened, false_discovery_bar, long_setups, short_setups,
                 null_control_planted, outcome, observed_at)
            VALUES
                (@as_of, @version, @body_digest, @body_bytes, @sections_rendered, @sections_empty,
                 @signals_screened, @false_discovery_bar, @long_setups, @short_setups,
                 @null_control_planted, @outcome, @observed_at)
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@body_digest", pack.BodyDigest());
        command.Parameters.AddWithValue("@body_bytes", pack.BodyBytes());
        command.Parameters.AddWithValue("@sections_rendered", pack.Sections.Count);
        command.Parameters.AddWithValue("@sections_empty", pack.SectionsEmpty);
        command.Parameters.AddWithValue("@signals_screened", correction.SignalsScreened);
        command.Parameters.AddWithValue("@false_discovery_bar",
            correction.FalseDiscoveryThreshold is double fdr
                ? StoreText.StatisticToStorageText(fdr)
                : DBNull.Value);
        command.Parameters.AddWithValue("@long_setups", longSetups);
        command.Parameters.AddWithValue("@short_setups", shortSetups);
        command.Parameters.AddWithValue("@null_control_planted", library.Any(d => d.IsNullControl) ? 1 : 0);
        command.Parameters.AddWithValue("@outcome", RunOutcome.Clean.ToStorageText());
        command.Parameters.AddWithValue("@observed_at", StoreText.TimestampToStorageText(observedAt));

        command.ExecuteNonQuery();
    }

    /// <summary>An integer in the pack body, invariant so it is one string on both machines.</summary>
    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A statistic in the pack body, fixed so it does not reach exponent notation.</summary>
    private static string Ratio(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>A decimal in the pack body, through the same fixed form.</summary>
    private static string Money(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
}

/// <summary>
/// What one cut of the pack came to.
///
/// The two populations are separate fields with no total, which is the pooling rule made structural
/// rather than remembered: there is no field for a figure over both and nowhere for one to go.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record PackResult(
    DateOnly AsOf,
    int Version,
    bool VersionIsNew,
    string Fingerprint,
    EvidencePack Pack,
    string BodyDigest,
    int BodyBytes,
    int LongSetups,
    int ShortSetups,
    int SignalsScreened,
    bool NullControlPlanted,
    int RowsWritten,
    RunOutcome Outcome)
{
    /// <summary>
    /// Why no pack was written, on exactly the runs where none was.
    ///
    /// Null on every run that produced one, so a caller cannot read a reason off a successful cut.
    /// </summary>
    public string? RefusedBecause { get; init; }
}
