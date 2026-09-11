-- 068  the trims after the first, and the night's count of shorts closed on the hold limit
--
-- <b>A trim after the first is recorded beside it and never over it.</b> From 7.10 a position is
-- trimmed at 3R and again at 5R, on both sides. The `trim_*` columns 045 added keep the first trim
-- exactly as they always have, stamp included, and the four columns here carry every trim after it:
-- how many, their shares and their money together, and when the last was observed. Overwriting the
-- first trim's columns with running totals would have left a replay standing between the two trims
-- with no way to read the state that existed then, because one stamp can only say when the last
-- change happened. With two stamps a read bounds each half on its own, which is the ruling this table
-- already took for its close and its first trim.
-- see: Generation 1 trims 15% at 3R and again at 5R on both sides, and a short is held three sessions rather than trailed
--
-- `further_trims` is nought on every row written before 7.10, which is what they were: generation 0
-- trimmed once. The money is TEXT, being money.
--
-- `manage_run.closed_hold_limit` counts shorts closed at the next open after three sessions held, the
-- short exit from 7.10. `closed_reclaim` stays: a row armed on an hourly reclaim before the rule
-- retired still fills and is counted there, and a night's row that dropped the column would be
-- unable to say so.

ALTER TABLE position ADD COLUMN further_trims INTEGER NOT NULL DEFAULT 0 CHECK (further_trims >= 0);
ALTER TABLE position ADD COLUMN further_trimmed_shares INTEGER NULL;
ALTER TABLE position ADD COLUMN further_trim_realised_pnl TEXT NULL;
ALTER TABLE position ADD COLUMN further_trim_observed_at TEXT NULL;

ALTER TABLE manage_run ADD COLUMN closed_hold_limit INTEGER NOT NULL DEFAULT 0 CHECK (closed_hold_limit >= 0);
