-- 003  daily_bar
--
-- Append-only. Never deleted, never updated. A vendor correction arrives as a new row with
-- a later observed_at, and reads take the latest observed_at at or before the as-of date.
-- That is what makes a replay reproducible: the same as-of date gives the same bars today
-- and in six months, including the wrong ones the lab actually saw on the night.
--
-- Prices are TEXT holding a decimal, never REAL. open, high, low and close are raw, as
-- traded; adj_close is split and dividend adjusted. Averages and ranges compute on the
-- adjusted price and trigger and stop prices are raw, and mixing them produces a plan that
-- says buy at 37.67 when the real price is 150.68, silently, because both look reasonable.

CREATE TABLE daily_bar (
    ticker      TEXT    NOT NULL REFERENCES security (ticker),
    bar_date    TEXT    NOT NULL,
    open        TEXT    NOT NULL,
    high        TEXT    NOT NULL,
    low         TEXT    NOT NULL,
    close       TEXT    NOT NULL,
    adj_close   TEXT    NOT NULL,
    volume      INTEGER NOT NULL,
    observed_at TEXT    NOT NULL,
    PRIMARY KEY (ticker, bar_date, observed_at)
);

-- The read every stage makes: one ticker, a window of dates, latest observation first.
-- The primary key already orders on exactly that, so nothing else is indexed here until a
-- measurement says otherwise.
CREATE INDEX ix_daily_bar_bar_date ON daily_bar (bar_date);
