namespace PullbackStrategyLab.Core.Research;

/// <summary>
/// The signal library as the specification states it: one entry per signal, carrying its formula,
/// the stored columns it reads and the status the specification declares.
///
/// <b>This is the runnable copy of SCHEMA.md's Signals section and not a second statement of it.</b>
/// The section is the specification, `signal_definition` is the runtime form, and this list is the
/// only path between them: SignalAdmissionTest seeds the table from here, and `signal-library`
/// reconciles this list against the section in both directions, verbatim after markup is
/// normalised away. A signal in the section and not here fails, a signal here and not in the
/// section fails, and a formula or a source-column list that drifts on either side fails.
/// see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
///
/// <b>Why the prose is duplicated into code at all.</b> The Worker cannot read `docs/` at runtime:
/// the application ships without the corpus and a store has to stay a directory that can be copied.
/// So the formula and the source columns reach the store through a list somebody has to keep
/// honest, and the check is what keeps it honest rather than a convention that a later session
/// remembers. The cost is fifty-four strings; what it buys is that the column a proposal reads and
/// the sentence a person reads are the same sentence.
///
/// <b>Status here is the specification's status and never a verdict.</b> The section can say
/// `active` or `candidate` and cannot say `rejected_correlation`, because a rejection is something
/// the admission test measured rather than something the library declares. That is why the
/// reconciliation admits one difference and exactly one: a stored row may read
/// <see cref="SignalStatus.RejectedCorrelation"/> where the section reads
/// <see cref="SignalStatus.Candidate"/>, and no other disagreement passes.
/// </summary>
public static class SignalLibrary
{
    /// <summary>
    /// The one signal that means nothing, declared here so the column it reads is on the record and
    /// planted at 6.4 by the packer.
    /// see: One meaningless signal is planted in the conditional tables
    /// </summary>
    public const string NullControl = "day_of_month";

