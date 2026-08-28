-- 026  setup.correction_lateness_minutes
--
-- How late the latest input a correction used was, in minutes past the session's own end of day.
--
-- An answer the session itself asked for may arrive after it and still be attributed to it, up to a
-- stated bound, provided the lateness is recorded on the row and is countable. This is the column
-- that makes it countable. A sentence in `corrected_because` could say the same words and could not
-- be summed, filtered or excluded, and the first question anybody will ask of a corrected figure is
-- how much of it rests on late answers.
-- see: A late answer is attributed to the session it was fetched for, up to a recorded lateness bound
--
-- Minutes rather than hours, because the bound is stated in hours and a column in the same unit as
-- its own threshold cannot show how close to it a row sat. Zero is a real value and means the input
-- was inside the session's own day, which is the ordinary case a correction of a rerun-in-time
-- night produces.
--
-- Null wherever `corrected_at` is null, and written in the same statement, so a corrected row can
-- never carry a mark without its lateness.

ALTER TABLE setup ADD COLUMN correction_lateness_minutes INTEGER NULL
