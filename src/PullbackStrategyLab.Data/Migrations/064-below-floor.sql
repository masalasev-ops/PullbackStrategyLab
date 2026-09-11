-- 064  below_floor, the names that missed the recording floor, kept with the verdicts that sank them
--
-- A detector evaluates every check on every name it examines and keeps a row only for a name that
-- clears the recording floor: four cheap checks a side. Everything else was counted and thrown away,
-- 7,201 names a side on the golden fixture's night, so a later change to the floor could only ever be
-- measured forward: nothing held what the names below it had scored. This is the record that makes
-- such a change replayable rather than forward-only.
--
-- A table rather than rows in `setup` with a flag, and that is the property rather than a
-- preference. Membership in `setup` is itself the fact six readers consume, being that the name was
-- flagged: the minute fetch buys for it, the control pool draws against it, band 1 counts it and the
-- plan and the vendor budget follow. A below-floor name was not flagged, and one predicate away from
-- every one of those reads is where it must never be, on the same grounds `calibration_setup` is a
-- table rather than a flag. Nothing in the nightly pipeline reads this table.
--
-- `check_results` has the shape a setup row gives it, and `failed_floor` names which of the side's
-- floor clauses the name failed. `generation` says whose gate set produced the vector: generation 0's
-- detector writes it today, and from the switch night the live detector is generation 1's, whose
-- clauses are not these ten, so a vector that did not say which list it was scored under could not be
-- read at all.
-- see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole

CREATE TABLE below_floor (
    as_of          TEXT    NOT NULL,
    ticker         TEXT    NOT NULL,
    direction      TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    generation     INTEGER NOT NULL DEFAULT 0 CHECK (generation >= 0),
    check_results  TEXT    NOT NULL,
    failed_floor   TEXT    NOT NULL,
    observed_at    TEXT    NOT NULL,
    PRIMARY KEY (as_of, ticker, direction, generation),
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);
