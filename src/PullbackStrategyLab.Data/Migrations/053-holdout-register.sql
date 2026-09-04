-- The holdout register: the eight quarters of forward-collected evidence, and what each was spent
-- on.
--
-- <b>Two tables and not one, and the split is the rule rather than a tidiness.</b> A spent window is
-- never re-spent, and the whole point of that rule is that nothing may quietly break it. Held as a
-- nullable column on the window row, the rule would live in whatever `UPDATE` statement happened to
-- carry the right `WHERE` clause, and a second statement written years from now would spend a window
-- twice with the store agreeing. Held as a row whose primary key is the window, the second spend is
-- refused by SQLite before any code sees it: there is one row per window and a second insert
-- collides.
-- (see: Holdout windows are quarters of forward-collected evidence, allocated as they mature, capped at eight)
--
-- <b>Both tables are insert-only and neither has an update path at all.</b> A window is a fact about
-- the calendar and a spend is a fact about a decision, and neither becomes untrue.

-- One quarter the lab collected in full. Written the day the quarter completes, never before.
--
-- The window's identity is the quarter and nothing else, so a register rebuilt from the same first
-- session produces the same eight names in the same order.
CREATE TABLE holdout_window (
    window_id      TEXT    NOT NULL PRIMARY KEY,

    -- One to eight, oldest first, which is the order they are spent in. Unique as well as bounded:
    -- the cap is a fact about the register rather than a count somebody keeps, so an attempt to
    -- record a ninth fails on the CHECK and an attempt to record a second first fails on the index.
    ordinal        INTEGER NOT NULL CHECK (ordinal BETWEEN 1 AND 8),

    quarter_start  TEXT    NOT NULL,
    quarter_end    TEXT    NOT NULL,

    -- The first day after the quarter, which is the earliest date the window can be spent on. A
    -- window is not available on the last day of its own quarter, because that day's session has
    -- not closed.
    matures_on     TEXT    NOT NULL,

    recorded_at    TEXT    NOT NULL,

    CHECK (quarter_end > quarter_start),
    CHECK (matures_on > quarter_end)
);

CREATE UNIQUE INDEX ux_holdout_window_ordinal ON holdout_window (ordinal);

-- One spend of one window. The primary key is the window, so a window can be spent once.
--
-- <b>This key is the rule, and it is the reason the spend is not a column on the row above.</b> A
-- test spends a window, attempts it again and reads back a constraint violation from the store
-- itself rather than a refusal from a stage that remembered to check.
CREATE TABLE holdout_spend (
    window_id   TEXT NOT NULL PRIMARY KEY,

    -- What it was spent on and what came of it, which is the whole of what a register is for: a
    -- budget that records only that something was spent cannot say whether spending it was worth it.
    spent_on    TEXT NOT NULL,
    outcome     TEXT NOT NULL,

    spent_at    TEXT NOT NULL,

    FOREIGN KEY (window_id) REFERENCES holdout_window (window_id)
);

-- What one run of the registry did.
--
-- <b>`matured` and `recorded` are two numbers because a register holding nothing has two causes.</b>
-- No quarter has completed yet, which is the ordinary state for the first three months and is not a
-- fault, and quarters have completed and nothing wrote them down, which is. One count could not tell
-- them apart, and they will read identically for as long as the first state lasts.
CREATE TABLE holdout_run (
    observed_at       TEXT    NOT NULL PRIMARY KEY,

    as_of             TEXT    NOT NULL,

    -- The earliest session the evidence store holds, which is what the whole schedule is computed
    -- from. Null where the store holds none, which is a third state again: no quarter has begun.
    first_session     TEXT    NULL,

    matured           INTEGER NOT NULL CHECK (matured BETWEEN 0 AND 8),
    recorded          INTEGER NOT NULL CHECK (recorded BETWEEN 0 AND 8),
    written           INTEGER NOT NULL CHECK (written >= 0),
    spent             INTEGER NOT NULL CHECK (spent BETWEEN 0 AND 8),
    available         INTEGER NOT NULL CHECK (available BETWEEN 0 AND 8),

    outcome           TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),

    -- Why the register holds no window available to spend, present exactly when none is. Three
    -- readings and they are different facts: no session recorded, no quarter matured yet, and every
    -- matured window already spent.
    empty_because     TEXT    NULL,

    stopped_because   TEXT    NULL,

    CHECK ((available = 0) = (empty_because IS NOT NULL)),
    CHECK (recorded <= matured)
);
