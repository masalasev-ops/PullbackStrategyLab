-- 051  the variant store, and the fan-out of a plan per live version
--
-- <b>Two things, and they are one migration because the second has no meaning without the first.</b>
-- `variant` is the register of rule versions: the baseline and, later, whatever proposals are
-- admitted against it. The fan-out is what makes a plan belong to a version rather than to the lab.
--
-- <b>The key below the plan changes from the setup to the plan, and that is the whole design.</b>
-- Until now one capped candidate produced one plan, so the setup's own identifier served as the
-- plan's, and `trigger_resolution`, `trade_order`, `position`, `trade` and `plan_audit` each carried
-- `setup_id` with a uniqueness constraint on it. Two versions selecting the same stock produce two
-- plans, and every one of those constraints would refuse the second: the night would silently lose
-- one version's order, which is the failure ARCHITECTURE's "Two variants pick the same stock" row
-- exists to describe. Giving `trade_plan` its own `plan_id` and pointing everything below at that
-- makes the fan-out fall out of the key rather than out of a stage remembering to allow it, and it
-- does so without a composite key anywhere.
--
-- <b>`setup_id` stays on every row it was on.</b> It is carried rather than keyed, so a row still
-- reads without a join and every existing read that groups a night by setup keeps working.
--
-- <b>It moves no row in any store that exists.</b> `trade_plan` and everything below it are empty
-- everywhere: the funnel has passed a median of nought candidates a night since it was built and no
-- trade has ever fired. The carrying statements are written anyway, because a migration that is
-- correct only against an empty store is one nobody can run twice, and each defaults `variant_id`
-- to the baseline. A store that did hold rows would fail on the foreign key rather than quietly
-- attributing them to a version that was not running, which is the right way round.
-- see: An approved proposal creates a new version from zero, and a running version is never edited
-- see: Targets and minimum samples are written at creation and are immutable

-- <b>`legacy_alter_table` is on for the whole of this file, and it is load-bearing.</b> By default
-- SQLite's `ALTER TABLE ... RENAME TO` rewrites the name everywhere it appears, including inside
-- other tables' foreign-key clauses. Seven tables here reference each other, so renaming `trade` out
-- of the way silently repointed `plan_audit`'s foreign key at `trade_before_051`, and dropping the
-- transient then left `plan_audit` naming a table that does not exist: every later statement against
-- it failed to prepare with "no such table". The rename-out-of-the-way rebuild this corpus uses
-- assumes a rename is only a rename, and this is the setting that makes that true. Unlike
-- `foreign_keys`, it takes effect inside a transaction, so it can live in the file with the
-- statements it governs rather than in the runner.
PRAGMA legacy_alter_table = ON;

-- The register of rule versions.
--
-- <b>The pre-registration columns are written once and the store is what enforces it.</b>
-- VariantAdmitter inserts; AcceptanceGate updates `status` and `resolved_at` and nothing else. The
-- target and the minimum sample cannot be revised by any path, because a target that can move after
-- the result is not a target.
--
-- <b>`minimum_sample_unit` exists because the two families count different things.</b> A selection
-- version's minimum is effective paired setup observations, discounted for overlap and the shared
-- market factor; an execution version's is paired trades, a row count, because the trade-level
-- design effect cannot be measured until a trade exists. One integer column with no unit beside it
-- would make those two figures look comparable, and they are not.
-- see: The execution minimum is 200 paired trades and its conversion waits on a trade existing
CREATE TABLE variant (
    variant_id           TEXT    NOT NULL PRIMARY KEY,

    -- The generation this version belongs to. Editing the baseline closes every open version as
    -- unresolved and starts a new one, so a version is only ever comparable to versions of its own.
    generation           INTEGER NOT NULL CHECK (generation >= 0),

    family               TEXT    NOT NULL CHECK (family IN ('baseline', 'selection', 'execution')),
    definition           TEXT    NOT NULL,

    -- Pre-registration. Written by VariantAdmitter at creation and never again.
    target               TEXT    NOT NULL,
    minimum_sample       INTEGER NOT NULL CHECK (minimum_sample > 0),
    minimum_sample_unit  TEXT    NOT NULL
        CHECK (minimum_sample_unit IN ('effective_paired_setup_observations', 'paired_trades')),

    -- AcceptanceGate's two columns, and its only two.
    status               TEXT    NOT NULL
        CHECK (status IN ('open', 'accepted', 'rejected', 'unresolved')),
    resolved_at          TEXT    NULL,

    created_at           TEXT    NOT NULL,

    -- An open version has not been settled and a settled one says when. Either without the other is
    -- a row that cannot be read as either.
    CHECK ((status = 'open') = (resolved_at IS NULL)),

    -- The baseline's minimum is in setup observations and an execution version's is in trades. A
    -- selection version is scored on forward return like the baseline, so it shares the unit.
    CHECK ((family = 'execution') = (minimum_sample_unit = 'paired_trades'))
);

