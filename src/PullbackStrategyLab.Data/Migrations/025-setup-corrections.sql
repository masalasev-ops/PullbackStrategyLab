-- 025  setup.corrected_at, setup.corrected_because
--
-- The mark that makes a corrected row distinguishable from one that was right the first time.
--
-- 011 says this table is immutable after write "in the sense that matters: the detector's own
-- columns are never revisited", and lists the two later updates on columns the detector does not
-- own. This is the third, and it is the first on a column the detector does own, so it is the one
-- that needs the mark.
--
-- The permission is narrow and the columns are what enforce the narrow half. A setup row may be
-- corrected only where the correction uses no information the night did not have, which is two
-- conditions: the inputs are bounded to the setup's own date, and the row records that it was
-- corrected. The bound lives in the query CheckRecomputer issues; the record lives here.
-- see: A setup row is corrected only where the correction uses no information the night did not have
--
-- Null on every row until something corrects one. Written as a pair, because a correction with no
-- reason recorded is indistinguishable from the plan-improvement immutability exists to refuse,
-- and a later reader excluding corrected rows has to be able to say why each one was corrected.
--
-- Nothing here is added to calibration_setup. A calibration row is not evidence and nothing
-- downstream reads it, so there is no reader for whom a correction mark would mean anything.

ALTER TABLE setup ADD COLUMN corrected_at TEXT NULL;

ALTER TABLE setup ADD COLUMN corrected_because TEXT NULL
