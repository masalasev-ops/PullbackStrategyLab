using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Data;
using PullbackStrategyLab.Tests.Support;
using PullbackStrategyLab.Worker.Stages;
using Xunit;
using Xunit.Abstractions;

namespace PullbackStrategyLab.Tests.Checks;

/// <summary>
/// No signal or check reads a value whose observation could not have been made by its own date.
///
/// <b>The single most important property in the system,</b> because breaking it produces an
/// encouraging result that means nothing. A replay that can see Tuesday's correction while answering
/// Monday's question does not answer Monday's question; it answers a question nobody can trade, and
/// every figure downstream inherits that without anything looking wrong.
///
/// Three halves, and the third is the one a convention could not hold.
///
/// <b>The readers.</b> Every public read in <c>PullbackStrategyLab.Data</c> takes a date, and none of
/// them offers an overload that does not. A read that could omit it would compile, run, and answer.
///
/// <b>The statements written by hand.</b> Stages and the read surface write SQL of their own, and a
/// statement selecting from a table that carries an observation stamp has to bound that stamp. This
/// is where the convention fails on its own: a reader whose signature demands a date proves nothing
/// about a query somebody wrote beside it, and three such queries were in the shipped source when
/// this check was written.
///
/// <b>The behaviour.</b> A row observed after the as-of instant is invisible, and the same row is
/// visible once the as-of moves past it. Both directions, because a reader that returned nothing at
/// all would satisfy the first.
/// </summary>
public sealed class PointInTimeCheck
{
    private readonly ITestOutputHelper _output;

    public PointInTimeCheck(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The tables carrying an observation stamp, and the column a read of them has to bound.
    ///
    /// Named here rather than derived from the migrations, and the trade is deliberate: a derivation
    /// would find every column ending in `_at` and would quietly stop finding one that was renamed,
    /// where a list that goes stale fails against the migration text in the test below.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Stamped { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["daily_bar"] = "observed_at",
            ["index_bar"] = "observed_at",
            ["intraday_bar"] = "observed_at",
            ["spread_snapshot"] = "observed_at",

            // The anchored level is read by a gate, so "what the anchored average was, as far as
            // the lab could know by this date" is a point-in-time question in the strictest sense:
            // a replay seeing a level computed after the night it is answering would pass a clause
            // the night itself could not have. `vwap_run` is not here, on the same grounds
            // `intraday_fetch` is not: nothing reads it to decide an answer.
            ["anchored_vwap"] = "observed_at",

            // The pass row is here rather than in NotAnObservation beside `intraday_fetch`, which it
            // otherwise resembles, and the difference is that something reads it to decide an answer.
            // Whether a session was sampled at all is what the spread reader refuses on, so "sampled,
            // as far as the lab could know by this date" is a point-in-time question and a replay
            // that saw a pass recorded after the instant it is answering would refuse differently
            // from the night itself. `intraday_fetch` is exempt because nothing reads it that way.
            ["spread_pass"] = "observed_at",

            // The plan is read to decide an answer, which is what puts it here rather than beside
            // `plan_run` below. A resolver asks what was resting when a session opened, so a replay
            // standing at an old session that saw a plan written after it would resolve a fill the
            // night itself could not have. The plan is immutable and keyed on the setup, so the
            // bound will rarely exclude a row today; that is a fact about the writer rather than a
            // property of the read.
            ["trade_plan"] = "observed_at",

            // A resolution is an observation about a session, and it is read to decide an answer:
            // 4.6 fills the earliest trigger of a session and blocks the later ones, so a replay
            // standing at an old date that saw a resolution written after it would fill an order the
            // night could not have placed.
            ["trigger_resolution"] = "observed_at",

            // An order is an observation about a session and is read to decide an answer: 4.9
            // compares planned against executed, so a replay standing at an old date that saw an
            // order written after it would audit a position the night could not have held.
            ["trade_order"] = "observed_at",
            ["position"] = "observed_at",
            ["fill"] = "observed_at",

            // A trade is read to decide an answer: LossClassifier at 4.10 classifies a closed loss
            // and the scoreboard scores what closed, so a replay standing at an old date that saw a
            // trade written after it would classify a loss the night could not have had. The audit
            // is on the same footing, being a reading of a trade.
            ["trade"] = "observed_at",
            ["plan_audit"] = "observed_at",

            // A classification is read to decide an answer from 4.11, where the journal page shows
            // each loss with its cause, and from phase 5 where a variant's losses are compared. It
            // also carries a second stamp for the aftermath, so a replay standing between the close
            // and the horizon sees a mechanism and nothing else.
            ["loss_class"] = "observed_at",
            ["corporate_action"] = "observed_at",
            ["indicator_daily"] = "computed_at",
            ["history_refetch"] = "refetched_at",
            ["security"] = "sector_resolved_at",
            ["setup_signal"] = "computed_at",

            // Phase 3's four stamps, and the correction mark added at 3.8. None of the five was in
            // this list when it was written, and nothing anywhere asked the other direction: the
            // test below reconciles name to migration and no assertion reconciles migration to name,
            // so a table gaining a stamp joined the corpus and this list did not notice.
            //
            // Adding them turned eight reads red, being two `scoreboard` and two `ceiling_bound` in
            // ScoreboardBuilder and LabScoreboard and four `control_setup`. None was a wrong result
            // on the day it was found, and the reason is two guards deep rather than one. A control
            // row is transitively bounded by the setup date its query already bounds, which holds
            // only while the draw happens on the setup's own night and is a property of the schedule
            // rather than of the query. And the scoreboard cannot be rebuilt in place at all: its
            // insert is ON CONFLICT DO NOTHING, so a second build for a date that already has panels
            // writes none of them. What reaches a row is a store restored from a snapshot and re-run,
            // or panels deleted and rebuilt, which is what StampBoundTests does.
            ["control_setup"] = "drawn_at",
            ["forward_return"] = "filled_at",

            // The reconstructed pair, on exactly the terms of the two above and not exempted for
            // being calibration. A reconstructed read is still a read: its filler bounds bars on the
            // fill instant and its sampler bounds the draw, and the whole reason those bounds are
            // there is that a replay can hold draws made after the instant being answered for. The
            // rows are not evidence; the reads that produce them obey the same rule.
            // see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
            ["calibration_control_setup"] = "drawn_at",
            ["calibration_forward_return"] = "filled_at",
            ["ceiling_bound"] = "computed_at",
            ["scoreboard"] = "computed_at",
            ["setup"] = "corrected_at",

            // And a sixth the brief did not count, which the reverse reconciliation below found.
            // `detector_error.observed_at` has been outside this list since 2.7. It is here because
            // the property is every observation stamp, and stopping at a number rather than at the
            // property is the failure this corpus keeps meeting from new directions.
            ["detector_error"] = "observed_at",

            // Added at 3.9(d). It was the last table feeding a point-in-time read with no
            // stamp at all, so a hit inserted for a past session was invisible to every bound
            // rather than merely unbounded by one. The 300 rows that predate the column were
            // backfilled from the `scans` run that wrote them, which recorded both instants
            // and a row count that matches exactly, so the honest answer was available and a
            // null was not needed. Where a null does occur, a read of a session other than the
            // row's own refuses it.
            ["scan_hit"] = "observed_at",
        };

