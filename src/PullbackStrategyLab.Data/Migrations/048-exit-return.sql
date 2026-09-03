-- 048  the second aftermath figure, measured to the exit
--
-- <b>The aftermath gains a second figure and the first is not replaced.</b> `forward_return_signed`
-- is what the day offered: the direction-signed return from the trigger to the adjusted close of the
-- tenth session after the one the trigger was touched in. `exit_return_signed` is what the trade
-- earned: the same return taken to the exit fill instead. The gap between them is what the trail
-- rule is judged on, because with one figure a trail that captured a move and a trail that gave one
-- back are the same number. Neither replaces the other and nothing adds them together.
-- see: The aftermath is measured from the exit as well as from the close, as two figures and never one
--
-- <b>Written when the aftermath is, beside the first figure, and never alone.</b> The exit is known
-- the night the trade closes and the figure is still held until the horizon closes, because it is
-- one half of a pair and the pair is the thing being judged: a row carrying what the trade earned
-- and not yet what the day offered would state half a comparison. Both ends are put on the adjusted
-- basis through their own session's bar, so a split between the trigger and the exit is not a move,
-- which is the care the first figure already takes; where the store holds no bar for the session the
-- trade closed in the figure is absent and the sentence beside it says so.
--
-- <b>Rebuilt rather than altered, on the terms migration 045 rebuilt `fill`.</b> SQLite cannot add a
-- CHECK to a column it adds, and the constraint is the point: the second figure may be present only
-- where the first is. Nothing holds a foreign key into `loss_class`, so the rename rewrites no child
-- clause, and the live store holds no row in it because no position has ever closed at a loss.

DROP INDEX ix_loss_class_awaiting;
DROP INDEX ix_loss_class_session;

ALTER TABLE loss_class RENAME TO loss_class_before_048;

CREATE TABLE loss_class (
    trade_id              TEXT    NOT NULL PRIMARY KEY,
    setup_id              TEXT    NOT NULL UNIQUE,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    closed_session        TEXT    NOT NULL,

    -- What is being explained, carried so a classification reads without a join.
    net_pnl               TEXT    NOT NULL,
    result_r              REAL    NOT NULL,

    -- The first question, answered at the close.
    mechanism             TEXT    NOT NULL CHECK (mechanism IN ('gap', 'ordinary')),
    exit_basis            TEXT    NOT NULL CHECK (exit_basis IN ('slipped', 'gapped')),

    -- The second, answered when the horizon closes. Null while it has not.
    aftermath             TEXT    NULL CHECK (aftermath IS NULL OR aftermath IN ('noise', 'failed-setup', 'unclassified')),
    forward_return_signed TEXT    NULL,
    one_r_in_return       TEXT    NULL,

    -- What the trade earned, beside what the day offered. Two figures and never one.
    exit_return_signed    TEXT    NULL,

    aftermath_because     TEXT    NULL,

    observed_at           TEXT    NOT NULL,
    aftermath_observed_at TEXT    NULL,

    -- The aftermath and its stamp arrive together, in both directions, so a row cannot carry an
    -- answer nothing dated or a date with no answer.
    CHECK ((aftermath IS NULL) = (aftermath_observed_at IS NULL)),

    -- The two figures the boundary was read from are present exactly when the answer came from them,
    -- which is every aftermath but `unclassified`. That value means the horizon closed and the
    -- figure was absent, so a row carrying both would be one nobody could tell from a placed one.
    CHECK ((aftermath = 'noise' OR aftermath = 'failed-setup') = (forward_return_signed IS NOT NULL)),
    CHECK ((forward_return_signed IS NULL) = (one_r_in_return IS NULL)),

    -- The second figure is written beside the first and never on its own: what the trade earned is
    -- half of a comparison, and the other half is what the day offered.
    CHECK (exit_return_signed IS NULL OR forward_return_signed IS NOT NULL),

    FOREIGN KEY (trade_id) REFERENCES trade (trade_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO loss_class (
    trade_id, setup_id, ticker, direction, closed_session, net_pnl, result_r, mechanism, exit_basis,
    aftermath, forward_return_signed, one_r_in_return, exit_return_signed, aftermath_because,
    observed_at, aftermath_observed_at)
SELECT
    trade_id, setup_id, ticker, direction, closed_session, net_pnl, result_r, mechanism, exit_basis,
    aftermath, forward_return_signed, one_r_in_return, NULL, aftermath_because,
    observed_at, aftermath_observed_at
  FROM loss_class_before_048;

DROP TABLE loss_class_before_048;

CREATE INDEX ix_loss_class_awaiting ON loss_class (aftermath, closed_session);
CREATE INDEX ix_loss_class_session ON loss_class (closed_session, direction);
