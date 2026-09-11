-- 063  check_definition, the gate lists as data with the dates each check was in force
--
-- `check-completeness` has claimed since 2026-08-26 that every setup row records a result for every
-- check defined at its date, and it compared every row against today's list. A check added to the
-- detectors would have read every historical row as missing it, and a check retired would have read
-- every later row as missing one that no longer runs; the label was date-aware and the comparison
-- never was, because nothing stored which checks were in force on which night.
--
-- So the lists become data, on the pattern `signal_definition` set: the section of code that is the
-- specification stays where it is, being `SetupChecks`, and this table is written from it by the one
-- component that registers a night's list, reconciled against it in both directions.
-- see: The signal library stays a spec section and gains a runtime table, reconciled in both directions
--
-- One row per check, side and introduction. A check retired and brought back is a second row rather
-- than a rewritten one, so the dates a check was in force are never overwritten. A retirement is an
-- update of `retired_on` and `retired_observed_at` together, and `observed_at` is never touched after
-- the insert, so a read as of an earlier instant sees the row as it stood then: introduced, and not
-- yet retired.
--
-- Per side, because the two gate lists are two lists and a check name can appear on both.
-- see: Long and short are never pooled into one figure

CREATE TABLE check_definition (
    check_name           TEXT NOT NULL,
    direction            TEXT NOT NULL CHECK (direction IN ('long', 'short')),

    -- The first session the check was in force for, and the first it was not. Dates, because what
    -- the list is keyed to is the session a row was flagged on.
    introduced_on        TEXT NOT NULL,
    retired_on           TEXT NULL,

    -- When the row was written and when its retirement was, which is what a read bounds on.
    observed_at          TEXT NOT NULL,
    retired_observed_at  TEXT NULL,

    PRIMARY KEY (check_name, direction, introduced_on),
    CHECK ((retired_on IS NULL) = (retired_observed_at IS NULL)),
    CHECK (retired_on IS NULL OR retired_on > introduced_on)
);

CREATE INDEX ix_check_definition_direction ON check_definition (direction, introduced_on);
