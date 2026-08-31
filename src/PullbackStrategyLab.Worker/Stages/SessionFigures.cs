using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Measurement;
using PullbackStrategyLab.Data;

namespace PullbackStrategyLab.Worker.Stages;

/// <summary>
/// Where a detector gets everything about a session that is not a bar.
///
/// On a forward night all of it is in the store, written earlier the same evening by the stages
/// that own it. On a calibration run over history none of it is, and none of it may be: the engine,
/// the scan and the ladder all compute for the members of a night's snapshot, and a night the lab
/// was not running has no snapshot. Writing rows for those nights is the reconstruction the evidence
/// rule forbids.
///
/// So the two runs differ in where the figures come from and in nothing else. The detector reads
/// this interface either way, the rules see the same evidence shape, and a change to the arithmetic
/// moves the nightly answer and the calibration count together.
/// see: A calibration run reconstructs against current membership and computes its indicators in memory
/// </summary>
public interface ISessionFigures
{
    /// <summary>
    /// The derived figures for one name on one session, or null where there are none.
    ///
    /// The bar window is passed in because the calibration implementation computes from it and the
    /// store implementation ignores it. Handing it over rather than reading it twice is what keeps
    /// a calibration session to one read per name.
    /// </summary>
    StoredIndicators? Indicators(string ticker, DateOnly asOf, IReadOnlyList<StoredDailyBar> bars);

    /// <summary>The scan hits for one name inside a window ending at the session.</summary>
    IReadOnlyList<StoredScanHit> Hits(string ticker, DateOnly asOf, DateOnly windowStart);

    /// <summary>How many sessions of this name the store holds at or before the session.</summary>
    int SessionsListed(string ticker, DateOnly asOf);

    /// <summary>The market capitalisation as it was resolved, or null.</summary>
    decimal? MarketCap(string ticker, DateOnly asOf);

    /// <summary>
    /// Whether the market-cap clause of `tradable-shortable` is exempted by name for this run.
    ///
    /// False on every forward night. True in calibration, where `SecurityReader` bounds the lookup
    /// on `sector_resolved_at` like every other point-in-time read and a reconstructed 2024 session
    /// therefore has no capitalisation at all: it was resolved in 2026 or it was never resolved.
    /// Left alone every short candidate fails the first gate, and a threshold calibrated against an
    /// empty distribution is worse than no threshold.
    /// </summary>
    bool MarketCapExempt { get; }

    /// <summary>
    /// Every name's control-matching figures for one session, keyed by ticker, already past the
    /// liquidity floor.
    ///
    /// <b>Bulk rather than one call per name, and that is not an optimisation.</b>
    /// <see cref="Indicators"/> answers per ticker, which is right for a detector walking a night's
    /// members once. `ControlSampler` reads a whole session's pool and reads it again for every
    /// earlier session sharing the mood, so a per-ticker seam here would issue a read per name per
    /// session and grow with the record. The stage's own comment says exactly that, which is why it
    /// held its own single query until this seam gained a method shaped like the question it asks.
    ///
    /// The liquidity floor is applied here rather than by the caller, because the subject of a draw
    /// has to be readable on the same terms as its candidates: a setup matched against a pool
    /// filtered differently from itself is matched on a dimension nobody stated.
    /// </summary>
    IReadOnlyDictionary<string, ControlMatching.Candidate> Candidates(DateOnly asOf, string sessionZone);

    /// <summary>
    /// One session's market-mood label, or null where the session carries none.
    ///
    /// Read as a value and never compared against a named one. Nothing asks which mood a session is
    /// in; the tight draw asks whether two sessions carry the same one.
    /// see: The market-mood label is recorded on every setup and filters nothing in the baseline
    /// </summary>
    string? Mood(DateOnly asOf);
}

/// <summary>
/// A forward night: every figure read from the store, through the readers that own it.
///
/// One instance per run rather than per name, so the connection is held once. Nothing here is
/// clever; it exists so the nightly path and the calibration path go through the same call sites
/// in the detector and a reader cannot be reached from one and not the other.
/// </summary>
public sealed class StoredFigures : ISessionFigures
{
    private readonly SqliteConnection _connection;

