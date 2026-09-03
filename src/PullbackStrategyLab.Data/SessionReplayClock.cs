using Microsoft.Data.Sqlite;

namespace PullbackStrategyLab.Data;

/// <summary>
/// One session's stored minutes, handed out one minute at a time, every name at once.
///
/// <b>It owns no table.</b> It reads <c>intraday_bar</c> and writes nothing, which is why it appears
/// in SCHEMA's list of components that own none rather than in the ownership table.
/// see: Trades are resolved by replaying minute bars after the close, not by watching live
///
/// <b>One clock for the session rather than one per name, and that is the shape the contention rule
/// needs.</b> When more plans trigger than the caps allow the earliest trigger fills and the later
/// ones are blocked, so "which fired first" is a comparison across names. A clock per name would
/// resolve each name correctly and would give a caller no ordering at all, and the ordering would
/// then have to be reconstructed afterwards by sorting recorded times, which is the same answer
/// arrived at by a second implementation that could disagree with the first.
/// see: Plans are resting orders and fills go in time order when the caps bind
///
/// <b>Forward blindness is structural rather than a comment.</b> The only way out is
/// <see cref="Walk"/>, which yields ascending minutes and hands each caller that minute's bars and
/// nothing else. There is no method taking an instant, so a caller cannot ask what happens later,
/// and <see cref="Walk"/> may be enumerated once: a second enumeration is exactly how a caller would
/// look ahead while standing still, so it is refused rather than left available.
///
/// <b>Bars of another session are refused at construction.</b> The reader is bounded on a session,
/// so this cannot happen through it today. It is asserted anyway because the walk's whole meaning is
/// that its minutes are one trading day in order, and a second session's minutes would extend the
/// sequence with instants that look like a continuation of the first.
/// </summary>
public sealed class SessionReplayClock
{
    private readonly IReadOnlyList<ReplayMinute> _minutes;
    private bool _walked;

    private SessionReplayClock(DateOnly session, IReadOnlyList<ReplayMinute> minutes)
    {
        Session = session;
        _minutes = minutes;
    }

    /// <summary>The trading day being walked.</summary>
    public DateOnly Session { get; }

    /// <summary>How many distinct minutes the store holds for this session, across every name.</summary>
    public int Minutes => _minutes.Count;

    /// <summary>The minute currently being evaluated, or null before the walk starts and after it ends.</summary>
    public DateTimeOffset? Now { get; private set; }

    /// <summary>
    /// The minutes of <paramref name="sessionDate"/> for <paramref name="tickers"/>, as last observed
    /// by the end of <paramref name="asOf"/>.
    ///
    /// Regular-session minutes only. The extended-hours bars are stored and are deliberately not
    /// walked here: a resting order in this lab is live in the regular session, and admitting the
    /// pre-market would fill plans at prices no regular-session order could have been hit at.
    /// </summary>
    public static SessionReplayClock ForSession(
        SqliteConnection connection, IReadOnlyCollection<string> tickers, DateOnly sessionDate, DateOnly asOf, string sessionZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tickers);

        return Over(sessionDate, IntradayBarReader.ReadSession(connection, tickers, sessionDate, asOf, sessionZone));
    }

    /// <summary>
    /// The same walk over bars a caller already holds, so the ordering is assertable without a store.
    ///
    /// <paramref name="bars"/> may arrive in any order and is sorted here. The reader returns them
    /// ordered already; this does not rely on that, because the property the walk carries is that
    /// minutes ascend, and a property that holds only because of what some other component happened
    /// to do is one nothing is asserting.
    /// </summary>
    public static SessionReplayClock Over(DateOnly sessionDate, IEnumerable<StoredIntradayBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);

        var byMinute = new SortedDictionary<DateTimeOffset, Dictionary<string, StoredIntradayBar>>();

        foreach (StoredIntradayBar bar in bars)
        {
            if (bar.SessionDate != sessionDate)
            {
                throw new ArgumentException(
                    $"{bar.Ticker} has a bar of {bar.SessionDate:yyyy-MM-dd} among the minutes of "
                    + $"{sessionDate:yyyy-MM-dd}. A replay walks one trading day in order, and a second "
                    + "session's minutes would extend that sequence with instants that read as a "
                    + "continuation of the first.",
                    nameof(bars));
            }

            if (!byMinute.TryGetValue(bar.OpenedAt, out Dictionary<string, StoredIntradayBar>? names))
            {
                names = new Dictionary<string, StoredIntradayBar>(StringComparer.Ordinal);
                byMinute[bar.OpenedAt] = names;
            }

            // The reader already returns one observation per minute per name. A second here would be
            // two answers to one question, so it is refused rather than resolved by taking either.
            if (!names.TryAdd(bar.Ticker, bar))
            {
                throw new ArgumentException(
                    $"{bar.Ticker} has two bars opening at {bar.OpenedAt:yyyy-MM-dd HH:mm:ssK}. The read "
                    + "that feeds a replay takes the latest observation of each minute, so two here means "
                    + "the bars were assembled by something else and one of them would be silently dropped.",
                    nameof(bars));
            }
        }

        var minutes = new List<ReplayMinute>(byMinute.Count);

        foreach ((DateTimeOffset openedAt, Dictionary<string, StoredIntradayBar> names) in byMinute)
        {
            minutes.Add(new ReplayMinute(openedAt, names));
        }

        return new SessionReplayClock(sessionDate, minutes);
    }

    /// <summary>
    /// The session, one minute at a time, earliest first.
    ///
    /// Enumerable once. A caller that wants to look ahead has to enumerate a second time from a
    /// standing position, so the second enumeration is refused: it is the one move that turns a
    /// forward-only walk into a read of the whole day, and it would leave every figure the walk
    /// produced looking exactly as it does now.
    /// </summary>
    public IEnumerable<ReplayMinute> Walk()
    {
        if (_walked)
        {
            throw new InvalidOperationException(
                $"The replay of {Session:yyyy-MM-dd} has already been walked. A clock is walked once, "
                + "because a second walk from inside the first is how a caller sees a minute later than "
                + "the one it is evaluating, and the answer it produced would look no different.");
        }

        _walked = true;

        return Walking();
    }

    private IEnumerable<ReplayMinute> Walking()
    {
        foreach (ReplayMinute minute in _minutes)
        {
            Now = minute.OpenedAt;
            yield return minute;
        }

        Now = null;
    }
}

/// <summary>
/// One minute of a session, and what every name did in it.
///
/// The bars are the minute's own and no other's, which is the whole property the walk carries. A
/// name that did not trade in this minute is absent rather than carried forward at its last price:
/// a minute bar exists only for a minute that traded, and a stale bar repeated forward would let a
/// trigger be reached by a price that was not printed.
/// </summary>
public sealed record ReplayMinute(
    DateTimeOffset OpenedAt,
    IReadOnlyDictionary<string, StoredIntradayBar> Bars)
{
    /// <summary>This minute's bar for <paramref name="ticker"/>, or null where it did not trade.</summary>
    public StoredIntradayBar? Of(string ticker) =>
        Bars.TryGetValue(ticker, out StoredIntradayBar? bar) ? bar : null;
}