    /// <summary>
    /// Tables carrying a column shaped like a stamp that is not an observation stamp, with the
    /// reason each is not in <see cref="Stamped"/>.
    ///
    /// This exists so the reconciliation can run in the direction that was missing. Reading name to
    /// migration says every name is real; reading migration to name says every real one is named,
    /// and only the second could have noticed four stamps arriving in phase 3, a fifth at 3.8 and a
    /// sixth sitting outside since 2.7.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotAnObservation { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run_log"] =
                "started_at and ended_at are when a job ran, which is operational rather than evidential. "
                + "Nothing computes a figure about the market from a run entry.",
            ["indicator_rebuild"] =
                "requested_at and rebuilt_at are the two ends of a demand rather than observations of the "
                + "market. The demand is state: it is raised, and it is satisfied.",
            ["intraday_fetch"] =
                "observed_at is when one night's minute-bar fetch ran and what it reached, which is "
                + "operational on the same terms as run_log. Nothing computes a figure about the market "
                + "from it: the bars it counts are in intraday_bar, which is stamped and bounded.",
            ["vwap_run"] =
                "observed_at is when one night's averaging ran and what it reached, which is operational "
                + "on the same terms as intraday_fetch above. Nothing computes a figure about the market "
                + "from it: the levels it counts are in anchored_vwap, which is stamped and bounded.",
            ["plan_run"] =
                "observed_at is when one evening's plan stage ran and what it refused, which is operational "
                + "on the same terms as vwap_run above. Nothing computes a figure about the market from it: "
                + "the plans it counts are in trade_plan, which is stamped and bounded.",
            ["fill_run"] =
                "what one evening's fill stage priced and what it could not, on the same terms as "
                + "order_run below. The positions it counts are in position, which carries three stamps "
                + "and is bounded on all three.",
            ["loss_run"] =
                "what each of the classifier's two passes wrote, on the same terms as trade_run below. "
                + "The classifications it counts are in loss_class, which is stamped twice and bounded "
                + "on both.",
            ["trade_run"] =
                "what one evening's journal wrote, on the same terms as manage_run below. The trades it "
                + "counts are in trade, which is stamped and bounded.",
            ["audit_run"] =
                "what one evening's audit read and wrote, on the same terms as trade_run above. The audits "
                + "it counts are in plan_audit, which is stamped and bounded.",
            ["manage_run"] =
                "what one evening's two rule sets closed, trimmed and armed, on the same terms as "
                + "fill_run above. The positions it counts are in position, which carries three stamps "
                + "and is bounded on all three.",

