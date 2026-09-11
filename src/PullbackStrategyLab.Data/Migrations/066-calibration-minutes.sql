-- 066  the calibration minutes: bars, the windows that bought them, and the rows left short
--
-- <b>A research store beside the live capture table and never inside it.</b> `intraday_bar` holds the
-- minutes the nightly fetch buys for the forward nights, and that population is what the execution
-- family's reopening condition counts: capture running, one night a night. A minute bought once for a
-- calibration row of 2024 sitting in the same table would be counted as a night the lab captured, and
-- nothing about a bar's shape says which of the two it came from. So the backfilled minutes are a table
-- of their own, on the same grounds `calibration_setup` is a table rather than a flag.
-- see: A one-time backfill is outside the nightly ceiling, whether it buys daily history or minutes
--
-- `calibration_minute_bar` has `intraday_bar`'s shape less the session average, which nothing computes
-- over history, and no foreign key to `security`, on `calibration_setup`'s own reasoning: a historical
-- walk reaches names the nightly universe no longer lists. It is a bar table and append-only like the
-- other three.
--
-- `calibration_minute_window` is one row per request the backfill made, which is what makes a run that
-- was stopped part way resume rather than pay twice, and what the fetched counts reconcile against.
--
-- `calibration_minute_shortfall` names every flagged calibration row whose window leaves it fewer
-- warm-up sessions than the hourly 21 needs, with how many it has. A population rather than a flag on
-- the row, and the rows it names are the ones the rule's own deduplication leaves short, reported
-- rather than left to be discovered as an average that never converged.

CREATE TABLE calibration_minute_bar (
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
    observed_at     TEXT    NOT NULL,
    PRIMARY KEY (ticker, bar_ts, observed_at)
);

CREATE INDEX ix_calibration_minute_bar_session ON calibration_minute_bar (ticker, session_date, bar_ts);

CREATE TABLE calibration_minute_window (
    ticker             TEXT    NOT NULL,
    window_from        TEXT    NOT NULL,
    window_to          TEXT    NOT NULL,
    rows_served        INTEGER NOT NULL CHECK (rows_served > 0),
    bars_returned      INTEGER NOT NULL CHECK (bars_returned >= 0),
    bars_written       INTEGER NOT NULL CHECK (bars_written >= 0 AND bars_written <= bars_returned),
    sessions_answered  INTEGER NOT NULL CHECK (sessions_answered >= 0),
    calls_used         INTEGER NOT NULL CHECK (calls_used >= 0),
    observed_at        TEXT    NOT NULL,
    PRIMARY KEY (ticker, window_to, observed_at),
    CHECK (window_from < window_to)
);

CREATE TABLE calibration_minute_shortfall (
    setup_id         TEXT    NOT NULL,
    ticker           TEXT    NOT NULL,
    entry_session    TEXT    NOT NULL,
    window_from      TEXT    NOT NULL,
    warmup_sessions  INTEGER NOT NULL CHECK (warmup_sessions >= 0),
    warmup_wanted    INTEGER NOT NULL CHECK (warmup_wanted > warmup_sessions),
    observed_at      TEXT    NOT NULL,
    PRIMARY KEY (setup_id, observed_at),
    FOREIGN KEY (setup_id) REFERENCES calibration_setup (setup_id)
);
