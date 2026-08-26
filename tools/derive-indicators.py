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
        extreme_index = max(range(thrust_index, len(adj)), key=lambda i: adj[i]["high"])
        extreme = adj[extreme_index]["high"]
        origin = adj[thrust_index - 1]["close"] if thrust_index > 0 else adj[thrust_index]["close"]
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
        extreme_index = min(range(thrust_index, len(adj)), key=lambda i: adj[i]["low"])
        extreme = adj[extreme_index]["low"]
        origin = adj[thrust_index - 1]["close"] if thrust_index > 0 else adj[thrust_index]["close"]
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


def main(argv):
    if len(argv) > 1 and argv[1] == "--gates":
        return gates_main(argv[2:])

    if len(argv) > 1 and argv[1] == "--checks":
        return checks_main(argv[2:])

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