            // `fill_before_045` sat here from 4.8 to 5.0(c) as the transient name migration 045
            // renames `fill` to while redeclaring it. It is gone because the entry was doing the
            // opposite of what it read as doing: the rename-follow above sent `fill` itself here, so
            // the real table was skipped and this reason covered it. A rename whose target is dropped
            // is no longer followed, the transient never appears in the parse, and `fill` is
            // reconciled under its own name, as is `loss_class` after 048 rebuilds it the same way.
            ["order_run"] =
                "observed_at is when one evening's gate ran and what it refused, which is operational on the "
                + "same terms as trigger_run below. Nothing computes a figure about the market from it: the "
                + "orders it counts are in trade_order, which is stamped and bounded.",
            ["trigger_run"] =
                "observed_at is when one session's replay ran, what it walked and what it could not ask, "
                + "which is operational on the same terms as plan_run above. Nothing computes a figure "
                + "about the market from it: the resolutions it counts are in trigger_resolution, which is "
                + "stamped and bounded.",
        };

    /// <summary>
    /// Stamps that are in <see cref="Stamped"/> and that no read bounds, with the reason.
    ///
    /// <b>Named here rather than left out of the list, which is the point.</b> A stamp missing from
    /// <c>Stamped</c> is a stamp nothing knows about; a stamp here is one the check knows about and
    /// has been told not to require, and the difference is that this entry is readable, is counted,
    /// and fails if its table stops carrying the column.
    ///
    /// One today. <c>setup.corrected_at</c> records that a check verdict was recomputed, and a
    /// correction is bounded to the night's own inputs by construction, so a corrected value is what
    /// that night should have recorded and a rebuild for that date ought to see it. Bounding it would
    /// reproduce the defect rather than the truth. The mark exists so an analysis can exclude
    /// corrected rows, which is a different question from what a night could see. It is also the only
    /// nullable stamp in the list, so the predicate every other one uses would hide every row that
    /// was never corrected, which is all of them but a few.
    /// see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotBounded { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["setup"] =
                "a correction reads only inputs bounded to the setup's own date, so the corrected value is what "
                + "that night should have recorded and a rebuild for that date should see it. The mark is for "
                + "excluding corrected rows from an analysis, not for hiding them from a rebuild. It is also the "
                + "one nullable stamp here, and the predicate the others use would hide every uncorrected row.",
            ["detector_error"] =
                "nothing reads these rows to decide anything. They are counted, and read by a person asking what "
                + "last night lost, and the stamp records when the failure was seen rather than bounding what may "
                + "be believed. Bounding it to the session's own day would hide the errors of a late rerun from "
                + "exactly the reader the table exists for, which is the opposite of the property. The table "
                + "joined the stamped list at 3.9 because every observation stamp belongs there; being stamped "
                + "and being a bound are different claims and this is the one table where they part.",
        };

    /// <summary>
    /// One statement that legitimately does not bound its stamp, named by a fragment of itself
    /// rather than by the file it sits in.
    ///
    /// <b>By statement rather than by file, because both files below hold a bounded read as well.</b>
    /// <see cref="Exempt"/> is keyed by file, and a file-level exemption on either of these would
    /// take the guard off the correct read sitting beside the exempt one. That is the narrowing this
    /// checkpoint is about, so the exemption added to permit a repair does not get to be an instance
    /// of it.
    ///
    /// Every fragment has to match a statement that is actually there. An exemption covering nothing
    /// is a comment that reads as a guard.
    /// </summary>
    public static IReadOnlyList<StatementExemption> ExemptStatements { get; } =
    [
        new("IndicatorDailyReader.cs", "WHERE ticker = @ticker AND as_of = @session",
            "Latest asks which computation of a session is newest, whenever it was made, so the engine can tell "
            + "a rerun that produces identical figures from a rebuild that produces different ones. The answer is "
            + "about the store's contents rather than about a night, and bounding it would make a rebuild blind "
            + "to its own prior row and write a duplicate. The evidence read in the same file takes an as-of and "
            + "bounds computed_at against it."),
        new("SetupSignalReader.cs", "SELECT signal_name FROM setup_signal WHERE setup_id = @setup_id",
            "NamesFor asks which signals are already frozen for one setup, which is what makes a rerun write "
            + "nothing. It is a question about what is in the store, it takes no date because it is not answering "
            + "for one, and bounding it would let a rerun write a second copy of a signal it had already frozen."),
        new("HistoryBackfill.cs", "LEFT JOIN universe_member m ON m.ticker = s.ticker",
            "ReadDelistedSecurities asks which securities the universe has never held, so the delisted fetch "
            + "knows what it may buy. It is a question about the store's contents rather than about a night: "
            + "sector_resolved_at is a lazily resolved attribute of the instrument and has nothing to do with "
            + "whether the name was ever a member, and bounding it would make every delisted name invisible "
            + "until something resolved its sector, which for a delisted name nothing ever will."),
        new("TradeOrderReader.cs", "SELECT order_id, observed_at",
            "ProvenanceOfEveryOrder is what `order-provenance` reads, and it asks whether a row exists in this "
            + "store that RiskGate did not write. That is a question about the whole store rather than about "
            + "what a session could have known, and bounding it on an as-of would let a row written outside a "
            + "run scope hide behind the bound, which is the one fault the read exists to find. It returns an "
            + "identity and an instant and no price, so nothing can compute a figure about the market from it. "
            + "The read in the same file that answers for a session is ForLiveSession, which takes an as-of and "
            + "bounds observed_at against it."),
        new("HistoryBackfill.cs", "SELECT DISTINCT ticker FROM history_refetch;",
            "ReadRefetchedTickers asks which names a backfill of any mode has already taken, which is what lets "
            + "a purchase spread across nights ask for each name once. Bounding it on the as-of would hide every "
            + "refetch made after that date and buy the same history again at one call a name, which is the "
            + "opposite of the property the read exists for. The other statement in this file that answers for a "
            + "night is DailyBarReader.Latest, which takes the observed instant and bounds on it."),
    ];

    /// <summary>
    /// Statements whose table is an interpolation hole rather than a name, with the reason each is
    /// not a point-in-time question.
    ///
    /// <b>Named because a statement this scan cannot resolve is one it was silently not asserting.</b>
    /// Until 4.17 the scanner required a quote immediately after `CommandText =` or a raw literal, so
    /// a statement built through an interpolated handler was matched by neither: not asserted, not
    /// exempted, and counted nowhere. The scanner reads them now, and a statement whose table is a
    /// hole cannot be matched against a stamped table by any amount of reading, so what is owed is
    /// that each one is placed by hand rather than that the pattern gets cleverer.
    ///
    /// A statement here has to be found. An exemption covering nothing is an exemption that has
    /// stopped applying, which is the same defect one level up.
    /// </summary>
    public static IReadOnlyList<StatementExemption> ExemptInterpolatedTables { get; } =
    [
        new("LabStatus.cs", "SELECT COUNT(*) FROM {table}",
            "a row count for the status band, over a table named by the two call sites above it. It is "
            + "a health figure about the store rather than an answer about a session: nothing computes "
            + "a figure about the market from it, and a band that said how many bars were stored as at "
            + "some earlier date would be answering a question nobody asked it. The count over "
            + "`daily_bar` is unbounded for that reason and is stated here rather than left to be "
            + "found again"),
        new("RunLogger.cs", "SELECT COUNT(*) FROM {table}",
            "the row-count baseline a run scope takes at its start and again at its end, over the "
            + "tables the stage declared it writes. The difference is `rows_written`, which is a "
            + "figure about a run rather than about the market, and a baseline bounded on an as-of "
            + "would measure the delta against a store the run could not have seen"),
        new("ReconstructedRead.cs", "SELECT COUNT(*) FROM {table}",
            "the before-and-after row count the reconstructed walk takes over the evidence tables, "
            + "which is how it asserts that a calibration run wrote into `calibration_setup` and "
            + "nowhere near `setup`. It is a count of the whole table on purpose: the property is "
            + "that the number did not move at all, and a bounded count would answer for one date "
            + "while a row written for another slipped past"),
        new("SetupReader.cs", "FROM {table}",
            "the evidence table or the calibration one, chosen by comparing against a constant so "
            + "nothing from outside reaches the statement. The read is bounded on `as_of`, which is "
            + "the session being asked about; `setup.corrected_at` is the row's own stamp and is "
            + "exempted above by name, where the reason it is not bounded is written out"),
    ];

    /// <summary>A statement exempted by a fragment of its own text, with the reason.</summary>
    public sealed record StatementExemption(string File, string Fragment, string Why);

    /// <summary>
    /// Statements that select from a stamped table and legitimately do not bound the stamp, by the
    /// file and the reason.
    ///
    /// Every entry is a read that is not answering a question as of a date. An exemption that could
    /// not say that about itself is a defect wearing a name, so each one states what it is instead.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Exempt { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IndicatorEngine.cs"] =
                "it asks which sessions have ever been computed, so it can skip them. The answer is about the "
                + "store's contents rather than about a night, and bounding it would recompute every session "
                + "the engine has already done.",
            ["MigrationRunner.cs"] =
                "migrations read and write structure rather than evidence.",
        };

    /// <summary>
    /// The one read that takes no date, by name and with the reason.
    ///
    /// Calibration mode reads membership as it stands today, deliberately, which is the survivorship
    /// bias its own table exists to quarantine. Exempting it by name rather than by shape is the
    /// whole point: a rule that let any read drop its date would let the next one drop it by
    /// accident, and this one is the only read in the lab that is entitled to.
    /// see: A calibration run reconstructs against current membership and computes its indicators in memory
    /// see: The evidence store holds only setups flagged forward, never setups reconstructed from history
    /// </summary>
    public static IReadOnlyDictionary<string, string> DatelessByName { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UniverseSnapshotReader.CurrentMembers"] =
                "calibration mode reads membership as it stands today on purpose, and the rows it produces go to a "
                + "table nothing downstream reads. Its name says which mode it is for, and the evidence read beside "
                + "it takes a date like everything else.",
        };

    [Fact]
    [Trait("check", "point-in-time")]
    public void No_signal_or_check_reads_a_value_observed_after_its_own_date()
    {
        var coverage = new CheckCoverage("point-in-time", _output);
        var failures = new List<string>();

        // 1. The readers. Every public read takes a date, and there is no overload that omits it.
        Type[] readers =
        [
            typeof(DailyBarReader), typeof(IndexBarReader), typeof(IndicatorDailyReader),
            typeof(ScanHitReader), typeof(SetupReader), typeof(SetupSignalReader),
            typeof(SecurityReader), typeof(CorporateActionReader), typeof(UniverseSnapshotReader),
            typeof(RegimeReader), typeof(SpreadSnapshotReader),
        ];

        int readsExamined = 0;

        foreach (Type reader in readers)
        {
            foreach (MethodInfo method in reader
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && Reads(m.Name)))
            {
                readsExamined++;

                if (!method.GetParameters().Any(p => p.ParameterType == typeof(DateOnly)
                        || p.ParameterType == typeof(DateOnly?))
                    && !DatelessByName.ContainsKey($"{reader.Name}.{method.Name}"))
                {
                    failures.Add(
                        $"{reader.Name}.{method.Name} reads the store and takes no date, so it can answer with "
                        + "figures the lab could not have had.");
                }
            }
        }

        // 2. The statements written by hand, outside the readers. This is the half a signature
        //    cannot hold: a query beside a reader is not bound by the reader's shape.
        int statementsExamined = 0;
        int stampedStatements = 0;
        int interpolatedTables = 0;
        var exemptionsMatched = new HashSet<StatementExemption>();
        var interpolatedMatched = new HashSet<StatementExemption>();
        var interpolationsSeen = new HashSet<string>(StringComparer.Ordinal);
        var unplacedInterpolations = new List<string>();

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            string source = RepositoryLayout.Read(file);
            string name = Path.GetFileName(file);

            foreach (string statement in Statements(source))
            {
                statementsExamined++;

                // A statement whose table is an interpolation hole cannot be matched against a
                // stamped table by any amount of reading, so it is placed by hand or it is a
                // finding. Reported either way, because the failure this repairs is a statement
                // counted nowhere.
                if (statement.Contains("FROM {", StringComparison.Ordinal))
                {
                    StatementExemption? placed = ExemptInterpolatedTables.FirstOrDefault(
                        e => string.Equals(e.File, name, StringComparison.Ordinal)
                             && statement.Contains(e.Fragment, StringComparison.Ordinal));

                    // Counted once per statement rather than once per match. The three readers above
                    // overlap and cut one literal at different points, so a single statement is
                    // yielded as several substrings; a scope reporting four for one statement would
                    // be a figure over a population other than the one its name gives, which is the
                    // fifth defect shape this corpus catalogues. A placed statement is identified by
                    // the fragment that placed it and an unplaced one by its own text.
                    interpolatedTables +=
                        interpolationsSeen.Add($"{name}: {placed?.Fragment ?? statement.Trim()}") ? 1 : 0;

                    if (placed is StatementExemption exemption)
                    {
                        interpolatedMatched.Add(exemption);
                    }
                    else if (!unplacedInterpolations.Contains($"{name}: {statement.Trim()}"))
                    {
                        unplacedInterpolations.Add($"{name}: {statement.Trim()}");
                    }
                }

                foreach ((string table, string stamp) in Stamped)
                {
                    if (!SelectsFrom(statement, table))
                    {
                        continue;
                    }

                    stampedStatements++;

                    StatementExemption? exemption = ExemptStatements.FirstOrDefault(
                        e => string.Equals(e.File, name, StringComparison.Ordinal)
                             && statement.Contains(e.Fragment, StringComparison.Ordinal));

                    if (exemption is not null)
                    {
                        exemptionsMatched.Add(exemption);
                        continue;
                    }

                    if (Bounds(statement, stamp)
                        || Exempt.ContainsKey(name)
                        || NotBounded.ContainsKey(table))
                    {
                        continue;
                    }

                    failures.Add(
                        $"{name} selects from {table} without bounding {stamp}, so it can see an observation made "
                        + "after the date it is answering for.");
                }
            }
        }

        // An exemption that matched nothing has gone stale, and a stale exemption reads as a guard
        // while covering a statement that is no longer there.
        foreach (StatementExemption stale in ExemptStatements.Where(e => !exemptionsMatched.Contains(e)))
        {
            failures.Add(
                $"the statement exemption for {stale.File} matched nothing. Its fragment "
                + $"\"{stale.Fragment}\" is in no statement in that file, so the exemption covers a read that has "
                + "moved or gone. Remove it, or point it at the statement it is now about.");
        }

        // 3. The behaviour, both directions.
        (bool hiddenBefore, bool visibleAfter) = FutureObservation();

        if (!hiddenBefore)
        {
            failures.Add("a bar observed after the as-of instant was visible to a read as of that date.");
        }

        if (!visibleAfter)
        {
            failures.Add(
                "the same bar was still invisible once the as-of moved past its observation, so the read is "
                + "returning nothing rather than bounding anything.");
        }

        // 4. The run log's stamp, which is read by two windows with different edges. A truncation to
        //    a date computes the vendor's quota day, which is correct for the budget read and wrong
        //    for the session read, and the two are indistinguishable once written that way. 3.12
        //    repaired the wrong one and left the right one, which is why no guard could exist until
        //    4.3 named the quantity: the pattern would have failed a correct use on the first file
        //    it read. Both windows now bound between two instants and the truncation is in nothing.
        //    It reads statements rather than file text, which is the difference between the property
        //    and a phrase: both files below carry the old expression in a comment explaining what it
        //    got wrong, and a scan over the source would fail on the record of the defect rather
        //    than on the defect.
        int runLogStatements = 0;

        foreach (string file in RepositoryLayout.ProductionSourceFiles)
        {
            string source = RepositoryLayout.Read(file);

            foreach (string statement in Statements(source))
            {
                if (!SelectsFrom(statement, "run_log"))
                {
                    continue;
                }

                runLogStatements++;

                if (statement.Contains("substr(started_at", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{Path.GetFileName(file)} truncates run_log.started_at to a date. That computes a vendor "
                        + "quota day, which is what VendorQuotaDay is for, and it is not a session night, which is "
                        + "what SessionBoundaries is for. Both bound between two instants so the two reads cannot be "
                        + "mistaken for each other.");
                }
            }
        }

        coverage
            .Examined("public reads on the store's readers", readsExamined)
            .Examined("statements selecting from a stamped table", stampedStatements)
            .Examined("statements whose table is an interpolation hole, placed by hand", interpolatedTables)
            .Examined("stamped tables the check knows about", Stamped.Count)
            .Examined("statements reading the run log", runLogStatements)
            // The four exemption counts are context and carry no floor, deliberately. A floor is a
            // minimum, so flooring an exemption count fails the run when an exemption is *removed*,
            // which is what fixing the gap under it looks like. Narrowing stays silent and
            // tightening goes red, which is the incentive backwards. What carries the property here
            // is the number of reads and statements examined, and those are floored above.
            .Context("exempted files, each with its reason", Exempt.Count)
            .Context("stamps in the list that no read bounds, each with its reason", NotBounded.Count)
            .Context("statements exempted by a fragment of themselves, each with its reason", ExemptStatements.Count)
            .Context("dateless reads exempted by name, each with its reason", DatelessByName.Count)
            .Examined("directions of the future-dated case", 2)
            .Context("SQL statements read across the shipped source", statementsExamined)
            .Scan("every public read on a store reader takes a date",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_bar_dated_after_the_as_of_date_is_invisible_to_a_read",
                    "a bar dated past the as-of is stored and then not returned, which is what a signature "
                    + "carrying a date is for. The signature is necessary and this is what it buys"))
            .Scan("every hand-written statement selecting from a stamped table bounds that stamp",
                CheckCoverage.Backing.Test(
                    "DailyBarIngestorTests.A_read_sees_the_figure_that_had_been_observed_by_its_as_of_date_and_not_the_correction",
                    "the same session is read from both sides of a correction's instant and gives two figures. "
                    + "That is what a bound does; the scan is what says every statement written by hand beside a "
                    + "reader has one, which is the half four unbounded queries were on the wrong side of"))
            .Scan("the run log's stamp is never truncated to a date",
                CheckCoverage.Backing.Test(
                    "VendorQuotaDayTests.The_two_spends_of_one_evening_land_in_the_quota_days_they_belong_to",
                    "two spends on one evening, one before the UTC date rolls and one after, are counted into "
                    + "two quota days and read back as one session. That is the property; this scan is what "
                    + "says the expression that used to answer both questions is in no file"));

        // Calibration mode reconstructs against membership as it stands today, deliberately, which
        // is why its rows go to a table nothing downstream reads. It is out of scope by design
        // rather than deferred: nothing can close it, because closing it would mean the lab had a
        // universe snapshot for a night it was not running.
        // see: The evidence store holds only setups flagged forward, never setups reconstructed from history
        coverage.OutOfScope(
            "reads made by a detector in calibration mode",
            DatelessByName.Count,
            CheckCoverage.OutOfScopeReason.ByDesign(
                "a calibration run reads membership as it stands today on purpose, which is the survivorship bias "
                + "its own table exists to quarantine. A point-in-time calibration run is not a stricter version of "
                + "this one; it is a run that cannot exist, because there is no record of who was listed on a night "
                + "the lab was not running"));

        coverage.Report();

        Assert.True(failures.Count == 0,
            $"{failures.Count} point-in-time failure(s):\n  " + string.Join("\n  ", failures));

        // Stated in advance, because every assertion above holds trivially over an empty sweep.
        Assert.True(readsExamined >= 15, $"Only {readsExamined} public read(s) found on the readers.");
        Assert.True(unplacedInterpolations.Count == 0,
            "These statements build their table through an interpolation, so no amount of reading can "
            + "match them against a stamped table and nothing here asserts or exempts them:\n  "
            + string.Join("\n  ", unplacedInterpolations)
            + "\n  Place each in ExemptInterpolatedTables with why it is not a point-in-time "
            + "question, or give it a literal table name.");

        Assert.True(
            interpolatedMatched.Count == ExemptInterpolatedTables.Count,
            "These interpolated-table exemptions matched no statement, so they exempt nothing and would "
            + "hide a real one silently: "
            + string.Join(", ", ExemptInterpolatedTables.Except(interpolatedMatched).Select(e => e.Fragment)));

        Assert.True(stampedStatements >= 5,
            $"Only {stampedStatements} statement(s) selecting from a stamped table were found outside the readers. "
            + "The scanner stopped matching rather than the source getting cleaner.");
    }

    /// <summary>
    /// The stamped-table list against the migrations, so a renamed column fails here.
    ///
    /// The list is named rather than derived, and this is what stops "named" turning into "stale":
    /// every table it claims exists and carries the column it claims, read from the SQL that creates
    /// them.
    /// </summary>
    [Fact]
    public void Every_stamped_table_the_check_names_carries_the_column_it_names()
    {
        string migrations = string.Concat(
            Directory.EnumerateFiles(
                Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations"), "*.sql")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        var missing = new List<string>();

        foreach ((string table, string stamp) in Stamped)
        {
            if (!migrations.Contains($"CREATE TABLE {table}", StringComparison.Ordinal))
            {
                missing.Add($"no migration creates {table}");
                continue;
            }

            if (!migrations.Contains(stamp, StringComparison.Ordinal))
            {
                missing.Add($"{table} is said to carry {stamp} and no migration mentions that column");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n  ", missing));
    }

    /// <summary>
    /// And the other direction: every table a migration gives a stamp-shaped column is named, either
    /// in <see cref="Stamped"/> or in <see cref="NotAnObservation"/> with its reason.
    ///
    /// <b>This is the assertion that was missing, and its absence is what let five stamps arrive
    /// unnoticed.</b> The test above reads the list and asks the migrations whether each entry is
    /// real, which catches a renamed column and cannot catch a new table. Phase 3 added four stamps
    /// and 3.8 added a fifth; none turned anything red, because nothing read in this direction, and
    /// `detector_error.observed_at` had been outside since 2.7 for the same reason.
    ///
    /// A one-way reconciliation against a hand-named list reports the instances somebody happened to
    /// look at, while the corpus it is read against keeps growing.
    /// </summary>
    [Fact]
    public void Every_stamped_column_a_migration_creates_is_named_by_this_check()
    {
        string migrations = string.Concat(
            Directory.EnumerateFiles(
                Path.Combine(RepositoryLayout.Source, "PullbackStrategyLab.Data", "Migrations"), "*.sql")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (Match table in Regex.Matches(
            migrations, @"CREATE TABLE (?<name>\w+) \((?<body>.*?)\n\);", RegexOptions.Singleline))
        {
            List<string> stamps =
            [
                .. Regex.Matches(table.Groups["body"].Value, @"^\s+(?<column>\w+_at)\s", RegexOptions.Multiline)
                    .Select(m => m.Groups["column"].Value),
            ];

            if (stamps.Count > 0)
            {
                found[table.Groups["name"].Value] = stamps;
            }
        }

        // The form a column added later arrives in, which price-storage-form still cannot see and
        // this one can. `setup.corrected_at` arrived that way at 3.8.
        foreach (Match added in Regex.Matches(migrations, @"ALTER TABLE (?<name>\w+) ADD COLUMN (?<column>\w+_at)"))
        {
            string name = added.Groups["name"].Value;

            if (!found.TryGetValue(name, out List<string>? stamps))
            {
                stamps = [];
                found[name] = stamps;
            }

            stamps.Add(added.Groups["column"].Value);
        }

        // A rebuild's intermediate table is reconciled under the name it ends up with.
        //
        // SQLite cannot relax a constraint in place, so a migration that needs to creates the table
        // under a working name, copies the rows, drops the original and renames. Migrations 005, 009
        // and 031 all do it. The two intermediates that existed before 031 were hand-listed in
        // NotAnObservation with a reason, which is the one-directional list this corpus keeps
        // finding: it works until the next rebuild, and then the next rebuild is a red run and a
        // third hand entry. Following the rename is the rule those entries were standing in for.
        //
        // <b>And a rename is followed only where the name it leads to survives.</b> The other
        // rebuild shape renames the live table out of the way, redeclares it under its own name,
        // copies and drops the transient: 045 did it to `fill` and 048 to `loss_class`. Followed
        // blindly, that rename sent `fill` to `fill_before_045`, which sat in NotAnObservation with
        // a reason, so the real `fill` was never reconciled at all and the entry read as covering
        // something. What tells the two shapes apart is the order of the drop: the working-name
        // shape drops the original and then renames onto its name, so the target is dropped before
        // the rename; the out-of-the-way shape renames and then drops the target. A rename whose
        // target is dropped after it is not followed, and the transient never appears in `found`
        // because nothing creates it.
        Match[] drops = [.. Regex.Matches(migrations, @"DROP TABLE (?<name>\w+)").Cast<Match>()];

        var renamedTo = new Dictionary<string, string>(StringComparer.Ordinal);
        var renamedOutOfTheWay = new List<string>();

        foreach (Match rename in Regex.Matches(
            migrations, @"ALTER TABLE (?<from>\w+) RENAME TO (?<to>\w+)"))
        {
            string to = rename.Groups["to"].Value;
            bool droppedAfterwards = drops.Any(
                d => d.Index > rename.Index && string.Equals(d.Groups["name"].Value, to, StringComparison.Ordinal));

            if (droppedAfterwards)
            {
                renamedOutOfTheWay.Add(to);
                continue;
            }

            renamedTo[rename.Groups["from"].Value] = to;
        }

        // Stated in advance for the same reason as the table count below: the two rebuilds of that
        // shape are known, and a parser that stopped seeing the drops would follow both renames again.
        Assert.True(
            renamedOutOfTheWay.Contains("fill_before_045", StringComparer.Ordinal)
                && renamedOutOfTheWay.Contains("loss_class_before_048", StringComparer.Ordinal),
            "the two transient names the rebuilds of fill and loss_class rename out of the way are not both "
            + "read as dropped after their rename, so the parser stopped seeing DROP TABLE rather than the "
            + $"rebuilds going away. Read: {string.Join(", ", renamedOutOfTheWay)}");

        var unnamed = new List<string>();

        foreach ((string found_, List<string> stamps) in found.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string table = renamedTo.TryGetValue(found_, out string? final) ? final : found_;

            if (NotAnObservation.ContainsKey(table))
            {
                continue;
            }

            if (!Stamped.TryGetValue(table, out string? named))
            {
                unnamed.Add(
                    $"{table} carries {string.Join(", ", stamps)} and this check names neither a stamp for it nor "
                    + "a reason it is not an observation.");
                continue;
            }

            if (!stamps.Contains(named, StringComparer.Ordinal))
            {
                unnamed.Add($"{table} is said to carry {named} and its migration declares {string.Join(", ", stamps)}.");
            }
        }

        // Stated in advance, because a parser that stopped matching would find no tables and no gaps,
        // and "no gaps" reads exactly like the property holding.
        Assert.True(found.Count >= 15,
            $"only {found.Count} table(s) with a stamp-shaped column were found in the migrations. There have "
            + "been at least fifteen since 3.5, so the parser stopped matching rather than the tables going away.");

        Assert.True(unnamed.Count == 0,
            $"{unnamed.Count} stamped table(s) this check does not name:\n  " + string.Join("\n  ", unnamed));
    }

    /// <summary>
    /// A bar observed tomorrow, read as of today and as of the day after.
    ///
    /// Written as a permanent case rather than as a break-and-revert, and asserted in both
    /// directions: a reader that returned nothing at all would satisfy "the future bar is invisible"
    /// perfectly.
    /// </summary>
    private static (bool HiddenBefore, bool VisibleAfter) FutureObservation()
    {
        using var root = new TemporaryDirectory();
        var connections = new StoreConnectionFactory(new PullbackStrategyLabPaths(root.Path));
        new MigrationRunner(connections).Apply();

        var asOf = new DateOnly(2026, 8, 24);
        DateTimeOffset tomorrow = new DateTimeOffset(2026, 8, 25, 22, 0, 0, TimeSpan.Zero);

        using SqliteConnection write = connections.OpenWrite();

        using (SqliteCommand security = write.CreateCommand())
        {
            security.CommandText = """
                INSERT INTO security (ticker, name, exchange, type, first_seen)
                VALUES ('AAAA', 'A', 'US', 'Common Stock', '2020-01-01')
                """;
            security.ExecuteNonQuery();
        }

        using (SqliteCommand bar = write.CreateCommand())
        {
            // The same session, twice: what the lab knew on the night, and a correction made the
            // following evening. The correction is the row a replay of that night must not see.
            bar.CommandText = """
                INSERT INTO daily_bar (ticker, bar_date, open, high, low, close, adj_close, volume, observed_at)
                VALUES ('AAAA', '2026-08-24', '10.00', '11.00', '9.00', '10.50', '10.50', 1000, '2026-08-24T22:00:00.000Z'),
                       ('AAAA', '2026-08-24', '10.00', '11.00', '9.00', '99.00', '99.00', 1000, @tomorrow)
                """;
            bar.Parameters.AddWithValue("@tomorrow", StoreText.TimestampToStorageText(tomorrow));
            bar.ExecuteNonQuery();
        }

        using SqliteConnection read = connections.OpenReadOnly();

        StoredDailyBar onTheNight = DailyBarReader.Read(read, "AAAA", asOf, 1)[0];
        StoredDailyBar afterwards = DailyBarReader.Read(read, "AAAA", asOf, 1, tomorrow)[0];

        return (onTheNight.Close == 10.50m, afterwards.Close == 99.00m);
    }

    private static bool Reads(string method) =>
        method is "Read" or "ReadCalibration" or "ReadDate" or "Latest" or "Members"
            or "CurrentMembers" or "ForTicker" or "MarketCap" or "Industry" or "SessionsStored"
            or "Series" or "Open" or "Demands";

    /// <summary>
    /// Whether a statement uses the stamp in a predicate, rather than merely naming it.
    ///
    /// <b>This was <c>statement.Contains(stamp)</c> until 3.10, and the store readers were not read
    /// at all.</b> Half two skipped every file under <c>PullbackStrategyLab.Data</c> on the ground
    /// that half one covered them, and half one only asserts that a method's signature carries a
    /// <see cref="DateOnly"/>. Between the two, nothing asserted that a reader's query bounded
    /// anything, and the containment test would not have caught it either: a statement naming the
    /// column in its <c>SELECT</c> list satisfied it. <c>SetupSignalReader.Read</c> was on the wrong
    /// side of both and selects <c>s.computed_at</c>, so it passed twice over.
    ///
    /// A bound is a comparison. The forms the shipped source uses are the operators against a
    /// parameter, the correlated <c>= (SELECT MAX(...))</c>, an equality against another table's
    /// stamp in a join, and <c>IS NULL</c> for the rows a backfill could not stamp. All four put the
    /// stamp next to an operator, and a mention in a projection does not.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private static bool Bounds(string statement, string stamp) =>
        Regex.IsMatch(
            statement,
            @"(?:\w+\s*\.\s*)?\b" + Regex.Escape(stamp) + @"\b\s*(?:<=|>=|<>|!=|<|>|=|\bIS\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    /// <summary>
    /// Whether a statement selects from this table, rather than merely mentioning its name.
    ///
    /// A word boundary on both sides, because `setup` is a prefix of `setup_signal` and a naive
    /// match would report every read of one as a read of the other.
    /// </summary>
    private static bool SelectsFrom(string statement, string table) =>
        Regex.IsMatch(statement, $@"\bFROM\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(statement, $@"\bJOIN\s+{Regex.Escape(table)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Every SQL statement in a source file, as the text between a command-text assignment and the
    /// end of its literal.
    ///
    /// Read whole rather than line by line, because the bound a statement puts on its stamp is
    /// usually several lines below the FROM clause and a per-line scan would report every one of
    /// them as unbounded.
    /// </summary>
    /// <summary>
    /// Every SQL statement written as a literal in a file, each yielded once.
    ///
    /// <b>Once is the repair.</b> Both passes below match a raw-string literal assigned to
    /// CommandText, so every such statement was yielded twice and the scope read 29 over roughly
    /// fifteen. A doubled scope is not a harmless miscount: it is the number a floor is set on, so a
    /// check that quietly halved its real coverage would still have cleared a floor set on the
    /// doubled figure. Yielded through a set keyed on the literal's position in the file, so the
    /// second pass adds only what the first could not see, and two arms of one conditional that
    /// happen to carry identical text are still two statements.
    /// </summary>
    private static IEnumerable<string> Statements(string source)
    {
        var seen = new HashSet<int>();

        foreach (Match match in Regex.Matches(
            source,
            """CommandText\s*=\s*(?<raw>"{3}(?<body>.*?)"{3}|"(?<line>(?:\\.|[^"])*)")""",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            Group group = match.Groups["body"].Success ? match.Groups["body"] : match.Groups["line"];

            if (group.Value.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && seen.Add(group.Index))
            {
                yield return group.Value;
            }
        }

        // And the conditional form, where a statement is chosen between two literals. Both arms are
        // statements and a scan that read only the assignment would see neither.
        foreach (Match match in Regex.Matches(
            source,
            "\"{3}(?<body>\\s*(?:SELECT|INSERT|UPDATE).*?)\"{3}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            Group group = match.Groups["body"];

            if (group.Value.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && seen.Add(group.Index))
            {
                yield return group.Value;
            }
        }

        // <b>And the interpolated forms, which this scan could not see until 4.17.</b> The two
        // above require a quote immediately after `CommandText =` or a raw literal, so a
        // statement built through an interpolated handler was matched by neither: it was not
        // asserted and it was not exempted either, and the check reported a coverage it did not
        // have. `LabStatus.CountRows` was one such statement and it counted `daily_bar` rows
        // with no bound at all.
        //
        // Any single-line literal opening with a verb, wherever it is assigned from. Wider than
        // the two above on purpose: this half is about the statements the assignment form hides,
        // so keying on the assignment is the mistake being repaired.
        foreach (Match match in Regex.Matches(
            source,
            "\\$?\"(?<body>\\s*(?:SELECT|INSERT|UPDATE|DELETE)[^\"]*)\"",
            RegexOptions.CultureInvariant))
        {
            Group group = match.Groups["body"];

            if (group.Value.Contains("SELECT", StringComparison.OrdinalIgnoreCase) && seen.Add(group.Index))
            {
                yield return group.Value;
            }
        }
    }
}
