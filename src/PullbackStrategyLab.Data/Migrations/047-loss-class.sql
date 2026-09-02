-- 047  loss_class and loss_run
--
-- <b>Two answers per loss and they arrive at different times, which is why this table is updated.</b>
-- The mechanism names how the loss occurred and is known the moment the trade closes: the exit
-- either filled at an open past the price it named or it crossed the book at that price. The
-- aftermath names what happened next and cannot be known for ten sessions after the trigger. Holding
-- the first back until the second exists would be discarding an answer the lab already has, which is
-- what the recording floor refuses everywhere else.
-- see: A stop-out is noise when the ten-day return reached one R, and cause of loss is two questions rather than one ordered list
--
-- <b>So it carries two stamps, on exactly the terms `position` carries three.</b> An update
-- overwrites a state without moving the stamp that says when it was observed, so a replay standing
-- between the close and the horizon has to see a row with a mechanism and no aftermath. That is what
-- it was.
--
-- <b>A row awaiting its horizon carries null and not `unclassified`.</b> The two are different facts:
-- null is a question the lab cannot answer yet, and `unclassified` is one it could answer and could
-- not place. Collapsing them would make the taxonomy's own coverage unreadable, which is the reason
-- `unclassified` exists as a value at all.
--
-- <b>The mechanism is read from the exit fill's basis and not from the size of the loss.</b>
-- ARCHITECTURE's failure table has said since it was written that a gap loss is a "loss larger than
-- one unit of risk", and that detector fires on every ordinary stop-out: a round trip costs two
-- crossings, so an ordinary stop loses slightly more than one unit of risk by construction. A
-- taxonomy whose largest bucket is guaranteed to hold every member of another is one whose shares
-- mean nothing. The document is corrected at 4.10 rather than the code being written to it.
--
-- <b>Both questions are asked of every loss.</b> A gap loss that later recovers satisfies both
-- without contradiction, and it can only do so if the second question is put to it; asking the
-- aftermath only of the losses that were not gaps is what a single ranked list would have done.

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

    FOREIGN KEY (trade_id) REFERENCES trade (trade_id),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE INDEX ix_loss_class_awaiting ON loss_class (aftermath, closed_session);
CREATE INDEX ix_loss_class_session ON loss_class (closed_session, direction);

-- One row per run, on the pattern the four run rows of the trading night already set.
--
-- <b>The two passes are counted apart because they are two questions.</b> A night that classified
-- three mechanisms and no aftermaths is an ordinary night early in a horizon; a night that wrote
-- three aftermaths and no mechanisms is an ordinary night ten sessions later. One total would make
-- both read the same.

CREATE TABLE loss_run (
    session_date          TEXT    NOT NULL,
    losses_closed         INTEGER NOT NULL,
    mechanisms_written    INTEGER NOT NULL,
    gap                   INTEGER NOT NULL,
    ordinary              INTEGER NOT NULL,
    longs                 INTEGER NOT NULL,
    shorts                INTEGER NOT NULL,
    awaiting_aftermath    INTEGER NOT NULL,
    aftermaths_written    INTEGER NOT NULL,
    noise                 INTEGER NOT NULL,
    failed_setup          INTEGER NOT NULL,
    unclassified          INTEGER NOT NULL,
    outcome               TEXT    NOT NULL CHECK (outcome IN ('clean', 'partial', 'failed')),
    stopped_because       TEXT    NULL,
    observed_at           TEXT    NOT NULL,
    PRIMARY KEY (session_date, observed_at)
);

CREATE INDEX ix_loss_run_session ON loss_run (session_date);
