using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Research;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Core.Trading;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// The panels the scoreboard shows, computed nightly and stored as they stood.
///
/// <b>Three bands, none denominated in money.</b> Band 0 asks whether the record is healthy. Band 1
/// asks whether the pattern exists at all, which is the project's central question and the one
/// phase 3 answers. Band 2 asks whether the lab can sort what it finds.
///
/// <b>Every panel carries its own count, and a number without one is not shown.</b> The failure this
/// whole system exists to avoid is reading a pattern in forty observations, and a scoreboard that
/// prints a figure with no denominator is the most efficient way to commit it.
///
/// <b>Band 2's loss-cause panels arrive at 4.10 with the classifier that fills them.</b> Four per
/// side: the share of losses whose mechanism was a gap, and the shares of the three aftermaths. The
/// two are over different populations and each says which, because a mechanism is known at the close
/// and an aftermath is not: a row still waiting on its ten-session horizon is out of the aftermath
/// denominator rather than silently counted as unclassified
/// (see: A loss awaiting its horizon carries no aftermath, and that is not the same as being
/// unclassified).
/// </summary>
public sealed class ScoreboardBuilder
{
    public const string Name = "scoreboard";

    /// <summary>How many rank deciles band 2 reports. Ten, because it is a decile curve.</summary>
    public const int Deciles = 10;

    /// <summary>
    /// The two populations this page computes over, named so a panel can say which it used.
    ///
    /// <b>Flagged is every setup the detectors recorded</b>, which is what ARCHITECTURE means by the
    /// word: its worked night is twenty-two flagged, of which fourteen pass every check, and all
    /// twenty-two are followed up. The evidence store's whole purpose is that a stock nobody bought
    /// is worth as much as one that filled.
    ///
    /// <b>Candidates are the subset that passed every gating check and carry a rank</b>, which a
    /// decile curve needs because a decile is a position in an ordering.
    ///
    /// They differ by three orders of magnitude at the calibrated thresholds, so a panel that cannot
    /// say which it used is a panel a reader will compare against the wrong one.
    /// see: The subject is the flagged setup population, not the trade log
    /// </summary>
    public const string Flagged = "every flagged setup";

    /// <summary>
    /// The classified losses, which is what a mechanism share is over.
    ///
    /// Every loss carries a mechanism from the night it closed, so this population is every row the
    /// classifier has ever written.
    /// </summary>
    public const string ClassifiedLosses = "every classified loss";

    /// <summary>
    /// The placed losses, which is what an aftermath share is over and is not the same population.
    ///
    /// A loss waiting on its ten-session horizon carries no aftermath, so it is out of this
    /// denominator rather than counted as unclassified. Folding the two together would make the
    /// unclassified share read as the ordinary state of every recent loss.
    /// </summary>
    public const string PlacedLosses = "every loss whose horizon has closed";

    /// <summary>The ranked subset, which is what a decile curve can be computed over.</summary>
    public const string Candidates = "capped candidates only";

    /// <summary>The population the degraded panel and its own denominator are both over.</summary>
    public const string NightsRun = "nights the lab ran a stage";

    /// <summary>The population band 3's library panels are over.</summary>
    public const string Library = "the signal library as the store holds it";

    /// <summary>What the degraded panel reads badly on, stated where the state is computed.</summary>
    public const string DegradedBar =
        "reads badly above 5% of the nights the lab ran, because a night the lab lost is more likely "
        + "to be a night something unusual happened and a series with those quietly absent flatters "
        + "every figure below it";

    /// <summary>
    /// What the roll-up panel says once versions exist, which is that there is no roll-up.
    ///
    /// The claim is that proposals made against a richer pack hit their targets more often, so one
    /// figure over every version would be a figure about no pack at all.
    /// </summary>
    public const string RatePerVersion =
        "the hit rate is by pack version and is never pooled across versions, so there is no figure "
        + "here. The count is how many versions exist, and each has a panel of its own beside this one";

    /// <summary>What band 3 says where no pack has ever been cut.</summary>
    public const string NoPackVersion =
        "no evidence pack has been cut, so there is no version to attribute a proposal to. The hit "
        + "rate is by pack version and a figure over no version is not a smaller figure";

    /// <summary>What a pack version's panel says where nothing it carried has been settled.</summary>
    public const string NothingAdmitted =
        "no proposal cut against this version has become a version and been settled, so the hit rate "
        + "would be a share over nothing. The count beside it is what has been filed";

    /// <summary>What the twin-spread panel says where the window found no pair.</summary>
    public const string NoTwinPairs =
        "no twin pair has been found, so there is no outcome spread to take a mean over. The count "
        + "beside it is how many setups the trailing window actually held";

    /// <summary>What the separating-signals panel says while no outcome has closed.</summary>
    public const string NothingSeparates =
        "no signal has been measured against the corrected threshold, because that measurement is "
        + "over closed outcomes and the pack screens rather than shows. The count beside it is the "
        + "library the measurement will be over";

    /// <summary>
    /// What a withheld band 1 panel says when what it lacks is sessions.
    ///
    /// <b>A constant rather than the tail of an interpolated sentence, from 4.11.</b> The two
    /// shortages are settled by completely different things: sessions arrive by waiting and control
    /// outcomes do not, so a panel that could not tell a reader which one is blocking would be
    /// telling them to wait for something waiting cannot fix. `surface-claims` names both sentences
    /// as text the scoreboard must carry, and until 4.11 each claim held a hand-written copy of the
    /// words this stage emits: the check rendered the copy and proved only that the template does
    /// not swallow a string. The claims resolve against these two members now.
    /// </summary>
    public const string SessionShortage = "a shortage of sessions rather than of evidence";

