-- 009  indicator_daily, rekeyed on the computation as made
--
-- Bars, corporate actions and rebuild demands are all append-only with an observation column
-- and latest-at-or-before on read. indicator_daily was the one computed store that was not,
-- and that is exactly why a rebuild could not reach the rows it invalidates: an indicator row
-- written before an action was honoured stood for ever, because the engine could insert and
-- nothing could replace it.
--
-- So a computation is an observation like everything else. A rebuild landing on a past date
-- writes a second row for that date under a later computed_at, a read as of a date before the
-- rebuild returns what the lab had then, and a read after it returns what it has now.
--
-- ladder_grade comes with it. TierClassifier no longer updates a row somebody else owns; it
-- writes a later observation of its own carrying the grade, which is why SCHEMA now declares
-- two inserters and states how they are disjoint rather than the check carrying an exception.
--
-- Storage is a fraction of the store. A rebuild is rare and the ordinary night writes one row
-- per name per session either way.

CREATE TABLE indicator_daily_computed (
    ticker                  TEXT NOT NULL REFERENCES security (ticker),
    as_of                   TEXT NOT NULL,
    computed_at             TEXT NOT NULL,
    ema_9                   TEXT NOT NULL,
    ema_21                  TEXT NOT NULL,
    ema_50                  TEXT NOT NULL,
    atr_14                  TEXT NOT NULL,
    adr_20                  TEXT NOT NULL,
    dollar_volume_median_20 TEXT NOT NULL,
    range_avg_20            TEXT NOT NULL,
    ladder_grade            TEXT NULL,
    PRIMARY KEY (ticker, as_of, computed_at)
);

-- Every row already written was computed by the run that wrote it, and the store has no record
-- of when that was beyond the run log. The first instant of its own session is the reading that
-- cannot do harm: a read as of that session still returns it, which is the property that has to
-- survive, and any real computation made later that evening supersedes it. Stamping the end of
-- the session instead would keep these rows in front of every recomputation made the same
-- evening, which is exactly the wrongness this table was rekeyed to remove.
INSERT INTO indicator_daily_computed
    (ticker, as_of, computed_at, ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20, ladder_grade)
SELECT ticker, as_of, as_of || 'T00:00:00.000Z',
       ema_9, ema_21, ema_50, atr_14, adr_20, dollar_volume_median_20, range_avg_20, ladder_grade
  FROM indicator_daily;

DROP TABLE indicator_daily;
ALTER TABLE indicator_daily_computed RENAME TO indicator_daily;

-- The nightly read is one date across the universe, which the primary key cannot serve because
-- it leads on ticker.
CREATE INDEX ix_indicator_daily_as_of ON indicator_daily (as_of);
