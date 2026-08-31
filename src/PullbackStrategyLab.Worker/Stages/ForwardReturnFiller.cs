using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PullbackStrategyLab.Core.Configuration;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// What every flagged setup and every one of its matched controls did over the next 1, 3, 5 and 10
/// sessions, traded or not.
///
/// <b>The clock the whole project runs on starts here.</b> Phase 3's answers need accumulated
/// outcomes and nothing substitutes for elapsed time, so a night not spent filling is a night the
/// lab never gets back.
/// see: Forward returns are recorded for every flagged setup, traded or not
///
/// <b>Both kinds, and the control half was missing until 3.5 was reopened.</b> This stage bound
/// `subject_kind` to the literal `setup` and read only the `setup` table, while
/// `ScoreboardBuilder.Series` joins outcomes on `subject_kind = 'control'`. So the control-mean
/// subquery matched nothing on every night, band 1's difference series was empty for every
/// direction and every set, and the panel was withheld with an effective count pinned at nought.
/// **3.6 fires on that count**, so the decision point the whole phase exists to reach could never
/// arrive, and the page said the shortage was a horizon that had not closed. Thirty nights of
/// closed horizons say otherwise.
///
/// A control's outcome is measured over the control's own bars, from the flagging setup's own
/// session, and **signed by the setup's direction rather than by anything of its own**. The paired
/// difference subtracts one from the other, so a control signed the market's way and a setup signed
/// the direction's way would make the comparison a sum of two unlike quantities on the short side
/// and nothing would say so.
/// see: Matched control populations are drawn nightly, loose and tight
///
/// <b>This is the one stage that reads bars dated after its subject's own date, by design, and it
/// is the sharpest point-in-time case in the system.</b> Every other read in the lab is bounded so
/// that a row observed after the as-of is invisible. This one must see the future of a setup, or it
/// has nothing to measure. The resolution is that <b>the fill's as-of is the fill date, not the
/// setup date</b>: the stage answers "what can the lab know today", the row carries `filled_at`
/// saying when that was, and a reader bounded on it sees exactly what was knowable when it asked.
/// Backdating the row to the night that flagged the setup is what would break the property, because
/// then a replay of that night would find an outcome the night could not have had.
/// see: A reader's signature does not establish point-in-time; the query does
///
/// <b>Written once per subject per horizon and never revised.</b> A horizon that has elapsed has one
/// answer; a restated bar arriving later is a correction to the market's record and not a licence to
/// rewrite an outcome the lab already acted on. The store's own key refuses the second write.
/// </summary>
public sealed class ForwardReturnFiller
{
    public const string Name = "forward-returns";

    /// <summary>The two subject kinds `forward_return` records, spelled once each.</summary>
    public const string SetupKind = "setup";

    public const string ControlKind = "control";

    private readonly StoreConnectionFactory _connections;
    private readonly RunLogger _runLogger;
    private readonly IClock _clock;
    private readonly PullbackStrategyLabOptions _options;

