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
        python tools/derive-indicators.py --scans   <store.db> <as-of> [<ranks>]
        python tools/derive-indicators.py --ladder  <store.db> <as-of>
        python tools/derive-indicators.py --regime  <store.db> <as-of> [<symbol> ...]
        python tools/derive-indicators.py --checks  <store.db> <as-of> <ticker> [--short]
        python tools/derive-indicators.py --gates   <gate-cases.json>
        python tools/derive-indicators.py --cap     <cap-cases.json>
        python tools/derive-indicators.py --point-in-time <store.db> <as-of> <ticker>
"""

import datetime
import json
import math
import os
import sqlite3
import sys
from decimal import Decimal, ROUND_HALF_UP, getcontext

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
        SELECT bar_date, high, low, close, adj_close, volume, open
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
            "open": Decimal(r[6]),
        }
        for r in rows
    ]


def adjusted(bars):
    """Open, high, low and close on one basis, through each bar's own adj_close/close factor."""
    out = []
    for b in bars:
        factor = Decimal(1) if b["close"] == 0 else b["adj_close"] / b["close"]
        out.append(
            {
                "open": b["open"] * factor,
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
SCAN_BREADTH = 50
MONTH_WINDOW = 20

# Which magnitude each scan ranks on, and which way. Restated from the library rather than read
# from the stage: the whole point is that a sign flipped in one place shows up as a disagreement
# rather than as a plausible ranked list.
SCANS = {
    "gainer":   ("daily", True),
    "gapper":   ("gap",   True),
    "leader":   ("month", True),
    "decliner": ("daily", False),
    "gapdown":  ("gap",   False),
    "laggard":  ("month", False),
}


def scan_magnitudes(connection, ticker, as_of):
    """The three magnitudes for one name, on the adjusted basis.

    The adjusted basis is the whole of why this is worth deriving. Read raw, a two-for-one split
    is a fifty percent decline and tops the decliner scan every time one happens, which is a
    plausible ranked list rather than an error. The fixture has exactly that case: IESC split on
    the captured session.

    The open has no stored adjusted counterpart, so it goes onto the adjusted basis through its own
    bar's adj_close/close factor. Using the previous bar's factor instead would be wrong on exactly
    the session a distribution goes ex, which is the session the gap scan exists to notice.
    """
    # Its own query rather than the shared window above, because the gap magnitude needs the open
    # and nothing else here does. Written out for the same reason the window is: a derivation that
    # borrows the selection it is checking is checking less than it looks.
    bound = as_of + "T23:59:59.999Z"
    rows = connection.execute(
        """
        SELECT bar_date, open, close, adj_close
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
        (ticker, as_of, bound, bound, MONTH_WINDOW + 2),
    ).fetchall()

    rows.reverse()
    bars = [
        {"date": r[0], "open": Decimal(r[1]), "close": Decimal(r[2]), "adj_close": Decimal(r[3])}
        for r in rows
    ]

    if len(bars) < MONTH_WINDOW + 2 or bars[-1]["date"] != as_of:
        return None

    today, yesterday, month_ago = bars[-1], bars[-2], bars[-(MONTH_WINDOW + 1)]

    def ratio(a, b):
        return Decimal(0) if a == 0 else (b - a) / a

    factor = Decimal(1) if today["close"] == 0 else today["adj_close"] / today["close"]

    return {
        "daily": ratio(yesterday["adj_close"], today["adj_close"]),
        "gap": ratio(yesterday["adj_close"], today["open"] * factor),
        "month": ratio(month_ago["adj_close"], today["adj_close"]),
    }


def scans_main(argv):
    if len(argv) < 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of, ranks = argv[0], argv[1], int(argv[2]) if len(argv) > 2 else 3
    connection = sqlite3.connect(store)

    members = [
        row[0] for row in connection.execute(
            "SELECT ticker FROM universe_snapshot WHERE as_of = ? ORDER BY ticker", (as_of,)).fetchall()
    ]

    measured = {}
    for ticker in members:
        magnitudes = scan_magnitudes(connection, ticker, as_of)
        if magnitudes is not None:
            measured[ticker] = magnitudes

    print("\nscans  as of %s, %d member(s), %d measured" % (as_of, len(members), len(measured)))

    for scan in ("gainer", "gapper", "leader", "decliner", "gapdown", "laggard"):
        key, descending = SCANS[scan]
        # Ticker as the tiebreak, so the boundary of the top N does not depend on the order the
        # store happened to return rows in.
        ordered = sorted(measured.items(), key=lambda kv: (-kv[1][key] if descending else kv[1][key], kv[0]))
        top = ordered[:SCAN_BREADTH]

        print("  scan.%s.hits %d" % (scan, len(top)))
        for rank in range(1, ranks + 1):
            if rank > len(top):
                print("  scan.%s.rank%d no hit" % (scan, rank))
                continue
            ticker, magnitudes = top[rank - 1]
            print("  scan.%s.rank%-2d %-6s %s" % (scan, rank, ticker, magnitudes[key].quantize(PLACES)))

    return 0


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


def ladder_main(argv):
    """The ladder grade for every universe member, from the close and the three averages.

    Restated from the definition rather than read from the stage: rising is price above the 9-day,
    9 above 21, 21 above 50; falling is every one of those reversed; mixed is anything else. The
    comparisons are strict, so two averages exactly equal grades mixed, which is a real state on a
    flat series and is neither a rise nor a fall.

    The averages are recomputed over the engine's own warm-up rather than read from the stored row.
    Reading the row would check the grading and skip the thing most likely to be wrong, which is
    whether the grade was taken against the right numbers.
    """
    if len(argv) < 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of = argv[0], argv[1]
    connection = sqlite3.connect(store)

    members = [
        row[0] for row in connection.execute(
            "SELECT ticker FROM universe_snapshot WHERE as_of = ? ORDER BY ticker", (as_of,)).fetchall()
    ]

    counts = {"rising": 0, "mixed": 0, "falling": 0}
    graded = []

    for ticker in members:
        bars = window(connection, ticker, as_of, WARMUP)
        if len(bars) < WARMUP or bars[-1]["date"] != as_of:
            continue

        closes = [b["close"] for b in adjusted(bars)]
        close = closes[-1]
        short, medium, long_ = (ema(closes, p) for p in EMA_PERIODS)

        if close > short > medium > long_:
            grade = "rising"
        elif close < short < medium < long_:
            grade = "falling"
        else:
            grade = "mixed"

        counts[grade] += 1
        graded.append((ticker, grade))

    print("\nladder  as of %s, %d member(s), %d graded" % (as_of, len(members), len(graded)))
    print("  rising %d, mixed %d, falling %d" % (counts["rising"], counts["mixed"], counts["falling"]))
    for ticker, grade in graded:
        print("  ladder.%-8s %s" % (ticker, grade))

    return 0


INDEX_PERIOD = 21
BREADTH_UPPER = Decimal("1.5")
BREADTH_LOWER = Decimal("0.67")


def regime_main(argv):
    """The market mood, from the two scores, restated from their definitions.

    Index trend: how many of the trackers closed above their own 21-day average, +1 for all of
    them, -1 for none, 0 otherwise. Breadth: the ratio of rising names to falling ones, +1 above
    1.5, -1 below 0.67, 0 between. The label is risk_on at +2, risk_off at -2, mixed otherwise, so
    the three states buffer themselves: neither extreme is reachable without both scores agreeing.

    The tracker averages are computed over the engine's warm-up, not over 21 sessions. An average
    seeded 21 sessions back is not the one seeded 150 sessions back and both look like an average,
    which the chart page found at 1.10 and the signals derivation found again at 2.2.
    """
    if len(argv) < 2:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of, symbols = argv[0], argv[1], argv[2:] or ["SPY", "QQQ", "IWM"]
    connection = sqlite3.connect(store)

    above = 0
    measured = 0

    for symbol in symbols:
        bound = as_of + "T23:59:59.999Z"
        rows = connection.execute(
            """
            SELECT bar_date, close, adj_close FROM index_bar b
             WHERE b.symbol = ? AND b.bar_date <= ? AND b.observed_at <= ?
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM index_bar l
                                     WHERE l.symbol = b.symbol AND l.bar_date = b.bar_date
                                       AND l.observed_at <= ?)
             ORDER BY b.bar_date DESC LIMIT ?
            """,
            (symbol, as_of, bound, bound, WARMUP),
        ).fetchall()
        rows.reverse()

        if len(rows) < WARMUP or rows[-1][0] != as_of:
            print("  %s short of the %d-session window, not measured" % (symbol, WARMUP))
            continue

        measured += 1
        closes = [Decimal(r[2]) for r in rows]
        average = ema(closes, INDEX_PERIOD)
        if closes[-1] > average:
            above += 1
        print("  %s close %s against its %d-day average %s" % (symbol, closes[-1], INDEX_PERIOD, average.quantize(PLACES)))

    grades = connection.execute(
        """
        SELECT d.ladder_grade, COUNT(*) FROM indicator_daily d
         WHERE d.as_of = ? AND d.ladder_grade IS NOT NULL
           AND d.computed_at = (SELECT MAX(l.computed_at) FROM indicator_daily l
                                 WHERE l.ticker = d.ticker AND l.as_of = d.as_of)
         GROUP BY d.ladder_grade
        """,
        (as_of,),
    ).fetchall()
    counts = dict(grades)
    rising = counts.get("rising", 0)
    falling = counts.get("falling", 0)

    index_score = 0 if measured == 0 else (1 if above == measured else (-1 if above == 0 else 0))

    if falling == 0:
        breadth_score = 0 if rising == 0 else 1
    else:
        ratio = Decimal(rising) / Decimal(falling)
        breadth_score = 1 if ratio > BREADTH_UPPER else (-1 if ratio < BREADTH_LOWER else 0)

    total = index_score + breadth_score
    label = "risk_on" if total == 2 else ("risk_off" if total == -2 else "mixed")

    print("\nregime  as of %s" % as_of)
    print("  regime.indexesMeasured   %d" % measured)
    print("  regime.indexesAbove      %d" % above)
    print("  regime.longLadderCount   %d" % rising)
    print("  regime.shortLadderCount  %d" % falling)
    print("  regime.indexScore        %d" % index_score)
    print("  regime.breadthScore      %d" % breadth_score)
    print("  regime.label             %s" % label)
    return 0


LIQUIDITY_FLOOR = Decimal("20000000")
PRICE_FLOOR = Decimal("5")
RANGE_FLOOR = Decimal("0.05")
THRUST_WINDOW = 10
MIN_PULLBACK, MAX_PULLBACK = 2, 7
MAX_RETRACE = Decimal("0.40")
TRIGGER_REACH = Decimal("1.5")
GIVE_UP = Decimal("0.5")
CLUSTER_THRESHOLD = 2


def checks_main(argv):
    """The ten long checks, restated from the gate list rather than read from the detector.

    Each gate as ARCHITECTURE.html words it:

      tradable      $20M median daily turnover and a price above $5.
      moves-enough  typical daily range of 5% or more.
      uptrend       price above the 9-day, which is above the 21-day, above the 50-day.
      thrust        appeared on an upward mover scan within the last ten sessions.
      dip-shape     two to seven sessions of drift, giving back no more than 40% of the jump.
      held-floor    no daily close below the 21-day average during the dip.
      contraction   the latest day's range is narrower than its 20-session average.
      trigger-near  the trigger sits within 1.5 daily ranges of the current price.
      exit-tight    entry to give-up distance no more than half the daily range.
      cluster       two or more same-industry names on the same scan the same night.

    A check whose input is absent fails and says so. That is not the same as a threshold that was
    not cleared, and the distinction is why the detector records a note beside the verdict: with no
    pullback at all there is no trigger and no give-up point, so trigger-near and exit-tight have
    nothing to measure. Passing them on a distance of zero would be the tightest possible stop on a
    trade that does not exist.
    """
    if len(argv) < 3:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2

    store, as_of, ticker = argv[0], argv[1], argv[2]
    connection = sqlite3.connect(store)

    # The short list is its own restatement rather than the long one with the signs flipped,
    # because three of its ten gates are not sign flips and one shared shape reads a different
    # average. Flipping would agree with the detector by construction on exactly the checks the
    # detector is most likely to have got wrong.
    if "--short" in argv:
        verdicts = short_checks(connection, as_of, ticker)
        if verdicts is None:
            print("%s: short of the warm-up" % ticker, file=sys.stderr)
            return 1

        print("\n%s  as of %s  short" % (ticker, as_of))
        for name in SHORT_ORDER:
            print("  setup.%s-%s-short.%-18s %s"
                  % (as_of, ticker, name, "pass" if verdicts[name] else "fail"))
        return 0

    bars = window(connection, ticker, as_of, WARMUP)
    if len(bars) < WARMUP or bars[-1]["date"] != as_of:
        print("%s: %d sessions, short of the warm-up" % (ticker, len(bars)), file=sys.stderr)
        return 1

    adj = adjusted(bars)
    figures = derive(bars)
    close = adj[-1]["close"]

    hit = connection.execute(
        """
        SELECT as_of, scan, cluster_count FROM scan_hit
         WHERE ticker = ? AND as_of <= ? AND scan IN ('gainer','gapper','leader')
         ORDER BY as_of DESC, rank LIMIT 1
        """, (ticker, as_of)).fetchone()

    verdicts = {}
    verdicts["tradable"] = figures["dollar_volume_median_20"] >= LIQUIDITY_FLOOR and close > PRICE_FLOOR
    verdicts["moves-enough"] = figures["adr_20"] >= RANGE_FLOOR

    closes = [b["close"] for b in adj]
    short, medium, long_ = (ema(closes, p) for p in EMA_PERIODS)
    verdicts["uptrend"] = close > short > medium > long_

    if hit is None:
        sessions_since = None
        verdicts["thrust"] = False
    else:
        sessions_since = sum(1 for b in bars if b["date"] > hit[0] and b["date"] <= as_of)
        verdicts["thrust"] = sessions_since <= THRUST_WINDOW

    if hit is None:
        for name in ("dip-shape", "held-floor", "contraction", "trigger-near", "exit-tight"):
            verdicts[name] = False
    else:
        thrust_index = next(i for i, b in enumerate(bars) if b["date"] == hit[0])
        # The span the scan flags: one session for the day scans, twenty for the month ones. A
        # `leader` hit flags a move that began nineteen sessions before the session it is flagged
        # on, so measuring it from the flag puts one session of a twenty-session run in the
        # denominator and finds the extreme at the flag whenever the real high sits before it.
        span = MONTH_WINDOW if hit[1] in ("leader", "laggard") else 1
        thrust_start = max(0, thrust_index - span + 1)
        extreme_index = max(range(thrust_start, len(adj)), key=lambda i: adj[i]["high"])
        extreme = adj[extreme_index]["high"]
        # The close before the thrust. With the thrust on the first bar of the window there is
        # none, and the thrust's own open is the nearest thing to where the move began. Its close
        # is not: the close sits inside the move being measured, so using it reports a shorter
        # thrust than happened. This read the close until the geometry cases at 3.0 reached the
        # branch, where the two implementations disagreed by 0.0098 on a case nothing had run.
        origin = adj[thrust_start - 1]["close"] if thrust_start > 0 else adj[thrust_start]["open"]
        pullback_bars = len(adj) - 1 - extreme_index

        if pullback_bars == 0:
            low = extreme
        else:
            low = min(adj[i]["low"] for i in range(extreme_index + 1, len(adj)))

        move = extreme - origin
        retrace = None if move == 0 else (extreme - low) / move

        verdicts["dip-shape"] = (
            MIN_PULLBACK <= pullback_bars <= MAX_PULLBACK
            and retrace is not None and 0 <= retrace <= MAX_RETRACE)

        beyond = sum(1 for i in range(extreme_index + 1, len(adj)) if adj[i]["close"] < medium)
        verdicts["held-floor"] = beyond == 0

        today_range = adj[-1]["high"] - adj[-1]["low"]
        verdicts["contraction"] = today_range < figures["range_avg_20"]

        if pullback_bars == 0:
            verdicts["trigger-near"] = False
            verdicts["exit-tight"] = False
        else:
            trigger = max(bars[i]["high"] for i in range(extreme_index + 1, len(bars)))
            stop = min(bars[i]["low"] for i in range(extreme_index + 1, len(bars)))
            daily_range = figures["adr_20"] * bars[-1]["close"]
            verdicts["trigger-near"] = abs(trigger - bars[-1]["close"]) / daily_range <= TRIGGER_REACH
            verdicts["exit-tight"] = abs(trigger - stop) / daily_range <= GIVE_UP

    cluster = None if hit is None else hit[2]
    verdicts["cluster"] = (cluster or 0) >= CLUSTER_THRESHOLD

    print("\n%s  as of %s" % (ticker, as_of))
    for name in ("tradable", "moves-enough", "uptrend", "thrust", "dip-shape",
                 "held-floor", "contraction", "trigger-near", "exit-tight", "cluster"):
        print("  setup.%s.%-14s %s" % (ticker, name, "pass" if verdicts[name] else "fail"))

    return 0


# --- the short gates, and the authored boundary cases ------------------------------------

SHORT_LIQUIDITY_FLOOR = Decimal("50000000")
MARKET_CAP_FLOOR = Decimal("2000000000")
LISTING_AGE_FLOOR = 90
SQUEEZE_WINDOW = 20
CEILING_REACH = Decimal("0.5")


def ema_series(values, period, warmup):
    """The average at every session the warm-up can support, absent before it.

    Seeded once and marched forward, which is what the engine does. Reseeding on each prefix
    would give a different number for every session but the last, and the difference decays
    slowly enough to be invisible.
    """
    out = [None] * len(values)
    if len(values) < warmup:
        return out

    seed = sum(values[:period]) / period
    multiplier = Decimal(2) / (period + 1)
    value = seed
    for i in range(period, len(values)):
        value = value + (values[i] - value) * multiplier
        if i >= warmup - 1:
            out[i] = value
    return out


def gap_series(closes, warmup):
    """The 21-to-50 distance as a signed fraction of the 50-day, at every session it exists."""
    medium = ema_series(closes, 21, warmup)
    longer = ema_series(closes, 50, warmup)
    return [
        (m - l) / l
        for m, l in zip(medium, longer)
        if m is not None and l is not None and l != 0
    ]


def squeeze_ratio(closes, warmup):
    """Today's distance over its own mean distance across the window. Absolute, not signed.

    In a downtrend the 21-day sits below the 50-day, so a signed comparison would read "narrower"
    as "further below" and invert the rule on the one side this check runs.
    """
    gaps = gap_series(closes, warmup)
    if len(gaps) < SQUEEZE_WINDOW:
        return None
    tail = gaps[-SQUEEZE_WINDOW:]
    average = sum(abs(g) for g in tail) / SQUEEZE_WINDOW
    if average == 0:
        return None
    return abs(gaps[-1]) / average


SHORT_ORDER = ("tradable-shortable", "moves-enough", "downtrend", "averages-squeezing", "thrust",
               "bounce-shape", "reached-ceiling", "no-reclaim", "exit-tight", "cluster")


def short_checks(connection, as_of, ticker):
    """The ten short checks, restated from the gate list rather than read from the detector.

      tradable-shortable  price above $5, cap above $2B, $50M median turnover, 90 sessions listed.
      moves-enough        typical daily range of 5% or more. Identical to the long side.
      downtrend           price below the 9-day, which is below the 21-day, below the 50-day.
      averages-squeezing  the 21-to-50 gap narrower than its own average over 20 sessions.
      thrust              appeared on a downward mover scan within the last ten sessions.
      bounce-shape        two to seven sessions of rising, recovering no more than 40% of the drop.
      reached-ceiling     within half a daily range of the 21-day or the 50-day average. The third
                          clause, the average price anchored to the last swing high, needs minute
                          bars and arrives at 4.4; it is not approximated here either.
      no-reclaim          no daily close above the 50-day average during the bounce.
      exit-tight          entry to give-up distance no more than half the daily range.
      cluster             two or more same-industry names on the same scan the same night.
    """
    bars = window(connection, ticker, as_of, WARMUP + RANGE_WINDOW)
    if len(bars) < WARMUP or bars[-1]["date"] != as_of:
        return None

    adj = adjusted(bars)
    figures = derive(bars[-WARMUP:])
    closes = [b["close"] for b in adj]
    close = closes[-1]
    bound = as_of + "T23:59:59.999Z"

    cap = connection.execute(
        "SELECT market_cap FROM security"
        " WHERE ticker = ? AND market_cap IS NOT NULL AND sector_resolved_at IS NOT NULL"
        "   AND sector_resolved_at <= ?",
        (ticker, bound)).fetchone()

    listed = connection.execute(
        "SELECT COUNT(DISTINCT bar_date) FROM daily_bar"
        " WHERE ticker = ? AND bar_date <= ? AND observed_at <= ?",
        (ticker, as_of, bound)).fetchone()[0]

    hit = connection.execute(
        "SELECT as_of, scan, cluster_count FROM scan_hit"
        " WHERE ticker = ? AND as_of <= ? AND scan IN ('decliner','gapdown','laggard')"
        " ORDER BY as_of DESC, rank LIMIT 1",
        (ticker, as_of)).fetchone()

    verdicts = {}
    verdicts["tradable-shortable"] = (
        cap is not None
        and figures["dollar_volume_median_20"] >= SHORT_LIQUIDITY_FLOOR
        and close > PRICE_FLOOR
        and Decimal(cap[0]) > MARKET_CAP_FLOOR
        and listed >= LISTING_AGE_FLOOR)

    verdicts["moves-enough"] = figures["adr_20"] >= RANGE_FLOOR

    short_, medium, long_ = (ema(closes[-WARMUP:], p) for p in EMA_PERIODS)
    verdicts["downtrend"] = close < short_ < medium < long_

    ratio = squeeze_ratio(closes, WARMUP)
    verdicts["averages-squeezing"] = ratio is not None and ratio < 1

    if hit is None:
        verdicts["thrust"] = False
    else:
        sessions_since = sum(1 for b in bars if b["date"] > hit[0] and b["date"] <= as_of)
        verdicts["thrust"] = sessions_since <= THRUST_WINDOW

    daily_range = figures["adr_20"] * bars[-1]["close"]
    nearest = min(abs(close - medium), abs(close - long_))
    verdicts["reached-ceiling"] = daily_range != 0 and nearest / daily_range <= CEILING_REACH

    if hit is None:
        for name in ("bounce-shape", "no-reclaim", "exit-tight"):
            verdicts[name] = False
    else:
        thrust_index = next(i for i, b in enumerate(bars) if b["date"] == hit[0])
        # The span the scan flags. `laggard` is the short side's twenty-session scan.
        span = MONTH_WINDOW if hit[1] in ("leader", "laggard") else 1
        thrust_start = max(0, thrust_index - span + 1)
        extreme_index = min(range(thrust_start, len(adj)), key=lambda i: adj[i]["low"])
        extreme = adj[extreme_index]["low"]
        # The close before the thrust. With the thrust on the first bar of the window there is
        # none, and the thrust's own open is the nearest thing to where the move began. Its close
        # is not: the close sits inside the move being measured, so using it reports a shorter
        # thrust than happened. This read the close until the geometry cases at 3.0 reached the
        # branch, where the two implementations disagreed by 0.0098 on a case nothing had run.
        origin = adj[thrust_start - 1]["close"] if thrust_start > 0 else adj[thrust_start]["open"]
        bounce_bars = len(adj) - 1 - extreme_index

        high = extreme if bounce_bars == 0 else max(
            adj[i]["high"] for i in range(extreme_index + 1, len(adj)))
        drop = origin - extreme
        recovery = None if drop == 0 else (high - extreme) / drop

        verdicts["bounce-shape"] = (
            MIN_PULLBACK <= bounce_bars <= MAX_PULLBACK
            and recovery is not None and 0 <= recovery <= MAX_RETRACE)

        verdicts["no-reclaim"] = sum(
            1 for i in range(extreme_index + 1, len(adj)) if adj[i]["close"] > long_) == 0

        if bounce_bars == 0:
            # No bounce means no entry and no give-up point. A distance of zero would clear the
            # threshold, which is a tight stop on a trade that does not exist.
            verdicts["exit-tight"] = False
        else:
            trigger = min(bars[i]["low"] for i in range(extreme_index + 1, len(bars)))
            stop = max(bars[i]["high"] for i in range(extreme_index + 1, len(bars)))
            verdicts["exit-tight"] = abs(trigger - stop) / daily_range <= GIVE_UP

    cluster = None if hit is None else hit[2]
    verdicts["cluster"] = (cluster or 0) >= CLUSTER_THRESHOLD

    return verdicts


def number(fields, key):
    return None if key not in fields else Decimal(fields[key])


def whole(fields, key):
    return None if key not in fields else int(fields[key])


def gate_verdict(direction, gate, f):
    """One gate over one constructed evidence, restated from the document's wording.

    A gate handed nothing fails. That is a rule rather than a convenience: an absent quantity has
    not cleared a threshold, and the alternative is not an error but a pass, which is how a gate
    ends up reading as easy to clear when it was never tested at all.
    """
    if direction == "long":
        if gate == "tradable":
            v, c = number(f, "medianDollarVolume"), number(f, "close")
            return v is not None and c is not None and v >= LIQUIDITY_FLOOR and c > PRICE_FLOOR
        if gate == "moves-enough":
            a = number(f, "averageDailyRange")
            return a is not None and a >= RANGE_FLOOR
        if gate == "uptrend":
            return f.get("ladderGrade") == "rising"
        if gate == "thrust":
            s = whole(f, "sessionsSinceThrust")
            return s is not None and s <= THRUST_WINDOW
        if gate == "dip-shape":
            b, r = whole(f, "pullback.pullbackBars"), number(f, "pullback.retraceDepth")
            return (b is not None and MIN_PULLBACK <= b <= MAX_PULLBACK
                    and r is not None and 0 <= r <= MAX_RETRACE)
        if gate == "held-floor":
            b = whole(f, "closesBeyondFloor")
            return b is not None and b == 0
        if gate == "contraction":
            r = number(f, "rangeTodayOverAverage")
            return r is not None and r < 1
        if gate == "trigger-near":
            d = number(f, "triggerDistanceRanges")
            return d is not None and d <= TRIGGER_REACH
        if gate == "exit-tight":
            d = number(f, "stopDistanceRanges")
            return d is not None and d <= GIVE_UP
        if gate == "cluster":
            c = whole(f, "clusterCount")
            return (c or 0) >= CLUSTER_THRESHOLD
    else:
        if gate == "tradable-shortable":
            v, c = number(f, "medianDollarVolume"), number(f, "close")
            cap, listed = number(f, "marketCap"), whole(f, "sessionsListed")
            return (v is not None and c is not None and cap is not None and listed is not None
                    and v >= SHORT_LIQUIDITY_FLOOR and c > PRICE_FLOOR
                    and cap > MARKET_CAP_FLOOR and listed >= LISTING_AGE_FLOOR)
        if gate == "moves-enough":
            a = number(f, "averageDailyRange")
            return a is not None and a >= RANGE_FLOOR
        if gate == "downtrend":
            return f.get("ladderGrade") == "falling"
        if gate == "averages-squeezing":
            r = number(f, "gapOverAverageGap")
            return r is not None and r < 1
        if gate == "thrust":
            s = whole(f, "sessionsSinceThrust")
            return s is not None and s <= THRUST_WINDOW
        if gate == "bounce-shape":
            b, r = whole(f, "bounce.pullbackBars"), number(f, "bounce.retraceDepth")
            return (b is not None and MIN_PULLBACK <= b <= MAX_PULLBACK
                    and r is not None and 0 <= r <= MAX_RETRACE)
        if gate == "reached-ceiling":
            d = number(f, "distanceToNearestAverageRanges")
            return d is not None and d <= CEILING_REACH
        if gate == "no-reclaim":
            b = whole(f, "closesBeyondFloor")
            return b is not None and b == 0
        if gate == "exit-tight":
            d = number(f, "stopDistanceRanges")
            return d is not None and d <= GIVE_UP
        if gate == "cluster":
            c = whole(f, "clusterCount")
            return (c or 0) >= CLUSTER_THRESHOLD

    raise SystemExit("no restatement for the %s gate %s" % (direction, gate))


def gates_main(argv):
    """The authored boundary cases, decided by a second reading of the same gate list.

    Reads fixtures/gate-cases.json, applies each case's overrides to its direction's baseline and
    restates every gate from the wording in ARCHITECTURE.html. Nothing here imports the lab, so a
    threshold that moved in one place and not the other shows as a named difference.

    These cases say nothing about the market. What they answer is whether both branches of every
    gate work, which thirty real names on one session cannot: two setups give a gate two results,
    and two results are one-sided unless they happen to disagree.
    """
    if len(argv) < 1:
        print("usage: --gates <gate-cases.json>", file=sys.stderr)
        return 2

    with open(argv[0], encoding="utf-8") as handle:
        book = json.load(handle)

    if book["tier"] != "AUTHORED":
        print("gate-cases.json says tier %s, not AUTHORED" % book["tier"], file=sys.stderr)
        return 1

    print("\ngates  from %s" % os.path.basename(argv[0]))
    differences = 0

    for case in book["cases"]:
        fields = dict(book["baseline"][case["direction"]])
        fields.update(case["set"])

        got = "pass" if gate_verdict(case["direction"], case["gate"], fields) else "fail"
        flag = ""
        if got != case["expect"]:
            differences += 1
            flag = "   <-- differs, the file expects %s" % case["expect"]

        print("  gate.%s.%s.%-8s %s%s" % (case["direction"], case["gate"], case["side"], got, flag))

    print("  %d case(s), %d differing from the side each was built for" % (len(book["cases"]), differences))
    return 0


# --- the nightly cap over the authored candidate lists ------------------------------------

LONG_ALLOCATION = 40
SHORT_ALLOCATION = 20
NIGHTLY_TOTAL = LONG_ALLOCATION + SHORT_ALLOCATION


def cap_take(long_count, short_count):
    """How many each side takes, restated from the decision rather than read from the code.

    "Each side takes the lesser of its candidate count and its allocation. Whatever either leaves
    unfilled is offered to the other, by rank within that other side."

    No priority order is needed and none is written here. A slot is only released by a side that ran
    out of candidates, and a side that ran out is not also asking for more, so the two conditions are
    mutually exclusive and one pass is deterministic.
    """
    taken_long = min(long_count, LONG_ALLOCATION)
    taken_short = min(short_count, SHORT_ALLOCATION)

    taken_long += min(NIGHTLY_TOTAL - taken_long - taken_short, long_count - taken_long)
    taken_short += min(NIGHTLY_TOTAL - taken_long - taken_short, short_count - taken_short)

    return taken_long, taken_short


def cap_order(candidates, direction):
    """One side's candidates, ranked on give-up distance ascending with ticker as the tiebreak.

    Within a direction and never across: a pooled ranking would put a short's give-up distance beside
    a long's and truncate one on the other's account.
    """
    side = [c for c in candidates if c["direction"] == direction]
    side.sort(key=lambda c: (Decimal(c["stopDistanceRanges"]), c["ticker"]))
    return [c["setupId"] for c in side]


def cap_main(argv):
    """The cap over fixtures/cap-cases.json, decided a second time.

    Nothing here imports the lab. An allocation that moved in one place and not the other, or a
    tiebreak that stopped being the ticker, shows as a named difference rather than as a count.
    """
    if len(argv) < 1:
        print("usage: --cap <cap-cases.json>", file=sys.stderr)
        return 2

    with open(argv[0], encoding="utf-8") as handle:
        book = json.load(handle)

    if book["tier"] != "AUTHORED":
        print("cap-cases.json says tier %s, not AUTHORED" % book["tier"], file=sys.stderr)
        return 1

    allocation = book["allocation"]
    if (allocation["long"], allocation["short"], allocation["total"]) != (
            LONG_ALLOCATION, SHORT_ALLOCATION, NIGHTLY_TOTAL):
        print("the case file states an allocation this derivation does not hold", file=sys.stderr)
        return 1

    print("\ncap  from %s" % os.path.basename(argv[0]))

    for scenario in book["scenarios"]:
        taken_long, taken_short = cap_take(scenario["long"], scenario["short"])
        print("  cap.%s.long   %d" % (scenario["name"], taken_long))
        print("  cap.%s.short  %d" % (scenario["name"], taken_short))

    candidates = book["ordering"]["candidates"]
    for direction in ("long", "short"):
        print("  cap.ordering.%-6s %s" % (direction, " ".join(cap_order(candidates, direction))))

    return 0


# --- the point-in-time bound, read from both sides of one correction --------------------------


def observed_close(connection, ticker, as_of, bound):
    """The adjusted close of one session, as it stood at `bound`.

    The rule written out rather than borrowed: a bar is in the window if it is dated at or before
    the as-of, it was observed at or before the bound, and no later observation of that same session
    was also made by the bound. The last clause is the whole of it. Without it a correction and the
    figure it corrects are both in the answer, and which one a reader gets is whichever the store
    happened to return.
    """
    row = connection.execute(
        """
        SELECT b.adj_close FROM daily_bar b
         WHERE b.ticker = ? AND b.bar_date = ? AND b.observed_at <= ?
           AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                 WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                   AND l.observed_at <= ?)
        """,
        (ticker, as_of, bound, bound),
    ).fetchone()

    return None if row is None else Decimal(row[0])


def pit_main(argv):
    """What a read of one session returns from either side of a correction's own instant.

    Two figures rather than one verdict. "The night did not see it" is satisfied perfectly by a read
    that returns nothing and by a store that never took the row, so the same session read from after
    the correction is what makes the first figure mean the bound held.
    """
    if len(argv) < 3:
        print("usage: --point-in-time <store.db> <as-of> <ticker>", file=sys.stderr)
        return 2

    store, as_of, ticker = argv[0], argv[1], argv[2]
    connection = sqlite3.connect(store)

    on_the_night = observed_close(connection, ticker, as_of, as_of + "T23:59:59.999Z")

    # Whatever the latest observation of that session is, whenever it was made. The bound has to be
    # past it or this second read would be the first one again.
    latest = connection.execute(
        "SELECT MAX(observed_at) FROM daily_bar WHERE ticker = ? AND bar_date = ?",
        (ticker, as_of)).fetchone()[0]

    afterwards = observed_close(connection, ticker, as_of, latest)

    observations = connection.execute(
        "SELECT COUNT(*) FROM daily_bar WHERE ticker = ? AND bar_date = ?",
        (ticker, as_of)).fetchone()[0]

    print("\npoint in time  %s as of %s" % (ticker, as_of))
    print("  pointInTime.%s.onTheNight   %s"
          % (ticker, "no bar" if on_the_night is None else on_the_night.quantize(PLACES)))
    print("  pointInTime.%s.afterwards   %s"
          % (ticker, "no bar" if afterwards is None else afterwards.quantize(PLACES)))
    print("  pointInTime.%s.observations %d" % (ticker, observations))

    if on_the_night is not None and afterwards is not None and on_the_night == afterwards:
        print("  the two reads agree, so nothing here is bounding anything", file=sys.stderr)
        return 1

    return 0



# --- the calibration range, derived from the captured histories ------------------------------


def calibration_population(captured):
    """Which names a calibration run over the fixture could ever decide, and over which sessions.

    Derived from the captured responses rather than from the store the replay built, on the same
    terms `--universe` uses. Three of the seeded histories are index trackers, an ETF fails the
    security-type filter, and a tracker is never a universe member: partitioning by the same type
    rule the screen uses is what makes this a derivation rather than a subtraction of a number
    somebody already knew.

    The warm-up is the rule and not a number carried over. A name needs a whole warm-up of sessions
    behind a date before any figure exists for it, so the first session a run can decide is the
    hundred-and-fiftieth, and a name with fewer than a hundred and fifty stored sessions can never
    be decided at all.
    """
    with open(os.path.join(captured, "manifest.json"), encoding="utf-8") as handle:
        manifest = json.load(handle)

    with open(os.path.join(captured, "exchange-symbol-list.json"), encoding="utf-8") as handle:
        types = {row["Code"]: row.get("Type") for row in json.load(handle)}

    sessions_by_ticker = {}
    every_session = set()

    for entry in manifest["responses"]:
        if not entry["endpoint"].startswith("eod/"):
            continue

        ticker = entry["endpoint"].split("/")[1].split(".")[0]
        with open(os.path.join(captured, entry["file"]), encoding="utf-8") as handle:
            bars = json.load(handle)

        dates = sorted({bar["date"] for bar in bars})
        sessions_by_ticker[ticker] = dates
        every_session.update(dates)

    members = sorted(
        ticker for ticker, dates in sessions_by_ticker.items()
        if types.get(ticker) == SECURITY_TYPE and len(dates) >= WARMUP
    )

    ordered = sorted(every_session)

    return {
        "storedSessions": len(ordered),
        "from": ordered[WARMUP - 1],
        "to": ordered[-1],
        "sessions": len(ordered) - (WARMUP - 1),
        "membersWithHistory": len(members),
        "members": members,
        "candidatesPerSession": len(members),
    }


def calibration_main(argv):
    """What a calibration run over this fixture can cover, and why it cannot answer for a threshold.

    The last figure is the one worth printing. A scan takes the top fifty by its own magnitude, and
    the fixture holds thirty names that could ever be measured, so every one of them is inside the
    top fifty of all six scans on every session. The most recent thrust is therefore always the
    session itself, every pullback has no bars, and every geometry check fails on every row. The run
    exercises the code and the population is degenerate by construction.
    """
    if len(argv) < 1:
        print("usage: --calibration <captured-directory>", file=sys.stderr)
        return 2

    population = calibration_population(argv[0])

    print("\ncalibration  from the captured responses and nothing else")
    for name in ("storedSessions", "from", "to", "sessions", "membersWithHistory"):
        print("  calibration.%-32s %s" % (name, population[name]))

    print("\n  candidates a session       %d, against a scan breadth of %d"
          % (population["candidatesPerSession"], SCAN_BREADTH))

    if population["candidatesPerSession"] < SCAN_BREADTH:
        print("  every candidate is inside every scan's breadth, so the thrust is always the session"
              " itself and no pullback has any bars")

    return 0


def q(value):
    """Four places, rounding halves away from zero, which is what the replay prints.

    Stated here rather than left to `.quantize(PLACES)` as the rest of this file does, because
    the default is banker's rounding and the replay rounds away from zero. On the values below
    the two agree, and a disagreement on a tie would arrive later as a one-digit difference in a
    figure nobody would think to suspect. The point of a second implementation is to disagree
    about the arithmetic, not about the printing.
    """
    return str(Decimal(value).quantize(PLACES, rounding=ROUND_HALF_UP))


def geometry_window(connection, ticker, as_of, sessions):
    """The last `sessions` bars up to `as_of`, oldest first, point in time, carrying the open.

    A second window rather than a parameter on the first, for the reason the first one gives:
    the window is as much a part of the answer as the arithmetic is, and a derivation that
    borrows the selection it is checking is checking less than it looks. The open is here and
    not there because only the pullback shape needs it, to stand in for a close that does not
    exist when the thrust is the first bar of the window.
    """
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
    for date, o, h, l, c, ac in rows:
        o, h, l, c, ac = (Decimal(x) for x in (o, h, l, c, ac))
        factor = Decimal(1) if c == 0 else ac / c
        out.append(
            {
                "date": date,
                # Adjusted, for the shape. A split read raw is a 50% decline.
                "open": o * factor,
                "high": h * factor,
                "low": l * factor,
                "close": ac,
                # Raw, for the two prices that trade tomorrow. Kept on the same record rather
                # than in a parallel list, because the whole class of error here is reading one
                # where the other was meant.
                "raw_high": h,
                "raw_low": l,
            }
        )
    return out


def pullback_shape(bars, thrust_index, is_long, span=1):
    """The shape of one pullback, restated from what the quantity is rather than from the code.

    The move is measured from the close before the thrust to the furthest the move reached after
    it; the pullback is everything after that extreme, and the retrace is the fraction of the move
    given back. Signed so both directions read the same way: zero is no give-back and one is the
    whole move.

    Two edges, and both are places where a wrong answer would still look like a right one:

      With the thrust on the first bar of the window there is no close before it. The move has to
      be measured from somewhere, and the thrust's own open is the nearest thing to where it began.
      Its close is not, because the close is inside the move being measured and using it would
      report a shorter thrust than actually happened.

      A thrust of no size cannot be retraced by a fraction of itself, so the depth is undefined
      rather than zero or infinite. Zero would read as a pullback that gave nothing back, which is
      a different fact about a different shape.
    """
    if not bars or thrust_index < 0 or thrust_index >= len(bars):
        return None

    # Where the flagged move began. A one-session scan starts where it is flagged; a
    # twenty-session scan started nineteen sessions earlier. Clamped at the window's own start,
    # because a window holding part of the move still holds a real shape and refusing it would
    # drop exactly the names whose run began before the history the detector reads.
    thrust_start = max(0, thrust_index - span + 1)

    if thrust_start > 0:
        origin = bars[thrust_start - 1]["close"]
    else:
        origin = bars[thrust_start]["open"]

    if is_long:
        extreme_index = max(range(thrust_start, len(bars)), key=lambda i: bars[i]["high"])
        extreme = bars[extreme_index]["high"]
    else:
        extreme_index = min(range(thrust_start, len(bars)), key=lambda i: bars[i]["low"])
        extreme = bars[extreme_index]["low"]

    after = range(extreme_index + 1, len(bars))
    pullback_bars = len(bars) - 1 - extreme_index

    if pullback_bars == 0:
        # No drift yet, so the extreme is its own answer and there is no level to enter or
        # abandon against. A real state rather than a missing one.
        pullback_extreme = extreme
        trigger = bars[extreme_index]["raw_high"] if is_long else bars[extreme_index]["raw_low"]
        stop = trigger
    elif is_long:
        pullback_extreme = min(bars[i]["low"] for i in after)
        trigger = max(bars[i]["raw_high"] for i in after)
        stop = min(bars[i]["raw_low"] for i in after)
    else:
        pullback_extreme = max(bars[i]["high"] for i in after)
        trigger = min(bars[i]["raw_low"] for i in after)
        stop = max(bars[i]["raw_high"] for i in after)

    move = (extreme - origin) if is_long else (origin - extreme)
    given_back = (extreme - pullback_extreme) if is_long else (pullback_extreme - extreme)
    retrace = None if move == 0 else given_back / move

    return {
        "extremeIndex": extreme_index,
        "pullbackBars": pullback_bars,
        "thrustOrigin": origin,
        "thrustExtreme": extreme,
        "pullbackExtreme": pullback_extreme,
        "retraceDepth": retrace,
        "trigger": trigger,
        "stop": stop,
    }


def geometry_main(argv):
    """The authored geometry windows, restated independently of PullbackGeometry.

    The captured fixture puts every name inside every scan on every session, so the thrust is
    always the last bar and the shipped method returns nought bars and nought depth on every row.
    fixtures/geometry-cases.json names windows and thrust indices that reach the other branches,
    over the fixture's own bars. This mode says what each one should compute.
    """
    if len(argv) < 1:
        print("usage: derive-indicators.py --geometry <store> [cases.json]", file=sys.stderr)
        return 2

    store = argv[0]
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    path = argv[1] if len(argv) > 1 else os.path.join(here, "fixtures", "geometry-cases.json")

    with open(path, encoding="utf-8") as handle:
        spec = json.load(handle)

    if spec.get("tier") != "AUTHORED":
        print("%s does not declare itself AUTHORED" % path, file=sys.stderr)
        return 1

    connection = sqlite3.connect(store)
    as_of = spec["window"]["asOf"]
    sessions = spec["window"]["sessions"]

    print("\ngeometry, over %s, %d session(s) to %s" % (store, sessions, as_of))

    for case in spec["cases"]:
        bars = geometry_window(connection, case["ticker"], as_of, sessions)
        shape = pullback_shape(
            bars, case["thrustIndex"], case["direction"] == "long", case.get("thrustSpanSessions", 1))
        name = case["name"]

        if shape is None:
            print("  geometry.%-38s %s" % (name, "no shape"))
            continue

        print()
        print("  %s  (%s index %d, span %d, %s, %d bar(s) read)"
              % (name, case["ticker"], case["thrustIndex"], case.get("thrustSpanSessions", 1),
                 case["direction"], len(bars)))

        for key in ("extremeIndex", "pullbackBars"):
            print("    geometry.%s.%-16s %d" % (name, key, shape[key]))

        for key in ("thrustOrigin", "thrustExtreme", "pullbackExtreme"):
            print("    geometry.%s.%-16s %s" % (name, key, q(shape[key])))

        depth = shape["retraceDepth"]
        print("    geometry.%s.%-16s %s"
              % (name, "retraceDepth", "undefined" if depth is None else q(depth)))

        for key in ("trigger", "stop"):
            print("    geometry.%s.%-16s %s" % (name, key, q(shape[key])))

    return 0


def thrust_main(argv):
    """Which scan produced each setup's thrust, restated from the rule rather than read back.

    The detector's rule, in words: of the hits on this name at or before the as-of, keep the three
    scans that move the setup's own way, take the most recent session, and break a tie within a
    session by rank. That is a different statement from "read the column back", which is what makes
    this worth writing: the column is new at 3.0(b) and the whole point of it is the 3.0(c) split by
    scan family, so a column populated from the wrong hit would be a wrong split nobody could see.
    """
    if len(argv) < 2:
        print("usage: derive-indicators.py --thrust <store> <as-of>", file=sys.stderr)
        return 2

    store, as_of = argv[0], argv[1]
    connection = sqlite3.connect(store)

    upward = ("gainer", "gapper", "leader")
    downward = ("decliner", "gapdown", "laggard")

    rows = connection.execute(
        "SELECT setup_id, ticker, direction FROM setup ORDER BY setup_id").fetchall()

    print("\nthrust scan, over %s, as of %s" % (store, as_of))

    for setup_id, ticker, direction in rows:
        scans = upward if direction == "long" else downward

        hit = connection.execute(
            """
            SELECT as_of, scan FROM scan_hit
             WHERE ticker = ? AND as_of <= ? AND scan IN (?, ?, ?)
             ORDER BY as_of DESC, rank
             LIMIT 1
            """, (ticker, as_of, *scans)).fetchone()

        print("  setup.%s.%-14s %s" % (setup_id, "thrustScan", "none" if hit is None else hit[1]))
        print("  setup.%s.%-14s %s" % (setup_id, "thrustSession", "none" if hit is None else hit[0]))

    return 0


def journal_main(argv):
    """What the journal should find, restated from the invariants rather than read back.

    The stage seals the night between the signal freeze and the cap, so at that moment every setup
    row of the night must carry a complete check-result set and a frozen signal row, and must carry
    neither a rank, a cap verdict nor an agreement, because the components that write those run
    after it or belong to a person. Restated here from that sentence, over the store, so a stage
    that silently stopped checking one of the four is a difference rather than a quieter pass.
    """
    if len(argv) < 2:
        print("usage: derive-indicators.py --journal <store> <as-of>", file=sys.stderr)
        return 2

    store, as_of = argv[0], argv[1]
    connection = sqlite3.connect(store)

    # Every row in the store, on the same terms the stage reads them: the fixture holds one night
    # and the authored row carries no date prefix in its id, so filtering on as_of would drop it and
    # report a smaller population than the stage saw.
    rows = connection.execute(
        "SELECT setup_id, direction, check_results, rank, capped_out, agreement FROM setup").fetchall()

    long_gates = ["tradable", "moves-enough", "uptrend", "thrust", "dip-shape",
                  "held-floor", "contraction", "trigger-near", "exit-tight", "cluster"]
    short_gates = ["tradable-shortable", "moves-enough", "downtrend", "averages-squeezing", "thrust",
                   "bounce-shape", "reached-ceiling", "no-reclaim", "exit-tight", "cluster"]

    setups = 0
    with_signals = 0
    breaches = 0

    for setup_id, direction, blob, rank, capped_out, agreement in rows:
        setups += 1
        names = {r["name"] for r in json.loads(blob)}
        expected = long_gates if direction == "long" else short_gates

        if any(g not in names for g in expected):
            breaches += 1
        if rank is not None or capped_out is not None:
            breaches += 1
        if agreement is not None:
            breaches += 1

        signals = connection.execute(
            "SELECT COUNT(*) FROM setup_signal WHERE setup_id = ?", (setup_id,)).fetchone()[0]

        if signals > 0:
            with_signals += 1
        else:
            breaches += 1

    print("\njournal, over %s, as of %s" % (store, as_of))
    print("  journal.%-14s %d" % ("setups", setups))
    print("  journal.%-14s %d" % ("withSignals", with_signals))
    print("  journal.%-14s %d" % ("breaches", breaches))
    return 0


def forward_main(argv):
    """The forward outcomes, restated from what the quantity is rather than from the code.

    The return is the change in adjusted close from the subject's own session to the session the
    horizon lands on, signed by direction so a short that fell reads positive. The excursions are
    the furthest the path ran either way over the sessions after the subject's own, expressed in
    the subject's ATR on its own date.

    Two edges, both places a wrong answer still looks right:

      The horizon is trading sessions, not calendar days. A ten-day return that quietly became
      fourteen over a holiday is not comparable with one that did not. The calendar date is
      recorded beside the session actually used, and where they differ the follow-up says so.

      The subject's own session is excluded from the excursions. The lab flagged the name on that
      session's close, so what its own high and low did is not something a position could have
      lived through.
    """
    if len(argv) < 1:
        print("usage: derive-indicators.py --forward <store> [cases.json]", file=sys.stderr)
        return 2

    store = argv[0]
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    path = argv[1] if len(argv) > 1 else os.path.join(here, "fixtures", "forward-cases.json")

    with open(path, encoding="utf-8") as handle:
        spec = json.load(handle)

    if spec.get("tier") != "AUTHORED":
        print("%s does not declare itself AUTHORED" % path, file=sys.stderr)
        return 1

    connection = sqlite3.connect(store)
    horizons = (1, 3, 5, 10)

    print("\nforward outcomes, over %s" % store)

    for case in spec["cases"]:
        ticker, as_of = case["ticker"], case["asOf"]
        is_long = case["direction"] == "long"
        name = case["name"]

        # Bounded on the fixture's as-of. The fixture plants one observation dated the day after,
        # and an unbounded read takes it: over the split case that is a return of 1.6601 where the
        # bounded answer is -0.1369. This mode had no bound until it disagreed with the shipped
        # method, which is what a second implementation is for.
        bound = spec["observedBefore"]

        rows = connection.execute(
            """
            SELECT b.bar_date, b.high, b.low, b.close, b.adj_close
              FROM daily_bar b
             WHERE b.ticker = ? AND b.bar_date >= ?
               AND b.observed_at <= ?
               AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                     WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                       AND l.observed_at <= ?)
             ORDER BY b.bar_date
            """, (ticker, as_of, bound, bound)).fetchall()

        bars = []
        for date, high, low, close, adj in rows:
            high, low, close, adj = (Decimal(x) for x in (high, low, close, adj))
            factor = Decimal(1) if close == 0 else adj / close
            bars.append({"date": date, "high": high * factor, "low": low * factor, "close": adj})

        # From the case rather than the store, on the same grounds the case states it: the fixture
        # holds indicator rows for its as-of night only, so a subject placed earlier has none.
        atr = Decimal(case["averageTrueRange"])

        print()
        print("  %s  (%s from %s, %s, %d bar(s))" % (name, ticker, as_of, case["direction"], len(bars)))

        for horizon in horizons:
            key = "forward.%s.h%d" % (name, horizon)

            if len(bars) <= horizon or bars[0]["close"] == 0:
                print("    %-52s %s" % (key, "not yet elapsed"))
                continue

            start, end = bars[0], bars[horizon]
            move = (end["close"] - start["close"]) / start["close"]
            signed = move if is_long else -move

            best = max((b["high"] - start["close"]) if is_long else (start["close"] - b["low"])
                       for b in bars[1:horizon + 1])
            worst = min((b["low"] - start["close"]) if is_long else (start["close"] - b["high"])
                        for b in bars[1:horizon + 1])

            intended = (datetime.date.fromisoformat(as_of)
                        + datetime.timedelta(days=horizon)).isoformat()

            print("    %s.%-14s %s" % (key, "intendedDate", intended))
            print("    %s.%-14s %s" % (key, "actualDate", end["date"]))
            print("    %s.%-14s %s" % (key, "slipped", "no" if intended == end["date"] else "yes"))
            print("    %s.%-14s %s" % (key, "returnSigned", q(signed)))
            print("    %s.%-14s %s" % (key, "mfeAtr", "undefined" if atr == 0 else q(best / atr)))
            print("    %s.%-14s %s" % (key, "maeAtr", "undefined" if atr == 0 else q(worst / atr)))

    return 0


def controls_main(argv):
    """Which controls each setup should have drawn, restated from the rule rather than read back.

    The rule, in words: from the names that cleared the liquidity floor on the night and were not
    flagged, take the five nearest on turnover and daily range, measured as a fraction of the
    subject's own figure, with ticker as the tiebreak. The tight set first drops every candidate
    whose trend ladder differs from the subject's.

    Two edges, both of which the shipped stage got wrong on its first run:

      The ladder grade is written as a later observation of the same session, so a read bounded on
      the run instant sees the ungraded row and the tight filter compares nothing to nothing. The
      bound is the end of the as-of date, which is what every other indicator read uses.

      The distance is relative, not absolute. Fifty million dollars of turnover is a wide gap for a
      small name and a rounding for a large one, and an absolute distance draws every control from
      the biggest names in the universe.
    """
    if len(argv) < 2:
        print("usage: derive-indicators.py --controls <store> <as-of>", file=sys.stderr)
        return 2

    store, as_of = argv[0], argv[1]
    connection = sqlite3.connect(store)
    bound = as_of + "T23:59:59.999Z"

    rows = connection.execute(
        """
        SELECT i.ticker, i.dollar_volume_median_20, i.adr_20, i.ladder_grade
          FROM indicator_daily i
         WHERE i.as_of = ? AND i.computed_at <= ?
           AND i.computed_at = (SELECT MAX(c.computed_at) FROM indicator_daily c
                                 WHERE c.ticker = i.ticker AND c.as_of = i.as_of
                                   AND c.computed_at <= ?)
         ORDER BY i.ticker
        """, (as_of, bound, bound)).fetchall()

    figures = {}
    for ticker, turnover, adr, grade in rows:
        turnover = Decimal(turnover) if turnover is not None else Decimal(0)
        if turnover < LIQUIDITY_FLOOR:
            continue
        figures[ticker] = {
            "ticker": ticker,
            "turnover": turnover,
            "adr": Decimal(adr) if adr is not None else Decimal(0),
            "grade": grade,
        }

    setups = connection.execute(
        "SELECT setup_id, ticker FROM setup ORDER BY setup_id").fetchall()
    flagged = {t for _, t in setups}

    def apart(subject, candidate):
        if subject == 0:
            return Decimal(10) ** 30
        return abs(candidate - subject) / abs(subject)

    print("\ncontrols, over %s, as of %s" % (store, as_of))

    for setup_id, ticker in setups:
        subject = figures.get(ticker)
        if subject is None:
            continue

        for name, tight in (("loose", False), ("tight", True)):
            scored = []
            for candidate in figures.values():
                if candidate["ticker"] == ticker or candidate["ticker"] in flagged:
                    continue
                if tight and candidate["grade"] != subject["grade"]:
                    continue
                liquidity = apart(subject["turnover"], candidate["turnover"])
                daily = apart(subject["adr"], candidate["adr"])
                scored.append((liquidity + daily, candidate["ticker"], liquidity, daily))

            scored.sort(key=lambda s: (s[0], s[1]))
            picked = scored[:5]

            print("  controls.%s.%-6s %s" % (setup_id, name, " ".join(p[1] for p in picked)))

            if picked:
                best = picked[0]
                print('  controls.%s.%s.nearest {"liquidity":"%s","dailyRange":"%s","ladderGrade":"%s"}'
                      % (setup_id, name, q(best[2]), q(best[3]), "same" if tight else (
                          figures[best[1]]["grade"] or "ungraded")))

    return 0


def ceiling_main(argv):
    """The win-rate bound over the authored populations, restated from what the quantity is.

    Perfect foresight takes the subjects that ended ahead, which is the only thing foresight is
    granted. Of those, it keeps the ones the stop let it keep: a name that finished 15% up having
    first traded through its give-up point was not available to any rule, because the position was
    already closed. The bound is kept over ahead; the achieved rate is kept over everything flagged.

    Two denominators, and their difference is the figure. A bound over the whole population is the
    achieved rate again and the gap is nought by construction, which is a ceiling that can only ever
    say selection has no room.

    The conversion is the trap. The excursion is in ATR and the give-up is in daily ranges, so both
    are turned into prices before they are compared. Read as bare multiples the two are different
    units on different bases, both small, both looking like volatility.
    """
    if len(argv) < 1:
        print("usage: derive-indicators.py --ceiling [cases.json]", file=sys.stderr)
        return 2

    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    path = argv[0] if argv and argv[0].endswith(".json") else os.path.join(
        here, "fixtures", "ceiling-cases.json")

    with open(path, encoding="utf-8") as handle:
        spec = json.load(handle)

    if spec.get("tier") != "AUTHORED":
        print("%s does not declare itself AUTHORED" % path, file=sys.stderr)
        return 1

    print("\nwin-rate ceiling, over %s" % os.path.basename(path))

    for scenario in spec["scenarios"]:
        name = scenario["name"]
        subjects = scenario["subjects"]

        def survived(s):
            atr = Decimal(s["atr"])
            daily = Decimal(s["dailyRange"])
            if atr <= 0 or daily <= 0:
                return False
            # The excursion is the least favourable point on the path, so it is POSITIVE whenever
            # the path never went against the subject at all. Floored at nought rather than taken
            # through abs(): the two agree on every negative input and differ on exactly the rows
            # the bound is most sensitive to, being the ones that rose without drawing down. This
            # restatement carried the same abs() as the shipped code until 3.5 was reopened, which
            # is what an independent restatement of a shared false premise buys.
            adverse = min(Decimal(0), Decimal(s["maeAtr"]))
            return -adverse * atr < Decimal(s["stopRanges"]) * daily

        ahead = [s for s in subjects if Decimal(s["return"]) > 0]
        kept = [s for s in ahead if survived(s)]

        bound = Decimal(0) if not ahead else Decimal(len(kept)) / Decimal(len(ahead))
        achieved = Decimal(len(kept)) / Decimal(len(subjects))

        print()
        print("  ceiling.%s.%-10s %d" % (name, "subjects", len(subjects)))
        print("  ceiling.%s.%-10s %s" % (name, "bound", q(bound)))
        print("  ceiling.%s.%-10s %s" % (name, "achieved", q(achieved)))
        print("  ceiling.%s.%-10s %s" % (name, "gap", q(bound - achieved)))

    return 0


def accumulation_main(argv):
    """The closed-horizon population's own counts, restated from its stated shape.

    The population is authored: a stated number of nights, a stated number of setups a night on each
    side, and a stated number of controls per set per setup. Every count below follows from those
    three and from the four horizons, so none of it is read back from the run it is checked against.

    Why it is owed at all. The captured fixture holds one market day, so no horizon closes in it and
    the whole measurement path past the flag was exercised by nothing. That is how ForwardReturnFiller
    binding its subject kind to the literal "setup" survived twelve checkpoints: nothing anywhere ran
    the query that came back empty against a store with something to put in it.
    """
    nights = 24
    setups_per_night_per_direction = 6
    directions = 2
    sets = 2
    controls_per_set = 5
    horizons = 4

    setups = nights * setups_per_night_per_direction * directions
    controls = setups * sets * controls_per_set

    print("\naccumulation, over the authored closed-horizon population")
    print()
    print("  accumulation.%-38s %d" % ("nights", nights))
    print("  accumulation.%-38s %d" % ("setups", setups))
    print("  accumulation.%-38s %d" % ("controls", controls))
    print("  accumulation.%-38s %d" % ("forward.setupsWritten", setups * horizons))
    print("  accumulation.%-38s %d" % ("forward.controlsWritten", controls * horizons))
    print("  accumulation.%-38s %d" % ("forward.setupOutcomeRows", setups * horizons))
    print("  accumulation.%-38s %d" % ("forward.controlOutcomeRows", controls * horizons))

    # One panel per direction per control set, and every one of them has to carry an interval once
    # the control outcomes exist. Nought here is the state the defect produced.
    print("  accumulation.band1.%-32s %d" % ("panelsWithAnInterval", directions * sets))

    return 0


def splitmix64(state):
    """One step of splitmix64, the generator the interval's block starts are drawn from.

    Four lines, one 64-bit word of state, and restated identically in any language with 64-bit
    unsigned arithmetic. The shipped implementation is PairedInterval.Next; this is the same
    arithmetic, and the seed is the same published constant.
    """
    mask = (1 << 64) - 1
    state = (state + 0x9E3779B97F4A7C15) & mask
    z = state
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & mask
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & mask
    return state, z ^ (z >> 31)


def interval_main(argv):
    """The interval and the effective sample, restated from what each quantity is.

    The interval is a studentised moving-block bootstrap: blocks of the scoring horizon's length,
    each draw taking its block starts INDEPENDENTLY from splitmix64 at a fixed published seed, and
    each resampled mean scored against its own block-to-block standard error before the 2.5th and
    97.5th percentiles of that ratio are read back onto the observed estimate.

    This restatement is the reason the defect it replaces survived. It hard-coded the same two
    coprime strides the shipped code used, so what the two agreed about was the transcription of an
    algorithm and never that the algorithm was a bootstrap at all. Every start in draw d was the
    corresponding start in draw 0 shifted by the same d * 7919, so the resample space had one point
    per night in it however many draws were asked for, and ten thousand draws was bit-identical to N
    draws. A second implementation of the wrong thing agrees with the first.

    The effective sample starts from rows rather than nights, because the paired difference has
    already removed the market factor the names in a night would otherwise share. Two discounts are
    then applied, both measured from the series:

      * the label overlap across nights, as the variance-inflation form over the lag-one
        autocorrelation of the nightly means, (1 - rho) / (1 + rho), capped at one;
      * whatever common movement the pairing failed to remove, as the ordinary design effect: the
        realised variance of the nightly means over the variance they would have if each night's
        pairs were independent, being within^2 / pairs, floored at one.

    Where no night carries more than one pair, or no night's pairs disperse at all, nothing in the
    series says anything about clustering and a night counts as one observation. That is the
    pessimistic corner rather than the assumption, and it is what the first four scenarios exercise.

    The trap this restatement exists to catch: any scheme whose draws are one fixed selection
    rotated. Walking the offsets in order is the loud version, where a rotation preserves the mean
    and the interval comes back with no width at all. Mixing them by strides is the quiet version,
    where the interval has a width and is two to three point seven times too narrow. Both clear zero
    far more often than 95% confidence claims, and band 1 turns on whether a bound clears zero.
    """
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    path = argv[0] if argv and argv[0].endswith(".json") else os.path.join(
        here, "fixtures", "interval-cases.json")

    with open(path, encoding="utf-8") as handle:
        spec = json.load(handle)

    if spec.get("tier") != "AUTHORED":
        print("%s does not declare itself AUTHORED" % path, file=sys.stderr)
        return 1

    block = 10
    draws = 10000
    seed = 0x5EED1F7

    print("\npaired interval, over %s" % os.path.basename(path))

    for scenario in spec["scenarios"]:
        name = scenario["name"]
        nights = [Decimal(str(x)) for x in scenario["nightlyMeans"]]
        key = "interval.%s" % name

        if len(nights) < block * 2:
            print("  %-46s %s" % (key, "withheld"))
            continue

        count = len(nights)
        within = Decimal(str(scenario.get("withinNightDispersion", 0)))

        # Per-night pair counts where the scenario states them, otherwise one count on every night.
        #
        # A scalar was the only form until 2026-08-30, and a scalar is the one population in which
        # the two quantities below are the same number, so this restatement and the shipped code
        # agreed over every scenario in the file while carrying different arithmetic.
        if "pairsByNight" in scenario:
            by_night = [int(x) for x in scenario["pairsByNight"]]
            if len(by_night) != count:
                raise SystemExit(
                    "%s states %d pair count(s) for %d night(s)" % (name, len(by_night), count))
        else:
            by_night = [int(scenario.get("pairsPerNight", 1))] * count

        # rows is n times the arithmetic mean of the pair counts. independent is n times their
        # harmonic mean, which is the effective sample of an unweighted mean of nightly means: the
        # variance of that estimator is (1/n^2) * sum_i sigma^2 / p_i, so its effective sample is
        # n^2 / sum_i (1/p_i). The harmonic mean never exceeds the arithmetic one, so rows can only
        # ever overstate, and it overstates by most where the counts are furthest apart.
        rows = sum(by_night)
        reciprocals = sum(Decimal(1) / Decimal(max(1, p)) for p in by_night)
        independent = (Decimal(count) * Decimal(count) / reciprocals
                       if reciprocals > 0 else Decimal(count))

        blocks = count // block

        # The bootstrap runs in IEEE-754 double, matching the shipped code exactly: these are
        # variances of ratios rather than prices, the arithmetic needs a square root, and both
        # sides do the same operations in the same order so the two agree to every place printed.
        # The effective-sample arithmetic below stays in Decimal, which is what the shipped code
        # uses for it.
        series = [float(x) for x in nights]

        def block_standard_error(block_means):
            """The standard error of a mean of block means, or None where there are too few."""
            if len(block_means) < 2:
                return None
            centre = sum(block_means) / len(block_means)
            squares = sum((x - centre) * (x - centre) for x in block_means)
            return math.sqrt(squares / (len(block_means) - 1) / len(block_means))

        def observed_standard_error():
            """The error of the observed mean, over a whole number of non-overlapping blocks.

            The direct analogue of what each resample is scored by. A resample's error is the sample
            error of `blocks` block means drawn independently, so the matching estimate on the
            observed series is the sample error of `blocks` non-overlapping block means. Any such
            tiling leaves count mod block nights out of the scale estimate; they still enter the
            point estimate, the effective sample and every resample. Anchored at the recent end so
            the nights left out are the oldest.
            """
            if blocks < 2:
                return None
            offset = count - blocks * block
            means = [sum(series[offset + b * block + i] for i in range(block)) / block
                     for b in range(blocks)]
            return block_standard_error(means)

        observed_mean = sum(series) / count
        error = observed_standard_error()

        if not error or error <= 0:
            # A series whose blocks all carry the same mean has no standard error to studentise
            # by, and the interval it would produce has no width. Withheld, never shown.
            print("  %-46s %s" % (key, "withheld"))
            continue

        state = seed
        ratios = []
        for _draw in range(draws):
            block_means = []
            for _b in range(blocks):
                state, value = splitmix64(state)
                start = value % count
                block_means.append(
                    sum(series[(start + i) % count] for i in range(block)) / block)
            resampled_error = block_standard_error(block_means)
            if resampled_error and resampled_error > 0:
                resampled = sum(block_means) / len(block_means)
                ratios.append((resampled - observed_mean) / resampled_error)

        if not ratios:
            print("  %-46s %s" % (key, "withheld"))
            continue

        ratios.sort()

        def ratio_at(fraction):
            return ratios[int(fraction * (len(ratios) - 1))]

        # The tails swap: the upper quantile of the ratio gives the lower bound.
        low = Decimal(repr(observed_mean - ratio_at(0.975) * error))
        high = Decimal(repr(observed_mean - ratio_at(0.025) * error))
        mean = sum(nights) / count

        centred = [x - mean for x in nights]
        variance = sum(c * c for c in centred)
        covariance = sum(centred[i] * centred[i - 1] for i in range(1, count))

        if variance == 0:
            effective = 1
        else:
            rho = covariance / variance
            serial = Decimal(1) if rho <= -1 else (1 - rho) / (1 + rho)
            serial = min(Decimal(1), max(Decimal(0), serial))

            # The design effect, pooled over the nights that can speak to it.
            #
            # A night of one pair says nothing about how its own pairs dispersed, so it carries no
            # degrees of freedom and is skipped here. It is still counted by the harmonic mean
            # above, and by the average below, which is the whole interaction a uniform series
            # cannot reach: the two discounts read the same series through different populations of
            # nights.
            degrees = sum(p - 1 for p in by_night if p >= 2)
            weighted = sum(Decimal(p - 1) * within * within for p in by_night if p >= 2)

            if degrees == 0 or weighted <= 0:
                # Either every night carries one pair, or no night's pairs disperse at all. Neither
                # says anything about clustering, so a night counts as one.
                scaled = Decimal(count) * serial
            else:
                pooled = weighted / Decimal(degrees)
                expected = sum(pooled / Decimal(max(1, p)) for p in by_night) / Decimal(count)

                if expected <= 0:
                    scaled = Decimal(count) * serial
                else:
                    observed = variance / (count - 1)
                    design = max(Decimal(1), observed / expected)
                    scaled = independent / design * serial

            effective = max(1, min(rows, int(scaled.quantize(Decimal("1"), rounding=ROUND_HALF_UP))))

        print()
        print("  %s.%-12s %s" % (key, "mean", q(mean)))
        print("  %s.%-12s %s" % (key, "low", q(low)))
        print("  %s.%-12s %s" % (key, "high", q(high)))
        print("  %s.%-12s %s" % (key, "clearsZero", "yes" if low > 0 else "no"))
        print("  %s.%-12s %d" % (key, "nights", count))
        print("  %s.%-12s %d" % (key, "rows", rows))
        print("  %s.%-12s %d" % (key, "effective", effective))

    return 0


def dispersion_main(argv):
    """The dispersion of ten-session forward returns, and the minimum sample that falls out of it.

    This is the input a minimum sample rests on and the one input to it that is a fact rather than a
    judgement. The corpus stated 160 paired setup observations "detecting about a two-point
    difference in ten-day forward return" without ever measuring the dispersion that arithmetic
    turns on, so the figure read as derived and was not.

    Restated from what the quantities are:

      * A ten-session forward return is next-but-nine's adjusted close over today's, less one.
      * Within one session every name carries the same market move, so the cross-sectional sample
        variance of that session's returns estimates the idiosyncratic variance directly: the common
        term cancels and the n-1 denominator makes it unbiased. That is the same cancellation the
        paired difference buys on the scoreboard.
      * Pooling those variances across sessions by their degrees of freedom gives the single-name
        figure, and a setup's difference against the mean of m controls disperses by sqrt(1 + 1/m)
        times it, because the control mean carries noise of its own.
      * n = ((z_alpha + z_beta) * sigma_d / delta)^2, the one-sample form, because pairing has
        already turned two populations into one series tested against zero.

    Sessions thinner than the minimum name count are dropped: a session whose mean is mostly one of
    its own names has part of the dispersion removed with the market, and the estimate comes back
    too small. Too small is the direction that fires a decision early.
    """
    if len(argv) < 1 or not argv[0]:
        print("usage: derive-indicators.py --dispersion <store> [as-of]", file=sys.stderr)
        return 2

    store = argv[0]
    as_of = argv[1] if len(argv) > 1 else "2026-08-24"
    bound = as_of + "T23:59:59.999Z"

    horizon = 10
    minimum_names = 20
    controls = 5
    delta = 0.02
    z_alpha = 1.959964
    z_beta = 1.281552

    connection = sqlite3.connect(store)
    rows = connection.execute(
        """
        SELECT b.ticker, b.bar_date, b.adj_close
          FROM daily_bar b
         WHERE b.bar_date <= ?
           AND b.observed_at <= ?
           AND b.observed_at = (SELECT MAX(l.observed_at) FROM daily_bar l
                                 WHERE l.ticker = b.ticker AND l.bar_date = b.bar_date
                                   AND l.observed_at <= ?)
         ORDER BY b.ticker, b.bar_date
        """,
        (as_of, bound, bound),
    ).fetchall()

    series = {}
    for ticker, date, adj_close in rows:
        series.setdefault(ticker, []).append((date, float(adj_close)))

    by_session = {}
    names = 0
    for ticker in sorted(series):
        bars = series[ticker]
        if len(bars) <= horizon:
            continue
        names += 1
        for i in range(len(bars) - horizon):
            basis = bars[i][1]
            if basis <= 0:
                continue
            by_session.setdefault(bars[i][0], []).append(bars[i + horizon][1] / basis - 1.0)

    sum_squares = 0.0
    degrees = 0
    sessions = 0
    observations = 0

    for date in sorted(by_session):
        returns = by_session[date]
        count = len(returns)
        if count < minimum_names:
            continue
        mean = sum(returns) / count
        for value in returns:
            centred = value - mean
            sum_squares += centred * centred
        degrees += count - 1
        observations += count
        sessions += 1

    if degrees == 0:
        print("  no session carries %d names; nothing to measure" % minimum_names)
        return 1

    idiosyncratic = round(math.sqrt(sum_squares / degrees), 6)
    paired = round(idiosyncratic * math.sqrt(1.0 + 1.0 / controls), 6)
    scaled = (z_alpha + z_beta) * paired / delta
    minimum = int(math.ceil(scaled * scaled))

    print("\nforward dispersion, over %s, %d session(s) to %s" % (store, sessions, as_of))
    print("  dispersion.%-24s %d" % ("names", names))
    print("  dispersion.%-24s %d" % ("sessions", sessions))
    print("  dispersion.%-24s %d" % ("observations", observations))
    print("  dispersion.%-24s %.6f" % ("idiosyncratic", idiosyncratic))
    print("  dispersion.%-24s %.6f" % ("pairedDifference", paired))
    print("  minimumSample.%-21s %d" % ("effectiveObservations", minimum))

    print("\n  what it costs to ask for less, or for more:")
    for detect in (0.015, 0.02, 0.025, 0.03):
        s = (z_alpha + z_beta) * paired / detect
        print("    detecting %.3f needs %4d" % (detect, int(math.ceil(s * s))))
    for label, zb in (("70%", 0.524401), ("80%", 0.841621), ("90%", z_beta), ("95%", 1.644854)):
        s = (z_alpha + zb) * paired / delta
        print("    at %s power needs %4d" % (label, int(math.ceil(s * s))))

    return 0


def fundamentals_main(argv):
    """Derive the three fields the lab keeps from every captured fundamentals response.

    Written against the vendor's document rather than against EodhdClient, which is the whole
    point of the tier: the C# reads these through System.Text.Json with a converter, and this
    reads them with Python's json and its own rules. Two implementations agreeing on what
    `"NA"` means is worth something; one implementation agreeing with itself is not.

    The rules, stated from the responses rather than from the C#:

      sector, industry   the string, unless it is empty or only whitespace, in which case the
                         vendor is saying it holds none and the answer is absent.
      market cap         the number. The vendor writes it as a JSON number when it has one and
                         as the string "NA" when it does not, and it is absent in that case.
                         A quoted number is still a number.
      the row itself     absent when all three are, because a name the vendor holds nothing on
                         is a real answer and is not the same as a row of nulls.

    That last rule is the one that mattered. fundamentals-MUZ.json is 200 with two empty
    strings and "NA", and reading it as a document that would not parse took a whole stage
    down on 2026-08-27.
    """
    directory = argv[0] if argv else os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "fixtures", "captured")

    absent_words = {"na", "n/a", "none", "null", "-"}

    def text(value):
        if not isinstance(value, str):
            return None
        return value if value.strip() else None

    def number(value):
        if value is None:
            return None
        if isinstance(value, bool):
            raise ValueError("a boolean where a number was expected")
        if isinstance(value, (int, float)):
            return Decimal(str(value))
        if isinstance(value, str):
            if not value.strip() or value.strip().lower() in absent_words:
                return None
            return Decimal(value)
        raise ValueError(f"{value!r} where a number was expected")

    files = sorted(
        f for f in os.listdir(directory)
        if f.startswith("fundamentals-") and f.endswith(".json"))

    if not files:
        print(f"no captured fundamentals responses under {directory}")
        return 1

    rows = []

    for name in files:
        ticker = name[len("fundamentals-"):-len(".json")]

        with open(os.path.join(directory, name), encoding="utf-8") as handle:
            body = json.load(handle)

        sector = text(body.get("General::Sector"))
        industry = text(body.get("General::Industry"))
        cap = number(body.get("Highlights::MarketCapitalization"))

        rows.append((ticker, sector, industry, cap))

    print(f"{len(rows)} captured fundamentals response(s) under {directory}")
    print()

    absent = 0
    for ticker, sector, industry, cap in rows:
        if sector is None and industry is None and cap is None:
            absent += 1
            print(f"  {ticker:<8} the vendor holds nothing on this name")
            continue

        print(f"  {ticker:<8} {sector or '-':<24} {industry or '-':<34} "
              f"{cap if cap is not None else '-'}")

    print()
    print(f"{absent} of {len(rows)} are names the vendor holds nothing on.")
    print()
    print("As expectations, DERIVED:")
    print()

    for ticker, sector, industry, cap in rows:
        if sector is None and industry is None and cap is None:
            print(f'  fundamentals.{ticker}.held = "no"')
            continue

        print(f'  fundamentals.{ticker}.held = "yes"')
        print(f'  fundamentals.{ticker}.sector = "{sector}"')
        print(f'  fundamentals.{ticker}.industry = "{industry}"')
        print(f'  fundamentals.{ticker}.marketCap = "{cap}"')

    return 0


def main(argv):
    if len(argv) > 1 and argv[1] == "--dispersion":
        return dispersion_main(argv[2:] or [''])

    if len(argv) > 1 and argv[1] == "--accumulation":
        return accumulation_main(argv[2:] or [''])

    if len(argv) > 1 and argv[1] == "--interval":
        return interval_main(argv[2:] or [''])

    if len(argv) > 1 and argv[1] == "--ceiling":
        return ceiling_main(argv[2:] or [''])

    if len(argv) > 1 and argv[1] == "--controls":
        return controls_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--forward":
        return forward_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--journal":
        return journal_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--thrust":
        return thrust_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--geometry":
        return geometry_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--calibration":
        return calibration_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--point-in-time":
        return pit_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--cap":
        return cap_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--gates":
        return gates_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--checks":
        return checks_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--fundamentals":
        return fundamentals_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--regime":
        return regime_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--ladder":
        return ladder_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--scans":
        return scans_main(argv[2:])

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
