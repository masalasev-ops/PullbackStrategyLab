-- 046  trade, plan_audit, and the two run rows that go with them
--
-- <b>Two tables, two components, and the order between them is a foreign key rather than a note.</b>
-- TradeJournal closes a trade and states its result; PlanAudit holds the plan against what happened.
-- The audit points at the trade, so the trade has to exist first, which is the same shape PaperBroker
-- and PositionManager already stand in and is why the ordering is expressible rather than remembered.
-- see: TradeJournal runs first and PlanAudit second, and the audit never changes a result
--
-- <b>`trade` is not a copy of `position` and the difference is the borrow line.</b> A position is
-- what the lab held; a trade is what holding it came to. Everything here that also lives on the
-- position is carried because the trade is the row a person reads and a join to answer "how much did
-- it make" is a join nobody makes. What is new is the borrow cost on the short side and the result
-- after it, and those are the whole reason this table exists rather than a view.
--
-- <b>`result_r` is after borrow and `position.realised_r` is before it, and both names stay.</b> They
-- are equal on every long and differ by the borrow line on every short. Giving them one name would
-- be two numbers wearing one, which is the fault this corpus keeps finding; giving the second one a
-- different name and saying so here is the whole fix.
--
-- <b>`plan_audit` asks three questions and they are not one question.</b> The first is execution: the
-- price an instruction named against the price it got, at both ends. The second is the plan's stop
-- against where the trade actually ended, which is only the same as the first on a give-up exit and
-- is a different quantity entirely on a trail exit. The third is the gate: the size the plan carried
-- against the size that was placed, and the risk that followed from each. ARCHITECTURE, SCHEMA and
-- the mockup each named one of the three and none named all, which is what 4.9 was asked to settle.
-- see: The audit holds three pairs and they answer three different questions
--
-- <b>Every difference is derived from the two prices rather than copied from `fill.slippage`.</b> An
-- audit reading the model's own charge would be comparing a number against itself, and the two
-- legitimately differ on a gap, where the model charges nothing and the price moved anyway. So each
-- pair carries the fill's basis beside it, and a gap is never read as slippage.
--
-- Prices are TEXT holding a decimal, never REAL. The basis-point figures are REAL because a
-- difference as a fraction of a price is a statistic and not money, which is the line `spread_bps`
-- and `realised_r` already fall on.

CREATE TABLE trade (
    trade_id              TEXT    NOT NULL PRIMARY KEY,
    position_id           TEXT    NOT NULL UNIQUE,
    setup_id              TEXT    NOT NULL UNIQUE,
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
    -- armed one, which is every give-up exit. The obligation 4.8 raised is that an armed exit is
    -- never re-evaluated, so a session the store holds no minute of postpones it; this is the size
    -- of that on each trade rather than an argument about how often it happens.
    exit_armed_session    TEXT    NULL,
    armed_sessions_waited INTEGER NULL,

    observed_at           TEXT    NOT NULL,

    -- Borrow is charged exactly on the shorts, in both directions, so a long carrying a cost and a
    -- short carrying none are equally unwritable. That is the shape `position` already declares over
    -- the same two assumptions.
    CHECK ((direction = 'short') = (borrow_rate_assumed IS NOT NULL)),
    CHECK ((direction = 'short') = (borrow_cost IS NOT NULL)),

    -- The waiting figure is present exactly when something armed the exit.
    CHECK ((exit_armed_session IS NULL) = (armed_sessions_waited IS NULL)),

    -- A trim never takes more than was opened.
    CHECK (trimmed_shares <= shares),

    FOREIGN KEY (position_id) REFERENCES position (position_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_trade_closed ON trade (closed_session, direction);
CREATE INDEX ix_trade_ticker ON trade (ticker, closed_session);

CREATE TABLE plan_audit (
    trade_id                   TEXT    NOT NULL PRIMARY KEY,
    setup_id                   TEXT    NOT NULL UNIQUE,
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

    -- 2. The plan's stop against where the trade ended. Equal to the exit pair on a give-up exit and
    --    a different quantity on every other, which is why it is its own pair rather than a reading
    --    of the one above.
    planned_give_up            TEXT    NOT NULL,
    give_up_difference         TEXT    NOT NULL,
    give_up_difference_bps     REAL    NOT NULL,

    -- 3. The gate: the size the plan carried against the size that was placed, and the risk each
    --    implies. `reduced_because` names the cap that bound, or is null where none did.
    planned_shares             INTEGER NOT NULL CHECK (planned_shares > 0),
    executed_shares            INTEGER NOT NULL CHECK (executed_shares > 0),
    shares_difference          INTEGER NOT NULL,
    reduced_because            TEXT    NULL,
    risk_intended              TEXT    NOT NULL,
    risk_realised              TEXT    NOT NULL,
    risk_difference            TEXT    NOT NULL,

    observed_at                TEXT    NOT NULL,

    FOREIGN KEY (trade_id) REFERENCES trade (trade_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_plan_audit_ticker ON plan_audit (ticker);

-- One row per run of each stage, on the pattern `fill_run` and `manage_run` set.

CREATE TABLE trade_run (
    session_date        TEXT    NOT NULL,
    closed_in_session   INTEGER NOT NULL,
    journalled          INTEGER NOT NULL,
    longs               INTEGER NOT NULL,
    shorts              INTEGER NOT NULL,
    shorts_charged      INTEGER NOT NULL,
    trimmed             INTEGER NOT NULL,
    armed_exits         INTEGER NOT NULL,
    outcome             TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because     TEXT    NULL,
    observed_at         TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_trade_run_session ON trade_run (session_date);

CREATE TABLE audit_run (
    session_date        TEXT    NOT NULL,
    trades_read         INTEGER NOT NULL,
    audited             INTEGER NOT NULL,
    longs               INTEGER NOT NULL,
    shorts              INTEGER NOT NULL,
    reduced_by_a_cap    INTEGER NOT NULL,
    gapped_at_an_end    INTEGER NOT NULL,
    outcome             TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because     TEXT    NULL,
    observed_at         TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_audit_run_session ON audit_run (session_date);
