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
        // The population the two "considered" figures above are over, stated beside them rather
        // than left to be inferred: from 6.1 they are the subjects still owed an outcome, and the
        // subjects walked on no night again are these.
        Console.WriteLine(
            $"{Name}: {result.SetupsAlreadyComplete} setup and {result.ControlsAlreadyComplete} control "
            + "subject(s) already carry every horizon and were not walked");
        Console.WriteLine($"{Name}: {result.ControlsWritten} control outcome(s) written, {result.ControlHorizonsNotYetElapsed} horizon(s) not yet elapsed");
        Console.WriteLine(
            $"{Name}: {result.SetupsLaterThanTheCalendarStep} setup and {result.ControlsLaterThanTheCalendarStep} control "
            + "outcome(s) landed on a session later than the calendar step");
        Console.WriteLine(
            $"{Name}: {result.ExcursionsUndefined} written with no excursions, the subject having no range on its "
            + "own session, and the reason on the row");
        Console.WriteLine(
            $"{Name}: {result.SetupHorizonsCannotClose} setup and {result.ControlHorizonsCannotClose} control "
            + "horizon(s) can never close, the market having reached them and the subject's own series not, "
            + "which is not the same as not yet elapsed");
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
        (int totalSetups, int totalControls) = SubjectTotals(connection, asOf, filledAt, tables);

        int written = 0;
        int notYetElapsed = 0;
        int controlsWritten = 0;
        int controlHorizonsNotYetElapsed = 0;
        int setupsLaterThanTheCalendarStep = 0;
        int controlsLaterThanTheCalendarStep = 0;
        int withoutABarOnTheirOwnSession = 0;
        int excursionsUndefined = 0;
        int setupHorizonsCannotClose = 0;
        int controlHorizonsCannotClose = 0;
        var sessionsAfter = new Dictionary<DateOnly, int>();

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
                        // <b>Two states that were one counter until 5.8.</b> A horizon that has not
                        // elapsed is one the store holds too few sessions after the subject's own to
                        // reach, on any name; a horizon that can never close is one the store holds
                        // enough sessions for and the subject's own series has none of them, which is
                        // a name halted for a run of sessions, dropped from the universe, or delisted
                        // since. The second contributed nothing to its subject's control mean while
                        // reading as waiting, so a comparison that had narrowed said nothing. Counted
                        // apart and written nowhere: an outcome the lab cannot measure is not a row.
                        bool cannotClose = path.Count - 1 < horizon
                            && SessionsHeldAfter(connection, subject.AsOf, asOf, filledAt, sessionsAfter) >= horizon;

                        if (isControl)
                        {
                            if (cannotClose) { controlHorizonsCannotClose++; } else { controlHorizonsNotYetElapsed++; }
                        }
                        else
                        {
                            if (cannotClose) { setupHorizonsCannotClose++; } else { notYetElapsed++; }
                        }

                        continue;
                    }

                    // The calendar step: the subject's own session plus the horizon in calendar
                    // days, which is what the horizon would have been over an unbroken run of
                    // sessions and is a session itself only by accident. Stored beside the session
                    // actually used so a horizon that landed later says so, and counted per subject
                    // kind, because the step is later than a session on every weekend and not only
                    // across a holiday, which is what the counter's old name claimed.
                    // see: An intended date is a calendar step from the subject's session, stated as such, and the slip past it is counted per subject kind
                    DateOnly intended = subject.AsOf.AddDays(horizon);

                    if (intended != outcome.ActualDate)
                    {
                        if (isControl)
                        {
                            controlsLaterThanTheCalendarStep++;
                        }
                        else
                        {
                            setupsLaterThanTheCalendarStep++;
                        }
                    }

                    int rows = Insert(connection, transaction, subject, horizon, intended, outcome, filledAt, tables);

                    // An evidence row written with no excursions, counted so a night where a
                    // flagged name had no range is visible in the run rather than in a null.
                    if (rows > 0 && tables.ExcursionsAvailable
                        && (outcome.MaximumFavourableExcursion is null || outcome.MaximumAdverseExcursion is null))
                    {
                        excursionsUndefined++;
                    }

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
            asOf, setups.Count, written, notYetElapsed, setupsLaterThanTheCalendarStep,
            controls.Count, controlsWritten, controlHorizonsNotYetElapsed,
            withoutABarOnTheirOwnSession,
            summary.RowsWritten, summary.CallsUsed, RunOutcome.Clean,
            excursionsUndefined, controlsLaterThanTheCalendarStep,
            setupHorizonsCannotClose, controlHorizonsCannotClose,
            totalSetups - setups.Count, totalControls - controls.Count);
    }

    /// <summary>
    /// Every subject of each kind the fill's own date reaches, complete or not, which is the
    /// population the two readers above select from.
    ///
    /// <b>Read so that the walk's own shrinking is legible rather than silent.</b> From 6.1 those
    /// readers return only the subjects still owed an outcome, so `Subjects` and `ControlSubjects`
    /// stopped meaning "every subject ever recorded" and started meaning "the subjects this night
    /// has work for". A count whose population changed under it and kept its name is the fifth
    /// defect this corpus names, so the total is read beside it and the difference is reported as
    /// what it is: subjects already complete, walked on no night again.
    /// see: Long and short are never pooled into one figure
    /// </summary>
    private static (int Setups, int Controls) SubjectTotals(
        SqliteConnection connection, DateOnly asOf, DateTimeOffset filledAt, SubjectTables tables)
    {
        using SqliteCommand command = connection.CreateCommand();

        // Two literal statements rather than one interpolated name, for the reason the readers
        // above give: an interpolated table name is invisible to `point-in-time`, and a read that
        // no check can see is a read nothing holds to its bound.
        command.CommandText = tables.ExcursionsAvailable
            ? """
                SELECT (SELECT COUNT(*) FROM setup s WHERE s.as_of <= @as_of),
                       (SELECT COUNT(*) FROM control_setup c
                          JOIN setup s ON s.setup_id = c.setup_id
                         WHERE s.as_of <= @as_of AND c.drawn_at <= @filled_at)
              """
            : """
                SELECT (SELECT COUNT(*) FROM calibration_setup s WHERE s.as_of <= @as_of),
                       (SELECT COUNT(*) FROM calibration_control_setup c
                          JOIN calibration_setup s ON s.setup_id = c.setup_id
                         WHERE s.as_of <= @as_of AND c.drawn_at <= @filled_at)
              """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }

    /// <summary>
    /// How many sessions the store holds after <paramref name="session"/> and at or before the
    /// fill's own date, on any name, as the fill could know them, read once per session and kept.
    ///
    /// The market's own count of sessions, against which a subject's series is short or not: a
    /// horizon the market has reached and the subject has not is one the subject can never close.
    /// Bounded on the fill's date as the subject's own path is, so a fill run for an earlier date
    /// does not read the sessions after it as reached.
    /// </summary>
    private static int SessionsHeldAfter(
        SqliteConnection connection, DateOnly session, DateOnly asOf, DateTimeOffset filledAt, Dictionary<DateOnly, int> kept)
    {
        if (kept.TryGetValue(session, out int held))
        {
            return held;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT bar_date)
              FROM daily_bar
             WHERE bar_date > @session
               AND bar_date <= @to
               AND observed_at <= @filled_at
            """;
        command.Parameters.AddWithValue("@session", StoreText.DateToStorageText(session));
        command.Parameters.AddWithValue("@to", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));

        held = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        kept[session] = held;
        return held;
    }

    /// <summary>
    /// The subjects owed an outcome: every setup the lab has flagged and has not yet written all
    /// four horizons for, with the ATR it was flagged against.
    ///
    /// Bounded on the fill instant rather than on any setup's own date, which is what makes the read
    /// point-in-time: the question is what the lab can measure today.
    ///
    /// <b>"Not yet written all four" is the 6.1 repair and it is the whole of it.</b> This read was
    /// bounded only by `s.as_of &lt;= @as_of`, so every setup ever recorded was walked on every
    /// night and one path query was issued per subject per night; every already-written row was
    /// recomputed and thrown away by the conflict clause. Correctness was never affected, the
    /// immutability resting on the key rather than on the query, but the cost grows with the square
    /// of the accumulation: at about eighty-two setups a night with ten controls each, night 200
    /// walked roughly 180,000 subjects to write the few thousand that had closed.
    ///
    /// <b>A subject short of four horizons stays in the walk, and that is deliberate.</b> A horizon
    /// that can never close leaves its subject permanently incomplete, so the exclusion is on rows
    /// written rather than on the subject's age: a lower bound on the as-of would have been cheaper
    /// still and would have dropped exactly those subjects the night a later bar finally let one
    /// close. The count of them is small and bounded; the count of complete subjects is what grows.
    ///
    /// <b>The subquery is bounded on the fill instant like everything else here.</b> An unbounded
    /// count would let a row filled after the instant being answered for exclude a subject from a
    /// replay of an earlier night, which is the point-in-time rule broken by an optimisation.
    /// see: A reader's signature does not establish point-in-time; the query does
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
                   AND (SELECT COUNT(*) FROM forward_return f
                         WHERE f.subject_id = s.setup_id AND f.subject_kind = 'setup'
                           AND f.filled_at <= @filled_at) < @horizons
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
                   AND (SELECT COUNT(*) FROM calibration_forward_return f
                         WHERE f.subject_id = s.setup_id AND f.subject_kind = 'setup'
                           AND f.filled_at <= @filled_at) < @horizons
                 ORDER BY s.setup_id
              """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));
        command.Parameters.AddWithValue("@horizons", ForwardOutcome.Horizons.Count);

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
    /// <b>The date is the control's own, which is again the setup's on every row, and the column is
    /// kept anyway.</b> This read "joined through `setup` rather than carrying a date of its own,
    /// because a control's session is the session it was drawn for" until the tight set was allowed
    /// to reach across sessions, and the reach was reversed a day later. Read from the row rather
    /// than from the join because the two agreeing is a fact worth stating: a tight control drawn
    /// from a session three months earlier would have had its ten-day return measured from the
    /// setup's night, a real return of a real stock over the wrong window, and nothing downstream
    /// could have seen it. `A_tight_control_is_drawn_from_the_subjects_own_session` is what now
    /// holds the two together.
    /// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
    ///
    /// `COALESCE` because every row drawn before migration 035 carries the setup's own date and the
    /// migration backfills exactly that. The fallback is belt and braces for a row written between
    /// the two, and it is the setup's date, which is what such a row would have meant.
    ///
    /// Bounded on the fill instant like its sibling above, and carrying the same 6.1 exclusion for
    /// the same reason: a control already holding all four horizons is walked on no night again.
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
                   AND (SELECT COUNT(*) FROM forward_return f
                         WHERE f.subject_id = c.control_id AND f.subject_kind = 'control'
                           AND f.filled_at <= @filled_at) < @horizons
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
                   AND (SELECT COUNT(*) FROM calibration_forward_return f
                         WHERE f.subject_id = c.control_id AND f.subject_kind = 'control'
                           AND f.filled_at <= @filled_at) < @horizons
                 ORDER BY c.control_id
              """;

        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@filled_at", StoreText.TimestampToStorageText(filledAt));
        command.Parameters.AddWithValue("@horizons", ForwardOutcome.Horizons.Count);

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
                     return_signed, mfe_atr, mae_atr, excursions_absent_because, filled_at)
                VALUES (@subject_id, @subject_kind, @horizon_days, @intended_date, @actual_date,
                        @return_signed, @mfe_atr, @mae_atr, @absent, @filled_at)
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
            // Null with the reason beside it where the subject has no range to express the path
            // in, never nought: a nought here read as a path that never moved, which is a different
            // fact from one that could not be measured, and the store now refuses one without the
            // other. Until 5.8 both were coalesced to nought on this side alone.
            // see: A gate handed an absent or degenerate quantity fails rather than passing
            bool undefined = outcome.MaximumFavourableExcursion is null || outcome.MaximumAdverseExcursion is null;

            command.Parameters.AddWithValue(
                "@mfe_atr",
                undefined ? DBNull.Value : StoreText.RatioToStorageText(outcome.MaximumFavourableExcursion!.Value));
            command.Parameters.AddWithValue(
                "@mae_atr",
                undefined ? DBNull.Value : StoreText.RatioToStorageText(outcome.MaximumAdverseExcursion!.Value));
            command.Parameters.AddWithValue("@absent", undefined ? ExcursionsUndefined : DBNull.Value);
        }
        else
        {
            command.Parameters.AddWithValue("@absent", ExcursionsAbsent);
        }

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Why an evidence row carries no excursions, recorded on the row rather than as a nought.
    ///
    /// The subject's own session holds no ATR, or holds one of nought: `indicator_daily` has no
    /// row for it inside the fill's bound, or the range it computed is nought, and a path cannot be
    /// stated in a range that does not exist.
    /// </summary>
    public const string ExcursionsUndefined =
        "no ATR on the subject's own session: indicator_daily holds no row for it inside the fill's "
        + "bound, or holds a range of nought, so the path cannot be stated in the subject's own range";

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

/// <summary>
/// What one fill pass did, per subject kind.
///
/// <b>Two populations, counted apart.</b> `Subjects`, `Written` and `NotYetElapsed` are the setups;
/// the three that follow are the controls. A single pair of totals would have read as healthy on
/// every night of phase 3 while no control outcome was written at all, which is the shape of figure
/// CLAUDE.md's fifth defect names.
/// see: Long and short are never pooled into one figure
///
/// <b>`Subjects` and `ControlSubjects` are the subjects still owed an outcome, from 6.1.</b> They
/// were every subject ever recorded until then, which is the same word over a different population,
/// so `SetupsAlreadyComplete` and `ControlsAlreadyComplete` are reported beside them and never added
/// to them. Reading a "considered" count that quietly shrank as the store filled would say the lab
/// was doing less work rather than that it had stopped repeating work already done.
/// </summary>
public sealed record FillResult(
    DateOnly AsOf,
    int Subjects,
    int Written,
    int NotYetElapsed,
    int SetupsLaterThanTheCalendarStep,
    int ControlSubjects,
    int ControlsWritten,
    int ControlHorizonsNotYetElapsed,
    int WithoutABarOnTheirOwnSession,
    int RowsWritten,
    int CallsUsed,
    RunOutcome Outcome,
    int ExcursionsUndefined = 0,
    int ControlsLaterThanTheCalendarStep = 0,
    int SetupHorizonsCannotClose = 0,
    int ControlHorizonsCannotClose = 0,
    int SetupsAlreadyComplete = 0,
    int ControlsAlreadyComplete = 0);