    /// <summary>What a withheld band 1 panel says when what it lacks is control outcomes.</summary>
    public const string ControlShortage =
        "a shortage of control outcomes rather than of time, and waiting does not fix it";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ScoreboardBuilder(
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

    /// <summary>
    /// The flag that writes a new generation of a date's panels beside the one it already carries.
    ///
    /// <b>The supported way to restate a night, from 5.8.</b> Until then the stage's own failure
    /// message offered restoring a snapshot or deleting the date's panels, which no declared writer
    /// does and which would make the stale reading unreadable, when the table's grain is that a
    /// panel can be read back as it stood. A rebuild inserts every panel again under its own
    /// instant and a reader takes the latest generation at or before its bound.
    /// see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
    /// </summary>
    public const string RebuildFlag = "--rebuild";

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool rebuild = args.Contains(RebuildFlag, StringComparer.Ordinal);
        string[] rest = [.. args.Where(a => !string.Equals(a, RebuildFlag, StringComparison.Ordinal))];

        DateOnly asOf = rest.Length > 0
            ? DateOnly.ParseExact(rest[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        ScoreboardResult result = Build(asOf, rebuild);

        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Panels} panel(s) written");
        Console.WriteLine($"{Name}: {result.WithInterval} carrying an interval, {result.Withheld} withheld for want of a sample");
        Console.WriteLine($"{Name}: {result.Attempted} attempted, {result.Skipped} skipped");

        if (result.Rebuilt)
        {
            Console.WriteLine(
                $"{Name}: rebuilt as a new generation computed at {result.ComputedAt:yyyy-MM-dd'T'HH:mm:ss'Z'}, "
                + $"beside {result.Superseded} panel(s) of earlier generations, which stay readable as they stood");
        }

        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        if (result.Outcome == RunOutcome.Failed && result.Skipped == result.Attempted && result.Attempted > 0)
        {
            Console.Error.WriteLine(
                $"{Name}: all {result.Skipped} panel(s) were skipped, so {asOf:yyyy-MM-dd} already carries panels and "
                + "nothing was rebuilt. A second build of a date writes nothing and would otherwise report a clean "
                + $"run. To restate the date, rerun with {RebuildFlag}, which writes a new generation of its panels "
                + "beside the one it carries; the earlier generation stays readable as it stood.");
        }

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>One day's panels.</summary>
    public ScoreboardResult Build(DateOnly asOf) => Build(asOf, rebuild: false);

    /// <summary>
    /// One day's panels, or a new generation of them.
    ///
    /// <b>Without <paramref name="rebuild"/> a date that already carries a panel is left as it is,
    /// and a build that wrote nothing fails.</b> The presence test is a read in the same
    /// transaction as the insert rather than the insert's own conflict, because the key carries the
    /// instant from 049 and a later instant cannot conflict with an earlier one; a read inside the
    /// transaction cannot disagree with the insert that follows it. <b>With it, every panel is
    /// written again under this run's instant</b>, whatever the date carried, and what it carried
    /// is counted rather than touched.
    /// see: A scoreboard rebuild writes a new generation of the date's panels, and the stale generation stays readable as it stood
    /// </summary>
    public ScoreboardResult Build(DateOnly asOf, bool rebuild)
    {
        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, "scoreboard");

        DateTimeOffset computedAt = _clock.UtcNow;
        var panels = new List<Panel>();

        panels.AddRange(Health(connection, asOf, _options.SessionZone));

        foreach (string direction in new[] { "long", "short" })
        {
            panels.AddRange(AgainstControls(connection, direction, asOf, computedAt));
            panels.AddRange(RankDeciles(connection, direction, asOf, computedAt));
            panels.AddRange(CeilingGap(connection, direction, asOf, _options.SessionZone));
            panels.AddRange(LossCauses(connection, direction, asOf, _options.SessionZone));
        }

        panels.AddRange(LoopLearning(connection, asOf, _options.SessionZone));

        int skipped = 0;
        int superseded = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (Panel panel in panels)
            {
                int carried = Generations(connection, transaction, asOf, panel, computedAt);

                if (carried > 0 && !rebuild)
                {
                    skipped++;
                    run.CountSkipped();
                    continue;
                }

                superseded += carried;

                if (!Insert(connection, transaction, asOf, panel, computedAt))
                {
                    // The same instant twice, which a clock that moves cannot produce and a fixed
                    // one can. Counted as skipped so a run that wrote nothing still says so.
                    skipped++;
                    run.CountSkipped();
                }
            }

            transaction.Commit();
        }

        // A build that wrote nothing at all is a no-op wearing a clean run. It happens when the date
        // already carries panels and no rebuild was asked for: the supported way to restate a past
        // date is the rebuild flag, which writes a new generation beside the old. Failing here
        // rather than refusing up front keeps a first build for a date working and an accidental
        // second run loud, which is the pair that matters.
        //
        // Some panels skipped and some written is a different thing and is not a failure: it means
        // the date gained a panel the earlier build did not produce. It is still reported.
        RunOutcome outcome = panels.Count > 0 && skipped == panels.Count
            ? RunOutcome.Failed
            : RunOutcome.Clean;

        RunSummary summary = run.Complete(outcome);

        return new ScoreboardResult(
            asOf,
            panels.Count,
            panels.Count(p => p.Low is not null),
            panels.Count(p => string.Equals(p.Figure, "withheld", StringComparison.Ordinal)),
            summary.RowsWritten,
            summary.CallsUsed,
            outcome,
            panels.Count,
            skipped,
            rebuild,
            superseded,
            computedAt);
    }

