-- 005  corporate_action and indicator_rebuild, rekeyed on the action as observed
--
-- 004 keyed an action on ticker, effective date and type, and said the first observation of
-- an event stands. That was wrong, and wrong in a way nothing could see. Vendors restate
-- corporate actions. Under 004 a restated ratio could not be stored at all, so a ticker
-- rebuilt against a factor that later changed stayed rebuilt, permanently, with the record
-- showing a satisfied demand and the wrong number computed from it.
--
-- Bars already solve this and the discipline is copied here without variation: append-only,
-- keyed with observed_at, reads take the latest observation at or before the as-of date. A
-- restatement is a new row rather than an edit, so the store still says what the lab believed
-- on any given night.
--
-- The demand follows the action rather than the ticker. Its key is the action's key, so a
-- restated ratio raises a NEW demand instead of failing to reopen an old one. Nothing is
-- mutated and nothing is cleared: a demand gains a rebuilt_at and stays.
--
-- Row survival is asserted by a test either side of this migration, because a rebuild that
-- silently drops rows is the failure mode of hand-written table rebuilds.
--
-- The demand declares no foreign key to the action, though its key is the action's key.
-- SQLite rewrites a child's foreign key clause when the parent is renamed, and both tables
-- are rebuilt here, so declaring one would make each rebuild depend on the order of the
-- other. A test asserts every demand joins to an action instead, which is the property the
-- constraint would have bought.

-- The demand goes first. It is rebuilt from its own rows joined to the actions that raised
-- them, and every demand 004 could have written came from a split, which is what the join
-- asserts by returning the same number of rows it started with.
CREATE TABLE indicator_rebuild_rekeyed (
    ticker         TEXT NOT NULL REFERENCES security (ticker),
    effective_date TEXT NOT NULL,
    type           TEXT NOT NULL CHECK (type IN ('split', 'dividend')),
    observed_at    TEXT NOT NULL,
    rebuilt_at     TEXT NULL,
    PRIMARY KEY (ticker, effective_date, type, observed_at)
);

INSERT INTO indicator_rebuild_rekeyed (ticker, effective_date, type, observed_at, rebuilt_at)
SELECT r.ticker, r.effective_date, a.type, a.observed_at, r.rebuilt_at
  FROM indicator_rebuild r
  JOIN corporate_action a
    ON a.ticker = r.ticker
   AND a.effective_date = r.effective_date
   AND a.type = 'split';

DROP TABLE indicator_rebuild;
ALTER TABLE indicator_rebuild_rekeyed RENAME TO indicator_rebuild;

-- The read that gates every calculation: which demands are outstanding. Partial, because the
-- answer is almost always the empty set and the index should cost nothing to carry.
CREATE INDEX ix_indicator_rebuild_open ON indicator_rebuild (ticker) WHERE rebuilt_at IS NULL;

-- Then the action itself, which is now a measurement rather than an event: one row per
-- observation, and a later observation of the same action is a correction rather than an edit.
CREATE TABLE corporate_action_observed (
    ticker         TEXT NOT NULL REFERENCES security (ticker),
    effective_date TEXT NOT NULL,
    type           TEXT NOT NULL CHECK (type IN ('split', 'dividend')),
    ratio          TEXT NOT NULL,
    observed_at    TEXT NOT NULL,
    PRIMARY KEY (ticker, effective_date, type, observed_at)
);

INSERT INTO corporate_action_observed (ticker, effective_date, type, ratio, observed_at)
SELECT ticker, effective_date, type, ratio, observed_at FROM corporate_action;

DROP TABLE corporate_action;
ALTER TABLE corporate_action_observed RENAME TO corporate_action;

-- The nightly read is one date across the whole market, which the primary key cannot serve
-- because it leads on ticker.
CREATE INDEX ix_corporate_action_effective_date ON corporate_action (effective_date);
