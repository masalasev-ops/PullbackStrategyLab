-- 037  intraday_bar
--
-- Minute bars for every flagged setup, which is the only unrecoverable input to the session loop
-- phase 4 builds: a minute bar not captured on its own evening cannot be bought later, where a
-- page or a check can be built any evening.
-- see: Minute bars are fetched for every flagged setup, not only the planned ones
--
-- Prices are TEXT holding a decimal, never REAL. Volume is INTEGER because it is a count.
--
-- <b>Three columns describe the bars rather than the prices, and each is here because it moves an
-- answer.</b> `interval_code` says what one row spans, and a five-minute bar mixed into a minute
-- series gives a high that never traded at that minute. `session_window` says whether the series
-- includes pre-market and after-hours trading, which decides the day's high and low, decides
-- whether the open is a gap from the previous close or the first print of an extended session, and
-- moves any average computed across the day. `price_basis` says whether the prices are raw or
-- adjusted, which is the same distinction the geometry already draws between the shape of a move
-- and the two prices that trade. Stored per row rather than declared once in a document, because a
-- capture whose basis changed at the vendor would otherwise be indistinguishable from one that did
-- not, and the rows already in the store would silently become a mixed population.
--
-- <b>`session_date` is a column and not a derivation.</b> A bar's session could be computed from
-- `bar_ts` through the clock, and every reader would then be repeating that computation and could
-- disagree with the writer about a bar either side of a boundary. It is also the column the
-- point-in-time assertion is made against: a stored bar's session must be provably later than the
-- session its setup was flagged on, and an assertion resting on a value the reader recomputes is an
-- assertion about the reader.
--
-- <b>`vwap_session` is null here and is the one declared update against a bar table.</b> SCHEMA
-- declares Insert IntradayFetcher and Update VwapEngine on this column only. It is written at 4.4
-- and is a locally computed annotation rather than anything the vendor sent, so it does not make a
-- bar mutable in the sense the rule is about: a vendor correction still arrives as a new row with a
-- later observed_at and nothing rewrites a price. `bar-append-only` carries that exception by name
-- and by column rather than being widened to allow any update against this table.
--
-- <b>Append-only, with observed_at in the key</b>, which is the idiom `daily_bar` and `index_bar`
-- already set. A vendor that republishes a corrected minute writes a second row rather than
-- replacing the first, and the reader takes the latest observation at or before its as-of.

CREATE TABLE intraday_bar (
    ticker          TEXT    NOT NULL,
    bar_ts          TEXT    NOT NULL,
    session_date    TEXT    NOT NULL,
    interval_code   TEXT    NOT NULL CHECK (interval_code IN ('1m')),
    session_window  TEXT    NOT NULL CHECK (session_window IN ('regular', 'extended')),
    price_basis     TEXT    NOT NULL CHECK (price_basis IN ('raw', 'adjusted')),
    open            TEXT    NOT NULL,
    high            TEXT    NOT NULL,
    low             TEXT    NOT NULL,
    close           TEXT    NOT NULL,
    volume          INTEGER NOT NULL,
    vwap_session    TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (ticker, bar_ts, observed_at),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

-- What the replay walks: one name's minutes for one session, in order. The composite is on the
-- session rather than on the timestamp alone because every read is bounded by a session, and a
-- session bound over a timestamp index is a range scan where this is a seek.
CREATE INDEX ix_intraday_bar_session ON intraday_bar (ticker, session_date, bar_ts);

-- What one night's fetch records about itself: which session it was for, which setups it served,
-- and what it did not get. A partial fetch is a real outcome rather than a failure, and it has to
-- be distinguishable afterwards from a night nobody ran.
--
-- Grained on the session the bars are for, not the evening the fetch ran on. The stage runs on the
-- evening of session N and stores session N's bars for the setups flagged on the evening of N-1,
-- so the evening is derivable from the session and the session is what anything reads by.
CREATE TABLE intraday_fetch (
    session_date    TEXT    NOT NULL,
    setup_as_of     TEXT    NOT NULL,
    requested       INTEGER NOT NULL,
    fetched         INTEGER NOT NULL,
    empty           INTEGER NOT NULL,
    bars_written    INTEGER NOT NULL,
    outcome         TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_intraday_fetch_session ON intraday_fetch (session_date);
