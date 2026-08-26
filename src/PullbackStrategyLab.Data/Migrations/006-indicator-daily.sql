-- 006  indicator_daily
--
-- Computed locally from daily_bar and never requested from the vendor. Asking the provider to
-- calculate the same averages would cost about 45,000 calls a day for arithmetic that is one
-- recursive loop over data already stored.
--
-- One row per ticker per session, written only when the ticker has enough history to have
-- converged and no corporate action outstanding against it. A missing row is therefore
-- meaningful: it says either that the window was too short or that calculations refused to run,
-- and both are better than a number.
--
-- Prices are TEXT holding a decimal. adr_20 is a fraction, so 0.068 rather than 6.8, which is
-- the one column in the schema whose name argues against the convention.

CREATE TABLE indicator_daily (
    ticker                  TEXT NOT NULL REFERENCES security (ticker),
    as_of                   TEXT NOT NULL,
    ema_9                   TEXT NOT NULL,
    ema_21                  TEXT NOT NULL,
    ema_50                  TEXT NOT NULL,
    atr_14                  TEXT NOT NULL,
    adr_20                  TEXT NOT NULL,
    dollar_volume_median_20 TEXT NOT NULL,
    range_avg_20            TEXT NOT NULL,
    ladder_grade            TEXT NULL,
    PRIMARY KEY (ticker, as_of)
);

-- The nightly read is one date across the universe, which the primary key cannot serve because
-- it leads on ticker.
CREATE INDEX ix_indicator_daily_as_of ON indicator_daily (as_of);
