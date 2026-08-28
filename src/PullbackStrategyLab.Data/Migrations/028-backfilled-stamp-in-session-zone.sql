-- 028  the backfilled indicator stamps, moved into their own Eastern session
--
-- Migration 009 gave every pre-existing indicator row a synthesised `computed_at` of "the first
-- instant of its own session", written as `as_of || 'T00:00:00.000Z'`. That is midnight UTC, which
-- is 20:00 Eastern on the *previous* session. The defect was invisible while every point-in-time
-- bound was also built in UTC: both sides were wrong by the same offset and cancelled.
--
-- With the bound now closing a session at the end of its own Eastern day, they no longer cancel.
-- A row stamped `2026-08-25T00:00:00.000Z` is inside the bound for the session of 2026-08-24, whose
-- end of day is `2026-08-25T03:59:59.999Z`, so a read as of the 24th answers with indicators
-- belonging to the 25th. That is a point-in-time leak of exactly one day, and it is the only one
-- the boundary change introduces.
--
-- 27 rows in the live store carry the synthesised stamp, all with `as_of = 2026-08-25`.
--
-- `T05:00:00.000Z` rather than `T04:00:00.000Z`, and the choice is deliberate. Midnight Eastern is
-- 05:00Z in standard time and 04:00Z in daylight time, and a single stored literal has to be
-- correct in both halves of the year. 05:00Z is inside the session on either offset: it is past the
-- previous session's end, which is 04:59:59.999Z at worst, and it is at or after the session's own
-- start. Nothing here is an observation, so an hour of conservatism costs nothing; a synthesised
-- stamp that lands in the wrong session costs a wrong answer.
--
-- Only the rows 009 synthesised are touched, identified by carrying exactly that instant. A real
-- computation stamped at midnight UTC to the millisecond has never occurred and would be a stage
-- running at 20:00 Eastern, which no slot does.
-- see: A reader's signature does not establish point-in-time; the query does

UPDATE indicator_daily
   SET computed_at = as_of || 'T05:00:00.000Z'
 WHERE computed_at = as_of || 'T00:00:00.000Z'
