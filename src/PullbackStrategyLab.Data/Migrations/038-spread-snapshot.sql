-- 038  spread_snapshot, spread_pass
--
-- The bid-ask spread twice a session, which is the second of the lab's two unrecoverable inputs
-- and the one that decides what an entry costs.
-- see: Spread is captured intraday from day one
--
-- <b>The reader this is captured for is entry slippage at 4.7.</b> Named here rather than left to
-- be inferred, because until 4.3 this store had no reader anywhere in the solution and a capture
-- spending 120 unrecoverable calls a session on an input nothing consumes is a capture nobody can
-- justify. The fraction charged, and whether it is symmetric between directions, are 4.7's.
--
-- Prices are TEXT holding a decimal, never REAL. `spread_bps` is REAL because it is a statistic
-- rather than money, on the rule that prices are decimal and statistics are double.
--
-- <b>Every price column is nullable, and that is the point of the table rather than a concession.</b>
-- A quote the vendor did not carry, a name it answered with one side, and a name it omitted from
-- the batch are three different facts, and a schema that forced a number would have flattened all
-- three into whatever the writer chose. `absent_because` says which, and a reader that finds a null
-- bid learns why in the same row.
--
-- <b>Three stamps, because the vendor stamps each side separately.</b> `snapshot_ts` is when the lab
-- asked; `bid_ts` and `ask_ts` are what the vendor stamped each side at, and they differ: measured
-- on the capture of 2026-09-01, AAPL's bid and ask were 32 seconds apart. A spread computed across
-- two instants is not a spread at one instant, and the only way that fact survives into the store is
-- to keep both stamps. The feed is delayed by design, so the lag is recorded rather than corrected
-- for: `quote_lag_seconds` is measured from the older of the two sides, because a spread is only as
-- fresh as its stalest half, and 4.7 can bound on it or exclude a stale row instead of assuming a
-- lag the design happened to expect.
-- see: A delayed quote records its own lag rather than being corrected for it
--
-- <b>`spread_bps` is stored rather than derived by each reader.</b> It rests on a choice of
-- denominator, and a reader taking the mid while another takes the last trade would produce two
-- figures with one name. Computed once by the writer, from the mid of the two quoted sides, and null
-- wherever either side is.
--
-- <b>Append-only, with the observation in the key</b>, on the same grounds as the bar tables. A pass
-- rerun for the same session takes a genuinely different quote, because the market moved between
-- them, so it is a second observation rather than a correction of the first.

CREATE TABLE spread_snapshot (
    ticker            TEXT    NOT NULL,
    session_date      TEXT    NOT NULL,
    setup_as_of       TEXT    NOT NULL,
    pass              TEXT    NOT NULL CHECK (pass IN ('after_open', 'before_close')),
    snapshot_ts       TEXT    NOT NULL,
    bid               TEXT    NULL,
    ask               TEXT    NULL,
    bid_size          INTEGER NULL,
    ask_size          INTEGER NULL,
    bid_ts            TEXT    NULL,
    ask_ts            TEXT    NULL,
    last_trade        TEXT    NULL,
    last_trade_ts     TEXT    NULL,
    spread_bps        REAL    NULL,
    quote_lag_seconds INTEGER NULL,
    absent_because    TEXT    NULL,
    observed_at       TEXT    NOT NULL,
    PRIMARY KEY (ticker, session_date, pass, observed_at),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

-- What a reader asks: one name's spreads for one session. The session is in the key ahead of the
-- pass because every read is bounded by a session and no reader wants one pass without knowing
-- whether the other exists.
CREATE INDEX ix_spread_snapshot_session ON spread_snapshot (session_date, ticker);

-- What one pass did, written whatever the outcome, on the same footing as `intraday_fetch`.
--
-- <b>A session with no row here is a session nobody sampled</b>, and that is the whole of how the
-- missed snapshot is detected. The stage cannot record a run that never happened, so absence is the
-- only available signal and it is only readable because a pass that runs always writes. One row is a
-- session sampled once and is degraded; two is the design; none is a hole that no later call can
-- fill.
CREATE TABLE spread_pass (
    session_date    TEXT    NOT NULL,
    setup_as_of     TEXT    NOT NULL,
    pass            TEXT    NOT NULL CHECK (pass IN ('after_open', 'before_close')),
    requested       INTEGER NOT NULL,
    answered        INTEGER NOT NULL,
    quoted          INTEGER NOT NULL,
    unquoted        INTEGER NOT NULL,
    rows_written    INTEGER NOT NULL,
    outcome         TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (session_date, pass, observed_at)
);

CREATE INDEX ix_spread_pass_session ON spread_pass (session_date);
