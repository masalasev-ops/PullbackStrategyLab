using PullbackStrategyLab.Core.Time;
using PullbackStrategyLab.Data;
using Xunit;

namespace PullbackStrategyLab.Tests;

/// <summary>
/// The walk, on its own, without a store or a stage.
///
/// <b>Every case here is authored, and the reason is the one the fixture cannot answer.</b> The
/// golden fixture holds one market day whose setups are flagged on it, so it holds no session with a
/// plan resting in it and no minute walked against one. The properties below are about ordering and
/// refusal, which are exercised by bars written to sit either side of each boundary.
/// see: Gate boundaries are exercised by authored cases and the captured fixture is not asked to do it
/// </summary>
public sealed class SessionReplayClockTests
{
    private static readonly DateOnly Session = new(2026, 8, 26);

    /// <summary>
    /// The walk hands out ascending minutes, and each minute carries that minute's bars and no
    /// other's.
    ///
    /// This is what "cannot see a later minute than the one it is evaluating" means as an assertion
    /// rather than as a comment: at every step, the bars in hand open exactly at the minute in hand.
    /// </summary>
    [Fact]
    public void The_walk_ascends_and_every_minute_carries_only_its_own_bars()
    {
        SessionReplayClock clock = SessionReplayClock.Over(Session,
        [
            Bar("MSFT", new TimeOnly(9, 31), high: 51m, low: 50m),
            Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m),
            Bar("AAPL", new TimeOnly(9, 31), high: 102m, low: 101m),
        ]);

        DateTimeOffset? previous = null;
        var seen = new List<int>();

        foreach (ReplayMinute minute in clock.Walk())
        {
            Assert.True(previous is null || minute.OpenedAt > previous,
                $"The walk handed {minute.OpenedAt:HH:mm} after {previous:HH:mm}, so the session is not "
                + "in order and every earliest-trigger comparison over it is meaningless.");

            foreach (StoredIntradayBar bar in minute.Bars.Values)
            {
                Assert.Equal(minute.OpenedAt, bar.OpenedAt);
            }

            Assert.Equal(minute.OpenedAt, clock.Now);

            previous = minute.OpenedAt;
            seen.Add(minute.Bars.Count);
        }

        // Two minutes, one name in the first and two in the second, which is what "every name at
        // once" produces: 09:31 is one minute of the session and not one minute of each name's day.
        Assert.Equal([1, 2], seen);
        Assert.Equal(2, clock.Minutes);
        Assert.Null(clock.Now);
    }

    /// <summary>
    /// A name that did not trade in a minute is absent from it rather than carried forward.
    ///
    /// A minute bar exists only for a minute that traded, so repeating the last one forward would let
    /// a trigger be reached by a price that was never printed. The gap below is a real shape: a
    /// thinly traded name has minutes the exchange has no print for.
    /// </summary>
    [Fact]
    public void A_name_that_did_not_trade_in_a_minute_is_absent_from_it()
    {
        SessionReplayClock clock = SessionReplayClock.Over(Session,
        [
            Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m),
            Bar("THIN", new TimeOnly(9, 30), high: 11m, low: 10m),
            Bar("AAPL", new TimeOnly(9, 31), high: 102m, low: 101m),
        ]);

        ReplayMinute[] minutes = [.. clock.Walk()];

        Assert.NotNull(minutes[0].Of("THIN"));
        Assert.Null(minutes[1].Of("THIN"));
        Assert.NotNull(minutes[1].Of("AAPL"));
    }

    /// <summary>
    /// The walk happens once, and a second is refused.
    ///
    /// A second enumeration from inside the first is exactly how a caller sees a minute later than
    /// the one it is standing on, and the answer it produced would look no different from an honest
    /// one. Refusing is what makes the forward-only property a thing the type holds rather than a
    /// thing the caller is trusted with.
    /// </summary>
    [Fact]
    public void A_second_walk_is_refused()
    {
        SessionReplayClock clock = SessionReplayClock.Over(Session,
            [Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m)]);

        _ = clock.Walk().ToList();

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => clock.Walk());
        Assert.Contains("already been walked", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bars of another session are refused at construction, rather than extending the sequence.
    ///
    /// It cannot happen through the reader, which is bounded on a session. It is asserted because the
    /// walk's whole meaning is that its minutes are one trading day in order, and a second session's
    /// minutes read as a continuation of the first: ascending, well formed, and a day later.
    /// </summary>
    [Fact]
    public void Bars_of_another_session_are_refused()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => SessionReplayClock.Over(Session,
        [
            Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m),
            Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m, session: Session.AddDays(1)),
        ]));

        Assert.Contains("2026-08-27", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two bars for one name in one minute are refused rather than resolved by taking either.
    ///
    /// The read that feeds a replay already takes the latest observation of each minute, so two here
    /// means the bars were assembled by something else. Silently keeping one would drop an
    /// observation with nothing recording that it had been dropped.
    /// </summary>
    [Fact]
    public void Two_bars_for_one_name_in_one_minute_are_refused()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => SessionReplayClock.Over(Session,
        [
            Bar("AAPL", new TimeOnly(9, 30), high: 101m, low: 100m),
            Bar("AAPL", new TimeOnly(9, 30), high: 109m, low: 100m),
        ]));

        Assert.Contains("two bars", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A session the store holds nothing for walks nothing and says so with a count.</summary>
    [Fact]
    public void A_session_with_no_stored_minute_walks_nothing()
    {
        SessionReplayClock clock = SessionReplayClock.Over(Session, []);

        Assert.Equal(0, clock.Minutes);
        Assert.Empty(clock.Walk());
    }

    private static StoredIntradayBar Bar(
        string ticker, TimeOnly local, decimal high, decimal low, DateOnly? session = null)
    {
        DateOnly on = session ?? Session;
        DateTimeOffset openedAt = SessionBoundaries.At(on, local, SessionBoundaries.UsEquities);

        return new StoredIntradayBar(
            ticker, openedAt, on, "1m", "regular", "raw",
            Open: low, High: high, Low: low, Close: high, Volume: 1_000, VwapSession: null,
            ObservedAt: openedAt);
    }
}
