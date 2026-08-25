#!/usr/bin/env python3
"""Derive indicator values independently of IndicatorEngine, and diff them against the store.

This is a verification aid for checkpoint 1.6, not part of the application and not run by
tools/ci.*. Nothing in the lab imports it and nothing breaks if it is deleted; it exists so a
number the engine wrote can be disagreed with by something that shares no code with it.

Why a second language. The done condition asks for an independent calculation, and the
decision written at 1.1 asks that it share no code with the engine. A second C# class in the
same solution would share the store readers, the decimal helpers, the window selection and,
more to the point, the same author's reading of what each formula means. Rewriting it here
from the textbook definitions removes everything except the last of those, which is why the
tier this can reach on its own is DERIVED and not CONFIRMED: it catches a transcription
error, an off-by-one in a window, a seed taken from the wrong place. It cannot catch the two
implementations agreeing on a definition that is wrong. That is what a charting platform's
own readout is for, and that check is a person's, not this script's.

The formulas, stated from their definitions rather than from the C#:

  EMA(n)    seed on the simple mean of the first n adjusted closes, then
            ema <- ema + (x - ema) * 2/(n+1) for every later close.
  ATR(14)   true range is max(high-low, |high-prev_close|, |low-prev_close|), taken on the
            adjusted basis. It is undefined for the first bar, which has no previous close.
            Wilder's smoothing, seeded on the simple mean of the first 14 true ranges:
            atr <- (atr*13 + tr)/14. Not an exponential average with period 14, which is a
            different number.
  ADR(20)   mean over the last 20 sessions of (high-low)/close. A fraction, so 0.068 rather
            than 6.8. Scale free: the adjustment factor cancels top and bottom.
  RANGE(20) mean over the last 20 sessions of (high-low), on the adjusted basis.
  DV(20)    median over the last 20 sessions of close*volume, on the RAW basis, because that
            is what actually changed hands and it is what the universe screen uses.

The adjusted basis: the store holds a raw open, high, low and close and an adjusted close,
so the high and the low are put on the adjusted basis through each bar's own factor,
adj_close / close.

The chart mode, --chart, derives the shared component's layout instead. A chart is the one
place where looking at the result is least reliable: a scale that clips an average, a body
drawn upside down and an axis on the wrong step all look like a chart. So the layout is
derived here from its own statement of what it should be:

  scale     lowest adjusted low to highest adjusted high across the window, and across every
            average drawn with it. A flat series widens by half a unit either side.
  y(price)  (high - price) / (high - low) * plot height, measured down from the top.
  x(i)      (i + 0.5) * plot width / count, so a candle sits in the middle of its slot.
  body      0.62 of a slot wide, never under one unit.
  ticks     five or so labels on a round step: the span over five, rounded up to 1, 2, 5 or 10
            times its own power of ten, starting at the first multiple at or above the low.

Usage:  python tools/derive-indicators.py <store.db> <as-of> <ticker> [<ticker> ...]
        python tools/derive-indicators.py --chart <store.db> <as-of> <ticker> <sessions> <width> <height>
"""

import sqlite3
import sys
from decimal import Decimal, getcontext

getcontext().prec = 40

WARMUP = 150
RANGE_WINDOW = 20
EMA_PERIODS = (9, 21, 50)
ATR_PERIOD = 14
PLACES = Decimal("0.0001")


def window(connection, ticker, as_of, sessions):
    """The last `sessions` bars up to `as_of`, oldest first, point in time.

    Only observations made by the end of the as-of date count, and within a date the latest
    such observation wins. Written out here rather than borrowed, because the window is as
    much a part of the answer as the arithmetic is.
    """
    bound = as_of + "T23:59:59.999Z"
    rows = connection.execute(
        """
        SELECT bar_date, high, low, close, adj_close, volume
          FROM daily_bar b
         WHERE b.ticker = ?
           AND b.bar_date <= ?
           AND b.observed_at <= ?
           AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                 WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                   AND l.observed_at <= ?)
         ORDER BY b.bar_date DESC
         LIMIT ?
        """,
        (ticker, as_of, bound, bound, sessions),
    ).fetchall()

    rows.reverse()
    return [
        {
            "date": r[0],
            "high": Decimal(r[1]),
            "low": Decimal(r[2]),
            "close": Decimal(r[3]),
            "adj_close": Decimal(r[4]),
            "volume": Decimal(r[5]),
        }
        for r in rows
    ]


