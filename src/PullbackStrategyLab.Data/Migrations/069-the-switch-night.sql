-- 069  which gate set scored a setup row, and generation 0's record kept beside generation 1's
--
-- <b>`setup` gains a generation and keeps its key.</b> From the switch night the detectors score the
-- rows they write to `setup` under generation 1's gate set, and every row before that night was
-- scored under generation 0's. The unique key on the night, the name and the side stands, because one
-- detector per side writes `setup` on any night and the lab's own record is generation 1's from that
-- night on. Every existing row is generation 0's, which is what it was.
-- see: Generation 0 is retired as measuring the entry-level mismatch, and generation 1 registers only once its rule is whole
--
-- <b>`setup_generation_zero` is generation 0's detector's record from the switch night</b>, for
-- comparison only, on the footing `below_floor` and `calibration_setup` already stand on: a second
-- population is a table rather than a flag. It carries the columns a comparison reads, the verdicts
-- and the evening's geometry, and nothing the nightly pipeline writes after detection, because nothing
-- in the pipeline reads it. The comparison joins the two on the night, the name and the side. No
-- foreign key to `setup`: a name generation 0 flags and generation 1 does not has no row there, and
-- that is the half of the comparison worth reading.

ALTER TABLE setup ADD COLUMN generation INTEGER NOT NULL DEFAULT 0 CHECK (generation >= 0);

CREATE TABLE setup_generation_zero (
    setup_id              TEXT    NOT NULL PRIMARY KEY,
    as_of                 TEXT    NOT NULL,
    ticker                TEXT    NOT NULL,
    direction             TEXT    NOT NULL CHECK (direction IN ('long', 'short')),
    check_results         TEXT    NOT NULL,
    passed_all            INTEGER NOT NULL CHECK (passed_all IN (0, 1)),
    trigger_price         TEXT    NULL,
    stop_price            TEXT    NULL,
    stop_distance_ranges  TEXT    NULL,
    thrust_scan           TEXT    NULL,
    thrust_session        TEXT    NULL,
    observed_at           TEXT    NOT NULL,
    FOREIGN KEY (ticker) REFERENCES security (ticker)
);

CREATE UNIQUE INDEX ux_setup_generation_zero_night ON setup_generation_zero (as_of, ticker, direction);