    public ForwardReturnFiller(
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

        // The nightly command line is unchanged and still fills the evidence store. A reconstructed
        // pass is reached through `Fill(asOf, SubjectTables.Calibration)` by the read that owns it,
        // never from here, so no flag on this stage can point it at the other population.
        DateOnly asOf = args.Length > 0
            ? DateOnly.ParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : _clock.SessionDate(_clock.UtcNow, _options.SessionZone);

        FillResult result = Fill(asOf);

        // The two kinds are reported separately and never added together. A single total would let
        // a night with every control outcome missing read as a healthy count, which is exactly the
        // state this stage was in for the whole of phase 3.
        Console.WriteLine($"{Name}: as of {asOf:yyyy-MM-dd}, {result.Subjects} setup(s) considered");
        Console.WriteLine($"{Name}: {result.Written} setup outcome(s) written, {result.NotYetElapsed} horizon(s) not yet elapsed");
        Console.WriteLine($"{Name}: {result.ControlSubjects} control(s) considered");
        Console.WriteLine($"{Name}: {result.ControlsWritten} control outcome(s) written, {result.ControlHorizonsNotYetElapsed} horizon(s) not yet elapsed");
        Console.WriteLine($"{Name}: {result.AcrossAHoliday} landed on a session later than the calendar horizon");
        Console.WriteLine(
            $"{Name}: {result.WithoutABarOnTheirOwnSession} skipped for having no bar on their own session");
        Console.WriteLine($"{Name}: {result.Outcome.ToStorageText()}, {result.RowsWritten} rows");

        return result.Outcome == RunOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// One fill pass. Every setup whose horizon has elapsed and whose outcome is not already
    /// recorded gets a row; everything else is left for a later night.
    /// </summary>
    public FillResult Fill(DateOnly asOf) => Fill(asOf, SubjectTables.Evidence);

    /// <summary>
    /// The same pass over either population, selected by the tables rather than by a flag.
    ///
    /// <b>The evidence path is the default overload above and is unchanged.</b> Everything here
    /// reads a table name off <paramref name="tables"/> where it read a literal before, and the
    /// arithmetic between the two is the same object code: a reconstructed outcome that disagreed
    /// with an evidence one would be a fact about which overload was called.
    /// see: A reconstructed read answers whether the pattern has anything in it, and never enters the evidence store
    /// </summary>
    public FillResult Fill(DateOnly asOf, SubjectTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        using SqliteConnection connection = _connections.OpenWrite();
        using RunScope run = _runLogger.Begin(connection, Name, tables.ForwardReturn);

        DateTimeOffset filledAt = _clock.UtcNow;

        IReadOnlyList<Subject> setups = Subjects(connection, asOf, filledAt, tables);
        IReadOnlyList<Subject> controls = ControlSubjects(connection, asOf, filledAt, tables);

        int written = 0;
        int notYetElapsed = 0;
        int controlsWritten = 0;
        int controlHorizonsNotYetElapsed = 0;
        int acrossAHoliday = 0;
        int withoutABarOnTheirOwnSession = 0;

        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            foreach (Subject subject in setups.Concat(controls))
            {
                bool isControl = string.Equals(subject.Kind, ControlKind, StringComparison.Ordinal);
                IReadOnlyList<ForwardOutcome.Bar> path = Path(connection, subject, asOf, filledAt);

                // <b>The window has to start on the subject's own session, and for a control that is
                // not a given.</b> ForwardOutcome measures from `path[0]`, documented as the as-of
                // session whose close the return is taken from. The read is bounded below by that
                // date rather than pinned to it, so a name that did not trade that day, being halted
                // or not yet listed, hands back a window whose first bar is a later session. The
                // return would then be measured from the wrong basis and the row is never revised.
                //
                // A detector cannot flag a name with no bar on the night it flags it, so in practice
                // only a control reaches this. It is counted rather than assumed away.
                if (path.Count == 0 || path[0].Date != subject.AsOf)
                {
                    withoutABarOnTheirOwnSession++;
                    continue;
                }

                foreach (int horizon in ForwardOutcome.Horizons)
                {
                    ForwardOutcome.Outcome? outcome =
                        ForwardOutcome.Of(path, horizon, subject.IsLong, subject.AverageTrueRange);

                    if (outcome is null)
                    {
                        if (isControl)
                        {
                            controlHorizonsNotYetElapsed++;
                        }
                        else
                        {
                            notYetElapsed++;
                        }

                        continue;
                    }

                    // Where a naive calendar step lands, which is what the horizon would have been
                    // over an unbroken run of trading days. Stored beside the session actually used
                    // so a follow-up that crossed a weekend or a holiday says so rather than being
                    // silently later than it claims.
                    DateOnly intended = subject.AsOf.AddDays(horizon);

                    if (intended != outcome.ActualDate)
                    {
                        acrossAHoliday++;
                    }

                    int rows = Insert(connection, transaction, subject, horizon, intended, outcome, filledAt, tables);

                    if (isControl)
                    {
                        controlsWritten += rows;
                    }
                    else
                    {
                        written += rows;
                    }
                }
            }

            transaction.Commit();
        }

        RunSummary summary = run.Complete(RunOutcome.Clean);

        return new FillResult(
            asOf, setups.Count, written, notYetElapsed, acrossAHoliday,
            controls.Count, controlsWritten, controlHorizonsNotYetElapsed,
            withoutABarOnTheirOwnSession,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean);
    }