    /// <summary>
    /// How many generations of one panel the date already carries, read inside the transaction the
    /// insert runs in so the two cannot disagree, and bounded on this run's own instant, which is
    /// the latest generation a build standing now could be beside.
    /// </summary>
    private static int Generations(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly asOf, Panel panel, DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
              FROM scoreboard
             WHERE as_of = @as_of AND panel = @panel AND direction IS @direction
               AND computed_at <= @computed_before
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@panel", panel.Name);
        command.Parameters.AddWithValue("@direction", (object?)panel.Direction ?? DBNull.Value);
        command.Parameters.AddWithValue("@computed_before", StoreText.TimestampToStorageText(computedAt));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Band 0. Account-wide, so no direction: nights recorded, degraded runs, setups on file, and
    /// how much of the population rests on an answer that arrived late.
    ///
    /// <b>It reads red when degraded nights exceed 5% of the record</b>, because excluded nights are
    /// not missing at random: a night the lab lost is more likely to be a night something unusual
    /// happened, and a series with those quietly absent flatters every figure below it.
    ///
    /// <b>The corrections panel is the reader the correction mark needed.</b> The superseded rule
    /// recorded a mark "so a later reader can exclude corrected rows" and shipped with a guard that
    /// made corrected rows impossible, so the mark had neither a producer nor a consumer: a claim
    /// about a surface, asserted against a store. This is the surface. A reader who wants to know how
    /// much of a figure rests on a late answer can see the count and the worst lateness here rather
    /// than deriving it, and a corpus in which corrections became common would say so on the page
    /// rather than in a column nobody queries.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    private static IReadOnlyList<Panel> Health(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        int nights = Count(connection, "SELECT COUNT(DISTINCT as_of) FROM setup WHERE as_of <= @as_of", asOf, sessionZone);

        // The two figures the degraded panel is a ratio of, over one population and counted the same
        // way. Read in the session zone rather than grouped in SQL, because a night's late slots run
        // after midnight UTC and a group by UTC date would split one night across two.
        (int degradedNights, int ranNights) = DegradedNights(connection, asOf, sessionZone);
        int setups = Count(connection, "SELECT COUNT(*) FROM setup WHERE as_of <= @as_of", asOf, sessionZone);

        int corrected = Count(
            connection,
            "SELECT COUNT(*) FROM setup WHERE as_of <= @as_of AND corrected_at IS NOT NULL",
            asOf, sessionZone);

        // The worst lateness rather than the mean, because the question a bound invites is how close
        // anything came to it, and a mean over mostly-zero rows answers a different one.
        int worstLateness = Count(
            connection,
            "SELECT COALESCE(MAX(correction_lateness_minutes), 0) FROM setup WHERE as_of <= @as_of",
            asOf, sessionZone);

        // The weeks the seat has been asked at all, and the latest one that failed. Null where the
        // last ask succeeded: a refusal three weeks back that a later week answered over is history
        // rather than a live warning, and a band that went on showing it would be a band nobody
        // reads.
        int asks = Count(
            connection,
            "SELECT COUNT(*) FROM proposal WHERE observed_at <= @end_of_day",
            asOf, sessionZone);

        StoredProposal? refused = ProposalReader.LatestUnavailable(connection, asOf, sessionZone);

        return
        [
            new Panel("band0.nightsRecorded", null, nights.ToString(CultureInfo.InvariantCulture), null, null, nights, null, Flagged),
            // **Three populations until 6.8, and no two of them were a ratio.** The figure counted
            // distinct non-clean run instants, the count beside it was the number of nights any
            // setup was flagged on, and the label said "runs recorded", so the reader could not form
            // the ratio the caption asked for and the threshold could not be computed at all. Both
            // figures are now nights the lab ran a stage on, counted from the same rows in the same
            // zone, and the panel is named for what it counts.
            new Panel(
                "band0.degradedNights", null,
                degradedNights.ToString(CultureInfo.InvariantCulture), null, null,
                ranNights, null, NightsRun,
                ReadsBadly: ranNights > 0
                    && degradedNights > ranNights * MeasurementParameters.DegradedNightShare,
                ReadsBadlyBecause: DegradedBar),
            new Panel("band0.setupsOnFile", null, setups.ToString(CultureInfo.InvariantCulture), null, null, setups, null, Flagged),
            new Panel("band0.correctedRows", null, corrected.ToString(CultureInfo.InvariantCulture), null, null, setups, null, Flagged),
            new Panel("band0.worstLatenessMinutes", null, worstLateness.ToString(CultureInfo.InvariantCulture), null, null, corrected, null, "corrected rows"),

            // **A seat that cannot ask is a fact about the running lab, so it is shown the morning
            // it happens.** Nothing in the verification harness reaches the running lab, and a
            // queued week that is recorded and not shown is one the operator learns of a quarter
            // later from a gap in the proposal record. The reason names the transport that refused,
            // because what the operator does about a lapsed subscription and about an endpoint that
            // is switched off are different acts.
            // see: The seat runs on the subscription against claude-opus-5, and the API path stays live for the day the subscription stops
            // see: Every phase ends in a generated phase report, not in a page somebody looks at
            new Panel("band0.researcherSeat", null, refused is null ? "asked" : "not asked",
                null, null, asks, null, "weekly asks", null,
                refused is null
                    ? null
                    : $"the {refused.Transport} seat could not be asked on {refused.AsOf:yyyy-MM-dd}: "
                      + refused.UnavailableBecause),
        ];
    }

