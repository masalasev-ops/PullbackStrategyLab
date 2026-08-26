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

The index mode, --index, derives what IndexIngestor should have stored for each tracker, and
it reads the captured vendor responses rather than the store. That is the point: everything
between the response and the row is what the stage does, so a derivation starting from the
store would start on the far side of the thing under test. It applies the ingestor's window
from its own statement of it, `from = as-of minus three years, to = as-of`, inclusive at both
ends, and reports the count, the first and last session, and the last raw and adjusted close.

  What it can catch: a symbol read into the wrong row, a window off by a session, a close taken
  from the adjusted column or the reverse, a de-duplicating insert dropping a bar.
  What it cannot: the three-year bound, because the capture holds one year and the vendor's own
  range is the narrower of the two. That bound is exercised on a live night and by nothing here.

Usage:  python tools/derive-indicators.py <store.db> <as-of> <ticker> [<ticker> ...]
        python tools/derive-indicators.py --chart <store.db> <as-of> <ticker> <sessions> <width> <height>
        python tools/derive-indicators.py --index <captured-dir> <as-of> <symbol> [<symbol> ...]
        python tools/derive-indicators.py --universe <captured-dir> <as-of>
        python tools/derive-indicators.py --signals <store.db> <as-of> <ticker> <trigger>