    public StoredFigures(SqliteConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public StoredIndicators? Indicators(string ticker, DateOnly asOf, IReadOnlyList<StoredDailyBar> bars) =>
        IndicatorDailyReader.Read(_connection, ticker, asOf, asOf);

    public IReadOnlyList<StoredScanHit> Hits(string ticker, DateOnly asOf, DateOnly windowStart) =>
        ScanHitReader.ForTicker(_connection, ticker, asOf, windowStart);

    public int SessionsListed(string ticker, DateOnly asOf) =>
        DailyBarReader.SessionsStored(_connection, ticker, asOf);

    public decimal? MarketCap(string ticker, DateOnly asOf) =>
        SecurityReader.MarketCap(_connection, ticker, asOf);

    public bool MarketCapExempt => false;

    /// <summary>
    /// One session's pool, from `indicator_daily`, bounded on the end of the as-of date.
    ///
    /// <b>The end of the date rather than the run instant, and the difference is not pedantry.</b>
    /// TierClassifier writes the ladder grade as a <i>later observation</i> of the same session
    /// rather than updating the row IndicatorEngine wrote, which is what 2.4 decided. Bounded on the
    /// run instant, this read takes the engine's row and every grade comes back null, so the tight
    /// set's ladder filter compares null to null, excludes nothing, and draws exactly the loose set.
    /// It did: the first run of ControlSampler produced identical loose and tight sets for all three
    /// fixture setups, and the two figures agreeing is not something a count would have shown.
    /// `IndicatorDailyReader` bounds on the end of the date for the same reason, and this follows it.
    /// see: A reader's signature does not establish point-in-time; the query does
    /// </summary>
    public IReadOnlyDictionary<string, ControlMatching.Candidate> Candidates(DateOnly asOf, string sessionZone)
    {
        var figures = new Dictionary<string, ControlMatching.Candidate>(StringComparer.Ordinal);
        string? mood = Mood(asOf);

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT i.ticker, i.dollar_volume_median_20, i.adr_20, i.ladder_grade
              FROM indicator_daily i
             WHERE i.as_of = @as_of
               AND i.computed_at <= @drawn_at
               AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                     WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                       AND c.computed_at <= @drawn_at)
             ORDER BY i.ticker
            """;
        command.Parameters.AddWithValue("@as_of", StoreText.DateToStorageText(asOf));
        command.Parameters.AddWithValue("@drawn_at", StoreText.EndOfSession(asOf, sessionZone));

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            decimal turnover = reader.IsDBNull(1) ? 0m : StoreText.StorageTextToPrice(reader.GetString(1));

            if (turnover < Core.Detection.LongPullbackRules.LiquidityFloor)
            {
                continue;
            }

            figures[reader.GetString(0)] = new ControlMatching.Candidate(
                reader.GetString(0),
                turnover,
                reader.IsDBNull(2) ? 0m : StoreText.StorageTextToPrice(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                asOf,
                mood);
        }

        return figures;
    }

    public string? Mood(DateOnly asOf) => RegimeReader.Read(_connection, asOf)?.Label;
}

/// <summary>
/// A reconstructed session, carried in memory.
///
/// <b>Assembly rather than a second implementation, and that distinction is the whole of it.</b>
/// The figures come from <see cref="IndicatorEngine.Calculate"/>, the ladder from
/// <see cref="TierClassifier.Grade"/>, the six rankings from <see cref="ScanMagnitudes"/> and
/// <see cref="ScanEngine.Top"/>. All four are the nightly stages' own, made public at 2.6 so the
/// nightly run, the calibration run and a test would share one implementation. A count produced by
/// a second implementation would be a fact about the calibration code rather than about the
/// thresholds, which is the one thing the run is for.
///
/// <b>Ranks accumulate forward and are never recomputed.</b> The thrust checks look back twenty
/// sessions, so a session's hits have to still be there when a later session asks. The caller ranks
/// each session once, in order, before detecting it.
/// </summary>
public sealed class CalibrationFigures : ISessionFigures
{
    private readonly SqliteConnection _connection;
    private readonly DateTimeOffset _computedAt;
    private readonly DateTimeOffset _observedBefore;

    /// <summary>Hits by ticker, in session order, which is the order they are ranked in.</summary>
    private readonly Dictionary<string, List<StoredScanHit>> _hits = new(StringComparer.Ordinal);

    /// <summary>Every session date the store holds for a name, read once and counted from.</summary>
    private readonly Dictionary<string, DateOnly[]> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Each ranked session's pool and mood, assembled at <see cref="Rank"/> time from the windows the
    /// caller is already holding.
    ///
    /// <b>Retained for every ranked session rather than for the current one, and the draw is no
    /// longer why.</b> It was kept per session because a tight control could be drawn from any
    /// earlier session sharing the mood, so a discarded pool was a pool the draw could not reach.
    /// The draw now stays within the night and needs one. What still needs all of them is the
    /// diagnosis, which counts a night's pool against the draw made from it, and the mood series the
    /// reconstructed read reports, both of which read sessions the walk has passed. The memory is
    /// proportional to the range walked either way and is reported rather than hidden: a run over
    /// hundreds of sessions holds hundreds of pools.
    /// see: The tight control set draws within the night, because a within-night draw controls the market mood exactly
    /// </summary>
    private readonly Dictionary<DateOnly, IReadOnlyDictionary<string, ControlMatching.Candidate>> _pools = [];

    /// <summary>Each ranked session's mood, computed from the ladder counts that session's ranking saw.</summary>
    private readonly Dictionary<DateOnly, string?> _moods = [];

    /// <summary>
    /// The indicators computed at <see cref="Rank"/> time, for the session being ranked.
    ///
    /// One session at a time, because the detector walks a session immediately after it is ranked
    /// and asks for exactly these. Without it every name's averages would be computed twice per
    /// session, once to build the pool and once to detect, and that arithmetic is the dominant cost
    /// of a calibration run rather than a rounding on it.
    /// </summary>
    private readonly Dictionary<string, StoredIndicators?> _ranked = new(StringComparer.Ordinal);

    private DateOnly _rankedSession;

    /// <summary>The trackers the mood is scored over, supplied rather than assumed.</summary>
    private readonly IReadOnlyList<string> _indexSymbols;

    public CalibrationFigures(
        SqliteConnection connection,
        DateTimeOffset computedAt,
        DateTimeOffset observedBefore,
        IReadOnlyList<string>? indexSymbols = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _computedAt = computedAt;
        _observedBefore = observedBefore;
        _indexSymbols = indexSymbols ?? [];
    }

    /// <summary>
    /// The last <paramref name="sessions"/> sessions strictly before <paramref name="from"/>.
    ///
    /// What the ranking needs behind the first session a calibration run detects: every check that
    /// reads a thrust looks back twenty sessions, so a run that started ranking and detecting on the
    /// same date would open with sessions that found no thrust and recorded nothing, and nothing
    /// about the count would say so.
    ///
    /// Read from the stored bars rather than counted back in calendar days, because a market week is
    /// five days and a holiday week is four, and a warm-up short by two sessions is the same silent
    /// hole one level down. Shared by both detectors rather than written twice: it decides what the
    /// run covers, and two copies could disagree about that without either one being wrong on its
    /// own terms.
    /// </summary>
    public static IReadOnlyList<DateOnly> SessionsBefore(
        SqliteConnection connection,
        DateOnly from,
        int sessions,
        DateTimeOffset observedBefore)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE bar_date < @from
               AND observed_at <= @observed_before
             ORDER BY bar_date DESC
             LIMIT @sessions
            """;
        command.Parameters.AddWithValue("@from", StoreText.DateToStorageText(from));
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(observedBefore));
        command.Parameters.AddWithValue("@sessions", sessions);

