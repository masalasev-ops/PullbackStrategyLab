-- 001  run_log
--
-- Every stage writes a start and an end entry here, so the table is delivered at
-- checkpoint 1.1 rather than with the research machinery.
--
-- One writer, not one per stage. Stages do not write this table; they call RunLogger,
-- which owns both operations. Declaring every stage as a writer would put the run
-- accounting logic in a dozen places and writer-ownership could never pass.
--
-- rows_written is measured from the store by RunLogger rather than reported by the
-- stage. A stage counting its own output reports what it believes it wrote, and the
-- nightly halt keys on this number.

CREATE TABLE run_log (
    run_id       TEXT    NOT NULL PRIMARY KEY,
    stage        TEXT    NOT NULL,
    started_at   TEXT    NOT NULL,
    ended_at     TEXT    NULL,
    outcome      TEXT    NULL CHECK (outcome IS NULL OR outcome IN ('clean', 'partial', 'failed')),
    rows_written INTEGER NULL CHECK (rows_written IS NULL OR rows_written >= 0),
    calls_used   INTEGER NOT NULL DEFAULT 0 CHECK (calls_used >= 0)
);

-- The daily call budget sums calls_used over one UTC date, and the runbook reruns a
-- single named stage, so both reads are indexed.
CREATE INDEX ix_run_log_started_at ON run_log (started_at);
CREATE INDEX ix_run_log_stage_started_at ON run_log (stage, started_at);