    /// <summary>
    /// How many nights the lab ran a stage on, and how many of those carried a run that was not
    /// clean.
    ///
    /// <b>Read in the session zone rather than grouped in SQL.</b> `run_log` carries an instant and
    /// no session, and a night's last slots fire at 21:50 and 22:00 Eastern, which is after midnight
    /// UTC. Grouping by the UTC date would put one night's early slots on one date and its late ones
    /// on the next, so a clean night would read as two nights and a degraded one as two degraded
    /// nights, and the ratio the panel is would be wrong in both directions at once.
    ///
    /// <b>Both figures come from the same rows.</b> That is the whole repair: the panel stated a
    /// count of run instants over a count of nights any setup was flagged on, and no two of its three
    /// numbers were a ratio.
    /// </summary>
    private static (int Degraded, int Ran) DegradedNights(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT started_at, outcome
              FROM run_log
             WHERE started_at <= @end_of_day
            """;

        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(asOf, sessionZone));

        var ran = new HashSet<DateOnly>();
        var degraded = new HashSet<DateOnly>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateOnly night = SessionBoundaries.SessionDateOf(
                StoreText.StorageTextToTimestamp(reader.GetString(0)), sessionZone);

            ran.Add(night);

            // A run with no outcome is one that started and has not finished, which is a running
            // stage rather than a degraded night. Counting it would make every build of an evening
            // read that evening as degraded, because the scoreboard's own run is open while it runs.
            if (!reader.IsDBNull(1) && !string.Equals(reader.GetString(1), "clean", StringComparison.Ordinal))
            {
                degraded.Add(night);
            }
        }

        return (degraded.Count, ran.Count);
    }

    /// <summary>
    /// Band 3. Whether the research loop is adding anything.
    ///
    /// <b>The only band that measures the AI, and it measures the evidence rather than the model.</b>
    /// The claim is that proposals made against a richer pack hit their targets more often; anything
    /// else is a story. So the hit rate is per pack version and never pooled across versions, on the
    /// same grounds the two sides are never pooled: one figure over every version would be a figure
    /// about no pack at all (see: The evidence pack is versioned, and the success criterion is
    /// proposal hit rate by pack version).
    ///
    /// <b>Every panel here is withheld today and every one carries a count.</b> A hit rate of nought
    /// over nought proposals reads as a loop that proposes nothing useful, and nought proposals is a
    /// fact about the record rather than a result. The count beside each is what a reader watches.
    ///
    /// <b>The library panels are three figures and not one.</b> Held, admitted and rejected at the
    /// correlation limit are counts of the same rows under different verdicts, and a single number
    /// would let a library that grew by rejections read as a library that grew.
    /// </summary>
    private static IReadOnlyList<Panel> LoopLearning(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        var panels = new List<Panel>();

        IReadOnlyList<StoredPackVersion> versions = PackVersionReader.Read(connection, asOf, sessionZone);

        // **Written on every build whether or not a version exists**, and that is not decoration.
        // A panel the builder stops writing keeps its last generation on the date, because a read
        // takes the latest generation of each panel and a panel with no new one has only the old:
        // the night the first pack is cut, a page would otherwise show "no pack has been cut"
        // beside the version it was just cut as. So this name is always here, and what it says
        // changes rather than whether it exists.
        panels.Add(new Panel(
            "band3.proposalHitRate", null, "withheld", null, null,
            versions.Count, null, "evidence pack versions, each with a panel of its own",
            WithheldBecause: versions.Count == 0 ? NoPackVersion : RatePerVersion));

        foreach (StoredPackVersion version in versions)
        {
            panels.Add(HitRate(connection, version, asOf, sessionZone));
        }

        panels.AddRange(LibraryPanels(connection, asOf, sessionZone));
        return panels;
    }

    /// <summary>
    /// One pack version's hit rate: of the proposals cut against it, how many became a version that
    /// was accepted.
    ///
    /// <b>The join is `variant.proposal_id`, added at 6.8 because it did not exist.</b> The success
    /// criterion is a rate over proposals and the settlement is a status on a version, and until this
    /// checkpoint nothing carried the second back to the first. A panel that could never compute its
    /// figure would have read withheld for ever for a reason about the build rather than about the
    /// evidence, which is the one thing a withheld panel must never do.
    ///
    /// <b>Withheld until something has been settled, and the count says what has been filed.</b> A
    /// version with proposals against it and none settled is the ordinary state of a pack for its
    /// first quarter, and a hit rate of nought there would read as a version that proposed only bad
    /// ideas.
    /// </summary>
    private static Panel HitRate(
        SqliteConnection connection, StoredPackVersion version, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN v.status IN ('accepted', 'rejected') THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN v.status = 'accepted' THEN 1 ELSE 0 END), 0)
              FROM proposal p
              LEFT JOIN variant v
                ON v.proposal_id = p.proposal_id AND v.created_at <= @observed_before
             WHERE p.observed_at <= @observed_before
               AND p.pack_version = @version
            """;

        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));
        command.Parameters.AddWithValue("@version", version.Version);

        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();

        int filed = reader.GetInt32(0);
        int settled = reader.GetInt32(1);
        int accepted = reader.GetInt32(2);

        string ordinal = version.Version.ToString(CultureInfo.InvariantCulture);
        string name = "band3.proposalHitRate.v" + ordinal;
        string population =
            "proposals filed against pack version " + ordinal
            + ", of which the ones that became a version and were settled";

        return settled == 0
            ? new Panel(name, null, "withheld", null, null, filed, null, population,
                WithheldBecause: NothingAdmitted)
            : new Panel(
                name, null, PairedInterval.Figure(accepted / (decimal)settled), null, null,
                settled, null, population);
    }

    /// <summary>
    /// The signal library beside the hit rate: what it holds, what it admitted, what it refused, and
    /// what the twins say about it.
    ///
    /// <b>The last two are the ones that would read as good news while saying nothing.</b> A count of
    /// signals separating outcomes beyond the corrected threshold is nought today because no outcome
    /// has closed, and a mean twin spread is absent because no pair exists; both are withheld with the
    /// count they will be over rather than rendered as nought.
    /// </summary>
    private static IReadOnlyList<Panel> LibraryPanels(
        SqliteConnection connection, DateOnly asOf, string sessionZone)
    {
        IReadOnlyList<StoredSignalDefinition> library =
            SignalDefinitionReader.Read(connection, asOf, sessionZone);

        int held = library.Count;
        int admitted = library.Count(
            s => string.Equals(s.LongOutcome, SignalVerdict.AdmittedOutcome, StringComparison.Ordinal)
              || string.Equals(s.ShortOutcome, SignalVerdict.AdmittedOutcome, StringComparison.Ordinal));
        int refused = library.Count(
            s => string.Equals(s.Status, SignalStatus.RejectedCorrelation, StringComparison.Ordinal));

        IReadOnlyList<TwinSideReading> twins = TwinPairReader.Read(connection, asOf, sessionZone);
        IReadOnlyList<StoredTwinPair> pairs = [.. twins.SelectMany(s => s.Pairs)];
        int windowSetups = twins.Sum(s => s.WindowSetups);

        return
        [
            new Panel("band3.signalsHeld", null, held.ToString(CultureInfo.InvariantCulture),
                null, null, held, null, Library),

            new Panel("band3.signalsAdmitted", null, admitted.ToString(CultureInfo.InvariantCulture),
                null, null, held, null, Library),

            new Panel("band3.signalsRefusedAtTheLimit", null, refused.ToString(CultureInfo.InvariantCulture),
                null, null, held, null, Library),

            // Withheld rather than nought, and the count is the library it will be measured over.
            new Panel("band3.signalsSeparatingOutcomes", null, "withheld", null, null, held, null,
                Library, WithheldBecause: NothingSeparates),

            // The window count rather than the pair count, because nought pairs over a window of four
            // and nought over a window of two hundred and fifty are different statements and only the
            // second says anything about the thresholds.
            pairs.Count == 0
                ? new Panel("band3.twinOutcomeSpread", null, "withheld", null, null, windowSetups, null,
                    "the setups the trailing window held, on both sides",
                    WithheldBecause: NoTwinPairs)
                : new Panel(
                    "band3.twinOutcomeSpread", null,
                    PairedInterval.Figure((decimal)pairs.Average(pair => pair.GapPoints)),
                    null, null, pairs.Count, null, "twin pairs found on both sides"),
        ];
    }

    /// <summary>
    /// Band 1. The flagged population against each control set, as a paired difference with an
    /// interval.
    ///
    /// <b>Paired, and the pairing is what makes it honest.</b> A setup's difference is its own return
    /// less the mean of its own matched controls, so the market factor the two share cancels rather
    /// than being adjusted for. The nightly means are then resampled in blocks, because a ten-day
    /// label overlaps its neighbours and an interval that ignored that would be too narrow exactly
    /// where confidence matters most.
    /// see: The interval is a studentised moving-block bootstrap over paired differences, and the effective sample is measured
    /// </summary>
    private static IReadOnlyList<Panel> AgainstControls(
        SqliteConnection connection, string direction, DateOnly asOf, DateTimeOffset computedAt)
    {
        var panels = new List<Panel>();

        foreach (string set in new[] { "loose", "tight" })
        {
            IReadOnlyList<PairedInterval.Night> series = Series(connection, direction, set, asOf, computedAt);

            PairedInterval.Estimate? estimate = PairedInterval.Of(
                series, MeasurementParameters.BootstrapBlockSessions, MeasurementParameters.BootstrapDraws);

            if (estimate is null)
            {
                // Withheld rather than printed wide. A panel showing an interval built from three
                // nights invites a reading, and the count beside it is not enough to stop that.
                //
                // <b>The counts are reported anyway, and from the first night.</b> The figure is
                // withheld because it would be read; the counts are the thing a reader is supposed
                // to watch, because 3.6 fires on the effective one. They are meaningless for the
                // first fortnight, which a number climbing from nothing says better than a date on a
                // calendar does.
                panels.Add(new Panel(
                    $"band1.vs{Capitalise(set)}", direction, "withheld", null, null,
                    series.Sum(n => n.Pairs),
                    PairedInterval.EffectiveObservations(series),
                    Flagged,
                    MeasurementParameters.MinimumEffectiveObservations,
                    WithheldBecause(
                        Shortage.Measure(connection, direction, set, asOf, computedAt),
                        series.Count),
                    // The session count comes from the series rather than from an estimate, because
                    // on this branch there is no estimate: `Of` returned null. That is the branch a
                    // reader watches for the whole of the wait, so it is the branch on which the
                    // count most needs to be there, and reporting it only once an interval exists
                    // would hide it for exactly as long as it is the thing being waited for.
                    series.Count,
                    MeasurementParameters.MinimumSessions));
                continue;
            }

            panels.Add(new Panel(
                $"band1.vs{Capitalise(set)}",
                direction,
                PairedInterval.Figure(estimate.Mean),
                PairedInterval.Figure(estimate.Low),
                PairedInterval.Figure(estimate.High),
                estimate.Rows,
                estimate.EffectiveObservations,
                Flagged,
                MeasurementParameters.MinimumEffectiveObservations,
                // The sixth field of the estimate, which was computed and discarded from the day the
                // interval was written. `withheld_because` carried the session count in prose and is
                // null on exactly this branch, so once an interval existed the count vanished from
                // the panel at the point it began to decide how much the interval was worth.
                Sessions: estimate.Nights,
                MinimumSessions: MeasurementParameters.MinimumSessions));
        }

        return panels;
    }

    /// <summary>
    /// Why a band 1 panel is showing no figure, in words, on the panel.
    ///
    /// <b>It named the wrong cause for the whole of phase 3, and that is worse than naming none.</b>
    /// It branched on the length of the difference series alone, so an empty series always printed
    /// "no session has a closed horizon yet". The series was empty because nothing ever wrote a
    /// control outcome, so with thirty nights of closed horizons in the store the panel still said
    /// the horizons had not closed. <b>A diagnostic that points away from the defect sends a reader
    /// to wait for something that has already happened.</b> The shortage is now measured rather than
    /// inferred, and the panel names which of the four it is.
    ///
    /// <b>The four are settled by different things and they arrive in order.</b> Nothing flagged, so
    /// there is no subject. Flagged but no setup outcome closed, which is the ten sessions everybody
    /// expects to wait. Setup outcomes closed but no control outcome, which is a defect rather than a
    /// wait and now says so in those words. And pairs on too few sessions, which is the bootstrap's
    /// own floor and the only one the old text ever got right.
    ///
    /// <b>The minimum sample is not one of the four.</b> The bootstrap needs twice its block length
    /// of sessions whatever the rows carry; the minimum is a separate statement shown beside the
    /// counts. They can contradict each other, which is why both are on the panel: a fortnight of
    /// very wide nights reaches the minimum before it reaches twenty sessions.
    ///
    /// <b>The population is not one of the reasons and cannot be.</b> Band 1 reads `setup`; a
    /// historical detector run writes to `calibration_setup`, which nothing downstream reads. That is
    /// settled by construction rather than by waiting, which is exactly why a reader of a withheld
    /// panel should not be left wondering whether it is the cause.
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    private static string WithheldBecause(Shortage shortage, int sessions)
    {
        int needed = MeasurementParameters.BootstrapBlockSessions * 2;
        int horizon = MeasurementParameters.ScoringHorizonSessions;

        if (shortage.Setups == 0)
        {
            return "no setup has been flagged on this side yet, so there is nothing to compare";
        }

        if (shortage.ClosedSetupOutcomes == 0)
        {
            return $"{Count(shortage.Setups)} setup(s) flagged and none has closed its {horizon}-session horizon yet, so there is no series to take an interval over";
        }

        if (shortage.ClosedControlOutcomes == 0)
        {
            return $"{Count(shortage.ClosedSetupOutcomes)} setup outcome(s) have closed and no control outcome has, so no pair exists. That is {ControlShortage}";
        }

        if (sessions == 0)
        {
            return $"{Count(shortage.ClosedSetupOutcomes)} setup and {Count(shortage.ClosedControlOutcomes)} control outcome(s) have closed but none pair up on the same session, so there is no series to take an interval over";
        }

        if (sessions < needed)
        {
            return $"only {Count(sessions)} session(s) carry a pair and a block bootstrap needs {needed}, which is {SessionShortage}";
        }

        return $"{Count(sessions)} session(s) carry a pair and the blocks they form do not differ, so the interval would have no width. An interval of no width clears zero always and is withheld instead";
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Capitalise(string set) =>
        string.Concat(char.ToUpperInvariant(set[0]), set[1..]);

    /// <summary>
    /// Writes one panel, and says whether it wrote.
    ///
    /// <b>The return value is the whole point of this method having one.</b> The insert is
    /// <c>ON CONFLICT DO NOTHING</c>, so a build for a date that already carries panels writes none
    /// of them and, until 3.9(e), reported a clean run either way. A rebuild path that reports
    /// success having written nothing is the failure shape this lab keeps producing, and it is worse
    /// than a crash because the operator's next act is to go and read the panels they think they
    /// just rebuilt.
    /// </summary>
    private static bool Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly asOf,
        Panel panel,
        DateTimeOffset computedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO scoreboard
                (as_of, panel, direction, figure, low, high, n_rows, n_effective, population,
                 n_minimum, withheld_because, computed_at, n_sessions, n_minimum_sessions,
                 reads_badly, reads_badly_because)
            VALUES (@as_of, @panel, @direction, @figure, @low, @high, @n_rows, @n_effective,
                    @population, @n_minimum, @withheld_because, @computed_at, @n_sessions,
                    @n_minimum_sessions, @reads_badly, @reads_badly_because)
            -- No conflict target. The primary key does not constrain an account-wide panel,
            -- because SQLite treats nulls as distinct and `direction` is null on every band 0
            -- row; migration 030 adds the partial unique index that does. Naming the primary
            -- key here would raise on a violation of that index rather than skipping it.
            ON CONFLICT DO NOTHING
            """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@panel", panel.Name);
        command.Parameters.AddWithValue("@direction", (object?)panel.Direction ?? DBNull.Value);
        command.Parameters.AddWithValue("@figure", panel.Figure);
        command.Parameters.AddWithValue("@low", (object?)panel.Low ?? DBNull.Value);
        command.Parameters.AddWithValue("@high", (object?)panel.High ?? DBNull.Value);
        command.Parameters.AddWithValue("@n_rows", panel.Rows);
        command.Parameters.AddWithValue("@n_effective", (object?)panel.Effective ?? DBNull.Value);

        // The state and its reason, present together or absent together, which the store holds as a
        // CHECK. A panel stating no threshold carries neither: a condition written in prose beside a
        // figure is a caption and belongs on the page.
        command.Parameters.AddWithValue(
            "@reads_badly", panel.ReadsBadly is bool badly ? badly ? 1 : 0 : (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "@reads_badly_because", (object?)panel.ReadsBadlyBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@population", panel.Population);
        command.Parameters.AddWithValue("@n_minimum", (object?)panel.Minimum ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@withheld_because", (object?)panel.WithheldBecause ?? DBNull.Value);
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));
        command.Parameters.AddWithValue("@n_sessions", (object?)panel.Sessions ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@n_minimum_sessions", (object?)panel.MinimumSessions ?? DBNull.Value);

        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// What the store actually holds behind a withheld panel, so the reason can name the shortage
    /// rather than assume it.
    ///
    /// Three counts on the same bound as the panel itself. Measured per direction and per control
    /// set, because one side or one set can be short of controls while the other is not, and a
    /// single number covering both would send a reader to look at the wrong half.
    /// </summary>
    private sealed record Shortage(int Setups, int ClosedSetupOutcomes, int ClosedControlOutcomes)
    {
        public static Shortage Measure(
            SqliteConnection connection,
            string direction,
            string set,
            DateOnly asOf,
            DateTimeOffset computedAt)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM setup s
                    WHERE s.direction = @direction AND s.as_of <= @as_of),
                  (SELECT COUNT(*) FROM setup s
                     JOIN forward_return f
                       ON f.subject_id = s.setup_id AND f.subject_kind = 'setup'
                      AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                    WHERE s.direction = @direction AND s.as_of <= @as_of),
                  (SELECT COUNT(*) FROM setup s
                     JOIN control_setup c ON c.setup_id = s.setup_id AND c.control_set = @set
                                          AND c.drawn_at <= @computed_at
                     JOIN forward_return f
                       ON f.subject_id = c.control_id AND f.subject_kind = 'control'
                      AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                    WHERE s.direction = @direction AND s.as_of <= @as_of)
                """;
            command.Parameters.AddWithValue("@direction", direction);
            command.Parameters.AddWithValue("@set", set);
            command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
            command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
            command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

            using SqliteDataReader reader = command.ExecuteReader();

            return reader.Read()
                ? new Shortage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))
                : new Shortage(0, 0, 0);
        }
    }

    /// <summary>
    /// Band 2's first panel. Mean forward return by rank decile.
    ///
    /// A downward slope from the first decile to the tenth means the ordering carries information. A
    /// flat line means the rank is decorative and the nightly cap is truncating at random, which is a
    /// different failure from the pattern not working and would otherwise look the same.
    /// </summary>
    private static IReadOnlyList<Panel> RankDeciles(
        SqliteConnection connection, string direction, DateOnly asOf, DateTimeOffset computedAt)
    {
        var byDecile = new SortedDictionary<int, List<decimal>>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.rank, f.return_signed
              FROM setup s
              JOIN forward_return f
                ON f.subject_id = s.setup_id AND f.subject_kind = 'setup'
               AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
             WHERE s.direction = @direction AND s.as_of <= @as_of AND s.rank IS NOT NULL
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                int rank = reader.GetInt32(0);
                int decile = Decile(rank, direction);

                if (!byDecile.TryGetValue(decile, out List<decimal>? returns))
                {
                    returns = [];
                    byDecile[decile] = returns;
                }

                returns.Add(StoreText.StorageTextToRatio(reader.GetString(1)));
            }
        }

        return
        [
            .. byDecile.Select(d => new Panel(
                $"band2.decile{d.Key.ToString(CultureInfo.InvariantCulture)}",
                direction,
                PairedInterval.Figure(d.Value.Average()),
                null,
                null,
                d.Value.Count,
                null,
                Candidates)),
        ];
    }

    /// <summary>
    /// Which decile of its own side's ranking a setup sits in.
    ///
    /// <b>The denominator is the direction's own allocation, and it was the pooled total.</b>
    /// NightlyCap ranks each side separately and says so: "Ranked within a direction and never
    /// across". Dividing a per-direction ordinal by the pooled sixty put long ranks 1 to 40 into
    /// deciles 1 to 7 and short ranks 1 to 20 into deciles 1 to 4, so band2.decile5 through
    /// decile10 did not exist on the short side at all and the same decile label covered a rank of
    /// 6 out of 40 on one side and 6 out of 20 on the other.
    ///
    /// The panel's whole purpose is that a flat curve across the deciles means the rank is
    /// decorative, and a curve over four points on one side and seven on the other, whose labels
    /// mean different fractions of different orderings, cannot be read that way or compared
    /// between the two.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    public static int Decile(int rank, string direction) =>
        Math.Clamp(((rank - 1) * Deciles / Math.Max(1, Allocation(direction))) + 1, 1, Deciles);

    /// <summary>How many the cap takes on one side, which is the ordering a rank on that side is in.</summary>
    private static int Allocation(string direction) =>
        string.Equals(direction, Core.Detection.SetupDirection.Short, StringComparison.Ordinal)
            ? Core.Detection.NightlyCap.ShortAllocation
            : Core.Detection.NightlyCap.LongAllocation;

    /// <summary>
    /// Band 2's second panel. The gap between what was achieved and what was available.
    ///
    /// Read straight off `ceiling_bound` rather than recomputed, because two implementations of a
    /// bound would eventually disagree and the scoreboard would be the last place anyone looked.
    /// </summary>
    private static IReadOnlyList<Panel> CeilingGap(
        SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT bound, achieved, subjects FROM ceiling_bound
             WHERE direction = @direction AND as_of <= @as_of
               AND computed_at <= @computed_before
             ORDER BY as_of DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));

        // The bound is recomputed weekly, so a week can carry more than one row over its life and
        // the panel must read the one that existed on the night it is building. Bounding the as-of
        // alone picks the right week and can still read a bound computed afterwards.
        command.Parameters.AddWithValue("@computed_before", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        if (!reader.Read())
        {
            // No bound yet. Withheld rather than a gap of nought, which would read as "selection has
            // no room" when it means "nobody has measured anything".
            return [new Panel("band2.ceilingGap", direction, "withheld", null, null, 0, null, Flagged)];
        }

        decimal bound = StoreText.StorageTextToRatio(reader.GetString(0));
        decimal achieved = StoreText.StorageTextToRatio(reader.GetString(1));

        return
        [
            new Panel("band2.ceilingGap", direction, PairedInterval.Figure(bound - achieved),
                null, null, reader.GetInt32(2), null, Flagged),
        ];
    }

    /// <summary>
    /// The nightly mean paired difference, per session, for one direction and one control set.
    ///
    /// Each setup's difference is its own return less the mean of its controls' returns at the same
    /// horizon. A setup with no controls filled contributes nothing rather than contributing its own
    /// return against nought, which would be the comparison silently becoming an absolute figure.
    /// </summary>
    private static IReadOnlyList<PairedInterval.Night> Series(
        SqliteConnection connection, string direction, string set, DateOnly asOf, DateTimeOffset computedAt)
    {
        var nights = new List<PairedInterval.Night>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.as_of,
                   AVG(sf.return_signed_num - cf.control_mean) AS difference,
                   COUNT(*) AS pairs,
                   AVG((sf.return_signed_num - cf.control_mean)
                     * (sf.return_signed_num - cf.control_mean)) AS mean_square
              FROM setup s
              JOIN (SELECT subject_id, CAST(return_signed AS REAL) AS return_signed_num
                      FROM forward_return
                     WHERE subject_kind = 'setup' AND horizon_days = @horizon
                       AND filled_at <= @computed_at) sf
                ON sf.subject_id = s.setup_id
              JOIN (SELECT c.setup_id, AVG(CAST(f.return_signed AS REAL)) AS control_mean
                      FROM control_setup c
                      JOIN forward_return f
                        ON f.subject_id = c.control_id AND f.subject_kind = 'control'
                       AND f.horizon_days = @horizon AND f.filled_at <= @computed_at
                     WHERE c.control_set = @set AND c.drawn_at <= @computed_at
                     GROUP BY c.setup_id) cf
                ON cf.setup_id = s.setup_id
             WHERE s.direction = @direction AND s.as_of <= @as_of
             GROUP BY s.as_of
             ORDER BY s.as_of
            """;
        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@set", set);
        command.Parameters.AddWithValue("@horizon", MeasurementParameters.ScoringHorizonSessions);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@computed_at", StoreText.TimestampToStorageText(computedAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            double difference = reader.GetDouble(1);
            int pairs = reader.GetInt32(2);

            // How far this night's own pairs sat apart, which is what lets the night count as more
            // than one observation. The sample form, so a night of one pair disperses by nought
            // rather than by a number computed from itself.
            double spread = pairs < 2
                ? 0d
                : Math.Sqrt(Math.Max(
                    0d,
                    (reader.GetDouble(3) - (difference * difference)) * pairs / (pairs - 1)));

            nights.Add(new PairedInterval.Night(
                StoreText.StorageTextToDate(reader.GetString(0)),
                (decimal)difference,
                pairs,
                (decimal)spread));
        }

        return nights;
    }

    private static int Count(SqliteConnection connection, string sql, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@end_of_day", StoreText.EndOfSession(asOf, sessionZone));

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One panel. <c>Population</c> is which rows the figure was computed over, and it is not
    /// optional: two panels on this page use different populations and a figure that cannot say
    /// which is a figure a reader will compare with the wrong one.
    ///
    /// <c>Minimum</c> is what the effective count has to reach before the panel's question may be
    /// answered, and it is set on band 1 alone because band 1 is the panel a checkpoint fires on.
    /// </summary>
    /// <summary>
    /// One stored panel.
    ///
    /// <b><c>Sessions</c> and <c>MinimumSessions</c> are the second half of 3.6's trigger.</b> They
    /// are null on every panel no checkpoint fires on, exactly as <c>Minimum</c> is, and set
    /// together on band 1: a count with no minimum beside it, or a minimum with no count, would be
    /// half a condition rendered as though it were the whole one.
    /// </summary>
    /// <summary>
    /// Band 2's loss causes, as shares, for one direction.
    ///
    /// <b>Four panels over two populations, and each says which.</b> The gap share is over every
    /// classified loss, because a mechanism is known the night a trade closes. The three aftermath
    /// shares are over the losses whose horizon has closed, because a row still waiting carries no
    /// aftermath at all. Computing all four over one denominator would make the unclassified share
    /// read as the ordinary state of every recent loss, which is the opposite of what that value
    /// means (see: A loss awaiting its horizon carries no aftermath, and that is not the same as
    /// being unclassified).
    ///
    /// <b>Withheld rather than nought where the population is empty.</b> A failed-setup share of
    /// nought over no losses reads as a filter that never fails, and a lab with nothing on file is
    /// not a lab with good news.
    ///
    /// <b>The two sides are computed separately and never added.</b> The sentence this panel exists
    /// for is that a failed-setup share shrinking is evidence the filter improved and a noise share
    /// shrinking is evidence the execution improved, and those are two different wins with the same
    /// symptom on each side of the book (see: Long and short are never pooled into one figure).
    /// </summary>
    private static IReadOnlyList<Panel> LossCauses(
        SqliteConnection connection, string direction, DateOnly asOf, string sessionZone)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT mechanism, aftermath
              FROM loss_class
             WHERE direction = @direction
               AND closed_session <= @as_of
               AND observed_at <= @observed_before
            """;

        command.Parameters.AddWithValue("@direction", direction);
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@observed_before", StoreText.EndOfSession(asOf, sessionZone));

        var mechanisms = new List<string>();
        var aftermaths = new List<string>();

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                mechanisms.Add(reader.GetString(0));

                if (!reader.IsDBNull(1))
                {
                    aftermaths.Add(reader.GetString(1));
                }
            }
        }

        return
        [
            Share("band2.lossCause.gap", direction, mechanisms, LossMechanism.Gap, ClassifiedLosses),
            Share("band2.lossCause.noise", direction, aftermaths, LossAftermath.Noise, PlacedLosses),
            Share("band2.lossCause.failedSetup", direction, aftermaths, LossAftermath.FailedSetup, PlacedLosses),
            Share("band2.lossCause.unclassified", direction, aftermaths, LossAftermath.Unclassified, PlacedLosses),
        ];
    }

    /// <summary>
    /// One value's share of a population, withheld where the population is empty.
    ///
    /// The count is the population rather than the matches, which is what the panel is read
    /// against: a share of a half over two losses and over two hundred are different statements
    /// and the figure alone cannot tell them apart.
    /// </summary>
    private static Panel Share(
        string name, string direction, IReadOnlyList<string> over, string value, string population)
    {
        if (over.Count == 0)
        {
            return new Panel(name, direction, "withheld", null, null, 0, null, population);
        }

        decimal share = (decimal)over.Count(v => string.Equals(v, value, StringComparison.Ordinal))
            / over.Count;

        return new Panel(
            name, direction, PairedInterval.Figure(share), null, null, over.Count, null, population);
    }

    private sealed record Panel(
        string Name, string? Direction, string Figure, string? Low, string? High, int Rows,
        int? Effective, string Population, int? Minimum = null, string? WithheldBecause = null,
        int? Sessions = null, int? MinimumSessions = null,
        bool? ReadsBadly = null, string? ReadsBadlyBecause = null);
}

/// <summary>What one day's build produced.</summary>
public sealed record ScoreboardResult(
    DateOnly AsOf,
    int Panels,
    int WithInterval,
    int Withheld,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    int Attempted,
    int Skipped,
    bool Rebuilt = false,
    int Superseded = 0,
    DateTimeOffset ComputedAt = default);