        var dates = new List<DateOnly>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            dates.Add(StoreText.StorageTextToDate(reader.GetString(0)));
        }

        dates.Reverse();
        return dates;
    }

    /// <summary>How many hits have been ranked so far, which is what says the ranking ran at all.</summary>
    public int RankedHits { get; private set; }

    /// <summary>How many sessions have been ranked.</summary>
    public int RankedSessions { get; private set; }

    /// <summary>
    /// Ranks one session's six scans from the windows the detector is about to read anyway.
    ///
    /// The same rule the nightly scan holds: a name short of the whole window is measured on no
    /// scan at all, because a stock with three sessions of history has moved a long way in all of
    /// them and would top the month movers every time.
    /// </summary>
    public void Rank(DateOnly asOf, IReadOnlyDictionary<string, IReadOnlyList<StoredDailyBar>> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        BuildPool(asOf, windows);

        var candidates = new List<ScanEngine.Candidate>();

        foreach ((string ticker, IReadOnlyList<StoredDailyBar> bars) in windows)
        {
            if (bars.Count < ScanEngine.HistorySessions || bars[^1].BarDate != asOf)
            {
                continue;
            }

            StoredDailyBar today = bars[^1];
            StoredDailyBar yesterday = bars[^2];
            StoredDailyBar monthAgo = bars[^(ScanEngine.MonthWindow + 1)];

            candidates.Add(new ScanEngine.Candidate(
                ticker,
                ScanMagnitudes.DailyChange(yesterday.AdjustedClose, today.AdjustedClose),
                ScanMagnitudes.Gap(yesterday.AdjustedClose, today.Open, today.Close, today.AdjustedClose),
                ScanMagnitudes.MonthChange(monthAgo.AdjustedClose, today.AdjustedClose)));
        }

        foreach (string scan in ScanEngine.Scans)
        {
            foreach ((ScanEngine.Candidate candidate, int rank) in ScanEngine.Top(candidates, scan))
            {
                if (!_hits.TryGetValue(candidate.Ticker, out List<StoredScanHit>? forTicker))
                {
                    forTicker = [];
                    _hits[candidate.Ticker] = forTicker;
                }

                // No cluster count. ThemeClusterer counts same-industry names among a night's scan
                // hits, and industry is resolved lazily on first sighting: a reconstructed session
                // has the industry the lab learned in 2026 or none at all, which is the same
                // reconstruction the market-cap clause is exempted for. The cluster check is
                // recorded and gates nothing, so a null here fails one non-gating check on every
                // calibration row rather than changing what the run counts.
                forTicker.Add(new StoredScanHit(
                    candidate.Ticker, asOf, scan, rank, ScanEngine.Magnitude(candidate, scan), ClusterCount: null));

                RankedHits++;
            }
        }

        RankedSessions++;
    }

    public StoredIndicators? Indicators(string ticker, DateOnly asOf, IReadOnlyList<StoredDailyBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        // The ranking pass computed these for the session it is about to be asked about, so the
        // second computation is served rather than repeated. Keyed on the session as well as the
        // name: a stale entry from the previous session would be a figure from the wrong day, which
        // is worse than the work it saves.
        if (asOf == _rankedSession && _ranked.TryGetValue(ticker, out StoredIndicators? ranked))
        {
            return ranked;
        }

        if (bars.Count < IndicatorEngine.WarmupSessions || bars[^1].BarDate != asOf)
        {
            return null;
        }

        // Exactly the warm-up, taken from the end, which is the window the engine reads on a
        // forward night. A longer window would give a different exponential average and the
        // calibration count would stop being a fact about the same arithmetic.
        IReadOnlyList<StoredDailyBar> window = [.. bars.Skip(bars.Count - IndicatorEngine.WarmupSessions)];
        IndicatorValues values = IndicatorEngine.Calculate(window);

        return new StoredIndicators(
            ticker,
            asOf,
            _computedAt,
            values.EmaShort,
            values.EmaMedium,
            values.EmaLong,
            values.AverageTrueRange,
            values.AverageDailyRange,
            values.DollarVolumeMedian,
            values.RangeAverage,
            TierClassifier.Grade(window[^1].AdjustedClose, values));
    }

    public IReadOnlyList<StoredScanHit> Hits(string ticker, DateOnly asOf, DateOnly windowStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        return _hits.TryGetValue(ticker, out List<StoredScanHit>? forTicker)
            ? [.. forTicker.Where(h => h.AsOf >= windowStart && h.AsOf <= asOf)]
            : [];
    }

    /// <summary>
    /// One session's pool and mood, assembled from the windows the caller already holds.
    ///
    /// <b>The ladder counts are the ranking's, not a second tally.</b> Every name's grade comes from
    /// <see cref="TierClassifier.Grade"/> through <see cref="Indicators"/>, which is the same call
    /// the detector makes, and the two counts fall out of the same pass that builds the pool. The
    /// nightly path counts them with a `GROUP BY` over `indicator_daily` and arrives at the same two
    /// numbers; what differs is where the grades live, which is the whole seam.
    ///
    /// <b>The trackers are read from the store, because index bars are backfilled and a
    /// reconstructed session has them.</b> That is the half of the mood that needs no
    /// reconstruction at all: `index_bar` holds SPY, QQQ and IWM for every session in the range,
    /// observed later than the sessions themselves like every other backfilled bar, so the read is
    /// bounded on the run's own instant for the reason the calibration entry gives.
    /// </summary>
    private void BuildPool(DateOnly asOf, IReadOnlyDictionary<string, IReadOnlyList<StoredDailyBar>> windows)
    {
        _ranked.Clear();
        _rankedSession = asOf;

        var pool = new Dictionary<string, ControlMatching.Candidate>(StringComparer.Ordinal);
        int rising = 0;
        int falling = 0;

        foreach ((string ticker, IReadOnlyList<StoredDailyBar> bars) in windows)
        {
            StoredIndicators? figures = Indicators(ticker, asOf, bars);
            _ranked[ticker] = figures;

            if (figures is null)
            {
                continue;
            }

            if (figures.LadderGrade == TierClassifier.Rising)
            {
                rising++;
            }
            else if (figures.LadderGrade == TierClassifier.Falling)
            {
                falling++;
            }

            if (figures.DollarVolumeMedian < Core.Detection.LongPullbackRules.LiquidityFloor)
            {
                continue;
            }

            pool[ticker] = new ControlMatching.Candidate(
                ticker, figures.DollarVolumeMedian, figures.AverageDailyRange,
                figures.LadderGrade, asOf, null);
        }

        string? mood = MarketMood.Of(
            Trackers(asOf), asOf, RegimeLabeler.HistorySessions, rising, falling).Label;

        // The mood is a property of the session, so it is stamped onto every candidate of that
        // session rather than looked up beside them. Built after the pass because the breadth score
        // needs the counts the pass produces.
        _pools[asOf] = pool.ToDictionary(
            e => e.Key, e => e.Value with { MarketMood = mood }, StringComparer.Ordinal);
        _moods[asOf] = mood;
    }

    /// <summary>The three trackers' windows for one session, as the mood scoring wants them.</summary>
    private IReadOnlyList<MarketMood.Tracker> Trackers(DateOnly asOf)
    {
        var trackers = new List<MarketMood.Tracker>();

        foreach (string symbol in _indexSymbols)
        {
            IReadOnlyList<StoredDailyBar> bars = IndexBarReader.Read(
                _connection, symbol, asOf, RegimeLabeler.HistorySessions, _observedBefore);

            trackers.Add(new MarketMood.Tracker(
                [.. bars.Select(b => b.AdjustedClose)],
                bars.Count == 0 ? default : bars[^1].BarDate));
        }

        return trackers;
    }

    /// <summary>
    /// One ranked session's pool, or nothing where that session was never ranked.
    ///
    /// <b>Nothing, rather than a read from the store.</b> A session this run did not rank has no
    /// reconstructed figures anywhere, and falling back on `indicator_daily` would answer with rows
    /// the nightly lab wrote for a different population, silently mixing a reconstructed draw with a
    /// forward one. Empty is the honest answer and the caller counts it.
    /// </summary>
    public IReadOnlyDictionary<string, ControlMatching.Candidate> Candidates(DateOnly asOf, string sessionZone) =>
        _pools.TryGetValue(asOf, out IReadOnlyDictionary<string, ControlMatching.Candidate>? pool)
            ? pool
            : new Dictionary<string, ControlMatching.Candidate>(StringComparer.Ordinal);

    public string? Mood(DateOnly asOf) => _moods.GetValueOrDefault(asOf);

    /// <summary>Every session this run ranked, which is what a reconstructed draw may reach across.</summary>
    public IReadOnlyCollection<DateOnly> RankedPools => _pools.Keys;

    /// <summary>
    /// How many sessions of this name the store holds, counted from a list read once.
    ///
    /// <c>DailyBarReader.SessionsStored</c> answers this with a <c>COUNT(DISTINCT bar_date)</c> over
    /// the whole series, which is right for one night and is one query per name per session here:
    /// five figures' worth on a run of any size. The list is the same list either way and the count
    /// is the same count; what changes is that it is read once.
    ///
    /// Bounded on the run's instant rather than the session's, like every other read this class
    /// makes, for the reason the calibration entry gives.
    /// </summary>
    public int SessionsListed(string ticker, DateOnly asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        if (!_sessions.TryGetValue(ticker, out DateOnly[]? dates))
        {
            dates = [.. Dates(ticker)];
            _sessions[ticker] = dates;
        }

        int index = Array.BinarySearch(dates, asOf);
        return index >= 0 ? index + 1 : ~index;
    }

    private IEnumerable<DateOnly> Dates(string ticker)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT bar_date FROM daily_bar
             WHERE ticker = @ticker
               AND observed_at <= @observed_before
             ORDER BY bar_date
            """;
        command.Parameters.AddWithValue("@ticker", ticker);
        command.Parameters.AddWithValue("@observed_before", StoreText.TimestampToStorageText(_observedBefore));

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return StoreText.StorageTextToDate(reader.GetString(0));
        }
    }

    public decimal? MarketCap(string ticker, DateOnly asOf) => null;

    public bool MarketCapExempt => true;
}
