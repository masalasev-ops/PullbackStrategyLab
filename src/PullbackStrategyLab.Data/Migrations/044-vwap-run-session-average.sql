-- 044  vwap_run loses the two columns that counted the session average
--
-- The session average stopped being written at 4.7. It was annotated onto every stored minute from
-- 4.4, and 4.4 raised the obligation that either a reader had to be named or the column had to stop
-- being written; the checkpoint it fell due at is this one, on the reasoning that the fill model was
-- its most likely reader. The fill model does not read it. A fill is the resting price plus the
-- captured spread, no rule in this lab compares a price against a session average, and nothing else
-- in the corpus reads it through phase 6 either.
--
-- <b>It stopped rather than being kept, because it is derivable.</b> A running session average is a
-- sum over the session's own stored minutes in order, so anything that wants one computes it from
-- `intraday_bar` at the moment it is wanted. That is the ruling VwapEngine already took over the
-- day's high and low and WatchlistPublisher took over a watchlist table. The anchored average is a
-- different case and is untouched: it needs a swing nothing else resolves.
-- see: The session average is derived when it is wanted and is not stored on a bar
--
-- <b>`intraday_bar.vwap_session` itself is not dropped, and that is deliberate.</b> Dropping it
-- would delete what past nights wrote from a bar table, which is the one table this store never
-- edits, and it would do it to tidy a document. The column stays, unwritten and unread, recorded in
-- SCHEMA with the date it stopped. What the removal buys is that `bar-append-only` no longer carries
-- an exception: nothing in the shipped source updates a bar table at all.
--
-- These two columns are a different case: `vwap_run` is a stage's own record of what it did, not a
-- bar, and two columns that will read nought on every future night are two columns a later session
-- will take for a stage that stopped working.

ALTER TABLE vwap_run DROP COLUMN sessions_priced;
ALTER TABLE vwap_run DROP COLUMN bars_annotated;
