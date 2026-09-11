-- 062  run_log, able to say a slot did not run and why
--
-- A slot that did not run left nothing. On 2026-09-07 thirty-one of the thirty-two slots due
-- refused on the tree guard and run_log held no row for the date, so the store could not tell a
-- refused night from a night the machine was off, a night the market was closed or a night nobody
-- scheduled. The guard exits before the worker starts and RunLogger is the table's one declared
-- writer, so the refusal goes to the night's log and the next night's reconciliation writes the
-- row through RunLogger.
--
-- Three columns, and all three are null on every run that happened:
--
--   slot                 the slot tools/nightly.ps1 dispatches by, which is what an operator
--                        reruns. A stage cannot say which slot ran it, so it is not asked to.
--   session_date         the session the row is about. started_at is when the row was written,
--                        which for a slot that never ran is the next night, so the session has to
--                        be stated rather than read off the instant.
--   did_not_run_because  one of the four in DidNotRunBecause.Reasons. slot-roster reconciles this
--                        list against that one in both directions.
--
-- outcome gains 'did-not-run', present exactly when the reason is, which the store holds as a
-- CHECK so a row cannot carry one without the other. One such row per slot and session, held by a
-- partial unique index, so the reconciliation can be run every night over the week before it and
-- write each fact once.
--
-- SQLite cannot widen a CHECK in place, so the table is rebuilt. Nothing references run_log, every
-- row is copied verbatim, and MigrationRowSurvivalTests is where the rebuild proves it lost nothing.

CREATE TABLE run_log_did_not_run (
    run_id                  TEXT    NOT NULL PRIMARY KEY,
    stage                   TEXT    NOT NULL,
    started_at              TEXT    NOT NULL,
    ended_at                TEXT    NULL,
    outcome                 TEXT    NULL CHECK (outcome IS NULL OR outcome IN ('clean', 'partial', 'failed', 'did-not-run')),
    rows_written            INTEGER NULL CHECK (rows_written IS NULL OR rows_written >= 0),
    calls_used              INTEGER NOT NULL DEFAULT 0 CHECK (calls_used >= 0),
    counts_against_ceiling  INTEGER NOT NULL DEFAULT 1 CHECK (counts_against_ceiling IN (0, 1)),
    skipped                 INTEGER NULL,
    slot                    TEXT    NULL,
    session_date            TEXT    NULL,
    did_not_run_because     TEXT    NULL CHECK (did_not_run_because IS NULL OR did_not_run_because IN
                                ('refused-by-tree-guard', 'market-closed', 'paused-by-operator', 'fault')),
    CHECK ((outcome IS 'did-not-run') = (did_not_run_because IS NOT NULL)),
    CHECK (did_not_run_because IS NULL OR (slot IS NOT NULL AND session_date IS NOT NULL))
);

INSERT INTO run_log_did_not_run (
    run_id, stage, started_at, ended_at, outcome, rows_written, calls_used,
    counts_against_ceiling, skipped)
SELECT
    run_id, stage, started_at, ended_at, outcome, rows_written, calls_used,
    counts_against_ceiling, skipped
FROM run_log;

DROP TABLE run_log;

ALTER TABLE run_log_did_not_run RENAME TO run_log;

CREATE INDEX ix_run_log_started_at ON run_log (started_at);

CREATE INDEX ix_run_log_stage_started_at ON run_log (stage, started_at);

CREATE UNIQUE INDEX ux_run_log_did_not_run ON run_log (slot, session_date)
    WHERE did_not_run_because IS NOT NULL;