def adjusted(bars):
    """High, low and close on one basis, through each bar's own adj_close/close factor."""
    out = []
    for b in bars:
        factor = Decimal(1) if b["close"] == 0 else b["adj_close"] / b["close"]
        out.append(
            {
                "high": b["high"] * factor,
                "low": b["low"] * factor,
                "close": b["adj_close"],
                "raw_dollar_volume": b["close"] * b["volume"],
            }
        )
    return out


def ema(values, period):
    seed = sum(values[:period]) / period
    multiplier = Decimal(2) / (period + 1)
    value = seed
    for x in values[period:]:
        value = value + (x - value) * multiplier
    return value


def atr(bars, period):
    ranges = []
    for i in range(1, len(bars)):
        previous = bars[i - 1]["close"]
        ranges.append(
            max(
                bars[i]["high"] - bars[i]["low"],
                abs(bars[i]["high"] - previous),
                abs(bars[i]["low"] - previous),
            )
        )

    value = sum(ranges[:period]) / period
    for tr in ranges[period:]:
        value = (value * (period - 1) + tr) / period
    return value


def median(values):
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2 == 1:
        return ordered[middle]
    return (ordered[middle - 1] + ordered[middle]) / 2


def derive(bars):
    adj = adjusted(bars)
    closes = [b["close"] for b in adj]
    tail = adj[-RANGE_WINDOW:]

    return {
        "ema_9": ema(closes, EMA_PERIODS[0]),
        "ema_21": ema(closes, EMA_PERIODS[1]),
        "ema_50": ema(closes, EMA_PERIODS[2]),
        "atr_14": atr(adj, ATR_PERIOD),
        "adr_20": sum((b["high"] - b["low"]) / b["close"] for b in tail) / RANGE_WINDOW,
        "range_avg_20": sum(b["high"] - b["low"] for b in tail) / RANGE_WINDOW,
        "dollar_volume_median_20": median([b["raw_dollar_volume"] for b in tail]),
    }


def stored(connection, ticker, as_of):
    """The latest computation of that session, which is what a read as of today returns.

    indicator_daily is append-only: a rebuild writes a second row for a session rather than
    replacing the first, so a query without the ordering below picks whichever row the engine
    happens to return and would compare against a superseded one.
    """
    row = connection.execute(
        """
        SELECT ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20
          FROM indicator_daily WHERE ticker = ? AND as_of = ?
         ORDER BY computed_at DESC LIMIT 1
        """,
        (ticker, as_of),
    ).fetchone()

    if row is None:
        return None

    names = ["ema_9", "ema_21", "ema_50", "atr_14", "adr_20", "dollar_volume_median_20", "range_avg_20"]
    return dict(zip(names, (Decimal(v) for v in row)))


PRICE_GUTTER = 56
DATE_GUTTER = 22
BODY_FRACTION = Decimal("0.62")
WANTED_TICKS = 5
COORDINATE = Decimal("0.01")


def chart_window(connection, ticker, as_of, sessions):
    """The same point-in-time window, with the open, which a candle needs and an average does not."""
    bound = as_of + "T23:59:59.999Z"
    rows = connection.execute(
        """
        SELECT bar_date, open, high, low, close, adj_close
          FROM daily_bar b
         WHERE b.ticker = ?
           AND b.bar_date <= ?
           AND b.observed_at <= ?
           AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                 WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                   AND l.observed_at <= ?)
         ORDER BY b.bar_date DESC
         LIMIT ?
        """,
        (ticker, as_of, bound, bound, sessions),
    ).fetchall()

    rows.reverse()
    out = []
    for date, o, h, l, c, adj in rows:
        close = Decimal(c)
        factor = Decimal(1) if close == 0 else Decimal(adj) / close
        out.append(
            {
                "date": date,
                "open": Decimal(o) * factor,
                "high": Decimal(h) * factor,
                "low": Decimal(l) * factor,
                "close": Decimal(adj),
            }
        )
    return out


