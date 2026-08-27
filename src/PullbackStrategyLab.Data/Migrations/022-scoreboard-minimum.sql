-- 022  scoreboard.n_minimum
--
-- What the effective count has to reach before the panel's question may be answered.
--
-- 3.6 used to fire on a calendar: "three months of accumulation, then read the scoreboard". That
-- figure was an estimate written before anything was measured, and it read as a derived quantity
-- ever since. It rested on about twelve setups a night; band 1 as built fills at about eighty-two,
-- and it is a paired comparison, so the market factor that dominates cross-sectional correlation
-- cancels and what is left to discount is the ten-day label overlap across nights.
--
-- So the trigger is a sample rather than a date, and a trigger has to be visible from the first
-- night or it is a date in disguise. The panel carries three numbers: rows, effective observations,
-- and the minimum. A reader watching the second climb toward the third can see whether the overlap
-- is costing forty percent or eighty-five, which a calendar could never have said.
--
-- Nullable, and set on band 1 alone. The other panels answer questions no checkpoint fires on, and
-- a minimum on every panel would read as a threshold each of them is being held to.
-- see: The minimum sample is 262 effective observations, ratified at two points and 90% power

ALTER TABLE scoreboard ADD COLUMN n_minimum INTEGER NULL;