    /// <summary>
    /// The subjects owed an outcome: every setup the lab has flagged, with the ATR it was flagged
    /// against.
    ///
    /// Bounded on the fill instant rather than on any setup's own date, which is what makes the read
    /// point-in-time: the question is what the lab can measure today.
    /// </summary>
    private static IReadOnlyList<Subject> Subjects(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset filledAt, SubjectTables tables)
    {
        var subjects = new List<Subject>();

        // <b>Two literal statements rather than an interpolated table name.</b> `point-in-time`
        // reads the shipped source for statements selecting from a stamped table and asserts each
        // bounds its stamp. An interpolated name matches nothing, so parameterising this read made
        // two statements invisible: the scan fell from 57 to 55 and the floor caught it. Written
        // out, both are read and both are held to the bound.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = tables.ExcursionsAvailable
            ? """
                SELECT s.setup_id, s.as_of, s.ticker, s.direction, i.atr_14
                  FROM setup s
                  LEFT JOIN indicator_daily i
                    ON i.ticker = s.ticker AND i.as_of = s.as_of
                   AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                         WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                           AND c.computed_at <= @filled_at)
                 WHERE s.as_of <= @as_of
                 ORDER BY s.setup_id
              """
            : """
                SELECT s.setup_id, s.as_of, s.ticker, s.direction, i.atr_14
                  FROM calibration_setup s
                  LEFT JOIN indicator_daily i
                    ON i.ticker = s.ticker AND i.as_of = s.as_of
                   AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                         WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                           AND c.computed_at <= @filled_at)
                 WHERE s.as_of <= @as_of
                 ORDER BY s.setup_id
              """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            subjects.Add(new Subject(
                reader.GetString(0),
                SetupKind,
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? 0m : StoreText.StorageTextToPrice(reader.GetString(4))));
        }