-- One baseline per generation, and the index rather than a stage is what says so. A second baseline
-- would leave every difference series with two things it could be measured against.
CREATE UNIQUE INDEX ux_variant_baseline ON variant (generation) WHERE family = 'baseline';

CREATE INDEX ix_variant_status ON variant (status);

-- The plan, rebuilt with its own identifier and the version it belongs to.
ALTER TABLE trade_plan RENAME TO trade_plan_before_051;

CREATE TABLE trade_plan (
    -- The plan's own identifier, and what everything below now points at. One per setup per live
    -- version, which is what the unique constraint below says and what the key above enforces.
    plan_id           TEXT    NOT NULL PRIMARY KEY,

    setup_id          TEXT    NOT NULL,
    variant_id        TEXT    NOT NULL,

    as_of             TEXT    NOT NULL,
    live_session      TEXT    NOT NULL,
    ticker            TEXT    NOT NULL,
    direction         TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    trigger_price     TEXT    NOT NULL,
    give_up_price     TEXT    NOT NULL,
    give_up_distance  TEXT    NOT NULL,
    shares            INTEGER NOT NULL CHECK (shares > 0),
    equity            TEXT    NOT NULL,
    risk_fraction     TEXT    NOT NULL,
    risk_budget       TEXT    NOT NULL,
    risk_at_stake     TEXT    NOT NULL,
    observed_at       TEXT    NOT NULL,

    -- A second plan for one candidate under one version is refused by the key rather than by the
    -- stage remembering to check, which is what the setup-keyed table did before the fan-out.
    UNIQUE (setup_id, variant_id),

    FOREIGN KEY (setup_id) REFERENCES setup (setup_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO trade_plan (
    plan_id, setup_id, variant_id, as_of, live_session, ticker, direction,
    trigger_price, give_up_price, give_up_distance, shares, equity,
    risk_fraction, risk_budget, risk_at_stake, observed_at)
SELECT setup_id, setup_id, 'V0', as_of, live_session, ticker, direction,
       trigger_price, give_up_price, give_up_distance, shares, equity,
       risk_fraction, risk_budget, risk_at_stake, observed_at
  FROM trade_plan_before_051;

DROP TABLE trade_plan_before_051;

CREATE INDEX ix_trade_plan_live ON trade_plan (live_session);
CREATE INDEX ix_trade_plan_as_of ON trade_plan (as_of);
CREATE INDEX ix_trade_plan_setup ON trade_plan (setup_id);

-- What the session did to each plan resting in it, now one row per plan rather than per setup.
ALTER TABLE trigger_resolution RENAME TO trigger_resolution_before_051;

CREATE TABLE trigger_resolution (
    plan_id            TEXT    NOT NULL PRIMARY KEY,
    setup_id           TEXT    NOT NULL,
    variant_id         TEXT    NOT NULL,
    live_session       TEXT    NOT NULL,
    ticker             TEXT    NOT NULL,
    direction          TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    outcome            TEXT    NOT NULL CHECK (outcome IN ('touched', 'not_touched', 'unresolvable')),
    touched_at         TEXT    NULL,
    minutes_walked     INTEGER NOT NULL,
    unresolved_because TEXT    NULL,
    observed_at        TEXT    NOT NULL,

    -- A touch names the minute it happened in, and nothing else may. An outcome of `touched` with
    -- no minute would be a fill nothing can price; a minute on either of the other two would be a
    -- time attached to an event that did not occur.
    CHECK ((outcome = 'touched') = (touched_at IS NOT NULL)),
    CHECK ((outcome = 'unresolvable') = (unresolved_because IS NOT NULL)),

    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO trigger_resolution (
    plan_id, setup_id, variant_id, live_session, ticker, direction, outcome,
    touched_at, minutes_walked, unresolved_because, observed_at)
SELECT setup_id, setup_id, 'V0', live_session, ticker, direction, outcome,
       touched_at, minutes_walked, unresolved_because, observed_at
  FROM trigger_resolution_before_051;

DROP TABLE trigger_resolution_before_051;

CREATE INDEX ix_trigger_resolution_session ON trigger_resolution (live_session);

-- The gate's orders. Two versions selecting one stock are two orders in two separate simulated
-- accounts, both tagged, both capped by the same code, and the uniqueness below is what stopped
-- that being possible before the fan-out.
ALTER TABLE trade_order RENAME TO trade_order_before_051;

CREATE TABLE trade_order (
    order_id        TEXT    NOT NULL PRIMARY KEY,
    plan_id         TEXT    NOT NULL UNIQUE,
    setup_id        TEXT    NOT NULL,
    variant_id      TEXT    NOT NULL,
    live_session    TEXT    NOT NULL,
    ticker          TEXT    NOT NULL,
    direction       TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    triggered_at    TEXT    NOT NULL,
    status          TEXT    NOT NULL CHECK (status IN ('placed', 'blocked')),
    planned_shares  INTEGER NOT NULL CHECK (planned_shares > 0),
    shares          INTEGER NOT NULL CHECK (shares >= 0),
    risk_at_stake   TEXT    NOT NULL,
    bound_by        TEXT    NULL,
    blocked_because TEXT    NULL,
    observed_at     TEXT    NOT NULL,

    -- A blocked order granted shares, or a placed one granted none, would be a status disagreeing
    -- with the only number anybody acts on.
    CHECK ((status = 'placed') = (shares > 0)),

    -- A refusal says what refused it. A cap that blocked with no reason is a row nobody can act on,
    -- and a reason with no cap is prose with nothing to group by.
    CHECK ((status = 'blocked') = (blocked_because IS NOT NULL)),
    CHECK (blocked_because IS NULL OR bound_by IS NOT NULL),

    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO trade_order (
    order_id, plan_id, setup_id, variant_id, live_session, ticker, direction, triggered_at,
    status, planned_shares, shares, risk_at_stake, bound_by, blocked_because, observed_at)
SELECT order_id, setup_id, setup_id, 'V0', live_session, ticker, direction, triggered_at,
       status, planned_shares, shares, risk_at_stake, bound_by, blocked_because, observed_at
  FROM trade_order_before_051;

DROP TABLE trade_order_before_051;

CREATE INDEX ix_order_session ON trade_order (live_session, triggered_at);

-- The position the broker opens against an order. One per plan rather than one per setup, so two
-- versions holding the same name are two positions in two simulated accounts.
ALTER TABLE position RENAME TO position_before_051;

CREATE TABLE position (
    position_id          TEXT    NOT NULL PRIMARY KEY,
    plan_id              TEXT    NOT NULL UNIQUE,
    setup_id             TEXT    NOT NULL,
    variant_id           TEXT    NOT NULL,
    order_id             TEXT    NOT NULL,
    ticker               TEXT    NOT NULL,
    direction            TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    status               TEXT    NOT NULL CHECK (status IN ('unfilled', 'open', 'closed')),
    opened_session       TEXT    NOT NULL,
    opened_at            TEXT    NULL,
    shares               INTEGER NOT NULL CHECK (shares >= 0),
    entry_fill_id        TEXT    NULL,
    entry_price          TEXT    NULL,
    value_at_entry       TEXT    NULL,
    fraction_at_entry    REAL    NULL,
    risk_intended        TEXT    NULL,
    risk_realised        TEXT    NULL,
    unfilled_because     TEXT    NULL,
    borrow_rate_assumed  TEXT    NULL,
    borrow_availability  TEXT    NULL,
    closed_session       TEXT    NULL,
    closed_at            TEXT    NULL,
    exit_fill_id         TEXT    NULL,
    exit_price           TEXT    NULL,
    exit_reason          TEXT    NULL,
    realised_pnl         TEXT    NULL,
    realised_r           REAL    NULL,
    observed_at          TEXT    NOT NULL,
    closed_observed_at   TEXT    NULL,
    trim_fill_id         TEXT    NULL,
    trimmed_at           TEXT    NULL,
    trimmed_shares       INTEGER NULL,
    trim_price           TEXT    NULL,
    trim_realised_pnl    TEXT    NULL,
    trim_observed_at     TEXT    NULL,
    exit_armed_session   TEXT    NULL,
    exit_armed_reason    TEXT    NULL,

    -- An unfilled position holds no shares and says why; a filled one holds shares and has a fill.
    -- Written as an equivalence in both directions, because a status disagreeing with the only
    -- number anybody acts on is the shape `trade_order` refuses one migration ago.
    CHECK ((status = 'unfilled') = (shares = 0)),
    CHECK ((status = 'unfilled') = (unfilled_because IS NOT NULL)),
    CHECK ((status = 'unfilled') = (entry_fill_id IS NULL)),
    CHECK ((status = 'unfilled') = (entry_price IS NULL)),
    CHECK ((status = 'unfilled') = (opened_at IS NULL)),

    -- A closed position has an exit and an instant the close was observed at, and nothing else has
    -- either. The second stamp is what lets a replay between the two dates read it as open.
    CHECK ((status = 'closed') = (exit_fill_id IS NOT NULL)),
    CHECK ((status = 'closed') = (exit_price IS NOT NULL)),
    CHECK ((status = 'closed') = (exit_reason IS NOT NULL)),
    CHECK ((status = 'closed') = (closed_at IS NOT NULL)),
    CHECK ((status = 'closed') = (closed_observed_at IS NOT NULL)),

    -- The two short assumptions are present exactly on the shorts, so a long carrying a borrow rate
    -- and a short carrying none are both refused rather than being read as nought.
    CHECK ((direction = 'short') = (borrow_rate_assumed IS NOT NULL)),
    CHECK ((direction = 'short') = (borrow_availability IS NOT NULL)),

    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (order_id) REFERENCES trade_order (order_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO position (
    position_id, plan_id, setup_id, variant_id, order_id, ticker, direction, status,
    opened_session, opened_at, shares, entry_fill_id, entry_price, value_at_entry,
    fraction_at_entry, risk_intended, risk_realised, unfilled_because, borrow_rate_assumed,
    borrow_availability, closed_session, closed_at, exit_fill_id, exit_price, exit_reason,
    realised_pnl, realised_r, observed_at, closed_observed_at, trim_fill_id, trimmed_at,
    trimmed_shares, trim_price, trim_realised_pnl, trim_observed_at, exit_armed_session,
    exit_armed_reason)
SELECT position_id, setup_id, setup_id, 'V0', order_id, ticker, direction, status,
       opened_session, opened_at, shares, entry_fill_id, entry_price, value_at_entry,
       fraction_at_entry, risk_intended, risk_realised, unfilled_because, borrow_rate_assumed,
       borrow_availability, closed_session, closed_at, exit_fill_id, exit_price, exit_reason,
       realised_pnl, realised_r, observed_at, closed_observed_at, trim_fill_id, trimmed_at,
       trimmed_shares, trim_price, trim_realised_pnl, trim_observed_at, exit_armed_session,
       exit_armed_reason
  FROM position_before_051;

DROP TABLE position_before_051;

CREATE INDEX ix_position_status ON position (status, opened_session);
CREATE INDEX ix_position_session ON position (opened_session);

-- The fills. Keyed on the fill and pointing at the position as before; the plan and the version are
-- carried for the same reason the setup already was, so a fill reads without two joins.
ALTER TABLE fill RENAME TO fill_before_051;

CREATE TABLE fill (
    fill_id           TEXT    NOT NULL PRIMARY KEY,
    position_id       TEXT    NOT NULL,
    plan_id           TEXT    NOT NULL,
    setup_id          TEXT    NOT NULL,
    variant_id        TEXT    NOT NULL,
    session_date      TEXT    NOT NULL,
    ticker            TEXT    NOT NULL,
    direction         TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    leg               TEXT    NOT NULL CHECK (leg IN ('entry', 'exit', 'trim')),
    filled_at         TEXT    NOT NULL,
    basis             TEXT    NOT NULL CHECK (basis IN ('slipped', 'gapped')),
    resting_price     TEXT    NOT NULL,
    price             TEXT    NOT NULL,
    slippage          TEXT    NOT NULL,
    shares            INTEGER NOT NULL CHECK (shares > 0),
    spread_bps        REAL    NULL,
    spread_pass       TEXT    NULL CHECK (spread_pass IS NULL OR spread_pass IN ('after_open', 'before_close')),
    quote_lag_seconds INTEGER NULL,
    straddle_seconds  INTEGER NULL,
    observed_at       TEXT    NOT NULL,

    -- A slipped fill charged a spread, so it has to say which one. A gap fill charges nothing and
    -- still carries the quote where the session had one, so the charge that was not made is legible.
    CHECK (basis <> 'slipped' OR spread_bps IS NOT NULL),
    CHECK (spread_bps IS NULL OR spread_pass IS NOT NULL),

    FOREIGN KEY (position_id) REFERENCES position (position_id),
    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO fill (
    fill_id, position_id, plan_id, setup_id, variant_id, session_date, ticker, direction, leg,
    filled_at, basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
    quote_lag_seconds, straddle_seconds, observed_at)
SELECT fill_id, position_id, setup_id, setup_id, 'V0', session_date, ticker, direction, leg,
       filled_at, basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
       quote_lag_seconds, straddle_seconds, observed_at
  FROM fill_before_051;

DROP TABLE fill_before_051;

CREATE INDEX ix_fill_position ON fill (position_id, leg);
CREATE INDEX ix_fill_session ON fill (session_date, filled_at);

-- The trade, one per closed position and so one per plan.
ALTER TABLE trade RENAME TO trade_before_051;

CREATE TABLE trade (
    trade_id              TEXT    NOT NULL PRIMARY KEY,
    position_id           TEXT    NOT NULL UNIQUE,
    plan_id               TEXT    NOT NULL UNIQUE,
    setup_id              TEXT    NOT NULL,
    variant_id            TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    opened_session        TEXT    NOT NULL,
    closed_session        TEXT    NOT NULL,
    held_calendar_days    INTEGER NOT NULL CHECK (held_calendar_days >= 0),
    held_sessions         INTEGER NOT NULL CHECK (held_sessions >= 1),
    entry_price           TEXT    NOT NULL,
    exit_price            TEXT    NOT NULL,
    exit_reason           TEXT    NOT NULL,
    shares                INTEGER NOT NULL CHECK (shares > 0),
    trimmed_shares        INTEGER NOT NULL CHECK (trimmed_shares >= 0),
    value_at_entry        TEXT    NOT NULL,
    risk_realised         TEXT    NOT NULL,
    gross_pnl             TEXT    NOT NULL,
    borrow_rate_assumed   TEXT    NULL,
    borrow_cost           TEXT    NULL,
    net_pnl               TEXT    NOT NULL,
    result_r              REAL    NOT NULL,

    -- How long an exit decided in one session waited for an open to fill at. Null where no rule
    -- armed one, which is every give-up exit.
    exit_armed_session    TEXT    NULL,
    armed_sessions_waited INTEGER NULL,

    observed_at           TEXT    NOT NULL,

    -- Borrow is charged exactly on the shorts, in both directions, so a long carrying a cost and a
    -- short carrying none are equally unwritable.
    CHECK ((direction = 'short') = (borrow_rate_assumed IS NOT NULL)),
    CHECK ((direction = 'short') = (borrow_cost IS NOT NULL)),

    -- The waiting figure is present exactly when something armed the exit.
    CHECK ((exit_armed_session IS NULL) = (armed_sessions_waited IS NULL)),

    -- A trim never takes more than was opened.
    CHECK (trimmed_shares <= shares),

    FOREIGN KEY (position_id) REFERENCES position (position_id),
    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO trade (
    trade_id, position_id, plan_id, setup_id, variant_id, ticker, direction, opened_session,
    closed_session, held_calendar_days, held_sessions, entry_price, exit_price, exit_reason,
    shares, trimmed_shares, value_at_entry, risk_realised, gross_pnl, borrow_rate_assumed,
    borrow_cost, net_pnl, result_r, exit_armed_session, armed_sessions_waited, observed_at)
SELECT trade_id, position_id, setup_id, setup_id, 'V0', ticker, direction, opened_session,
       closed_session, held_calendar_days, held_sessions, entry_price, exit_price, exit_reason,
       shares, trimmed_shares, value_at_entry, risk_realised, gross_pnl, borrow_rate_assumed,
       borrow_cost, net_pnl, result_r, exit_armed_session, armed_sessions_waited, observed_at
  FROM trade_before_051;

DROP TABLE trade_before_051;

CREATE INDEX ix_trade_closed ON trade (closed_session, direction);
CREATE INDEX ix_trade_ticker ON trade (ticker, closed_session);

-- The audit, one per trade and so one per plan.
ALTER TABLE plan_audit RENAME TO plan_audit_before_051;

CREATE TABLE plan_audit (
    trade_id                   TEXT    NOT NULL PRIMARY KEY,
    plan_id                    TEXT    NOT NULL UNIQUE,
    setup_id                   TEXT    NOT NULL,
    variant_id                 TEXT    NOT NULL,
    ticker                     TEXT    NOT NULL,
    direction                  TEXT    NOT NULL CHECK (direction IN ('long', 'short')),

    -- 1. Execution, at both ends: what the instruction named against what it got.
    planned_trigger            TEXT    NOT NULL,
    executed_entry             TEXT    NOT NULL,
    entry_difference           TEXT    NOT NULL,
    entry_difference_bps       REAL    NOT NULL,
    entry_basis                TEXT    NOT NULL CHECK (entry_basis IN ('slipped', 'gapped')),

    exit_resting_price         TEXT    NOT NULL,
    executed_exit              TEXT    NOT NULL,
    exit_difference            TEXT    NOT NULL,
    exit_difference_bps        REAL    NOT NULL,
    exit_basis                 TEXT    NOT NULL CHECK (exit_basis IN ('slipped', 'gapped')),
    exit_reason                TEXT    NOT NULL,

    -- 2. The plan's stop against where the trade ended.
    planned_give_up            TEXT    NOT NULL,
    give_up_difference         TEXT    NOT NULL,
    give_up_difference_bps     REAL    NOT NULL,

    -- 3. The gate: the size the plan carried against the size that was placed.
    planned_shares             INTEGER NOT NULL CHECK (planned_shares > 0),
    executed_shares            INTEGER NOT NULL CHECK (executed_shares > 0),
    shares_difference          INTEGER NOT NULL,
    reduced_because            TEXT    NULL,
    risk_intended              TEXT    NOT NULL,
    risk_realised              TEXT    NOT NULL,
    risk_difference            TEXT    NOT NULL,

    observed_at                TEXT    NOT NULL,

    FOREIGN KEY (trade_id) REFERENCES trade (trade_id),
    FOREIGN KEY (plan_id) REFERENCES trade_plan (plan_id),
    FOREIGN KEY (variant_id) REFERENCES variant (variant_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO plan_audit (
    trade_id, plan_id, setup_id, variant_id, ticker, direction, planned_trigger, executed_entry,
    entry_difference, entry_difference_bps, entry_basis, exit_resting_price, executed_exit,
    exit_difference, exit_difference_bps, exit_basis, exit_reason, planned_give_up,
    give_up_difference, give_up_difference_bps, planned_shares, executed_shares, shares_difference,
    reduced_because, risk_intended, risk_realised, risk_difference, observed_at)
SELECT trade_id, setup_id, setup_id, 'V0', ticker, direction, planned_trigger, executed_entry,
       entry_difference, entry_difference_bps, entry_basis, exit_resting_price, executed_exit,
       exit_difference, exit_difference_bps, exit_basis, exit_reason, planned_give_up,
       give_up_difference, give_up_difference_bps, planned_shares, executed_shares, shares_difference,
       reduced_because, risk_intended, risk_realised, risk_difference, observed_at
  FROM plan_audit_before_051;

DROP TABLE plan_audit_before_051;

CREATE INDEX ix_plan_audit_ticker ON plan_audit (ticker);

-- The plan stage's own run row gains the count of candidates beside the count of plans.
--
-- <b>They are the same number only while one version is live, and that is exactly why both are
-- stored.</b> `planned` counted candidates until the fan-out and now counts plans, so a night with
-- two versions would read as twice the funnel to anybody who did not know the column's meaning had
-- moved. Two columns and a night reads as what it was: this many candidates, fanned out to this
-- many plans.
--
-- Nullable and added in place rather than by a rebuild, because every row written before tonight
-- was written when the two were one number and inventing a value for them would be stating a figure
-- nobody measured.
ALTER TABLE plan_run ADD COLUMN candidates_planned INTEGER NULL;

PRAGMA legacy_alter_table = OFF;
