-- 042  trade_order, order_run
--
-- Every order the lab placed and every order it refused, written by one component.
-- see: RiskGate is the sole writer of orders, for both directions and every version
--
-- <b>The table is `trade_order` and not `order`, which SCHEMA declared until 4.6.</b> `order` is a
-- reserved word in SQLite, so every statement touching it would carry quotes and one unquoted use
-- would be a syntax error found at runtime. The half that decided it is that every parser in the
-- verification harness reads an unquoted identifier: `writer-ownership`, `bar-append-only`,
-- `price-storage-form` and `point-in-time` all match `[a-z_]+` after CREATE TABLE or INSERT INTO, so
-- a quoted table is a table none of them can see. A store nothing scans is the exact shape this
-- corpus keeps finding, and it is not worth buying with a name. `writer-ownership` now refuses a
-- quoted table name outright, so the next one fails rather than disappearing.
--
-- <b>A blocked order is a row and not an absence.</b> The caps are the thing under study as much as
-- the rules are: a night on which three good setups triggered and one slot was free is evidence
-- about the caps, and it is indistinguishable from a night on which one setup triggered unless the
-- refusals are stored. `order-provenance` starts here and reads these rows.
--
-- <b>The size arrives from the plan and leaves reduced, blocked, or unchanged.</b> `planned_shares`
-- is what the plan carried and `shares` is what was granted, which is nought on a blocked row. Both
-- are stored because a reduction that overwrote the plan's figure would leave the plan and the order
-- agreeing about a number the caps had changed, and `plan_audit` at 4.9 compares exactly those two.
-- see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
--
-- <b>No give-up price is copied here, and that is the decision rather than an omission.</b> A
-- reduction keeps the plan's give-up price, so there is one give-up price for a trade and it lives
-- in `trade_plan`. A column here would be a second statement of it that a later reduction could
-- move, and R would then depend on which row a reader opened.
--
-- <b>`bound_by` names the cap and `blocked_because` says what it saw.</b> The cap name is the
-- countable half, so a night's refusals group without parsing prose; the sentence carries the
-- figures the cap compared, which is what makes a refusal readable a month later. A placed row may
-- carry `bound_by` with no `blocked_because`, which is the reduction case, and that is the pair the
-- constraints below allow deliberately.
--
-- <b>Grained on the order, which today is one per plan.</b> A plan triggers at most once, because
-- the resolver records the first minute that reached the trigger and no later one moves it. The key
-- is the plan for that reason, which is what makes a second order for one plan unexpressible rather
-- than merely unwritten. 5.1 fans plans out per variant and this key follows `trade_plan`'s.

CREATE TABLE trade_order (
    order_id        TEXT    NOT NULL PRIMARY KEY,
    setup_id        TEXT    NOT NULL UNIQUE,
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

    FOREIGN KEY (setup_id) REFERENCES trade_plan (setup_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_order_session ON trade_order (live_session, triggered_at);

-- One row per run of the stage, on the pattern `trigger_run`, `plan_run` and `vwap_run` set.
--
-- <b>The refusals and the reductions are counted per cap rather than as two totals.</b> A night on
-- which the book filled is a different night from one on which every order was trimmed to fit the
-- account's total risk, and a `blocked` total would read the same for both. It is the ruling
-- PlanBuilder took over its three refusal reasons: a single total lets the reason that is a finding
-- hide inside the reason that is arithmetic.

CREATE TABLE order_run (
    session_date            TEXT    NOT NULL,
    triggers                INTEGER NOT NULL,
    placed                  INTEGER NOT NULL,
    reduced                 INTEGER NOT NULL,
    blocked                 INTEGER NOT NULL,
    blocked_open_positions  INTEGER NOT NULL,
    blocked_open_shorts     INTEGER NOT NULL,
    reduced_position_size   INTEGER NOT NULL,
    reduced_total_risk      INTEGER NOT NULL,
    blocked_below_one_share INTEGER NOT NULL,
    outcome                 TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because         TEXT    NULL,
    observed_at             TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_order_run_session ON order_run (session_date);
