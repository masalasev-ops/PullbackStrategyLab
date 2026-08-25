-- 008  run_log.counts_against_ceiling
--
-- The daily ceiling is the nightly job's guard. A one-time operation is not the nightly job,
-- and charging the two against each other is what made the backfill look like a two-day
-- procedure: it was not too big for the vendor, it was too big for a budget that had already
-- spent most of itself on the evening's work.
--
-- So a run says whether its calls count. The calls themselves are still recorded either way,
-- because the question "what did this cost" is worth answering about every run; what changes
-- is whether the nightly total is allowed to see them.
--
-- A column with a default rather than a rebuild, so nothing is copied and nothing can be lost.
-- Existing runs default to counting, which is what they did.

ALTER TABLE run_log ADD COLUMN counts_against_ceiling INTEGER NOT NULL DEFAULT 1
    CHECK (counts_against_ceiling IN (0, 1));
