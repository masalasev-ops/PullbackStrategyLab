-- 014  detector_error
--
-- What a detector does when it cannot decide one stock.
--
-- The alternative is a silent skip, and a silent skip shrinks the recorded universe without
-- anyone noticing. Every count downstream is over the setups that were recorded, so a name the
-- detector could not read simply is not there: the night looks lighter, the counts look
-- plausible, and nothing anywhere says a name was lost. That is the kind of defect that
-- survives for months, which is why the failure table in ARCHITECTURE.html names this one.
--
-- Written per stock per date per direction, because that is the grain at which the loss happens
-- and because a run-level count would say a name was lost without saying which. The message is
-- stored so the same failure can be recognised across nights rather than investigated fresh.
--
-- Append-only in the sense that matters: a rerun of the same night for the same name replaces
-- nothing and collides on the key instead, so the first record of a failure is the one kept and
-- a rerun that succeeds leaves the row behind as history. Nothing reads these rows to make a
-- decision; they exist to be counted and read by a person.

CREATE TABLE detector_error (
    as_of       TEXT NOT NULL,
    ticker      TEXT NOT NULL,
    direction   TEXT NOT NULL CHECK (direction IN ('long', 'short')),
    message     TEXT NOT NULL,
    observed_at TEXT NOT NULL,
    PRIMARY KEY (as_of, ticker, direction)
);

-- The read is one night, both directions, which is what a person asking "what did last night
-- lose" makes.
CREATE INDEX ix_detector_error_as_of ON detector_error (as_of);