    /// <summary>Every signal the specification declares, in the order the section lists them.</summary>
    public static IReadOnlyList<DeclaredSignal> Declared { get; } =
    [
        new("close_adjusted",
            "the setup session's adjusted close",
            "daily_bar.adj_close",
            "active"),
        new("ema_9_distance",
            "(adjusted close − ema_9) / ema_9",
            "daily_bar.adj_close, indicator_daily.ema_9",
            "active"),
        new("ema_21_distance",
            "(adjusted close − ema_21) / ema_21",
            "daily_bar.adj_close, indicator_daily.ema_21",
            "active"),
        new("ema_50_distance",
            "(adjusted close − ema_50) / ema_50",
            "daily_bar.adj_close, indicator_daily.ema_50",
            "active"),
        new("ema_gap_21_50",
            "(ema_21 − ema_50) / ema_50",
            "indicator_daily.ema_21, indicator_daily.ema_50",
            "active"),
        new("ema_gap_21_50_avg_20",
            "mean of ema_gap_21_50 over the last 20 sessions",
            "indicator_daily.ema_21, indicator_daily.ema_50",
            "active"),
        new("ema_gap_21_50_over_avg",
            "|ema_gap_21_50| over the mean of |ema_gap_21_50| across the last 20 sessions. Below one is a squeeze",
            "indicator_daily.ema_21, indicator_daily.ema_50",
            "active"),
        new("ceiling_distance_ranges",
            "the nearest of the 21-day average, the 50-day average and the anchored average price, over adr_20 × close. The anchored level is included only where one was computed",
            "daily_bar.adj_close, daily_bar.close, indicator_daily.ema_21, indicator_daily.ema_50, indicator_daily.adr_20, anchored_vwap.value",
            "active"),
        new("ladder_grade",
            "the grade TierClassifier wrote for that session",
            "indicator_daily.ladder_grade",
            "active"),
        new("adr_20",
            "as stored, a fraction",
            "indicator_daily.adr_20",
            "active"),
        new("atr_14",
            "as stored",
            "indicator_daily.atr_14",
            "active"),
        new("range_avg_20",
            "as stored",
            "indicator_daily.range_avg_20",
            "active"),
        new("range_today_over_avg",
            "(high − low) on the adjusted basis, over range_avg_20",
            "daily_bar.high, daily_bar.low, daily_bar.close, daily_bar.adj_close, indicator_daily.range_avg_20",
            "active"),
        new("thrust_scan",
            "which scan the most recent qualifying hit came from",
            "scan_hit.scan",
            "active"),
        new("thrust_rank",
            "its rank on that scan",
            "scan_hit.rank",
            "active"),
        new("thrust_session",
            "the session of that hit",
            "scan_hit.as_of",
            "active"),
        new("days_since_thrust",
            "trading sessions from thrust_session to the setup date",
            "scan_hit.as_of, daily_bar.bar_date",
            "active"),
        new("thrust_magnitude",
            "the scan magnitude that put it on the list, on the adjusted basis",
            "daily_bar.adj_close, daily_bar.open, daily_bar.close",
            "active"),
        new("thrust_size_in_ranges",
            "thrust_magnitude / adr_20",
            "daily_bar.adj_close, daily_bar.open, daily_bar.close, indicator_daily.adr_20",
            "active"),
        new("pullback_bars",
            "sessions from the thrust extreme to the setup date",
            "daily_bar.bar_date",
            "active"),
        new("pullback_extreme",
            "lowest adjusted low since the thrust extreme, long; highest adjusted high, short",
            "daily_bar.low, daily_bar.high, daily_bar.close, daily_bar.adj_close",
            "active"),
        new("retrace_depth",
            "(thrust extreme − pullback_extreme) / (thrust extreme − thrust origin), signed so both directions read the same way",
            "daily_bar.high, daily_bar.low, daily_bar.close, daily_bar.adj_close",
            "active"),
        new("closes_beyond_floor",
            "sessions in the pullback closing below the 21-day average as at that session, long; above the 50-day, short. The average is a series over the window, not the value at the as-of date",
            "daily_bar.adj_close",
            "active"),
        new("trigger_price",
            "as written, a raw price. Absent where the setup has none",
            "setup.trigger_price",
            "active"),
        new("stop_price",
            "as written, a raw price. Absent where the setup has none",
            "setup.stop_price",
            "active"),
        new("stop_distance_ranges",
            "|trigger − stop| / (adr_20 × close). Absent where the setup has none",
            "setup.stop_distance_ranges",
            "active"),
        new("trigger_distance_ranges",
            "|trigger − close| / (adr_20 × close). Absent where the trigger is",
            "daily_bar.close, setup.trigger_price, indicator_daily.adr_20",
            "active"),
        new("dollar_volume_median_20",
            "as stored",
            "indicator_daily.dollar_volume_median_20",
            "active"),
        new("market_cap",
            "as stored, short side only",
            "security.market_cap",
            "active"),
        new("listing_age_sessions",
            "trading sessions since first_seen",
            "security.first_seen, daily_bar.bar_date",
            "active"),
        new("industry",
            "as stored",
            "security.industry",
            "active"),
        new("cluster_count",
            "same-industry scan hits that night",
            "scan_hit.cluster_count",
            "active"),
        new("regime_index_score",
            "as stored",
            "regime_daily.index_score",
            "active"),
        new("regime_breadth_score",
            "as stored",
            "regime_daily.breadth_score",
            "active"),
        new("regime_label",
            "as stored",
            "regime_daily.label",
            "active"),
        new("volume_thrust",
            "raw volume on thrust_session",
            "daily_bar.volume",
            "candidate"),
        new("volume_pullback_mean",
            "mean raw volume over the pullback bars",
            "daily_bar.volume",
            "candidate"),
        new("volume_dryup",
            "volume_pullback_mean / volume_thrust",
            "daily_bar.volume",
            "candidate"),
        new("prior_thrust_outcome",
            "this security's adjusted move over the ten sessions after each earlier scan hit, averaged",
            "daily_bar.adj_close, scan_hit.as_of",
            "candidate"),
        new("intraday_pullback_shape",
            "the fraction of each pullback session's range travelled after midday",
            "intraday_bar",
            "candidate"),
        new("day_of_month",
            "the calendar day of as_of, meaning nothing",
            "setup.as_of",
            "candidate"),

        // The daily-bar quantities the trader's own clause forms compare, from 7.4, each citing the
        // SOURCES.md clause it was derived from. Candidates that SignalVectorizer freezes nightly, so
        // a rule written over them replays across every night from 7.4 before generation 1 registers;
        // they stay candidates until the admission test rules on them.
        // see: Generation 1's baseline is written clause by clause from SOURCES.md, and a stated qualifier makes a gate recorded rather than screening
        new("ema_150_distance",
            "(adjusted close − the 150-session exponential average of adjusted closes) / that average, over the last 300 sessions stored",
            "daily_bar.adj_close",
            "candidate",
            "uptrend, long"),
        new("ema_9_slope",
            "(ema_9 now − ema_9 five sessions earlier) / ema_9 five sessions earlier, each over the engine's 150-session window. The five is the author's",
            "daily_bar.adj_close",
            "candidate",
            "uptrend, long"),
        new("ema_21_slope",
            "(ema_21 now − ema_21 five sessions earlier) / ema_21 five sessions earlier, each over the engine's 150-session window. The five is the author's",
            "daily_bar.adj_close",
            "candidate",
            "uptrend, long"),
        new("ema_50_slope",
            "(ema_50 now − ema_50 five sessions earlier) / ema_50 five sessions earlier, each over the engine's 150-session window. The five is the author's",
            "daily_bar.adj_close",
            "candidate",
            "downtrend, short"),
        new("return_30_days",
            "the adjusted close over the adjusted close of the last session on or before thirty calendar days earlier, less one",
            "daily_bar.adj_close, daily_bar.bar_date",
            "candidate",
            "thrust, long"),
        new("base_span_ranges",
            "(highest adjusted high − lowest adjusted low over the last 40 sessions) / (adr_20 × adjusted close). The forty is the author's, eight weeks being the shortest base the sources describe",
            "daily_bar.high, daily_bar.low, daily_bar.close, daily_bar.adj_close, indicator_daily.adr_20",
            "candidate",
            "dip-shape, long"),
        new("undercut_reclaim_ema_9",
            "1 where the session's adjusted low is below ema_9 and its adjusted close above it, long, or its adjusted high above ema_9 and its close below, short, the short being the author's mirror; 0 otherwise",
            "daily_bar.high, daily_bar.low, daily_bar.close, daily_bar.adj_close, indicator_daily.ema_9",
            "candidate",
            "held-floor, long"),
        new("from_session_extreme",
            "(adjusted close − adjusted low) / adjusted low, long; (adjusted high − adjusted close) / adjusted high, short, the short being the author's mirror",
            "daily_bar.high, daily_bar.low, daily_bar.close, daily_bar.adj_close",
            "candidate",
            "trigger-near, long"),
        new("entry_ceiling",
            "the lesser of adr_20 / 2 and 0.05",
            "indicator_daily.adr_20",
            "candidate",
            "exit-tight, long"),
        new("weekly_ema_gap_9_21",
            "(9-week − 21-week exponential average) / the 21-week, over weekly closes: the last adjusted close of each Monday-to-Sunday week, the week in progress closing at the setup session, across the last 300 sessions stored",
            "daily_bar.adj_close, daily_bar.bar_date",
            "candidate",
            "averages-squeezing, short"),
        new("weekly_ema_21_distance",
            "(the week's adjusted close − the 21-week exponential average) / that average, over the same weekly closes",
            "daily_bar.adj_close, daily_bar.bar_date",
            "candidate",
            "dip-shape, long"),

        // The two generation 1's clauses compare that 7.4 did not cover, from 7.5.
        new("weekly_squeeze_ratio",
            "the absolute 9-week less 21-week gap over its mean across the last 20 weeks, each week's gap over every weekly close up to it, on the weekly closes weekly_ema_gap_9_21 reads. Below one is a squeeze. The twenty and the one are carried from generation 0 as the author's",
            "daily_bar.adj_close, daily_bar.bar_date",
            "candidate",
            "averages-squeezing, short"),
        new("ceiling_confluence_ranges",
            "the second-nearest of the 21-day average, the 50-day average and the anchored average price, over adr_20 × close, which is the levels coinciding. Absent where fewer than two could be measured",
            "daily_bar.adj_close, daily_bar.close, indicator_daily.ema_21, indicator_daily.ema_50, indicator_daily.adr_20, anchored_vwap.value",
            "candidate",
            "reached-ceiling, short"),
    ];

