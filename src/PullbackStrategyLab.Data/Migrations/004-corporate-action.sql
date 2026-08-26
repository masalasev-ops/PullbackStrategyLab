-- 004  corporate_action, indicator_rebuild
--
-- A split is the one event that corrupts every moving average a stock has at once, and it
-- does it silently: after a four-for-one, the adjusted closes already stored are on one
-- scale and every adjusted close arriving from tonight is on another, so an average taken
-- across the boundary is arithmetic on two different units and looks entirely plausible.
--
-- indicator_rebuild is where that demand is recorded rather than acted on. ActionIngestor
-- writes a row when a split lands; IndicatorEngine stamps rebuilt_at when it has recomputed
-- that ticker from scratch. A row still carrying a NULL rebuilt_at is a stock whose
-- calculations must refuse to run, which is the failure behaviour the architecture states
-- for an unprocessed split.
--
-- The demand is a record, not a queue that empties. The row stays with its date stamped, so
-- the question "which splits has this store honoured, and when" has an answer months later.

CREATE TABLE corporate_action (
    ticker         TEXT NOT NULL REFERENCES security (ticker),
    effective_date TEXT NOT NULL,
    type           TEXT NOT NULL CHECK (type IN ('split', 'dividend')),
    ratio          TEXT NOT NULL,
    observed_at    TEXT NOT NULL,
    PRIMARY KEY (ticker, effective_date, type)
);

-- The nightly read is one date across the whole market, which the primary key cannot serve
-- because it leads on ticker.
CREATE INDEX ix_corporate_action_effective_date ON corporate_action (effective_date);

CREATE TABLE indicator_rebuild (
    ticker         TEXT NOT NULL REFERENCES security (ticker),
    effective_date TEXT NOT NULL,
    requested_at   TEXT NOT NULL,
    rebuilt_at     TEXT NULL,
    PRIMARY KEY (ticker, effective_date)
);

-- The read that gates every calculation: which tickers are outstanding. Partial, because
-- the answer is almost always the empty set and the index should cost nothing to carry.
CREATE INDEX ix_indicator_rebuild_pending ON indicator_rebuild (ticker) WHERE rebuilt_at IS NULL;
