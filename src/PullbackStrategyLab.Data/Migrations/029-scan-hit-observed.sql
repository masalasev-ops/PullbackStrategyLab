-- 029  scan_hit.observed_at
--
-- `scan_hit` was the only table feeding a point-in-time read with no observation stamp on it. A hit
-- inserted for a past session after the fact was invisible to every bound the lab has, so a rerun of
-- `scans` for an old date wrote rows no read could tell from the originals, and a cluster count
-- derived afterwards would have counted them without saying so.
--
-- <b>The backfill is from a recorded instant, not an invented one.</b> The obligation that raised
-- this said the 300 existing rows would need "an instant nobody recorded", and that was wrong: the
-- `scans` run of 2026-08-27 is in `run_log` with `started_at` 22:10:03.506Z, `ended_at`
-- 22:10:03.959Z and `rows_written` 300, which is exactly the number of rows. The instant was
-- recorded in another table, and reading it across is not the same act as choosing one.
--
-- Three conditions on the match, all of them present in the statement rather than assumed:
--
--   the run is a `scans` run that finished clean, so a partial walk cannot stamp rows it never
--   reached;
--
--   its own session date, taken in the session zone rather than in UTC, equals the hits' `as_of`.
--   `-5 hours` is standard time, which is the larger offset, and the `scans` slot runs at 18:10
--   Eastern, so both offsets land on the same date and the conservative one cannot land early;
--
--   and its `rows_written` equals the number of hits carried for that date. This is what makes it a
--   match rather than an association. Two runs for one date, or a count that disagrees, leaves the
--   rows null, which is the honest answer and is what the reads then refuse.
--
-- `ended_at` rather than `started_at`, because it is the latest instant any of those rows could have
-- been written and a bound must never claim a row existed earlier than it did.
-- see: A reader's signature does not establish point-in-time; the query does

ALTER TABLE scan_hit ADD COLUMN observed_at TEXT NULL;

UPDATE scan_hit
   SET observed_at = (
       SELECT r.ended_at
         FROM run_log r
        WHERE r.stage = 'scans'
          AND r.outcome = 'clean'
          AND r.ended_at IS NOT NULL
          AND date(r.started_at, '-5 hours') = scan_hit.as_of
          AND r.rows_written = (SELECT COUNT(*) FROM scan_hit h WHERE h.as_of = scan_hit.as_of))
 WHERE observed_at IS NULL
