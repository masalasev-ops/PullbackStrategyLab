-- 031  the trade geometry, able to say it is absent
--
-- trigger_price, stop_price and stop_distance_ranges were TEXT NOT NULL, so a setup whose
-- geometry the detector could not compute had nowhere to record that. The detector wrote
-- `evidence.Pullback?.Trigger ?? 0m` and the two beside it, and the flattening survived
-- everywhere downstream: SignalVectorizer froze stop_distance_ranges as the literal 0 into
-- setup_signal, which is written once and never updated, and the gallery rendered a trade with
-- a give-up of nothing as though it were a trade.
--
-- The row that shows it is in the golden fixture. 2026-08-24-INTC-short records `bounce-shape`
-- failed with "0 bar(s)" and `exit-tight` failed with value null and the note "no stop or no
-- daily range for the session", so the detector said absent twice on the same row. The frozen
-- signal for the same setup on the same night says stop_distance_ranges = 0, and trigger_price
-- and stop_price are both 85.14, which is a trade whose give-up is zero and which clears every
-- threshold written as a maximum.
--
-- So the column gets to say what the detector already knew.
-- see: A gate handed an absent or degenerate quantity fails rather than passing
--
-- <b>Existing rows are copied verbatim and no value is rewritten.</b> A row whose
-- stop_distance_ranges is the literal 0 is one the detector meant as absent, and turning it into
-- NULL here would be reconstructing a decision from a sentinel, against the rule that no trigger,
-- stop, size or gating verdict is ever rewritten. The flattened rows stay as they are, they are
-- recorded in PROGRESS as an artefact of the columns they were written into, and every row
-- written after this migration says absent when it is absent. The golden fixture is rebuilt from
-- its inputs on every run, so its expectations move with the code rather than with the store.
--
-- SQLite cannot relax NOT NULL in place, so both tables are rebuilt. That is the idiom migrations
-- 005 and 009 established and MigrationRowSurvivalTests is where a rebuild proves it lost nothing.

CREATE TABLE setup_geometry_absent (
    setup_id              TEXT    NOT NULL PRIMARY KEY,
    as_of                 TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    check_results         TEXT    NOT NULL,
    passed_all            INTEGER NOT NULL CHECK (passed_all IN (0, 1)),
    rank                  INTEGER NULL,
    capped_out            INTEGER NULL CHECK (capped_out IS NULL OR capped_out IN (0, 1)),
    trigger_price         TEXT    NULL,
    stop_price            TEXT    NULL,
    stop_distance_ranges  TEXT    NULL,
    agreement             TEXT    NULL CHECK (agreement IS NULL OR agreement IN ('agree', 'disagree')),
    agreement_note        TEXT    NULL,
    thrust_scan           TEXT    NULL,
    thrust_session        TEXT    NULL,
    corrected_at          TEXT    NULL,
    corrected_because     TEXT    NULL,
    correction_lateness_minutes INTEGER NULL,
    corrected_from        TEXT    NULL,
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

INSERT INTO setup_geometry_absent (
    setup_id, as_of, ticker, direction, check_results, passed_all, rank, capped_out,
    trigger_price, stop_price, stop_distance_ranges, agreement, agreement_note,
    thrust_scan, thrust_session,
    corrected_at, corrected_because, correction_lateness_minutes, corrected_from)
SELECT
    setup_id, as_of, ticker, direction, check_results, passed_all, rank, capped_out,
    trigger_price, stop_price, stop_distance_ranges, agreement, agreement_note,
    thrust_scan, thrust_session,
    corrected_at, corrected_because, correction_lateness_minutes, corrected_from
FROM setup;

DROP TABLE setup;

ALTER TABLE setup_geometry_absent RENAME TO setup;

CREATE UNIQUE INDEX ux_setup_night ON setup (as_of, ticker, direction);

CREATE INDEX ix_setup_as_of ON setup (as_of, direction);

CREATE TABLE calibration_setup_geometry_absent (
    setup_id              TEXT    NOT NULL PRIMARY KEY,
    as_of                 TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    check_results         TEXT    NOT NULL,
    passed_all            INTEGER NOT NULL CHECK (passed_all IN (0, 1)),
    rank                  INTEGER NULL,
    capped_out            INTEGER NULL CHECK (capped_out IS NULL OR capped_out IN (0, 1)),
    trigger_price         TEXT    NULL,
    stop_price            TEXT    NULL,
    stop_distance_ranges  TEXT    NULL,
    agreement             TEXT    NULL CHECK (agreement IS NULL OR agreement IN ('agree', 'disagree')),
    agreement_note        TEXT    NULL,
    thrust_scan           TEXT    NULL,
    thrust_session        TEXT    NULL
);

INSERT INTO calibration_setup_geometry_absent (
    setup_id, as_of, ticker, direction, check_results, passed_all, rank, capped_out,
    trigger_price, stop_price, stop_distance_ranges, agreement, agreement_note,
    thrust_scan, thrust_session)
SELECT
    setup_id, as_of, ticker, direction, check_results, passed_all, rank, capped_out,
    trigger_price, stop_price, stop_distance_ranges, agreement, agreement_note,
    thrust_scan, thrust_session
FROM calibration_setup;

DROP TABLE calibration_setup;

ALTER TABLE calibration_setup_geometry_absent RENAME TO calibration_setup;

CREATE UNIQUE INDEX ux_calibration_setup_night ON calibration_setup (as_of, ticker, direction);

CREATE INDEX ix_calibration_setup_as_of ON calibration_setup (as_of, direction);
