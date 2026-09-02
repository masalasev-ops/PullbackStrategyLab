-- 043  fill, position, fill_run
--
-- What a resting order actually got, and the position it opened. Written by one component.
--
-- <b>A fill is one end of one trade and there are two of them.</b> Entry and exit are the same
-- shape, priced by the same rules, and separating them into two tables would put one model's output
-- in two places and let a later session price one end and forget the other. That is the fault the
-- exit-slippage decision exists to close, one level up.
-- see: Exit slippage is charged on the same terms as entry slippage
--
-- <b>`leg` and not `end`.</b> `END` is a SQLite keyword, so a column named for it would carry quotes
-- in every statement touching it, on exactly the grounds `trade_order` is not called `order`. The
-- rule that decided that one at 4.6 is the same here: a quoted identifier is one every parser in the
-- verification harness reads past.
--
-- <b>An order that could not be filled is a row and not an absence.</b> `position` carries three
-- statuses, on the shape `trigger_resolution` set with three outcomes and `trade_order` set by
-- writing a blocked order rather than dropping it. A name the session quoted no usable book for
-- cannot be charged the spread the model says it owes: charging nought is a free entry that clears
-- every threshold written as a maximum, and charging a figure taken from other names would be a
-- spread nobody measured wearing the authority of one that was. So it is not filled, and the row
-- says which of those it was.
-- see: A fill with no usable quote for its name is refused and recorded, never charged nought
-- see: A gate handed an absent or degenerate quantity fails rather than passing
--
-- <b>The stamp is in two columns because this is the one table in the phase that is updated.</b>
-- SCHEMA has declared `Insert PaperBroker · Update PaperBroker` since phase 4 was planned, and an
-- updated row loses the thing every other table in this store keeps: a replay standing between a
-- position's open and its close has to see it open. One `observed_at` cannot answer that, because
-- the update overwrites the state without moving the stamp, so `closed_observed_at` is written by
-- the close and every read bounds both. A row whose close was observed after the as-of reads as
-- open, which is what it was.
--
-- <b>`risk_intended` beside `risk_realised` is where slippage becomes visible.</b> The intended
-- figure is the share count against the give-up distance the plan named, which is what RiskGate
-- granted. The realised figure is the same share count against the distance from the price the fill
-- actually got to the same give-up point. The two differ by the entry slippage, so an R computed
-- from either is a real number and only one of them is the trade's.
-- see: Equity is a fixed $100,000 notional that never compounds
--
-- <b>No give-up price is copied here.</b> It is `trade_plan`'s and a reduction never moves it, so a
-- column here would be a second statement of the one price R is measured against. That is the ruling
-- `trade_order` took one migration ago and `trigger_resolution` took two.
--
-- <b>`value_at_entry` and `fraction_at_entry` are here to answer an obligation rather than to be
-- read by a rule.</b> The position-size cap is applied by RiskGate at the plan's trigger price,
-- because that is the only price it has; the fill is a whole spread worse. These two columns are
-- what the position was actually worth once filled, so the overshoot past the cap is a figure on the
-- row rather than an argument in a comment.
--
-- <b>The two short assumptions are stamped on the row.</b> ARCHITECTURE has said since the failure
-- table was written that the borrow rate and the unmodelled availability are recorded on every short
-- trade from 4.7. A rate held only as a constant would restate every historical short at whatever
-- the constant says today, which is what `trade_plan` stores `equity` and `risk_fraction` to avoid.
--
-- Prices are TEXT holding a decimal, never REAL. `realised_r` is REAL because a result in R is a
-- ratio and not money, which is the same line `spread_bps` falls on.

CREATE TABLE position (
    position_id          TEXT    NOT NULL PRIMARY KEY,
    setup_id             TEXT    NOT NULL UNIQUE,
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

    FOREIGN KEY (setup_id) REFERENCES trade_plan (setup_id),
    FOREIGN KEY (order_id) REFERENCES trade_order (order_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_position_status ON position (status, opened_session);
CREATE INDEX ix_position_session ON position (opened_session);

CREATE TABLE fill (
    fill_id           TEXT    NOT NULL PRIMARY KEY,
    position_id       TEXT    NOT NULL,
    setup_id          TEXT    NOT NULL,
    session_date      TEXT    NOT NULL,
    ticker            TEXT    NOT NULL,
    direction         TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    leg               TEXT    NOT NULL CHECK (leg IN ('entry', 'exit')),
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

CREATE INDEX ix_fill_position ON fill (position_id, leg);
CREATE INDEX ix_fill_session ON fill (session_date, filled_at);

-- One row per run of the stage, on the pattern `order_run`, `trigger_run` and `plan_run` set.
--
-- <b>The book at both ends of the night is here because the caps read it.</b> RiskGate counts what
-- is open, and from 4.7 it counts this table rather than what it has placed inside one session. A
-- night that opened four and closed none is a night the next morning's fifth trigger will be refused
-- on, and that is worth being able to read without joining anything.
--
-- <b>Refusals are counted by reason, not as one total.</b> The same ruling PlanBuilder took over its
-- three refusal reasons and RiskGate took over its five: a single total lets the reason that is a
-- finding hide inside the reason that is ordinary.

CREATE TABLE fill_run (
    session_date        TEXT    NOT NULL,
    open_at_start       INTEGER NOT NULL,
    orders_placed       INTEGER NOT NULL,
    entries_filled      INTEGER NOT NULL,
    entries_unfilled    INTEGER NOT NULL,
    exits_filled        INTEGER NOT NULL,
    gapped              INTEGER NOT NULL,
    slipped             INTEGER NOT NULL,
    open_at_end         INTEGER NOT NULL,
    names_walked        INTEGER NOT NULL,
    minutes_walked      INTEGER NOT NULL,
    outcome             TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because     TEXT    NULL,
    observed_at         TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_fill_run_session ON fill_run (session_date);
