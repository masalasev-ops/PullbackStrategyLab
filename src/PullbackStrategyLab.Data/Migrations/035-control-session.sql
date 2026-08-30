-- 035  control_setup.control_as_of
--
-- Which session a control was drawn from, which stopped being the setup's own session when the
-- tight set was allowed to reach across nights.
--
-- The tight set is declared to match on the trend ladder AND the market mood, and the second half
-- had never been implemented because within one night it cannot be: the mood is a property of the
-- session, so every candidate on a given night carries the same one and matching on it excludes
-- nothing. The operator ruled on 2026-08-30 that the dimension is kept and made real, so the tight
-- set draws from any session sharing the mood and the loose set stays within the night.
-- see: The tight control set draws from any session sharing the market mood, and the loose set stays within the night
--
-- What that breaks without this column. `ForwardReturnFiller` measured a control's outcome from
-- `setup.as_of`, joined through `control_setup.setup_id`, and its own doc comment said why: "a
-- control's session is the session it was drawn for". That sentence was true and is now false for
-- half the rows. Left alone, a tight control drawn from a session three months earlier would have
-- had its ten-day forward return measured from the setup's night instead of its own, which is not a
-- wrong number in a way anything downstream could see: it is a real return of a real stock over a
-- real window, and the window is the wrong one. The band 1 difference would then subtract a figure
-- taken over one fortnight from a figure taken over another.
--
-- The ATR moves with it, for the same reason and in the same query. Excursions are expressed in the
-- subject's own daily range, and a control's range is read on the control's own session.
--
-- Existing rows are backfilled from the setup they were drawn against, which is exactly correct
-- rather than a convenience: every row in this table was drawn under the within-night rule, so the
-- session it was drawn for IS its own session, and the backfill states what was already true. That
-- is the difference from 033, which declined to backfill because doing so would have meant reading
-- a value out of prose. Here the value is a column on a joined row.
--
-- `control_id` is unchanged and still carries no session, because the draw still yields one row per
-- ticker per set per setup. Where a name qualifies on several sessions the nearest is taken and the
-- others are not drawn, so the tight set is still five distinct names rather than one name seen five
-- times. That is a property worth keeping: five per set exists so a comparison does not inherit one
-- name's idiosyncratic move, and a set holding the same name on five adjacent sessions would inherit
-- it while looking like five.

ALTER TABLE control_setup ADD COLUMN control_as_of TEXT NULL;

UPDATE control_setup
   SET control_as_of = (SELECT s.as_of FROM setup s WHERE s.setup_id = control_setup.setup_id)
 WHERE control_as_of IS NULL;