"""

import datetime
import json
import os
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


BACKFILL_YEARS = 3


def index_window(captured, symbol, as_of):
    """The bars the ingestor would have kept for one tracker, from the captured response.

    The window is written out here from the ingestor's own statement of it rather than read
    from anywhere, for the same reason the formulas above are: a derivation that borrows the
    number it is checking is checking nothing.
    """
    end = datetime.date.fromisoformat(as_of)
    start = end.replace(year=end.year - BACKFILL_YEARS)

    with open(os.path.join(captured, "history-%s.json" % symbol), encoding="utf-8") as handle:
        published = json.load(handle)

    kept = [
        bar for bar in published
        if start <= datetime.date.fromisoformat(bar["date"]) <= end
    ]
    kept.sort(key=lambda bar: bar["date"])
    return published, kept


def derive_index(kept):
    """The six figures per symbol, chosen for what a wrong one would mean.

    The raw and adjusted close are taken at the first session and not the last. At the last
    session of a captured window the two are equal for all three trackers, because no
    distribution has gone ex since, so a pair read out of the wrong column there would agree
    with itself and say nothing. At the first session they differ by a year of distributions,
    which is where a swapped column shows.
    """
    first, last = kept[0], kept[-1]
    return {
        "bars": len(kept),
        "firstSession": first["date"],
        "lastSession": last["date"],
        "firstClose": Decimal(str(first["close"])).quantize(PLACES),
        "firstAdjustedClose": Decimal(str(first["adjusted_close"])).quantize(PLACES),
        "lastClose": Decimal(str(last["close"])).quantize(PLACES),
    }


def index_main(argv):
    if len(argv) < 3:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    captured, as_of, symbols = argv[0], argv[1], argv[2:]

    for symbol in symbols:
        published, kept = index_window(captured, symbol, as_of)
        print("\n%s  as of %s, %d bar(s) published, %d inside the window"
              % (symbol, as_of, len(published), len(kept)))

        if not kept:
            print("  nothing in the window, so there is nothing to expect")
            continue

        for name, value in derive_index(kept).items():
            print("  index.%s.%-18s %s" % (symbol, name, value))

    return 0


PRICE_FLOOR = Decimal("5")
LIQUIDITY_FLOOR = Decimal("20000000")
SECURITY_TYPE = "Common Stock"


def universe_screen(captured):
    """The screen, restated from the rules rather than read from the code.

    Three rules, each written out here from its own statement: the security type is common
    stock and nothing else; the price floor is five dollars on the session's close; the
    liquidity floor is twenty million dollars of median daily turnover over twenty sessions.

    The fixture holds one market day, so the median over twenty sessions is the median of one
    number, which is that number. That is not the floor the live screen applies and the
    difference is the fixture's own, recorded against it rather than papered over: the point
    here is a second implementation of the filter, not a second fixture.
    """
    with open(os.path.join(captured, "exchange-symbol-list.json"), encoding="utf-8") as handle:
        listed = json.load(handle)

    with open(os.path.join(captured, "bulk-end-of-day.json"), encoding="utf-8") as handle:
        published = json.load(handle)

    common = {row["Code"] for row in listed if row.get("Type") == SECURITY_TYPE}
    priced = [row for row in published if row["code"] in common]

    above_price = [row for row in priced if Decimal(str(row["close"])) >= PRICE_FLOOR]
    survivors = [
        row for row in above_price
        if Decimal(str(row["close"])) * Decimal(str(row["volume"])) >= LIQUIDITY_FLOOR
    ]

    return {
        "published": published,
        "listedCommonStock": len(common),
        "screened": len(priced),
        "sessionsScreened": len({row["date"] for row in published}),
        "admittedWithoutTheLiquidityFloor": len(above_price),
        "survivors": len(survivors),
        "rejectedByTheLiquidityFloor": len(above_price) - len(survivors),
        "admitted": {row["code"] for row in above_price},
    }


def universe_actions(captured, admitted):
    """Splits and dividends published for the market, and how many land in the universe.

    Every action that moves the adjusted close raises a rebuild demand and blocks its ticker,
    which is why the three counts below are equal by construction rather than by coincidence:
    a dividend does it as surely as a split does, and magnitude does not enter it.
    """
    counts = {}
    in_universe = set()
    all_acted = set()

    for kind, filename in (("splits", "bulk-splits.json"), ("dividends", "bulk-dividends.json")):
        with open(os.path.join(captured, filename), encoding="utf-8") as handle:
            rows = json.load(handle)
        counts[kind] = len(rows)
        all_acted.update(row["code"] for row in rows)
        in_universe.update(row["code"] for row in rows if row["code"] in admitted)

    return {
        "splitsPublished": counts["splits"],
        "dividendsPublished": counts["dividends"],
        "inUniverse": len(in_universe),
        "blockedTickers": sorted(in_universe),
        "acted": sorted(all_acted),
    }


def universe_fixture(captured, screen, acted, as_of):
    """What the fixture is made of, derived from the manifest rather than from the replay.

    The seeded histories are not all candidates. Three of them are the index trackers, and a
    tracker is an ETF: it fails the security-type filter, it is never part of the tradable
    universe and it never appears on a screen. So the population for "in the universe" is the
    seeded histories that are common stock, and partitioning them by the same type rule the
    screen uses is what makes that a derivation rather than a subtraction of a number somebody
    already knew.
    """
    with open(os.path.join(captured, "manifest.json"), encoding="utf-8") as handle:
        manifest = json.load(handle)

    with open(os.path.join(captured, "exchange-symbol-list.json"), encoding="utf-8") as handle:
        types = {row["Code"]: row.get("Type") for row in json.load(handle)}

    seeded = sorted(
        entry["endpoint"].split("/")[1].split(".")[0]
        for entry in manifest["responses"]
        if entry["endpoint"].startswith("eod/")
    )

    candidates = [ticker for ticker in seeded if types.get(ticker) == SECURITY_TYPE]
    trackers = [ticker for ticker in seeded if types.get(ticker) != SECURITY_TYPE]

    inside = [ticker for ticker in candidates if ticker in screen["admitted"]]
    outside = [ticker for ticker in candidates if ticker not in screen["admitted"]]

    return {
        "seededHistories": len(seeded),
        "trackersExcludedByType": ", ".join(trackers) if trackers else "none",
        "tickersInUniverse": len(inside),
        "tickersOutsideUniverse": ", ".join(outside) if outside else "none",
        "asOf": as_of,
        "barsPublished": len(screen["published"]),
        "barsInUniverse": sum(1 for row in screen["published"] if row["code"] in screen["admitted"]),
        # Every action that moves the adjusted close raises a demand, so the ticker that was
        # acted on and the ticker whose rebuild is stamped are the same ticker by the rule.
        "actionsObserved": ", ".join(sorted(set(acted) & set(candidates))) or "none",
    }


def universe_main(argv):
    if len(argv) < 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    captured, as_of = argv[0], argv[1]

    screen = universe_screen(captured)
    actions = universe_actions(captured, screen["admitted"])
    fixture = universe_fixture(captured, screen, actions["acted"], as_of)

    print("\nuniverse  as of %s, from the captured responses and nothing else" % as_of)
    for name in ("listedCommonStock", "screened", "sessionsScreened", "survivors",
                 "admittedWithoutTheLiquidityFloor", "rejectedByTheLiquidityFloor"):
        print("  universe.%-34s %s" % (name, screen[name]))

    print("\nbars")
    print("  bars.%-38s %s" % ("published", fixture["barsPublished"]))
    print("  bars.%-38s %s" % ("inUniverse", fixture["barsInUniverse"]))

    print("\nactions")
    for name in ("splitsPublished", "dividendsPublished", "inUniverse"):
        print("  actions.%-35s %s" % (name, actions[name]))
    print("  actions.%-35s %s" % ("blockedTickers", len(actions["blockedTickers"])))

    print("\nfixture")
    for name in ("seededHistories", "trackersExcludedByType", "tickersInUniverse",
                 "tickersOutsideUniverse", "actionsObserved"):
        print("  fixture.%-35s %s" % (name, fixture[name]))

    return 0


GAP_WINDOW = 20


def derive_signals(bars, trigger):
    """The signals SignalVectorizer freezes, from the bars alone.

    Restated from the library in SCHEMA rather than from the stage, which is the point: the stage
    reads a stored indicator row and this reads the window, so the two agree only if the engine,
    the reader and the vectorizer all agree. A distance signal is the one shape where a sign error
    is invisible, because a stock below its average and a stock above it both produce a plausible
    small number, so all three distances are derived rather than one.

    The gap average is the mean of (ema21-ema50)/ema50 over the last twenty sessions, each average
    recomputed over the engine's own warm-up ending at that session. Carrying one running average
    across the whole window instead would seed it in a different place and differ for a long time
    on the way to the same answer, which is the trap the chart page already fell into once.
    """
    # Every figure but the gap average is computed over the engine's own warm-up, and the wider
    # window is used only where the gap average needs it. Seeding a recursive average further back
    # gives a different number that still looks like an average: derived over 169 sessions instead
    # of 150, atr_14 came out 24.1363 against the engine's 24.1364. The chart page learned this at
    # 1.10 and it is the same lesson, so the window is chosen per figure rather than once.
    warm = bars[-WARMUP:]
    adj = adjusted(warm)
    closes = [b["close"] for b in adjusted(bars)]
    last = adj[-1]
    figures = derive(warm)

    close = last["close"]
    out = {
        "close_adjusted": close,
        "adr_20": figures["adr_20"],
        "atr_14": figures["atr_14"],
        "range_avg_20": figures["range_avg_20"],
        "dollar_volume_median_20": figures["dollar_volume_median_20"],
    }

    for period, name in ((9, "ema_9_distance"), (21, "ema_21_distance"), (50, "ema_50_distance")):
        average = ema(closes[-WARMUP:], period)
        out[name] = (close - average) / average

    medium = ema(closes[-WARMUP:], 21)
    longer = ema(closes[-WARMUP:], 50)
    out["ema_gap_21_50"] = (medium - longer) / longer

    gaps = []
    for end in range(len(closes) - GAP_WINDOW, len(closes)):
        window_closes = closes[end - WARMUP + 1:end + 1]
        if len(window_closes) < WARMUP:
            continue
        m = ema(window_closes, 21)
        l = ema(window_closes, 50)
        gaps.append((m - l) / l)

    if len(gaps) == GAP_WINDOW:
        out["ema_gap_21_50_avg_20"] = sum(gaps) / GAP_WINDOW

    out["range_today_over_avg"] = (last["high"] - last["low"]) / figures["range_avg_20"]

    raw_close = warm[-1]["close"]
    daily_range = figures["adr_20"] * raw_close
    out["trigger_distance_ranges"] = abs(Decimal(trigger) - raw_close) / daily_range

    return out


def signals_main(argv):
    if len(argv) < 4:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of, ticker, trigger = argv[0], argv[1], argv[2], argv[3]
    connection = sqlite3.connect(store)

    # The warm-up plus the gap window, stated from what the gap average needs rather than as a
    # constant: twenty averages, each computed over the engine's own warm-up ending at its own
    # session, need nineteen sessions of history behind the first of them.
    bars = window(connection, ticker, as_of, WARMUP + GAP_WINDOW - 1)

    if len(bars) < WARMUP:
        print("only %d sessions, short of the %d-session warm-up" % (len(bars), WARMUP), file=sys.stderr)
        return 1

    print("\n%s  as of %s, %d sessions, trigger %s" % (ticker, as_of, len(bars), trigger))
    for name, value in derive_signals(bars, trigger).items():
        print("  signal.%s-long.%-26s %s" % (ticker, name, value.quantize(PLACES)))

    return 0


def main(argv):
    if len(argv) > 1 and argv[1] == "--signals":
        return signals_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--chart":
        return chart_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--index":
        return index_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--universe":
        return universe_main(argv[2:])

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
