-- 011  setup, calibration_setup, setup_signal
--
-- The spine of the whole system, and the two tables that sit either side of it.
--
-- `setup` holds rows immutable after write, except by a correction the lateness bound admits, in the sense that
-- matters: the detector's own columns are never revisited by anything improving a plan. SCHEMA declares two later updates on columns the detector does not own,
-- SetupCapper on rank and capped_out and the setup inspector on the two agreement columns, and
-- those are the only writes any component makes after the row exists.
--
-- Two detectors write it on disjoint rows rather than disjoint columns, separated by direction.
-- Neither may ever write a row of the other's direction and a test asserts both ways, which is
-- why direction is constrained here rather than trusted: a check that reads the table can then
-- assume the value is one of two things.
--
-- `calibration_setup` has the same shape and is read by nobody. A historical detector run is
-- reconstructed against membership as it stands today, because the nightly universe snapshot
-- only starts when the lab does, so every reconstructed row carries survivorship bias. Counting
-- them is unaffected and measuring anything with them would be destroyed, so they go here and
-- the evidence store stays empty until the first forward night.
-- see: The evidence store holds only setups flagged forward, never setups reconstructed from history
--
-- No foreign key from calibration_setup to security, deliberately, and it is worth saying why
-- when setup has one. A calibration run walks years of history and can reach a ticker that was
-- listed then and is not in `security` now; refusing that row would silently shrink the count
-- the run exists to produce, which is the one thing it must not do.
--
-- `setup_signal` is the frozen point-in-time evidence: written once, never updated, by
-- SignalVectorizer nightly and by SignalBackfiller for signals added later. The two are disjoint
-- by date and signal, and the backfiller may never touch a signal the vectorizer owns for that
-- date.
--
-- Prices are TEXT holding a decimal. stop_distance_ranges is a ratio and goes through
-- StoreText.RatioToStorageText, because it is a count of daily ranges rather than money.

CREATE TABLE setup (
    setup_id              TEXT    NOT NULL PRIMARY KEY,
    as_of                 TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    check_results         TEXT    NOT NULL,
    passed_all            INTEGER NOT NULL CHECK (passed_all IN (0, 1)),
    rank                  INTEGER NULL,
    capped_out            INTEGER NULL CHECK (capped_out IS NULL OR capped_out IN (0, 1)),
    trigger_price         TEXT    NOT NULL,
    stop_price            TEXT    NOT NULL,
    stop_distance_ranges  TEXT    NOT NULL,
    agreement             TEXT    NULL CHECK (agreement IS NULL OR agreement IN ('agree', 'disagree')),
    agreement_note        TEXT    NULL,
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

-- One row per ticker per direction per night, asserted by the store rather than by the detector.
-- Two detectors writing the same table makes a duplicate a real possibility rather than a
-- theoretical one, and a duplicated setup would be counted twice by everything downstream.
CREATE UNIQUE INDEX ux_setup_night ON setup (as_of, ticker, direction);

-- The nightly read is one session, both directions, ranked. The gallery at 2.9 and the capper at
-- 2.8 both make exactly that read, which the primary key on setup_id does not order.
CREATE INDEX ix_setup_as_of ON setup (as_of, direction);

CREATE TABLE calibration_setup (
    setup_id              TEXT    NOT NULL PRIMARY KEY,
    as_of                 TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    check_results         TEXT    NOT NULL,
    passed_all            INTEGER NOT NULL CHECK (passed_all IN (0, 1)),
    rank                  INTEGER NULL,
    capped_out            INTEGER NULL CHECK (capped_out IS NULL OR capped_out IN (0, 1)),
    trigger_price         TEXT    NOT NULL,
    stop_price            TEXT    NOT NULL,
    stop_distance_ranges  TEXT    NOT NULL,
    agreement             TEXT    NULL CHECK (agreement IS NULL OR agreement IN ('agree', 'disagree')),
    agreement_note        TEXT    NULL
);

CREATE UNIQUE INDEX ux_calibration_setup_night ON calibration_setup (as_of, ticker, direction);

-- The calibration run's whole output is a count per night per direction, so that is the read.
CREATE INDEX ix_calibration_setup_as_of ON calibration_setup (as_of, direction);

CREATE TABLE setup_signal (
    setup_id     TEXT NOT NULL,
    signal_name  TEXT NOT NULL,
    value        TEXT NOT NULL,
    computed_at  TEXT NOT NULL,
    PRIMARY KEY (setup_id, signal_name),
    FOREIGN KEY (setup_id) REFERENCES setup (setup_id)
);

-- The key is the grain and it is what makes "written once" enforceable rather than merely
-- intended: a second write of the same signal for the same setup collides instead of quietly
-- landing beside the first. computed_at is recorded and is not part of the key, because a signal
-- has one value for a setup and the instant says when it was frozen, not which one is current.
