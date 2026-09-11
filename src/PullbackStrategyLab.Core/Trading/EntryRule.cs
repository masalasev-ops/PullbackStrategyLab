using PullbackStrategyLab.Core.Detection;
using PullbackStrategyLab.Core.Indicators;
using PullbackStrategyLab.Core.Time;

namespace PullbackStrategyLab.Core.Trading;

/// <summary>
/// Generation 1's entry rule, as the plan carries it and as the entry minute resolves it, from 7.8.
///
/// <b>The plan carries the rule and the minute resolves the prices.</b> The entry the strategy states
/// is a fast flush into the hourly 9 and 21 averages and then the first break of the previous bar's
/// extreme on a one, five or fifteen minute chart depending on how far into the session it is, so the
/// price it fills at and the stop that follows are known at the minute it happens and not the evening
/// before.
/// see: Order prices and the share count resolve at the entry minute
///
/// "a really, really, really fast flush to the downside into the support level, which is on the hourly 9
/// and the 21 EMA, and then I will look at the 1 minute or the 5 minute or the 15 minute candle for the
/// first breakout of the previous bar high" (`P1` 1:56:39). The short side is the same sentence turned
/// over, on his word that he trades shorts by flipping the long side (`P1` 2:06:31).
///
/// <b>What the source does not give is the author's, and each is named where it is used</b>: the sixty
/// minutes at which the candle widens to fifteen, the zone being reached rather than the speed of the
/// move into it, the stub counting towards an hourly average, and the first break after the flush being
/// the one decision a session gets.
/// see: Generation 1's entry is armed by the flush into the hourly zone and taken on the first break after it, and a session gets one such decision
/// </summary>
public static class EntryRule
{
    /// <summary>The rule a generation 1 plan carries, written on the plan row.</summary>
    public const string FlushReclaim = "flush-reclaim";

    /// <summary>A plan written before 7.8, which carried the evening's prices and the size.</summary>
    public const string EveningPrices = "evening-prices";

    /// <summary>The shorter hourly average of the zone.</summary>
    public const int ShortPeriod = 9;

    /// <summary>The longer hourly average of the zone, and the one whose warm-up decides whether a level exists.</summary>
    public const int LongPeriod = 21;

    /// <summary>
    /// The hourly closes a level needs behind it: three times the longer period, the lab's convergence
    /// rule for an exponential average, which is why the daily warm-up is 150 sessions for the 50-day.
    /// </summary>
    public const int WarmupHourlyBars = 3 * LongPeriod;

    /// <summary>One-minute candles for the first fifteen minutes of the session, in his words (`P1` 1:57:14).</summary>
    public const int OneMinuteBandMinutes = 15;

    /// <summary>
    /// Five-minute candles until sixty minutes in, and fifteen-minute candles after. **The sixty is the
    /// author's**: he gives the first band and puts the second at "maybe the 60 minute" (`P1` 1:57:14),
    /// and the third follows from his three-way list.
    /// </summary>
    public const int FiveMinuteBandMinutes = 60;

    /// <summary>He does not chase a name more than 3% off the session's low (`P1` 24:22); a short mirrors it off the high.</summary>
    public const decimal ChaseLimit = 0.03m;

    /// <summary>The day-extreme stop is too wide at more than 2% and moves to the entry candle (`P1` 2:12:13).</summary>
    public const decimal StopSwitchAbove = 0.02m;

    /// <summary>The absolute ceiling on a stop, being what half the range comes to at the widest range he trades (`P1` 17:48).</summary>
    public const decimal AbsoluteCeiling = 0.05m;

    /// <summary>The share of the daily range a stop may not exceed (`P1` 17:48, `P2` 25:48, `P3`).</summary>
    public const decimal RangeShare = 0.5m;

