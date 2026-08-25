-- 010  index_bar
--
-- SPY, QQQ and IWM, on the same terms as daily_bar: append-only, never deleted, never updated,
-- a correction arriving as a new row with a later observed_at and reads taking the latest
-- observation at or before the as-of date.
--
-- No foreign key to security. An index tracker is not part of the tradable universe and never
-- appears in a screen; it is read to say what the market did, which is a different question
-- from what any one stock did.

CREATE TABLE index_bar (
    symbol      TEXT    NOT NULL,
    bar_date    TEXT    NOT NULL,
    open        TEXT    NOT NULL,
    high        TEXT    NOT NULL,
    low         TEXT    NOT NULL,
    close       TEXT    NOT NULL,
    adj_close   TEXT    NOT NULL,
    volume      INTEGER NOT NULL,
    observed_at TEXT    NOT NULL,
    PRIMARY KEY (symbol, bar_date, observed_at)
);

-- The read RegimeLabeler will make is one symbol over a window, which the primary key already
-- orders on. The date index is for the other direction: all three symbols on one night.
CREATE INDEX ix_index_bar_bar_date ON index_bar (bar_date);