def ticks(low, high):
    """A round step covering the span in about five labels."""
    rough = (high - low) / WANTED_TICKS
    magnitude = Decimal(10) ** (rough.log10() // 1)
    ratio = rough / magnitude
    step = magnitude * (Decimal(1) if ratio <= 1 else
                        Decimal(2) if ratio <= 2 else
                        Decimal(5) if ratio <= 5 else Decimal(10))

    out = []
    price = (low / step).__ceil__() * step
    while price <= high:
        out.append(price)
        price += step
    return out


def derive_chart(bars, width, height):
    low = min(b["low"] for b in bars)
    high = max(b["high"] for b in bars)
    if high == low:
        low -= Decimal("0.5")
        high += Decimal("0.5")

    plot_width = Decimal(width - PRICE_GUTTER)
    plot_height = Decimal(height - DATE_GUTTER)
    step = plot_width / len(bars)

    def y(price):
        return (high - price) / (high - low) * plot_height

    labels = ticks(low, high)

    return {
        "sessions": len(bars),
        "low": low.quantize(PLACES),
        "high": high.quantize(PLACES),
        "upCandles": sum(1 for b in bars if b["close"] >= b["open"]),
        "priceTicks": len(labels),
        "firstTick": labels[0].quantize(PLACES),
        "lastTick": labels[-1].quantize(PLACES),
        "bodyWidth": max(Decimal(1), step * BODY_FRACTION).quantize(COORDINATE),
        "firstCentre": (step / 2).quantize(COORDINATE),
        "lastCentre": ((len(bars) - 1) * step + step / 2).quantize(COORDINATE),
        "lastHighY": y(bars[-1]["high"]).quantize(COORDINATE),
        "lastLowY": y(bars[-1]["low"]).quantize(COORDINATE),
    }


def chart_main(argv):
    store, as_of, ticker = argv[0], argv[1], argv[2]
    sessions, width, height = int(argv[3]), int(argv[4]), int(argv[5])

    connection = sqlite3.connect(store)
    bars = chart_window(connection, ticker, as_of, sessions)

    if not bars:
        print(f"no bars for {ticker} as of {as_of}", file=sys.stderr)
        return 2

    print(f"{ticker}  chart as of {as_of}, {bars[0]['date']} to {bars[-1]['date']}, {width}x{height}")
    for name, value in derive_chart(bars, width, height).items():
        print(f"  chart.{ticker}.{name:<12} {value}")

    return 0


def main(argv):
    if len(argv) > 1 and argv[1] == "--chart":
        return chart_main(argv[2:])

    if len(argv) < 4:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of, tickers = argv[1], argv[2], argv[3:]
    connection = sqlite3.connect(store)
    disagreements = 0

    for ticker in tickers:
        bars = window(connection, ticker, as_of, WARMUP)
        print(f"\n{ticker}  as of {as_of}")

        if len(bars) < WARMUP:
            print(f"  only {len(bars)} sessions, short of the {WARMUP}-session warm-up")
            continue

        print(f"  window {bars[0]['date']} to {bars[-1]['date']}, {len(bars)} sessions")

        derived = derive(bars)
        engine = stored(connection, ticker, as_of)

        if engine is None:
            print("  no indicator row in the store, so there is nothing to disagree with")
            for name, value in derived.items():
                print(f"    {name:<24} derived {value.quantize(PLACES)}")
            continue

        for name, value in derived.items():
            mine = value.quantize(PLACES)
            theirs = engine[name].quantize(PLACES)
            mark = "ok " if mine == theirs else "NO "
            if mine != theirs:
                disagreements += 1
            print(f"  {mark}{name:<24} derived {mine:>22}   stored {theirs:>22}")

    print(f"\n{disagreements} disagreement(s) at 4 decimal places.")
    return 1 if disagreements else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