    /// <summary>
    /// The widest stop an entry may take, as a fraction of the entry price: the tighter of half the
    /// daily range and 5%, the same on both sides.
    /// see: The entry ceiling is the tighter of half the daily range and 5%
    /// </summary>
    public static decimal CeilingFor(decimal averageDailyRange)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(averageDailyRange);
        return Math.Min(averageDailyRange * RangeShare, AbsoluteCeiling);
    }

    /// <summary>The candle width in force at <paramref name="minutesSinceOpen"/>: one, five or fifteen minutes.</summary>
    public static int CandleMinutesAt(int minutesSinceOpen) =>
        minutesSinceOpen < OneMinuteBandMinutes ? 1
        : minutesSinceOpen < FiveMinuteBandMinutes ? 5
        : 15;

    /// <summary>
    /// The hourly closes of one regular session, on the grid anchored to the open, **the closing stub
    /// included as a value**: the level he reads is a charting platform's hourly series, and those close
    /// the session's last bar at 16:00 as a bar. The stub still cannot fire a rule that turns on an hourly
    /// close; this is the series an average is taken over, which is a different question.
    /// see: The hourly grid anchors to the session open, and the closing stub is not an hourly bar
    /// </summary>
    public static IReadOnlyList<decimal> HourlyCloses(
        IEnumerable<(DateTimeOffset OpenedAt, decimal Close)> minutes, DateOnly session, string zone)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        var byBar = new SortedDictionary<int, (DateTimeOffset At, decimal Close)>();

        foreach ((DateTimeOffset openedAt, decimal close) in minutes)
        {
            if (BarOf(openedAt, session, zone) is not int bar)
            {
                continue;
            }

            if (!byBar.TryGetValue(bar, out (DateTimeOffset At, decimal Close) last) || openedAt > last.At)
            {
                byBar[bar] = (openedAt, close);
            }
        }

        return [.. byBar.Values.Select(v => v.Close)];
    }

    /// <summary>
    /// Which hourly bar of the session a minute falls in, the stub being the bar after the last complete
    /// one, or null outside the regular session.
    /// </summary>
    public static int? BarOf(DateTimeOffset instant, DateOnly session, string zone)
    {
        if (!SessionBoundaries.IsRegularSession(instant, session, zone))
        {
            return null;
        }

        return HourlyGrid.BarIndexOf(instant, session, zone) ?? HourlyGrid.CompleteBars;
    }
}

/// <summary>
/// The entry the rule took, as known at the minute it was taken and at no later one.
///
/// <paramref name="Price"/> is the previous candle's extreme the minute broke through, which is the
/// price a resting order on it rests at. <paramref name="SessionExtreme"/> and
/// <paramref name="CandleExtreme"/> are the session's and the entry candle's low for a long, high for a
/// short, **through the entry minute and never past it**, which is what the stop is set from.
/// </summary>
public sealed record EntryPoint(
    DateTimeOffset Minute,
    decimal Price,
    int CandleMinutes,
    string Level,
    decimal LevelValue,
    DateTimeOffset ArmedAt,
    decimal SessionExtreme,
    decimal CandleExtreme);

/// <summary>
/// The rule watched over one session, one minute at a time, for one plan.
///
/// <b>Forward-only by construction.</b> It is fed minutes in order and answers from what it has been
/// fed, so the stop it hands back is the session's extreme as of the entry minute: an extreme set after
/// the entry is a minute it has not been given. A replay of the evening has the whole session on disk,
/// and taking the completed session's extreme would set a stop from a price printed after the order
/// was filled.
///
/// <b>The levels move through the session.</b> Each hourly bar that completes adds its close to the
/// series, so the zone at 11:15 includes the 10:30 bar and not the 11:30 one.
/// </summary>
public sealed class EntryWatch
{
    private readonly string _direction;
    private readonly DateOnly _session;
    private readonly string _zone;
    private readonly DateTimeOffset _open;
    private readonly List<decimal> _closes;
    private readonly List<(int T, decimal High, decimal Low)> _minutes = [];

    private int? _hourBar;
    private decimal _hourClose;
    private decimal? _shortLevel;
    private decimal? _longLevel;
    private decimal _sessionHigh = decimal.MinValue;
    private decimal _sessionLow = decimal.MaxValue;
    private int? _armedAt;
    private DateTimeOffset _armedInstant;
    private string _armedLevel = string.Empty;
    private decimal _armedValue;