    /// <summary>The declared signals by name, which is how both the seed and the check index them.</summary>
    public static IReadOnlyDictionary<string, DeclaredSignal> ByName { get; } =
        Declared.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>
    /// The signals the specification calls active, being the set SignalVectorizer freezes on every
    /// setup. A test holds the two together, in both directions.
    /// </summary>
    public static IReadOnlyList<DeclaredSignal> Active { get; } =
        [.. Declared.Where(s => s.Status == SignalStatus.Active)];

    /// <summary>The candidates derived from the trader's own clause forms, from 7.4, which the vectorizer freezes.</summary>
    public static IReadOnlyList<DeclaredSignal> Sourced { get; } =
        [.. Declared.Where(s => s.IsSourced)];

    /// <summary>The signals the specification calls candidates, being what the admission test has to rule on.</summary>
    public static IReadOnlyList<DeclaredSignal> Candidates { get; } =
        [.. Declared.Where(s => s.Status == SignalStatus.Candidate)];
}

/// <summary>
/// One signal as the specification declares it.
///
/// <c>SourceColumns</c> is the list the point-in-time test is asserted against, held as the
/// section writes it: `table.column` entries separated by a comma and a space, in the section's
/// own order. One entry names a table rather than a column, and it is kept as written rather than
/// normalised into a column that does not exist.
/// </summary>
public sealed record DeclaredSignal(string Name, string Formula, string SourceColumns, string Status, string? Clause = null)
{
    /// <summary>
    /// The heading of the SOURCES.md clause a sourced candidate was derived from, as "name, side", or
    /// null on a signal that was not derived from one. `signal-library` holds it to the section's own
    /// clause cell and to a heading SOURCES.md carries.
    /// </summary>
    public bool IsSourced => Clause is not null;

