-- 027  setup.corrected_from
--
-- The check results as they stood before a correction, verbatim.
--
-- A correction that records only that it happened is auditable in the weakest sense: a reader can
-- see the row was touched and cannot see what it said. The two questions anybody will actually ask
-- are whether the verdict was absent or merely different, and whether a corrected population can be
-- put back the way it was. Both need the prior text rather than a flag.
-- see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
--
-- The whole JSON rather than the one verdict, because the column it restores is the whole JSON and
-- a partial record would need the restore to reconstruct the rest. It is small: a setup's check
-- results are twenty verdicts.
--
-- Null wherever `corrected_at` is null, written in the same statement as the mark and the lateness,
-- so a corrected row can never carry a mark without the state it was corrected from.

ALTER TABLE setup ADD COLUMN corrected_from TEXT NULL