    public EntryWatch(string direction, IReadOnlyList<decimal> priorHourlyCloses, DateOnly session, string zone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(priorHourlyCloses);

        if (direction is not (SetupDirection.Long or SetupDirection.Short))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                $"'{direction}' is neither '{SetupDirection.Long}' nor '{SetupDirection.Short}', and the rule "
                + "reads opposite ends of every bar on the two sides.");
        }

        _direction = direction;
        _session = session;
        _zone = zone;
        _open = SessionBoundaries.At(session, SessionBoundaries.RegularSessionOpen, zone);
        _closes = [.. priorHourlyCloses];
        Recompute();
    }

    /// <summary>Whether the series ever held enough closes for a level, which is what separates no reclaim from no answer.</summary>
    public bool HadLevels { get; private set; }

    /// <summary>Whether the price reached the zone at some point, which is the first half of the rule.</summary>
    public bool Armed => _armedAt is not null;

    /// <summary>The entry, once taken; the watch answers once and then says nothing more.</summary>
    public EntryPoint? Entry { get; private set; }

    /// <summary>
    /// One regular-session minute. Returns the entry on the minute the rule takes it and null on every
    /// other minute, including every minute after it.
    /// </summary>
    public EntryPoint? Observe(DateTimeOffset openedAt, decimal high, decimal low, decimal close)
    {
        if (Entry is not null || EntryRule.BarOf(openedAt, _session, _zone) is not int bar)
        {
            return null;
        }

        // An hourly bar that has completed before this minute joins the series first, so the zone this
        // minute is read against is the one its own time could see.
        if (_hourBar is int current && bar > current)
        {
            _closes.Add(_hourClose);
            Recompute();
        }

        _hourBar = bar;
        _hourClose = close;

        int t = (int)(openedAt - _open).TotalMinutes;
        _minutes.Add((t, high, low));
        _sessionHigh = Math.Max(_sessionHigh, high);
        _sessionLow = Math.Min(_sessionLow, low);

        bool isLong = _direction == SetupDirection.Long;

        if (_armedAt is int armed)
        {
            int width = EntryRule.CandleMinutesAt(t);
            int candleStart = t / width * width;

            // The previous candle has to be one the flush was in or came before, so the break is a break
            // after the flush rather than of a bar the flush had not reached yet.
            if (candleStart > armed)
            {
                (int T, decimal High, decimal Low)[] previous =
                    [.. _minutes.Where(m => m.T >= candleStart - width && m.T < candleStart)];

                if (previous.Length > 0)
                {
                    decimal level = isLong ? previous.Max(m => m.High) : previous.Min(m => m.Low);

                    if (TriggerTouch.Reached(_direction, level, high, low))
                    {
                        (int T, decimal High, decimal Low)[] candle = [.. _minutes.Where(m => m.T >= candleStart)];

                        Entry = new EntryPoint(
                            openedAt,
                            level,
                            width,
                            _armedLevel,
                            _armedValue,
                            _armedInstant,
                            isLong ? _sessionLow : _sessionHigh,
                            isLong ? candle.Min(m => m.Low) : candle.Max(m => m.High));

                        return Entry;
                    }
                }
            }

            return null;
        }

        // The flush: the minute reaches the nearer edge of the zone the two hourly averages make.
        if (_shortLevel is decimal nine && _longLevel is decimal twentyOne)
        {
            decimal nearer = isLong ? Math.Max(nine, twentyOne) : Math.Min(nine, twentyOne);
            bool reached = isLong ? low <= nearer : high >= nearer;

            if (reached)
            {
                _armedAt = t;
                _armedInstant = openedAt;
                _armedLevel = nearer == nine ? "hourly-ema-9" : "hourly-ema-21";
                _armedValue = nearer;
            }
        }

        return null;
    }

    private void Recompute()
    {
        if (_closes.Count < EntryRule.WarmupHourlyBars)
        {
            _shortLevel = null;
            _longLevel = null;
            return;
        }

        HadLevels = true;
        _shortLevel = Averages.Exponential(_closes, EntryRule.ShortPeriod);
        _longLevel = Averages.Exponential(_closes, EntryRule.LongPeriod);
    }
}

/// <summary>
/// The stop an entry resolves to, or why the entry is refused, from 7.8.
///
/// <b>Three sourced rules in order, and one filter before them.</b> The chase filter refuses an entry
/// more than 3% off the session's extreme. The stop is the session's extreme as of the entry minute;
/// where that is more than 2% from the entry it moves to the entry candle's extreme; and the entry is
/// refused where the stop is still wider than the tighter of half the daily range and 5%.
/// see: The stop switches to the entry candle at more than 2%, and 5% as the switch is fourth-hand
/// see: The entry ceiling is the tighter of half the daily range and 5%
/// </summary>
public static class EntryStop
{
    public const string SessionExtremeBasis = "session-extreme";
    public const string EntryCandleBasis = "entry-candle";

    public sealed record Resolution(
        string? Basis,
        decimal? Stop,
        decimal? Distance,
        decimal? Fraction,
        string? RefusedBecause);

    public static Resolution Resolve(string direction, EntryPoint entry, decimal ceiling)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentNullException.ThrowIfNull(entry);

        bool isLong = direction == SetupDirection.Long;
        decimal price = entry.Price;

        decimal chased = isLong
            ? (price - entry.SessionExtreme) / entry.SessionExtreme
            : (entry.SessionExtreme - price) / entry.SessionExtreme;

        if (chased > EntryRule.ChaseLimit)
        {
            return Refused(
                $"the entry is {chased:P2} off the session's {(isLong ? "low" : "high")}, past the "
                + $"{EntryRule.ChaseLimit:P0} he will not chase");
        }

        string basis = SessionExtremeBasis;
        decimal stop = entry.SessionExtreme;
        decimal distance = isLong ? price - stop : stop - price;

        if (distance / price > EntryRule.StopSwitchAbove)
        {
            basis = EntryCandleBasis;
            stop = entry.CandleExtreme;
            distance = isLong ? price - stop : stop - price;
        }

        if (distance <= 0m)
        {
            return Refused(
                $"the {basis} gives no stop {(isLong ? "below" : "above")} the entry, so there is no distance to size against");
        }

        decimal fraction = distance / price;

        // Refused with the stop it would have taken, so the row says how wide the stop it refused was.
        return new Resolution(
            basis, stop, distance, fraction,
            fraction > ceiling ? $"the stop is {fraction:P2} from the entry, wider than the ceiling of {ceiling:P2}" : null);
    }

    private static Resolution Refused(string because) => new(null, null, null, null, because);
}
