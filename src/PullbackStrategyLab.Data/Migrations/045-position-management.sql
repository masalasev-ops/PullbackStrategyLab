-- 045  the trim leg, the trim and trail columns, manage_run, and two fill_run columns that move
--
-- <b>Exits leave PaperBroker and the whole of this migration follows from that.</b> Until 4.8 a
-- position ended one way, on the give-up point, which is a resting instruction the plan carried from
-- 18:30 rather than a rule anybody evaluates. From 4.8 it can end three ways and the rule is that
-- the exit is whichever is reached first, which is a comparison across rules. A comparison cannot be
-- made by two components each of which sees one side of it, so every exit is PositionManager's and
-- every entry stays PaperBroker's. That is the same split RiskGate holds over orders, arrived at
-- from the other direction: one writer per operation is what makes a figure comparable between
-- versions.
-- see: Every exit is PositionManager's and every entry is PaperBroker's
--
-- <b>`fill` is rebuilt for one value and there is no cheaper way.</b> A trim is a third leg: it is
-- one end of nothing, because the position it reduces stays open, and calling it an exit would make
-- `position.exit_fill_id` ambiguous on every trimmed short. SQLite cannot alter a CHECK, so the
-- table is renamed, redeclared and copied. The rename is safe here and would not be on `position`:
-- nothing holds a foreign key into `fill`, and SQLite rewrites a child's foreign key clause when the
-- parent is renamed, which is the hazard SCHEMA names under `rebuild_demand`.
--
-- <b>`position` gains columns and no constraints, and that is the same reason inverted.</b> `fill`
-- holds a foreign key into `position`, so rebuilding `position` to add a CHECK would rewrite that
-- clause as a side effect of a tidiness. The trim columns' equivalence, that a trimmed row carries
-- all six and an untrimmed row carries none, is asserted by a test instead. A constraint would be
-- better and is not worth what it costs here, which is stated rather than left as an absence.
--
-- <b>`exits_filled` and `open_at_end` leave `fill_run`.</b> PaperBroker no longer closes anything,
-- so both would read nought on every future night, and a stage's record reading nought for ever is
-- one a later session reads as broken. That is the ruling migration 044 took over the two `vwap_run`
-- counters one checkpoint ago. The night's book at its end is `manage_run`'s, because the manager is
-- now the last stage that can change it.

DROP INDEX ix_fill_position;
DROP INDEX ix_fill_session;

ALTER TABLE fill RENAME TO fill_before_045;

CREATE TABLE fill (
    fill_id           TEXT    NOT NULL PRIMARY KEY,
    position_id       TEXT    NOT NULL,
    setup_id          TEXT    NOT NULL,
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
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO fill (
    fill_id, position_id, setup_id, session_date, ticker, direction, leg, filled_at,
    basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
    quote_lag_seconds, straddle_seconds, observed_at)
SELECT
    fill_id, position_id, setup_id, session_date, ticker, direction, leg, filled_at,
    basis, resting_price, price, slippage, shares, spread_bps, spread_pass,
    quote_lag_seconds, straddle_seconds, observed_at
  FROM fill_before_045;

DROP TABLE fill_before_045;

CREATE INDEX ix_fill_position ON fill (position_id, leg);
CREATE INDEX ix_fill_session ON fill (session_date, filled_at);

-- The trim, which reduces a position and leaves it open. Columns rather than a flag, because
-- the trade's money is the trim's plus the close's and a reader that could not price the first half
-- would have to derive it from a fill row nothing points at.
ALTER TABLE position ADD COLUMN trim_fill_id TEXT NULL;
ALTER TABLE position ADD COLUMN trimmed_at TEXT NULL;
ALTER TABLE position ADD COLUMN trimmed_shares INTEGER NULL;
ALTER TABLE position ADD COLUMN trim_price TEXT NULL;
ALTER TABLE position ADD COLUMN trim_realised_pnl TEXT NULL;

-- The trim's own observation stamp, on exactly the grounds `closed_observed_at` carries one. The
-- trim is an update, so `observed_at` stays at the open and cannot answer a replay standing between
-- the two. Every bound in this store is at session granularity, so `trimmed_at` would answer the
-- same question today by accident; a stamp that is right for a reason outlives one that is right
-- because of how coarse the bounds happen to be.
ALTER TABLE position ADD COLUMN trim_observed_at TEXT NULL;

-- An exit decided in one session and filled at the open of the next, with the rule that decided it.
--
-- <b>Two columns rather than one flag, and general rather than the trail alone.</b> The trail is the
-- rule that needs this: it is evaluated on a daily close and the next price after that close is the
-- next session's open. The short reclaim needs it in one case, being an hourly bar the store holds
-- no later minute than, and a second mechanism for the same shape would be a second thing to get
-- right. Stored rather than recomputed on the next night, because the evidence is the previous
-- session's close and a rerun that recomputed the condition would read a series the store may since
-- have corrected.
ALTER TABLE position ADD COLUMN exit_armed_session TEXT NULL;
ALTER TABLE position ADD COLUMN exit_armed_reason TEXT NULL;

ALTER TABLE fill_run DROP COLUMN exits_filled;
ALTER TABLE fill_run DROP COLUMN open_at_end;

-- One row per run of PositionManager, on the pattern `fill_run`, `order_run` and `plan_run` set.
--
-- <b>The three exits are counted apart.</b> A night of trail exits is a different night from a
-- night of stop-outs, and a single total lets the one that is a finding hide inside the one that is
-- ordinary. That is the ruling PlanBuilder took over its three refusal reasons and RiskGate over its
-- five.
--
-- <b>`closed_in_their_own_session` is what makes an approximation countable.</b> RiskGate runs at
-- 21:10 and reads the book as it stood coming into the session, so a position opened at 09:31 and
-- closed at 09:45 still occupies a slot the 10:00 trigger is refused on. Merging the two stages
-- would fix it and would give orders a second writer. The cost is carried instead, and this column
-- is its size on the night rather than an argument in a comment.
-- see: RiskGate reads the book as it stood coming into the session, and what that costs is counted

CREATE TABLE manage_run (
    session_date                TEXT    NOT NULL,
    open_at_start               INTEGER NOT NULL,
    longs_managed               INTEGER NOT NULL,
    shorts_managed              INTEGER NOT NULL,
    closed_give_up              INTEGER NOT NULL,
    closed_trail                INTEGER NOT NULL,
    closed_reclaim              INTEGER NOT NULL,
    trimmed                     INTEGER NOT NULL,
    exits_armed                 INTEGER NOT NULL,
    gapped                      INTEGER NOT NULL,
    slipped                     INTEGER NOT NULL,
    held_no_quote               INTEGER NOT NULL,
    closed_in_their_own_session INTEGER NOT NULL,
    open_at_end                 INTEGER NOT NULL,
    names_walked                INTEGER NOT NULL,
    minutes_walked              INTEGER NOT NULL,
    outcome                     TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because             TEXT    NULL,
    observed_at                 TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_manage_run_session ON manage_run (session_date);
