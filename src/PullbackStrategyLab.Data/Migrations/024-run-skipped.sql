-- 024  run_log.skipped
--
-- How many names a run passed over after a failure it survived.
--
-- rows_written cannot carry this and cannot carry anything else about an update-only stage.
-- RunScope measures it as a row-count delta over the tables the stage declares, so `sectors`,
-- which only ever issues UPDATE against `security`, reports 0 on a clean run and 0 on a failed
-- one. On 2026-08-27 it recorded outcome `failed`, 149 calls and 0 rows, and 0 rows is exactly
-- what a perfect run would have recorded. The column is null on a run that skipped nothing,
-- rather than 0, so a stage that does not walk a list is distinguishable from one that walked
-- it cleanly.
--
-- Nullable and with no default, because a run recorded before this column existed skipped an
-- unknown number rather than none, and a default of 0 would assert something about sixty-one
-- runs nobody measured.

ALTER TABLE run_log ADD COLUMN skipped INTEGER NULL
