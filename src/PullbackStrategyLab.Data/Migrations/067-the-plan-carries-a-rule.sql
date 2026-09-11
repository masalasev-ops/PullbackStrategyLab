-- 067  the plan carries a rule, and the entry minute resolves the prices
--
-- <b>A plan written from 7.8 names the rule rather than the prices.</b> The entry the strategy states
-- is a flush into the hourly averages and a break of the previous bar's extreme, so the price, the stop
-- and the size are known at the minute it happens and not the evening before. `trade_plan` is rebuilt
-- so its four prices, its size and its risk at stake may be absent, beside the rule it carries and the
-- ceiling the stop may not exceed, which is known at 18:30 because it is half the daily range capped at
-- 5%. A plan written before 7.8 carried the evening's prices and says so, and every row that exists is
-- copied as one: nothing is reinterpreted.
-- see: Order prices and the share count resolve at the entry minute
--
-- <b>The resolution is a table of its own, keyed on the plan, and the plan row is never touched.</b>
-- A plan is immutable after its session, and the audit rests on that, so the figures the entry minute
-- produces go into `entry_resolution` on `trigger_resolution`'s precedent: one row per plan that
-- triggered, with the entry, the stop and the size, or with why the stop refused it.
-- see: The plan is written before the session and is immutable after publication
--
-- `legacy_alter_table` is on for the reason 051 gives: six tables name `trade_plan` in a foreign key.

PRAGMA legacy_alter_table = ON;

ALTER TABLE trade_plan RENAME TO trade_plan_before_067;

CREATE TABLE trade_plan (
    plan_id           TEXT    NOT NULL PRIMARY KEY,
    setup_id          TEXT    NOT NULL,
    variant_id        TEXT    NOT NULL,
    as_of             TEXT    NOT NULL,
    live_session      TEXT    NOT NULL,
    ticker            TEXT    NOT NULL,
    direction         TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- Which rule the plan carries. The evening's prices until 7.8, the flush and reclaim from it.
    entry_rule        TEXT    NOT NULL DEFAULT 'evening-prices'
        CHECK (entry_rule IN ('evening-prices', 'flush-reclaim')),

    trigger_price     TEXT    NULL,
    give_up_price     TEXT    NULL,
    give_up_distance  TEXT    NULL,
    shares            INTEGER NULL CHECK (shares IS NULL OR shares > 0),

    -- The widest stop the entry may take, as a fraction of the entry price: the tighter of half the
    -- daily range and 5%. Present exactly on a plan that carries the rule.
    stop_ceiling      TEXT    NULL,

    equity            TEXT    NOT NULL,
    risk_fraction     TEXT    NOT NULL,
    risk_budget       TEXT    NOT NULL,
    risk_at_stake     TEXT    NULL,
    observed_at       TEXT    NOT NULL,

    UNIQUE (setup_id, variant_id),

    -- An evening's plan carries all five figures and a rule plan carries none, one clause a column so a
    -- row that fails says which is the odd one.
    CHECK ((entry_rule = 'evening-prices') = (trigger_price IS NOT NULL)),
    CHECK ((trigger_price IS NULL) = (give_up_price IS NULL)),
    CHECK ((trigger_price IS NULL) = (give_up_distance IS NULL)),
    CHECK ((trigger_price IS NULL) = (shares IS NULL)),
    CHECK ((trigger_price IS NULL) = (risk_at_stake IS NULL)),
    CHECK ((entry_rule = 'flush-reclaim') = (stop_ceiling IS NOT NULL)),

    FOREIGN KEY (setup_id) REFERENCES setup (setup_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO trade_plan (
    plan_id, setup_id, variant_id, as_of, live_session, ticker, direction, entry_rule,
    trigger_price, give_up_price, give_up_distance, shares, stop_ceiling,
    equity, risk_fraction, risk_budget, risk_at_stake, observed_at)
SELECT plan_id, setup_id, variant_id, as_of, live_session, ticker, direction, 'evening-prices',
       trigger_price, give_up_price, give_up_distance, shares, NULL,
       equity, risk_fraction, risk_budget, risk_at_stake, observed_at
  FROM trade_plan_before_067;

DROP TABLE trade_plan_before_067;

CREATE INDEX ix_trade_plan_live ON trade_plan (live_session);
CREATE INDEX ix_trade_plan_as_of ON trade_plan (as_of);
CREATE INDEX ix_trade_plan_setup ON trade_plan (setup_id);

PRAGMA legacy_alter_table = OFF;

-- What the entry minute made of one plan that triggered: the entry, the stop, and the size.
--
-- <b>Everything here is as of the entry minute and nothing later.</b> The session's extreme is the
-- extreme through the entry minute, so a low printed after the order filled cannot set the stop.
--
-- <b>A refusal is a row, not an absence.</b> The chase filter, the ceiling and a stop the entry candle
-- cannot give are refusals of the entry rather than caps, so no order is written for them and the row
-- says why, on the terms a blocked order carries its reason.
CREATE TABLE entry_resolution (
    plan_id          TEXT    NOT NULL PRIMARY KEY,
    setup_id         TEXT    NOT NULL,
    variant_id       TEXT    NOT NULL,
    live_session     TEXT    NOT NULL,
    ticker           TEXT    NOT NULL,
    direction        TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    entry_minute     TEXT    NOT NULL,
    candle_minutes   INTEGER NOT NULL CHECK (candle_minutes IN (1, 5, 15)),
    level            TEXT    NOT NULL CHECK (level IN ('hourly-ema-9', 'hourly-ema-21')),
    level_value      TEXT    NOT NULL,
    armed_at         TEXT    NOT NULL,
    entry_price      TEXT    NOT NULL,
    session_extreme  TEXT    NOT NULL,
    candle_extreme   TEXT    NOT NULL,
    stop_ceiling     TEXT    NOT NULL,

    stop_basis       TEXT    NULL CHECK (stop_basis IS NULL OR stop_basis IN ('session-extreme', 'entry-candle')),
    stop_price       TEXT    NULL,
    stop_distance    TEXT    NULL,
    stop_fraction    TEXT    NULL,
    shares           INTEGER NULL CHECK (shares IS NULL OR shares > 0),
    risk_budget      TEXT    NOT NULL,
    risk_at_stake    TEXT    NULL,
    refused_because  TEXT    NULL,
    observed_at      TEXT    NOT NULL,

    -- An entry either has a size or a reason it has none, never both and never neither.
    CHECK ((refused_because IS NULL) = (shares IS NOT NULL)),
    CHECK ((stop_price IS NULL) = (stop_basis IS NULL)),
    CHECK ((stop_price IS NULL) = (stop_distance IS NULL)),
    CHECK ((stop_price IS NULL) = (stop_fraction IS NULL)),
    CHECK ((shares IS NULL) = (risk_at_stake IS NULL)),
    CHECK (shares IS NULL OR stop_price IS NOT NULL),

    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_entry_resolution_session ON entry_resolution (live_session);

-- The gate's two new counts: the entries the stop rule refused before a cap was asked, and the orders
-- the risk per trade reduced now that it is enforced rather than asserted.
ALTER TABLE order_run ADD COLUMN refused_at_entry INTEGER NOT NULL DEFAULT 0;
ALTER TABLE order_run ADD COLUMN reduced_risk_per_trade INTEGER NOT NULL DEFAULT 0;
