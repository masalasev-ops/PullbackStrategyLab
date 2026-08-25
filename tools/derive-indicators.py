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

Usage:  python tools/derive-indicators.py <store.db> <as-of> <ticker> [<ticker> ...]
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
    row = connection.execute(
        """
        SELECT ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20
          FROM indicator_daily WHERE ticker = ? AND as_of = ?
        """,
        (ticker, as_of),
    ).fetchone()

    if row is None:
        return None

    names = ["ema_9", "ema_21", "ema_50", "atr_14", "adr_20", "dollar_volume_median_20", "range_avg_20"]
    return dict(zip(names, (Decimal(v) for v in row)))


def main(argv):
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
