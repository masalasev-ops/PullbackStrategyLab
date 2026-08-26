using Microsoft.Data.Sqlite;
using PullbackStrategyLab.Core.Indicators;
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

    public CalibrationFigures(SqliteConnection connection, DateTimeOffset computedAt, DateTimeOffset observedBefore)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _computedAt = computedAt;
        _observedBefore = observedBefore;
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