        return subjects;
    }

    /// <summary>
    /// The controls owed an outcome: every name drawn against a flagged setup, measured over its own
    /// bars from that setup's own session.
    ///
    /// <b>The direction is the setup's, not the control's.</b> A control has no direction of its own
    /// to be signed by, and the figure band 1 computes is the setup's return less the mean of its
    /// controls'. Signing the two differently would make that subtraction a sum on the short side,
    /// with the right arithmetic and the wrong meaning, and nothing downstream could see it.
    ///
    /// <b>The ATR is the control's own, on the control's own date.</b> The excursions are expressed
    /// in the subject's own range, so borrowing the setup's would state the control's path in units
    /// of a different stock's volatility.
    ///
    /// <b>The date is the control's own and no longer the setup's, and that sentence used to run the
    /// other way.</b> It read "joined through `setup` rather than carrying a date of its own,
    /// because a control's session is the session it was drawn for", which was true while every
    /// draw was within the night and became false when the tight set was allowed to reach across
    /// sessions. A tight control drawn from a session three months earlier would otherwise have had
    /// its ten-day return measured from the setup's night: a real return of a real stock over a real
    /// window, and the wrong window, which is not a shape anything downstream could have seen.
    /// see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
    ///
    /// `COALESCE` because every row drawn before migration 035 carries the setup's own date and the
    /// migration backfills exactly that. The fallback is belt and braces for a row written between
    /// the two, and it is the setup's date, which is what such a row would have meant.
    ///
    /// Bounded on the fill instant like its sibling above.
    ///
    /// <b>`control_setup` is stamped, so the read bounds `drawn_at` as well.</b> The sampler runs
    /// before this stage on the same night, so on a live run every draw is already older than the
    /// fill instant and the clause changes nothing. It is there because a replay can hold draws made
    /// after the instant being answered for, and an unbounded read is the shape the point-in-time
    /// rule exists to refuse whether or not today's ordering happens to make it safe.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    private static IReadOnlyList<Subject> ControlSubjects(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset filledAt, SubjectTables tables)
    {
        var subjects = new List<Subject>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = tables.ExcursionsAvailable
            ? """
                SELECT c.control_id, COALESCE(c.control_as_of, s.as_of), c.control_ticker, s.direction,
                       i.atr_14
                  FROM control_setup c
                  JOIN setup s ON s.setup_id = c.setup_id
                  LEFT JOIN indicator_daily i
                    ON i.ticker = c.control_ticker
                   AND i.as_of = COALESCE(c.control_as_of, s.as_of)
                   AND i.computed_at = (SELECT MAX(d.computed_at) FROM indicator_daily d
                                         WHERE d.ticker = i.ticker AND d.as_of = i.as_of
                                           AND d.computed_at <= @filled_at)
                 WHERE s.as_of <= @as_of
                   AND c.drawn_at <= @filled_at
                 ORDER BY c.control_id
              """
            : """
                SELECT c.control_id, COALESCE(c.control_as_of, s.as_of), c.control_ticker, s.direction,
                       i.atr_14
                  FROM calibration_control_setup c
                  JOIN calibration_setup s ON s.setup_id = c.setup_id
                  LEFT JOIN indicator_daily i
                    ON i.ticker = c.control_ticker
                   AND i.as_of = COALESCE(c.control_as_of, s.as_of)
                   AND i.computed_at = (SELECT MAX(d.computed_at) FROM indicator_daily d
                                         WHERE d.ticker = i.ticker AND d.as_of = i.as_of
                                           AND d.computed_at <= @filled_at)
                 WHERE s.as_of <= @as_of
                   AND c.drawn_at <= @filled_at
                 ORDER BY c.control_id
              """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            subjects.Add(new Subject(
                reader.GetString(0),
                ControlKind,
                StoreText.StorageTextToDate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? 0m : StoreText.StorageTextToPrice(reader.GetString(4))));
        }

        return subjects;
    }

    /// <summary>
    /// One subject's own bars from its as-of session forward, on the adjusted basis.
    ///
    /// Adjusted throughout, because a return read across a split on the raw basis is a collapse. The
    /// observation bound is the fill instant, so a correction the lab has not yet seen cannot change
    /// an outcome it is about to write.
    /// </summary>
    private static IReadOnlyList<ForwardOutcome.Bar> Path(
        SqliteConnection connection, Subject subject, DateOnly asOf, DateTimeOffset filledAt)
    {
        var path = new List<ForwardOutcome.Bar>();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.bar_date, b.high, b.low, b.close, b.adj_close
              FROM daily_bar b
             WHERE b.ticker = @ticker
               AND b.bar_date >= @from
               AND b.bar_date <= @to
               AND b.observed_at <= @filled_at
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= @filled_at)
             ORDER BY b.bar_date
            """;
        command.Parameters.AddWithValue("@ticker", subject.Ticker);
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(subject.AsOf));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            decimal close = StoreText.StorageTextToPrice(reader.GetString(3));
            decimal adjusted = StoreText.StorageTextToPrice(reader.GetString(4));
            decimal factor = close == 0m ? 1m : adjusted / close;

            path.Add(new ForwardOutcome.Bar(
                StoreText.StorageTextToDate(reader.GetString(0)),
                StoreText.StorageTextToPrice(reader.GetString(1)) * factor,
                StoreText.StorageTextToPrice(reader.GetString(2)) * factor,
                adjusted));
        }

        return path;
    }

    /// <summary>
    /// One outcome row, into whichever table the pass owns.
    ///
    /// <b>The excursions are the one thing the two populations do not share.</b> On the evidence
    /// side they are expressed in the subject's own ATR, read from `indicator_daily` on its own
    /// session. A reconstructed session has no such row and may not be given one, and the
    /// calibration walk computes its averages in memory and discards them, so there is no ATR to
    /// express them in. They are written null with the reason on the row rather than approximated
    /// from daily bars, which is the stand-in `reached-ceiling`'s anchored clause already refuses by
    /// name, and rather than coalesced to nought, which is a defect the evidence side already
    /// carries as an obligation raised at 3.5 and is not worth shipping twice.
    /// </summary>
    private static int Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Subject subject,
        int horizon,
        DateOnly intended,
        ForwardOutcome.Outcome outcome,
        DateTimeOffset filledAt,
        SubjectTables tables)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        // Never revised. A horizon that has elapsed has one answer, and a restated bar arriving
        // later corrects the market's record rather than licensing a rewrite of an outcome already
        // acted on. The key refuses the second write rather than this method remembering to.
        // <b>Two literal statements rather than one interpolated table name, and that is a
        // verification property rather than a style.</b> `writer-ownership` reads the shipped source
        // for `INSERT INTO <name>` and attributes each write to the type enclosing it. An
        // interpolated name matches nothing at all, so parameterising this insert made two writes
        // invisible to the check: the scan fell from 35 to 33 and the floor under it caught the
        // narrowing. Written out, the check sees both tables, attributes both to this stage, and
        // reconciles both against SCHEMA in the direction that matters.
        //
        // What is not duplicated is the arithmetic. `ForwardOutcome`, the bar path and the signing
        // are one implementation over both populations; these are two spellings of where the answer
        // is put, and the store's own key refuses a second write either way.
        command.CommandText = tables.ExcursionsAvailable
            ? """
                INSERT INTO forward_return
                    (subject_id, subject_kind, horizon_days, intended_date, actual_date,
                     return_signed, mfe_atr, mae_atr, filled_at)
                VALUES (@subject_id, @subject_kind, @horizon_days, @intended_date, @actual_date,
                        @return_signed, @mfe_atr, @mae_atr, @filled_at)
                ON CONFLICT (subject_id, subject_kind, horizon_days) DO NOTHING
              """
            : """
                INSERT INTO calibration_forward_return
                    (subject_id, subject_kind, horizon_days, intended_date, actual_date,
                     return_signed, mfe_atr, mae_atr, excursions_absent_because, filled_at)
                VALUES (@subject_id, @subject_kind, @horizon_days, @intended_date, @actual_date,
                        @return_signed, NULL, NULL, @absent, @filled_at)
                ON CONFLICT (subject_id, subject_kind, horizon_days) DO NOTHING
              """;

        command.Parameters.AddWithValue("@subject_id", subject.SubjectId);
        command.Parameters.AddWithValue("@subject_kind", subject.Kind);
        command.Parameters.AddWithValue("@horizon_days", horizon);
        command.Parameters.AddWithValue("@intended_date", StoreText.DateToStorageText(intended));
        command.Parameters.AddWithValue("@actual_date", StoreText.DateToStorageText(outcome.ActualDate));
        command.Parameters.AddWithValue("@return_signed", StoreText.RatioToStorageText(outcome.ReturnSigned));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        if (tables.ExcursionsAvailable)
        {
            command.Parameters.AddWithValue(
                "@mfe_atr", StoreText.RatioToStorageText(outcome.MaximumFavourableExcursion ?? 0m));
            command.Parameters.AddWithValue(
                "@mae_atr", StoreText.RatioToStorageText(outcome.MaximumAdverseExcursion ?? 0m));
        }
        else
        {
            command.Parameters.AddWithValue("@absent", ExcursionsAbsent);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>Why a reconstructed row carries no excursions, recorded on the row rather than in prose.</summary>
    public const string ExcursionsAbsent =
        "no ATR for a reconstructed session: indicator_daily holds no row for a night the lab was not "
        + "running, and the calibration walk computes its averages in memory and discards them";

    /// <summary>
    /// One thing owed an outcome. <c>Kind</c> is the subject's own rather than a constant supplied
    /// at the insert, which is what let the literal "setup" reach every row for the whole of phase 3.
    /// </summary>
    private sealed record Subject(
        string SubjectId,
        string Kind,
        DateOnly AsOf,
        string Ticker,
        string Direction,
        decimal AverageTrueRange)
    {
        public bool IsLong => string.Equals(Direction, "long", StringComparison.Ordinal);
    }
}

/// <summary>What one fill pass did.</summary>
/// <summary>
/// What one fill pass did, per subject kind.
///
/// <b>Two populations, counted apart.</b> `Subjects`, `Written` and `NotYetElapsed` are the setups;
/// the three that follow are the controls. A single pair of totals would have read as healthy on
/// every night of phase 3 while no control outcome was written at all, which is the shape of figure
/// CLAUDE.md's fifth defect names.
/// see: Long and short are never pooled into one figure
/// </summary>
public sealed record FillResult(
    DateOnly AsOf,
    int Subjects,
    int Written,
    int NotYetElapsed,
    int AcrossAHoliday,
    int ControlSubjects,
    int ControlsWritten,
    int ControlHorizonsNotYetElapsed,
    int WithoutABarOnTheirOwnSession,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome);
