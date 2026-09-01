-- 040  trade_plan, plan_run
--
-- The committed instruction: enter here, give up here, this many shares. Declared in SCHEMA under
-- Trading since phase 4 was planned and built by no checkpoint until now, which is how the phase
-- came to build PlanAudit, an auditor of a thing it never built.
-- see: The plan is written before the session and is immutable after publication
--
-- <b>The plan carries its own size.</b> RiskGate may reduce that size at trigger or block the order
-- outright, and it never recomputes it. The alternative, sizing at trigger from the same inputs,
-- would make `plan_audit`'s planned-against-executed comparison a comparison of two of this lab's
-- own numbers rather than of an intention against an outcome, and the watchlist published at 18:40
-- would have nothing to show ten hours before the sizing happened.
-- see: The plan carries its own size, and RiskGate reduces or blocks it but never recomputes it
--
-- <b>`live_session` is a column rather than an inference.</b> A plan written on the evening of N is
-- live in session N+1, and every reader that has to know which session a plan belongs to would
-- otherwise derive it by stepping a calendar forward over weekends and holidays. That is the shape
-- that made `intended_date` differ across every weekend, and 4.5's fail-closed assertion that a plan
-- is never resolved against a session at or before its own date should be reading a stored fact
-- rather than recomputing one.
--
-- <b>No variant column, and SCHEMA's grain is not wrong.</b> SCHEMA declares this store grained on
-- setup plus variant and says columns are owed at their checkpoint. There is one baseline and no
-- versions, so the key here is the setup alone; 5.1 adds the fan-out and does not move what this
-- wrote. Writing a variant column now would be a column with one value in it and a key that could
-- not refuse the second write it exists to refuse.
--
-- <b>Prices are TEXT holding a decimal, `shares` is INTEGER.</b> The money values go through
-- StoreText.PriceToStorageText and the ratio through RatioToStorageText, so nothing here is REAL.
--
-- <b>`risk_budget` sits beside `risk_at_stake` so the rounding is visible.</b> The share count is
-- rounded down, so what a plan actually risks is at or below the budget it was sized from. Storing
-- only the budget would state a number no trade will ever lose, and storing only the outcome would
-- hide that the difference is rounding rather than a different rule. It is the shape `position`
-- already declares with `risk_intended` beside `risk_realised`.
-- see: Equity is a fixed $100,000 notional that never compounds

CREATE TABLE trade_plan (
    setup_id          TEXT    NOT NULL PRIMARY KEY,
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
    FOREIGN KEY (setup_id) REFERENCES setup (setup_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_trade_plan_live ON trade_plan (live_session);
CREATE INDEX ix_trade_plan_as_of ON trade_plan (as_of);

-- One row per run of the stage, on the pattern `vwap_run` and `intraday_fetch` set.
--
-- <b>Refusals are counted here and are not rows of their own.</b> A capped candidate that gets no
-- plan gets none for a reason already in `setup`: its geometry is absent, or its trigger and give-up
-- point are the same price, or the risk budget cannot buy one share at that distance. A per-setup
-- refusal table would be a second statement of facts the store already holds, and the two could
-- disagree with nothing reading both. That is the ruling WatchlistPublisher took at 4.1 over a
-- watchlist table and VwapEngine took at 4.4 over the day's high and low.
--
-- What is not derivable from `setup` is how many of each there were on a night, because it depends
-- on the risk budget as well as on the row, so the counts are stored per reason rather than as one
-- `refused` total. A single total would let the reason that is a defect hide inside the reason that
-- is ordinary arithmetic.

CREATE TABLE plan_run (
    session_date              TEXT    NOT NULL,
    live_session              TEXT    NOT NULL,
    candidates                INTEGER NOT NULL,
    planned                   INTEGER NOT NULL,
    refused_absent_geometry   INTEGER NOT NULL,
    refused_equal_prices      INTEGER NOT NULL,
    refused_below_one_share   INTEGER NOT NULL,
    outcome                   TEXT    NOT NULL,
    stopped_because           TEXT    NULL,
    observed_at               TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_plan_run_session ON plan_run (session_date);