    /// <summary>Whether this is the planted null control, which is a fact about the signal rather than about a run.</summary>
    public bool IsNullControl => string.Equals(Name, SignalLibrary.NullControl, StringComparison.Ordinal);

    /// <summary>The source columns as a list, split on the separator the section uses.</summary>
    public IReadOnlyList<string> Columns =>
        [.. SourceColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}

/// <summary>
/// The three values `signal_definition.status` may hold, and which of them the specification may
/// state.
///
/// <b>Two are declarations and one is a verdict</b>, which is the whole reason they are named
/// together here. <see cref="Active"/> and <see cref="Candidate"/> are what SCHEMA's Signals
/// section says about a signal; <see cref="RejectedCorrelation"/> is what SignalAdmissionTest
/// measured about it. A check that compared the two sides for equality would go red the first time
/// the test did its job, which is why the reconciliation names this value as the one admitted
/// difference rather than leaving it to be discovered.
/// </summary>
public static class SignalStatus
{
    /// <summary>SignalVectorizer freezes it on every setup.</summary>
    public const string Active = "active";

    /// <summary>The formula and the source columns are settled and nothing computes it yet.</summary>
    public const string Candidate = "candidate";

    /// <summary>The admission test measured it above the correlation limit against a signal already present.</summary>
    public const string RejectedCorrelation = "rejected_correlation";

    /// <summary>The two the specification may state, which is what the reconciliation reads.</summary>
    public static IReadOnlySet<string> Declarable { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Active, Candidate };

    /// <summary>Every value the column admits, which is what the migration's constraint holds.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Active, Candidate, RejectedCorrelation };
}
